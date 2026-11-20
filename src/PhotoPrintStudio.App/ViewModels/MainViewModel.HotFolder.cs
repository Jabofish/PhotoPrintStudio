using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoPrintStudio.App.Helpers;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Portable;
using PhotoPrintStudio.Core.Rendering;

namespace PhotoPrintStudio.App.ViewModels;

public partial class MainViewModel
{
    public HotFolderWatcherService HotFolderService { get; private set; } = null!;
    private readonly SemaphoreSlim _hotFolderIngestGate = new(1, 1);
    private HotFolderWatchSettings _hotFolderSettings = new();
    private bool _suppressHotFolderConfigSave;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotFolderBadgeText))]
    [NotifyPropertyChangedFor(nameof(HotFolderStatusSummary))]
    private bool _isHotFolderWatching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotFolderBadgeText))]
    [NotifyPropertyChangedFor(nameof(HotFolderStatusSummary))]
    private int _hotFolderImportedCount;

    [ObservableProperty]
    private string _hotFolderLastEventText = "未启用联机文件夹监视";

    public string HotFolderBadgeText => IsHotFolderWatching
        ? (HotFolderImportedCount > 0 ? $"监视中（{HotFolderImportedCount} 张）" : "监视中")
        : "联机监视";

    public string HotFolderStatusSummary => IsHotFolderWatching
        ? $"正在监视：{HotFolderWatchDirectory}（已接收 {HotFolderImportedCount} 张，点击「打印」仅输出当前页）"
        : string.IsNullOrWhiteSpace(HotFolderWatchDirectory)
            ? "尚未选择监视文件夹（启用后自动接收相机写入的新照片）"
            : $"已暂停监视：{HotFolderWatchDirectory}";

    public IReadOnlyDictionary<HotFolderPlacementStrategy, string> HotFolderPlacementOptions { get; } = new Dictionary<HotFolderPlacementStrategy, string>
    {
        [HotFolderPlacementStrategy.ReplaceCurrentSheet] = "替换当前页成片（推荐：历史样张留存素材库，防误打）",
        [HotFolderPlacementStrategy.PoolOnlySelectLatest] = "仅加入素材库并选中预览（绝不改动当前打印页）",
        [HotFolderPlacementStrategy.FillEmptySlotsOnly] = "顺序填入当前页空位（满页后仅入素材库不增页）",
        [HotFolderPlacementStrategy.AppendNewPage] = "每张自动追加新打印页（连续流水出片）"
    };

    public IReadOnlyDictionary<HotFolderEditTemplateMode, string> HotFolderEditTemplateOptions { get; } = new Dictionary<HotFolderEditTemplateMode, string>
    {
        [HotFolderEditTemplateMode.CloneCurrentPhotoStyle] = "复刻当前照片全部调色/换底/影棚/正装样式（推荐）",
        [HotFolderEditTemplateMode.ApplyColorPreset] = "套用下方指定的调色预设",
        [HotFolderEditTemplateMode.None] = "不套用调色模板（保留相机原色，可叠加下方快捷项）"
    };

    public string HotFolderWatchDirectory
    {
        get => _hotFolderSettings.WatchDirectory;
        set
        {
            string next = value?.Trim() ?? "";
            if (string.Equals(_hotFolderSettings.WatchDirectory, next, StringComparison.Ordinal)) return;
            _hotFolderSettings.WatchDirectory = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHotFolderDirectory));
            OnPropertyChanged(nameof(HotFolderStatusSummary));
            PersistHotFolderSettings(pauseIfWatching: true);
        }
    }

    public bool HasHotFolderDirectory => !string.IsNullOrWhiteSpace(_hotFolderSettings.WatchDirectory);

    public bool HotFolderIncludeSubdirectories
    {
        get => _hotFolderSettings.IncludeSubdirectories;
        set
        {
            if (_hotFolderSettings.IncludeSubdirectories == value) return;
            _hotFolderSettings.IncludeSubdirectories = value;
            OnPropertyChanged();
            PersistHotFolderSettings(pauseIfWatching: true);
        }
    }

    public bool HotFolderIgnoreExistingOnStart
    {
        get => _hotFolderSettings.IgnoreExistingOnStart;
        set
        {
            if (_hotFolderSettings.IgnoreExistingOnStart == value) return;
            _hotFolderSettings.IgnoreExistingOnStart = value;
            OnPropertyChanged();
            PersistHotFolderSettings(pauseIfWatching: false);
        }
    }

    public HotFolderPlacementStrategy HotFolderPlacementStrategy
    {
        get => _hotFolderSettings.PlacementStrategy;
        set
        {
            if (_hotFolderSettings.PlacementStrategy == value) return;
            _hotFolderSettings.PlacementStrategy = value;
            OnPropertyChanged();
            PersistHotFolderSettings(pauseIfWatching: false);
        }
    }

    public HotFolderEditTemplateMode HotFolderEditTemplateMode
    {
        get => _hotFolderSettings.EditTemplateMode;
        set
        {
            if (_hotFolderSettings.EditTemplateMode == value) return;
            _hotFolderSettings.EditTemplateMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHotFolderPresetPickerVisible));
            PersistHotFolderSettings(pauseIfWatching: false);
        }
    }

    public bool IsHotFolderPresetPickerVisible => _hotFolderSettings.EditTemplateMode == HotFolderEditTemplateMode.ApplyColorPreset;

    public ColorPresetItem? HotFolderSelectedColorPreset
    {
        get => Presets.FirstOrDefault(p => string.Equals(p.Id, _hotFolderSettings.ColorPresetId, StringComparison.OrdinalIgnoreCase))
               ?? Presets.FirstOrDefault();
        set
        {
            string nextId = value?.Id ?? "";
            if (string.Equals(_hotFolderSettings.ColorPresetId, nextId, StringComparison.OrdinalIgnoreCase)) return;
            _hotFolderSettings.ColorPresetId = nextId;
            OnPropertyChanged();
            PersistHotFolderSettings(pauseIfWatching: false);
        }
    }

    public bool HotFolderAutoStudioEnhance
    {
        get => _hotFolderSettings.AutoStudioEnhance;
        set
        {
            if (_hotFolderSettings.AutoStudioEnhance == value) return;
            _hotFolderSettings.AutoStudioEnhance = value;
            OnPropertyChanged();
            PersistHotFolderSettings(pauseIfWatching: false);
        }
    }

    public double HotFolderStudioEnhanceStrength
    {
        get => _hotFolderSettings.StudioEnhanceStrength;
        set
        {
            double clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(_hotFolderSettings.StudioEnhanceStrength - clamped) < 0.01) return;
            _hotFolderSettings.StudioEnhanceStrength = clamped;
            OnPropertyChanged();
            PersistHotFolderSettings(pauseIfWatching: false);
        }
    }

    public StudioEnhanceMode HotFolderStudioEnhanceMode
    {
        get => _hotFolderSettings.StudioEnhanceMode;
        set
        {
            if (_hotFolderSettings.StudioEnhanceMode == value) return;
            _hotFolderSettings.StudioEnhanceMode = value;
            OnPropertyChanged();
            PersistHotFolderSettings(pauseIfWatching: false);
        }
    }

    public bool HotFolderAutoBackgroundReplace
    {
        get => _hotFolderSettings.AutoBackgroundReplace;
        set
        {
            if (_hotFolderSettings.AutoBackgroundReplace == value) return;
            _hotFolderSettings.AutoBackgroundReplace = value;
            OnPropertyChanged();
            PersistHotFolderSettings(pauseIfWatching: false);
        }
    }

    public string HotFolderReplacementBgHex
    {
        get => _hotFolderSettings.ReplacementBackgroundHex;
        set
        {
            string next = string.IsNullOrWhiteSpace(value) ? "#438EDB" : value.Trim();
            if (string.Equals(_hotFolderSettings.ReplacementBackgroundHex, next, StringComparison.OrdinalIgnoreCase)) return;
            _hotFolderSettings.ReplacementBackgroundHex = next;
            OnPropertyChanged();
            PersistHotFolderSettings(pauseIfWatching: false);
        }
    }

    public bool HotFolderAutoFaceFrame
    {
        get => _hotFolderSettings.AutoFaceFrame;
        set
        {
            if (_hotFolderSettings.AutoFaceFrame == value) return;
            _hotFolderSettings.AutoFaceFrame = value;
            OnPropertyChanged();
            PersistHotFolderSettings(pauseIfWatching: false);
        }
    }

    public bool HotFolderAutoSelectLatest
    {
        get => _hotFolderSettings.AutoSelectLatest;
        set
        {
            if (_hotFolderSettings.AutoSelectLatest == value) return;
            _hotFolderSettings.AutoSelectLatest = value;
            OnPropertyChanged();
            PersistHotFolderSettings(pauseIfWatching: false);
        }
    }

    private void InitializeHotFolderService(HotFolderWatchSettings? savedSettings)
    {
        HotFolderService = new HotFolderWatcherService(Logger);
        HotFolderService.PhotoReady += OnHotFolderPhotoReady;
        HotFolderService.WatchError += msg => UiDispatcher.BeginInvoke(() =>
        {
            if (_disposeCts.IsCancellationRequested || HotFolderService.IsWatching) return;
            IsHotFolderWatching = false;
            HotFolderLastEventText = msg;
            StatusText = msg;
        });

        _suppressHotFolderConfigSave = true;
        try
        {
            _hotFolderSettings = savedSettings?.Clone() ?? new HotFolderWatchSettings();
            if (string.IsNullOrWhiteSpace(_hotFolderSettings.ColorPresetId) && Presets.Count > 0)
                _hotFolderSettings.ColorPresetId = Presets[0].Id;
        }
        finally
        {
            _suppressHotFolderConfigSave = false;
        }
    }

    private void PersistHotFolderSettings(bool pauseIfWatching)
    {
        if (_suppressHotFolderConfigSave) return;
        SaveConfig();
        if (pauseIfWatching && IsHotFolderWatching)
            StopHotFolderWatch();
    }

    [RelayCommand]
    private void BrowseHotFolderDirectory()
    {
        string? folder = Dialogs.ShowOpenFolderDialog("选择相机联机输出的热文件夹");
        if (string.IsNullOrWhiteSpace(folder)) return;
        HotFolderWatchDirectory = folder;
        HotFolderLastEventText = $"已选择监视目录：{folder}";
    }

    [RelayCommand]
    private void OpenHotFolderDirectory()
    {
        if (string.IsNullOrWhiteSpace(HotFolderWatchDirectory) || !Directory.Exists(HotFolderWatchDirectory))
        {
            ShowToast("请先选择有效的监视文件夹");
            return;
        }
        Dialogs.OpenPathInShell(HotFolderWatchDirectory);
    }

    [RelayCommand]
    private void SetHotFolderBgColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        HotFolderReplacementBgHex = hex;
        if (HasAiMattingModel)
            HotFolderAutoBackgroundReplace = true;
    }

    [RelayCommand]
    private void ImportExistingAndWatchHotFolder() => StartHotFolderFromDialog(ignoreExisting: false);

    [RelayCommand]
    private void WatchNewHotFolderPhotos() => StartHotFolderFromDialog(ignoreExisting: true);

    private void StartHotFolderFromDialog(bool ignoreExisting)
    {
        if (string.IsNullOrWhiteSpace(HotFolderWatchDirectory) || !Directory.Exists(HotFolderWatchDirectory))
        {
            string? folder = Dialogs.ShowOpenFolderDialog("选择相机联机输出的热文件夹");
            if (string.IsNullOrWhiteSpace(folder)) return;
            HotFolderWatchDirectory = folder;
        }

        HotFolderIgnoreExistingOnStart = ignoreExisting;
        StartHotFolderWatch();
    }

    public bool StartHotFolderWatch()
    {
        if (!HotFolderService.Start(
                _hotFolderSettings.WatchDirectory,
                _hotFolderSettings.IncludeSubdirectories,
                _hotFolderSettings.IgnoreExistingOnStart,
                out string err))
        {
            IsHotFolderWatching = false;
            HotFolderLastEventText = err;
            ShowToast(err);
            return false;
        }

        IsHotFolderWatching = true;
        HotFolderLastEventText = $"已开启监视：{Path.GetFileName(_hotFolderSettings.WatchDirectory)}（等待相机写入新照片…）";
        ShowToast($"已开启联机文件夹监视：{Path.GetFileName(_hotFolderSettings.WatchDirectory)}");
        return true;
    }

    [RelayCommand]
    public void StopHotFolderWatch()
    {
        HotFolderService.Stop();
        if (!IsHotFolderWatching) return;
        IsHotFolderWatching = false;
        HotFolderLastEventText = "已暂停联机文件夹监视";
        ShowToast("已暂停联机文件夹监视");
    }

    [RelayCommand]
    private async Task ScanHotFolderExistingNow()
    {
        if (!IsHotFolderWatching) return;
        var token = HotFolderService.SessionToken;
        int queued = await Task.Run(() => HotFolderService.ScanExistingUnseenNow(token));
        if (token.IsCancellationRequested) return;
        ShowToast(queued > 0
            ? $"已发现 {queued} 张未导入照片，正在按预设策略依次处理…"
            : "当前监视文件夹内没有尚未导入的新照片");
    }

    private async void OnHotFolderPhotoReady(string filePath)
    {
        if (_disposeCts.IsCancellationRequested) return;
        try
        {
            await IngestHotFolderPhotoAsync(filePath, HotFolderService.SessionToken);
        }
        catch (Exception ex)
        {
            Logger.Error($"联机照片导入失败: {filePath}", ex);
        }
    }

    /// <summary>
    /// 按预设策略串行接收一张热文件夹新增照片：解码缩略图 → 应用预设调色/换底/影棚/AI 构图 → 按安全排版策略入库并刷新画布。
    /// 绝不自动触发物理打印。
    /// </summary>
    internal async Task<bool> IngestHotFolderPhotoAsync(string filePath, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || token.IsCancellationRequested)
            return false;

        bool acquired = false;
        bool committed = false;
        string? preparedAssetId = null;
        try
        {
            await _hotFolderIngestGate.WaitAsync(token);
            acquired = true;
            token.ThrowIfCancellationRequested();

            string fullPath = Path.GetFullPath(filePath);

            // 1. 在 UI 线程抓取当前预设、参考照片样式与目标格位几何，避免跨线程读取可变对象
            HotFolderWatchSettings settingsSnap = null!;
            PhotoEditState? cloneSourceEdit = null;
            PhotoEditState? presetSnap = null;
            PrintSheet? targetSheet = null;
            PrintSheet? appendPrototype = null;
            LayoutSlot? targetSlot = null;
            string?[]? replacedSlots = null;
            (double trimW, double trimH, double bleed, IdPhotoSpec? idSpec, FitMode? fitOverride, double slotZoom, double slotPanX, double slotPanY) targetGeom = default;
            LayoutScenario scenarioSnap = LayoutScenario.IdPhoto;
            bool hasMatting = false;
            bool hasFace = false;
            bool alreadyInPool = false;

            await RunOnUiThreadAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                alreadyInPool = Assets.Any(a => string.Equals(a.Asset.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
                settingsSnap = _hotFolderSettings.Clone();
                cloneSourceEdit = CurrentEdit?.Clone();
                presetSnap = HotFolderSelectedColorPreset?.Parameters.Clone();
                targetSheet = ActiveSheet;
                targetSlot = ResolveHotFolderPlacementSlot(targetSheet, settingsSnap.PlacementStrategy);
                targetGeom = ResolveTargetSlotGeometry(targetSlot, targetSlot?.PhotoAssetId ?? "");
                scenarioSnap = targetSheet?.Scenario ?? ActiveScenario;
                if (settingsSnap.PlacementStrategy == HotFolderPlacementStrategy.ReplaceCurrentSheet)
                    replacedSlots = targetSheet?.Slots.Select(s => s.PhotoAssetId).ToArray();
                if (settingsSnap is { PlacementStrategy: HotFolderPlacementStrategy.AppendNewPage, AutoFaceFrame: true } &&
                    scenarioSnap != LayoutScenario.IdPhoto)
                    appendPrototype = targetSheet?.Clone();
                hasMatting = HasAiMattingModel;
                hasFace = FaceService.HasModelLoaded;
            }, token);

            if (alreadyInPool || token.IsCancellationRequested)
                return false;

            // 2. 后台解码、生成缩略图、套用预处理策略与 AI 自动构图/正装贴合
            var prepared = await Task.Run<(PhotoAsset? Asset, BitmapSource? Thumb, bool AutoFramed, List<PrintSheet>? Pages)>(() =>
            {
                token.ThrowIfCancellationRequested();
                PhotoAsset asset;
                BitmapSource thumbWpf;
                try
                {
                    asset = ExifAndColorHelper.InspectAsset(fullPath);
                    preparedAssetId = asset.Id;
                    using var thumbSk = ImageCache.GetReadOnlyThumbnail(asset);
                    thumbWpf = SkiaWpfInterop.ToBitmapSource(thumbSk);
                }
                catch (Exception ex)
                {
                    Logger.Error($"联机导入解码失败: {fullPath}", ex);
                    return (null, null, false, null);
                }

                // (a) 基础调色/换底/正装参数模板
                if (settingsSnap.EditTemplateMode == HotFolderEditTemplateMode.CloneCurrentPhotoStyle && cloneSourceEdit != null)
                {
                    asset.EditState = cloneSourceEdit.CloneColorOnlyInto(asset.EditState, includePortraitEffects: true);
                }
                else if (settingsSnap.EditTemplateMode == HotFolderEditTemplateMode.ApplyColorPreset && presetSnap != null)
                {
                    asset.EditState = presetSnap.CloneColorOnlyInto(asset.EditState, includePortraitEffects: false);
                }

                // (b) 独立快捷处理开关叠加
                if (settingsSnap.AutoStudioEnhance)
                {
                    asset.EditState.EnableStudioPortraitEnhance = true;
                    asset.EditState.StudioEnhanceStrength = Math.Clamp(settingsSnap.StudioEnhanceStrength, 0, 100);
                    asset.EditState.StudioEnhanceMode = settingsSnap.StudioEnhanceMode;
                }

                if (settingsSnap.AutoBackgroundReplace && hasMatting)
                {
                    asset.EditState.EnableAiBackgroundReplace = true;
                    asset.EditState.ReplacementBackgroundHex = settingsSnap.ReplacementBackgroundHex;
                    asset.EditState.UseGradientBackground = false;
                }

                // (c) 可选 AI 自动构图 + 正装自动肩颈贴合
                bool framed = false;
                List<PrintSheet>? preparedPages = null;
                if (appendPrototype is { Slots.Count: > 0 })
                {
                    preparedPages = LayoutEngine.BuildPaginatedSheets(appendPrototype, [asset], orientSinglePhotoPages: true);
                    var placedSlot = preparedPages.SelectMany(page => page.Slots).First(slot => slot.PhotoAssetId == asset.Id);
                    var (w, h, bleed) = PortraitAutoFrameHelper.ResolveUprightSlotPhotoFrameMm(placedSlot);
                    targetGeom = (w, h, bleed, null, placedSlot.FitModeOverride,
                        placedSlot.SlotZoom, placedSlot.SlotPanX, placedSlot.SlotPanY);
                }

                if (settingsSnap.AutoFaceFrame || (hasFace && asset.EditState.FormalSuit != FormalSuitStyle.None))
                {
                    try
                    {
                        if (settingsSnap.AutoFaceFrame && scenarioSnap != LayoutScenario.IdPhoto)
                        {
                            ApplyContentFrame(asset, targetGeom.trimW, targetGeom.trimH, targetGeom.bleed,
                                targetGeom.fitOverride, targetGeom.slotZoom, targetGeom.slotPanX, targetGeom.slotPanY, token);
                            framed = true;
                        }

                        if (hasFace && (scenarioSnap == LayoutScenario.IdPhoto || asset.EditState.FormalSuit != FormalSuitStyle.None))
                        {
                            var (pw, ph, faces) = GetOrDetectFacesCached(asset, token);
                            if (faces.Count > 0)
                            {
                                if (settingsSnap.AutoFaceFrame && scenarioSnap == LayoutScenario.IdPhoto)
                                {
                                    var res = PortraitAutoFrameHelper.ComputeAutoFrame(
                                        pw, ph, faces, asset.EditState,
                                        targetGeom.trimW, targetGeom.trimH, targetGeom.bleed,
                                        targetGeom.fitOverride ?? asset.EditState.FitMode,
                                        targetGeom.idSpec,
                                        adjustZoomAndPan: true,
                                        targetGeom.slotZoom, targetGeom.slotPanX, targetGeom.slotPanY);
                                    if (res.Success)
                                    {
                                        asset.EditState.RotationDegrees = res.RotationDegrees;
                                        asset.EditState.Zoom = res.Zoom;
                                        asset.EditState.PanX = res.PanX;
                                        asset.EditState.PanY = res.PanY;
                                        framed = true;
                                    }
                                }

                                if (asset.EditState.FormalSuit != FormalSuitStyle.None)
                                {
                                    var (cx, ny, sc) = FormalSuitRenderer.ComputeSuitPlacement(faces, pw, ph);
                                    asset.EditState.SuitCenterXRatio = cx;
                                    asset.EditState.SuitNeckYRatio = ny;
                                    asset.EditState.SuitScale = sc;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Logger.Error($"联机照片自动 AI 构图异常: {fullPath}", ex);
                    }
                }

                return (asset, thumbWpf, framed, preparedPages);
            }, token);

            if (prepared.Asset == null || token.IsCancellationRequested)
                return false;

            // 3. 回到 UI 线程按安全入库策略落地（防样张误打）
            await RunOnUiThreadAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                if (Assets.Any(a => string.Equals(a.Asset.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)))
                    return;

                var vmItem = new AssetItemViewModel(prepared.Asset, prepared.Thumb);
                PushUndoSnapshot();
                Assets.Add(vmItem);
                committed = true;

                var currentTarget = ResolveHotFolderPlacementSlot(ActiveSheet, settingsSnap.PlacementStrategy);
                if (ActiveSheet != targetSheet || currentTarget != targetSlot ||
                    (replacedSlots != null && !replacedSlots.SequenceEqual(targetSheet!.Slots.Select(s => s.PhotoAssetId))))
                    settingsSnap.PlacementStrategy = HotFolderPlacementStrategy.PoolOnlySelectLatest;

                string placementSummary = ApplyHotFolderPlacement(vmItem, settingsSnap, prepared.Pages);

                HotFolderImportedCount++;
                string frameTag = prepared.AutoFramed ? "，已智能构图" : "";
                string msg = $"联机接收：{vmItem.DisplayName}（{placementSummary}{frameTag}）";
                HotFolderLastEventText = $"{DateTime.Now:HH:mm:ss} {msg}";
                ShowToast(msg);
            }, token);

            return committed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (acquired) _hotFolderIngestGate.Release();
            if (!committed && preparedAssetId != null)
            {
                ImageCache.RemoveAsset(preparedAssetId);
                _faceDetectionCache.TryRemove(preparedAssetId, out _);
            }
        }
    }

    private LayoutSlot? ResolveHotFolderPlacementSlot(PrintSheet? sheet, HotFolderPlacementStrategy strategy)
    {
        if (sheet is not { Slots.Count: > 0 }) return null;
        var empty = sheet.Slots.FirstOrDefault(s => string.IsNullOrEmpty(s.PhotoAssetId));
        return strategy switch
        {
            HotFolderPlacementStrategy.FillEmptySlotsOnly => empty,
            HotFolderPlacementStrategy.ReplaceCurrentSheet when sheet.Scenario != LayoutScenario.IdPhoto && sheet.Slots.Count > 1 =>
                SelectedSlot is { } selected && sheet.Slots.Contains(selected) ? selected : empty ?? sheet.Slots[0],
            _ => sheet.Slots[0]
        };
    }

    private void PreviewHotFolderAsset(AssetItemViewModel item)
    {
        if (SelectedSlot != null) SelectSlot(null);
        SelectAsset(item);
        if (ViewMode == CanvasViewMode.SheetOverview)
            SetViewMode(CanvasViewMode.SingleRetouchCrop);
    }

    private string ApplyHotFolderPlacement(AssetItemViewModel item, HotFolderWatchSettings settings, List<PrintSheet>? preparedPages)
    {
        string summary;
        if (ActiveSheet is not { Slots.Count: > 0 } sheet)
        {
            UpdateAssetUsageCounts();
            if (settings.AutoSelectLatest) PreviewHotFolderAsset(item);
            return "已加入素材库";
        }

        switch (settings.PlacementStrategy)
        {
            case HotFolderPlacementStrategy.ReplaceCurrentSheet:
                if (sheet.Scenario == LayoutScenario.IdPhoto || sheet.Slots.Count == 1)
                {
                    foreach (var s in sheet.Slots)
                        s.PhotoAssetId = item.Id;
                    UpdateAssetUsageCounts();
                    if (settings.AutoSelectLatest)
                        SelectSlot(sheet.Slots[0]);
                    else
                        ScheduleRender();
                    summary = "已替换当前页成片，原样张留存素材库";
                }
                else
                {
                    var targetSlot = ResolveHotFolderPlacementSlot(sheet, settings.PlacementStrategy)!;
                    targetSlot.PhotoAssetId = item.Id;
                    UpdateAssetUsageCounts();
                    if (settings.AutoSelectLatest)
                        SelectSlot(targetSlot);
                    else
                        ScheduleRender();
                    summary = $"已更新格位 #{targetSlot.Index + 1}，原样张留存素材库";
                }
                break;

            case HotFolderPlacementStrategy.FillEmptySlotsOnly:
                var empty = ResolveHotFolderPlacementSlot(sheet, settings.PlacementStrategy);
                if (empty != null)
                {
                    empty.PhotoAssetId = item.Id;
                    UpdateAssetUsageCounts();
                    if (settings.AutoSelectLatest) SelectSlot(empty);
                    else ScheduleRender();
                    summary = $"已填入空位 #{empty.Index + 1}";
                }
                else
                {
                    UpdateAssetUsageCounts();
                    if (settings.AutoSelectLatest) PreviewHotFolderAsset(item);
                    summary = "当前页已满，仅加入素材库待选";
                }
                break;

            case HotFolderPlacementStrategy.AppendNewPage:
                var importPlacement = sheet.Scenario == LayoutScenario.IdPhoto
                    ? ImportPlacement.OnePhotoPerPage
                    : ImportPlacement.Paginate;
                AppendPhotoPages([item], new ImportArrangement(importPlacement), preparedPages,
                    orientSinglePhotoPages: true, selectFirstPage: settings.AutoSelectLatest);
                UpdateAssetUsageCounts();
                summary = $"已追加至第 {Sheets.Count} 页";
                break;

            case HotFolderPlacementStrategy.PoolOnlySelectLatest:
            default:
                UpdateAssetUsageCounts();
                if (settings.AutoSelectLatest) PreviewHotFolderAsset(item);
                summary = "仅入素材库，未改动当前打印页";
                break;
        }

        UpdatePreflight();
        return summary;
    }

    private Task RunOnUiThreadAsync(Action action, CancellationToken token)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UiDispatcher.BeginInvoke(() =>
        {
            if (token.IsCancellationRequested) return;
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task.WaitAsync(token);
    }
}
