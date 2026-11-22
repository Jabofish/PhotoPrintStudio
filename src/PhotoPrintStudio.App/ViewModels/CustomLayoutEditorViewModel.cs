using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Persistence;

namespace PhotoPrintStudio.App.ViewModels;

public sealed partial class DesignerFrameItem : ObservableObject
{
    public string Id { get; }

    [ObservableProperty] private int _index;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private double _xMm;
    [ObservableProperty] private double _yMm;
    [ObservableProperty] private double _widthMm;
    [ObservableProperty] private double _heightMm;
    [ObservableProperty] private double _bleedMm;
    [ObservableProperty] private double _cornerRadiusMm;
    [ObservableProperty] private double _slotRotationDegrees;
    [ObservableProperty] private FitMode? _fitModeOverride;
    [ObservableProperty] private string? _idPhotoSpecId;
    [ObservableProperty] private PolaroidSlotStyle? _polaroidStyle;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isOutOfBounds;
    [ObservableProperty] private bool _isOverlapping;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Label) ? $"图片框 {Index + 1}" : Label;
    public string DimensionsSummary => $"{WidthMm:0.#} × {HeightMm:0.#} mm";
    public string PositionSummary => $"X {XMm:0.#}, Y {YMm:0.#} mm" + (SlotRotationDegrees > 0.1 ? $"，{SlotRotationDegrees:0}°" : "");

    public DesignerFrameItem(CustomSlotDefinition def, int index)
    {
        Id = string.IsNullOrWhiteSpace(def.Id) ? Guid.NewGuid().ToString("N") : def.Id;
        _index = index;
        _label = string.IsNullOrWhiteSpace(def.Label) ? $"图片框 {index + 1}" : def.Label;
        _xMm = Math.Round(def.XMm, 2);
        _yMm = Math.Round(def.YMm, 2);
        _widthMm = Math.Round(def.WidthMm, 2);
        _heightMm = Math.Round(def.HeightMm, 2);
        _bleedMm = Math.Round(def.BleedMm, 2);
        _cornerRadiusMm = Math.Round(def.CornerRadiusMm, 2);
        _slotRotationDegrees = NormalizeRotation(def.SlotRotationDegrees);
        _fitModeOverride = def.FitModeOverride;
        _idPhotoSpecId = def.IdPhotoSpecId;
        _polaroidStyle = def.PolaroidStyle?.Clone();
    }

    partial void OnIndexChanged(int value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnXMmChanged(double value) => OnPropertyChanged(nameof(PositionSummary));
    partial void OnYMmChanged(double value) => OnPropertyChanged(nameof(PositionSummary));
    partial void OnWidthMmChanged(double value) => OnPropertyChanged(nameof(DimensionsSummary));
    partial void OnHeightMmChanged(double value) => OnPropertyChanged(nameof(DimensionsSummary));
    partial void OnSlotRotationDegreesChanged(double value) => OnPropertyChanged(nameof(PositionSummary));

    public LayoutRectMm ToRect() => new(XMm, YMm, WidthMm, HeightMm);

    public CustomSlotDefinition ToDefinition() => new()
    {
        Id = Id,
        XMm = Math.Round(XMm, 3),
        YMm = Math.Round(YMm, 3),
        WidthMm = Math.Round(WidthMm, 3),
        HeightMm = Math.Round(HeightMm, 3),
        BleedMm = Math.Max(0, Math.Round(BleedMm, 3)),
        CornerRadiusMm = Math.Max(0, Math.Round(CornerRadiusMm, 3)),
        SlotRotationDegrees = NormalizeRotation(SlotRotationDegrees),
        Label = Label,
        FitModeOverride = FitModeOverride,
        IdPhotoSpecId = IdPhotoSpecId,
        PolaroidStyle = PolaroidStyle?.Clone()
    };

    public static double NormalizeRotation(double degrees)
    {
        double norm = ((Math.Round(degrees / 90.0) * 90.0) % 360.0 + 360.0) % 360.0;
        return Math.Abs(norm - 360.0) < 0.1 ? 0 : norm;
    }
}

public sealed record EditorSnapshot(
    List<CustomSlotDefinition> Slots,
    PaperSizeSpec Paper,
    bool IsLandscape,
    List<string> SelectedIds,
    string? PrimarySelectedId);

public sealed partial class CustomLayoutEditorViewModel : ObservableObject
{
    private readonly MainViewModel _mainVm;
    private readonly UndoRedoStack<EditorSnapshot> _undoStack = new(80);
    private EditorSnapshot? _pendingInteractiveSnapshot;
    private bool _interactiveMutated;
    private bool _suppressInspectorSync;
    private bool _suppressPaperChangeUndo;

    public string? EditingTemplateId { get; }
    public bool IsEditingExistingTemplate => !string.IsNullOrEmpty(EditingTemplateId);
    public string WindowTitle => IsEditingExistingTemplate ? $"编辑自定义版式 - {TemplateName}" : "自由自定义版式编辑器";

    public ObservableCollection<DesignerFrameItem> Frames { get; } = [];
    public IReadOnlyList<PaperSizeSpec> PaperSizes { get; }
    public IReadOnlyList<PolaroidSpec> PolaroidSpecs => _mainVm.PolaroidSpecs;
    public IReadOnlyList<FrameSizePreset> SizePresets => FreeformLayoutMath.CommonSizePresets;
    public IReadOnlyDictionary<CropMarkStyle, string> CropMarkStyles { get; }
    public IReadOnlyList<LayoutTemplateDefinition> ImportableTemplates { get; }
    public IReadOnlyList<double> GridStepOptions { get; } = [1.0, 2.0, 5.0, 10.0];
    public IReadOnlyList<double> RotationOptions { get; } = [0.0, 90.0, 180.0, 270.0];
    public IReadOnlyDictionary<int, string> FitModeOptions { get; } = new Dictionary<int, string>
    {
        [0] = "跟随全局设置",
        [1] = "填满裁切 (Cover)",
        [2] = "完整显示 (Contain)"
    };
    public IReadOnlyDictionary<LayoutScenario, string> ScenarioOptions { get; } = new Dictionary<LayoutScenario, string>
    {
        [LayoutScenario.FreeGrid] = "日常打印 / 自由排版",
        [LayoutScenario.SinglePhoto] = "日常打印 / 单张照片",
        [LayoutScenario.SixInchTwoInOne] = "日常打印 / 六寸二合一",
        [LayoutScenario.IdPhoto] = "证件照排版",
        [LayoutScenario.Polaroid] = "拍立得样式"
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _templateName = "我的自由版式";

    [ObservableProperty] private string _templateSubtitle = "";
    [ObservableProperty] private LayoutScenario _scenario = LayoutScenario.FreeGrid;
    [ObservableProperty] private PaperSizeSpec _selectedPaper;
    [ObservableProperty] private bool _isLandscape = true;
    [ObservableProperty] private CropMarkStyle _cropMarkStyle = CropMarkStyle.OuterCornerTicks;
    [ObservableProperty] private string _canvasBackgroundHex = "#FFFFFF";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaperSummaryText))]
    private double _paperWidthMm = 152.4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaperSummaryText))]
    private double _paperHeightMm = 101.6;

    // 视图与吸附选项
    [ObservableProperty] private bool _enableMagneticSnap = true;
    [ObservableProperty] private bool _enableGridSnap = false;
    [ObservableProperty] private bool _showGrid = true;
    [ObservableProperty] private double _gridStepMm = 5.0;
    [ObservableProperty] private bool _showSafeMargin = true;
    [ObservableProperty] private double _safeMarginMm = 4.0;
    [ObservableProperty] private bool _showSampleOverlay = true;
    [ObservableProperty] private bool _lockAspectRatio = false;
    [ObservableProperty] private double _zoom = 1.0;

    // 选中状态
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(IsSingleSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedCountText))]
    private DesignerFrameItem? _primarySelectedFrame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultiSelection))]
    [NotifyPropertyChangedFor(nameof(CanDistribute))]
    [NotifyPropertyChangedFor(nameof(IsSingleSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedCountText))]
    private int _selectedCount;

    public bool HasSelection => PrimarySelectedFrame != null && SelectedCount > 0;
    public bool IsSingleSelection => SelectedCount == 1 && PrimarySelectedFrame != null;
    public bool HasMultiSelection => SelectedCount >= 2;
    public bool CanDistribute => SelectedCount >= 3;
    public string SelectedCountText => SelectedCount switch
    {
        0 => "未选中图片框",
        1 => PrimarySelectedFrame?.DisplayTitle ?? "已选 1 个图片框",
        _ => $"已选中 {SelectedCount} 个图片框"
    };

    // 精确属性编辑字段（绑定到右侧面板）
    [ObservableProperty] private double _selectedXMm;
    [ObservableProperty] private double _selectedYMm;
    [ObservableProperty] private double _selectedWidthMm;
    [ObservableProperty] private double _selectedHeightMm;
    [ObservableProperty] private double _selectedRightMarginMm;
    [ObservableProperty] private double _selectedBottomMarginMm;
    [ObservableProperty] private double _selectedRotationDegrees;
    [ObservableProperty] private double _selectedCornerRadiusMm;
    [ObservableProperty] private double _selectedBleedMm;
    [ObservableProperty] private string _selectedLabel = "";
    [ObservableProperty] private int _selectedFitModeIndex;
    [ObservableProperty] private bool _selectedIsPolaroidFrame;
    [ObservableProperty] private PolaroidSpec? _selectedFramePolaroidSpec;

    // 快速网格生成器参数
    [ObservableProperty] private int _gridRows = 2;
    [ObservableProperty] private int _gridCols = 2;
    [ObservableProperty] private double _gridMarginMm = 4.0;
    [ObservableProperty] private double _gridGapMm = 4.0;
    [ObservableProperty] private double _gridCornerRadiusMm = 0.0;
    [ObservableProperty] private FrameSizePreset? _selectedFixedTilePreset;

    // 撤销、校验与状态栏
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private bool _canSave;
    [ObservableProperty] private bool _hasOutOfBounds;
    [ObservableProperty] private bool _hasOverlaps;
    [ObservableProperty] private string _validationMessage = "";
    [ObservableProperty] private string _pointerCoordText = "";

    public string PaperSummaryText => $"{PaperWidthMm:0.#} × {PaperHeightMm:0.#} mm";

    /// <summary>
    /// 用于画布可选底图预览的当前工作区示例缩略图（若用户已导入照片）。
    /// </summary>
    public BitmapSource? SamplePreviewImage { get; }

    public event Action? CanvasVisualChanged;

    public CustomLayoutEditorViewModel(MainViewModel mainVm, LayoutTemplateDefinition? existingTemplate = null, bool editCurrentPage = false)
    {
        _mainVm = mainVm;
        CropMarkStyles = new Dictionary<CropMarkStyle, string>(mainVm.CropMarkStyles)
        {
            [CropMarkStyle.CenterFoldTicks] = "纸张中线"
        };
        _selectedPaper = mainVm.SelectedPaper;
        _safeMarginMm = mainVm.CurrentCalibration.DefaultBorderless ? 0.0 : Math.Max(0, mainVm.CurrentCalibration.SafeMarginMm);
        _showSafeMargin = _safeMarginMm > 0.2;
        _selectedFixedTilePreset = FreeformLayoutMath.CommonSizePresets.FirstOrDefault(p => p.WidthMm == 63.0 && p.HeightMm == 90.0)
            ?? FreeformLayoutMath.CommonSizePresets[0];
        _selectedFramePolaroidSpec = mainVm.SelectedPolaroidSpec;

        SamplePreviewImage = mainVm.SelectedAsset?.Thumbnail
            ?? mainVm.Assets.FirstOrDefault()?.Thumbnail;

        ImportableTemplates = LayoutEngine.BuiltInTemplates.ToList();

        _suppressPaperChangeUndo = true;
        try
        {
            if (editCurrentPage && mainVm.ActiveSheet is { } current)
            {
                EditingTemplateId = existingTemplate?.Id;
                TemplateName = existingTemplate?.Name ?? SuggestNewTemplateName(mainVm);
                TemplateSubtitle = existingTemplate?.Subtitle ?? "";
                Scenario = current.Scenario;
                SelectedPaper = mainVm.SelectedPaper;
                IsLandscape = current.IsLandscape;
                CropMarkStyle = current.ShowCenterFoldTick && current.CropMarkStyle == CropMarkStyle.None
                    ? CropMarkStyle.CenterFoldTicks : current.CropMarkStyle;
                CanvasBackgroundHex = current.CanvasBackgroundHex;
                UpdatePaperDimensions();
                LoadSlots(current.Slots.Select(CustomSlotDefinition.FromLayoutSlot));
            }
            else if (existingTemplate != null && !existingTemplate.IsBuiltIn)
            {
                EditingTemplateId = existingTemplate.Id;
                TemplateName = existingTemplate.Name;
                TemplateSubtitle = existingTemplate.Subtitle;
                Scenario = existingTemplate.Scenario;
                CropMarkStyle = existingTemplate.DefaultCropMarkStyle;
                CanvasBackgroundHex = string.IsNullOrWhiteSpace(existingTemplate.CustomCanvasBackgroundHex)
                    ? "#FFFFFF"
                    : existingTemplate.CustomCanvasBackgroundHex;

                var paper = mainVm.PaperSizes.FirstOrDefault(p => p.Id == existingTemplate.DefaultPaperId)
                    ?? (existingTemplate.CustomPaperWidthMm > 10 && existingTemplate.CustomPaperHeightMm > 10
                        ? new PaperSizeSpec(existingTemplate.DefaultPaperId,
                            $"模板纸张 ({existingTemplate.CustomPaperWidthMm:0.#}×{existingTemplate.CustomPaperHeightMm:0.#} mm)",
                            existingTemplate.CustomPaperWidthMm.Value, existingTemplate.CustomPaperHeightMm.Value, "自定义", "", false)
                        : mainVm.SelectedPaper);

                SelectedPaper = paper;
                IsLandscape = existingTemplate.DefaultLandscape;
                UpdatePaperDimensions();

                if (existingTemplate.IsCustomFreeform && existingTemplate.CustomSlots is { Count: > 0 } customSlots)
                {
                    LoadSlots(customSlots);
                }
                else
                {
                    // 兼容旧版参数式自定义模板：自动展开为可自由编辑的图片框
                    var built = LayoutEngine.BuildSheet(existingTemplate, paper, isLandscapeOverride: IsLandscape,
                        idSpecOverride: mainVm.IdPhotoSpecs.FirstOrDefault(s => s.Id == existingTemplate.IdPhotoSpecId),
                        polaroidSpecOverride: PolaroidSpecs.FirstOrDefault(s => s.Id == existingTemplate.PolaroidSpecId));
                    LoadSlots(built.Slots.Select(CustomSlotDefinition.FromLayoutSlot));
                }
            }
            else
            {
                // 新建自由版式：默认从当前页的纸张与格位起步，立即可拖拽调整
                TemplateName = SuggestNewTemplateName(mainVm);
                Scenario = mainVm.ActiveScenario;
                SelectedPaper = mainVm.SelectedPaper;
                IsLandscape = mainVm.IsLandscape;
                CropMarkStyle = mainVm.SelectedCropMarkStyle;
                CanvasBackgroundHex = mainVm.CanvasBackgroundHex;
                UpdatePaperDimensions();

                if (mainVm.ActiveSheet is { Slots.Count: > 0 } active)
                {
                    LoadSlots(active.Slots.Select(CustomSlotDefinition.FromLayoutSlot));
                }
                else
                {
                    var initialRect = FreeformLayoutMath.ComputeNextSlotRect(PaperWidthMm, PaperHeightMm, [], null);
                    LoadSlots([new CustomSlotDefinition
                    {
                        XMm = initialRect.X,
                        YMm = initialRect.Y,
                        WidthMm = initialRect.Width,
                        HeightMm = initialRect.Height,
                        Label = "图片框 1"
                    }]);
                }
            }
        }
        finally
        {
            _suppressPaperChangeUndo = false;
        }

        PaperSizes = mainVm.PaperSizes.Contains(SelectedPaper) ? mainVm.PaperSizes : [.. mainVm.PaperSizes, SelectedPaper];
        if (Frames.Count > 0)
            SelectSingleFrame(Frames[0]);
        ValidateLayout();
    }

    private static string SuggestNewTemplateName(MainViewModel vm)
    {
        var existingNames = vm.FilteredTemplates.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i <= 99; i++)
        {
            string candidate = $"自定义版式 {i}";
            if (!existingNames.Contains(candidate)) return candidate;
        }
        return $"自定义版式 {DateTime.Now:HHmm}";
    }

    private void LoadSlots(IEnumerable<CustomSlotDefinition> defs)
    {
        Frames.Clear();
        int idx = 0;
        foreach (var d in defs)
        {
            var copy = d.Clone();
            if (string.IsNullOrWhiteSpace(copy.Label))
                copy.Label = $"图片框 {idx + 1}";
            Frames.Add(new DesignerFrameItem(copy, idx++));
        }
    }

    private void ReindexFrames()
    {
        for (int i = 0; i < Frames.Count; i++)
            Frames[i].Index = i;
    }

    private void UpdatePaperDimensions()
    {
        double longSide = Math.Max(SelectedPaper.WidthMm, SelectedPaper.HeightMm);
        double shortSide = Math.Min(SelectedPaper.WidthMm, SelectedPaper.HeightMm);
        PaperWidthMm = IsLandscape ? longSide : shortSide;
        PaperHeightMm = IsLandscape ? shortSide : longSide;
    }

    partial void OnSelectedPaperChanging(PaperSizeSpec value)
    {
        if (!_suppressPaperChangeUndo && value != null) PushUndo();
    }

    partial void OnSelectedPaperChanged(PaperSizeSpec value)
    {
        if (value == null) return;
        UpdatePaperDimensions();
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    partial void OnIsLandscapeChanging(bool value)
    {
        if (!_suppressPaperChangeUndo) PushUndo();
    }

    partial void OnIsLandscapeChanged(bool value)
    {
        UpdatePaperDimensions();
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    partial void OnShowGridChanged(bool value) => NotifyCanvasChanged();
    partial void OnGridStepMmChanged(double value) => NotifyCanvasChanged();
    partial void OnShowSafeMarginChanged(bool value) => NotifyCanvasChanged();
    partial void OnShowSampleOverlayChanged(bool value) => NotifyCanvasChanged();
    partial void OnCanvasBackgroundHexChanged(string value) => NotifyCanvasChanged();

    // === 选择管理 ===
    public IReadOnlyList<DesignerFrameItem> GetSelectedFrames() =>
        Frames.Where(f => f.IsSelected).ToList();

    public void SelectSingleFrame(DesignerFrameItem? frame)
    {
        foreach (var f in Frames)
            f.IsSelected = ReferenceEquals(f, frame);
        PrimarySelectedFrame = frame;
        SelectedCount = frame != null ? 1 : 0;
        SyncInspectorFromPrimary();
        NotifyCanvasChanged();
    }

    public void ToggleFrameSelection(DesignerFrameItem frame)
    {
        frame.IsSelected = !frame.IsSelected;
        var selected = GetSelectedFrames();
        SelectedCount = selected.Count;
        if (frame.IsSelected)
            PrimarySelectedFrame = frame;
        else
            PrimarySelectedFrame = selected.LastOrDefault();
        SyncInspectorFromPrimary();
        NotifyCanvasChanged();
    }

    public void SetSelectedFrames(IEnumerable<DesignerFrameItem> targets, DesignerFrameItem? primary = null)
    {
        var set = targets.ToHashSet();
        foreach (var f in Frames)
            f.IsSelected = set.Contains(f);
        var selected = GetSelectedFrames();
        SelectedCount = selected.Count;
        PrimarySelectedFrame = primary != null && set.Contains(primary) ? primary : selected.LastOrDefault();
        SyncInspectorFromPrimary();
        NotifyCanvasChanged();
    }

    [RelayCommand]
    public void SelectAllFrames()
    {
        if (Frames.Count == 0) return;
        SetSelectedFrames(Frames, Frames[^1]);
    }

    public void ClearSelection()
    {
        SelectSingleFrame(null);
    }

    // === 精确属性面板双向同步 ===
    public void SyncInspectorFromPrimary()
    {
        _suppressInspectorSync = true;
        try
        {
            if (PrimarySelectedFrame is { } f)
            {
                SelectedXMm = Math.Round(f.XMm, 2);
                SelectedYMm = Math.Round(f.YMm, 2);
                SelectedWidthMm = Math.Round(f.WidthMm, 2);
                SelectedHeightMm = Math.Round(f.HeightMm, 2);
                SelectedRightMarginMm = Math.Round(PaperWidthMm - f.XMm - f.WidthMm, 2);
                SelectedBottomMarginMm = Math.Round(PaperHeightMm - f.YMm - f.HeightMm, 2);
                SelectedRotationDegrees = f.SlotRotationDegrees;
                SelectedCornerRadiusMm = Math.Round(f.CornerRadiusMm, 2);
                SelectedBleedMm = Math.Round(f.BleedMm, 2);
                SelectedLabel = f.Label;
                SelectedFitModeIndex = f.FitModeOverride switch
                {
                    FitMode.Cover => 1,
                    FitMode.Contain => 2,
                    _ => 0
                };
                SelectedIsPolaroidFrame = f.PolaroidStyle != null;
                if (f.PolaroidStyle != null)
                {
                    SelectedFramePolaroidSpec = PolaroidSpecs.FirstOrDefault(p => p.Id == f.PolaroidStyle.PolaroidSpecId)
                        ?? PolaroidSpecs.FirstOrDefault();
                }
            }
            else
            {
                SelectedXMm = 0;
                SelectedYMm = 0;
                SelectedWidthMm = 0;
                SelectedHeightMm = 0;
                SelectedRightMarginMm = 0;
                SelectedBottomMarginMm = 0;
                SelectedRotationDegrees = 0;
                SelectedCornerRadiusMm = 0;
                SelectedBleedMm = 0;
                SelectedLabel = "";
                SelectedFitModeIndex = 0;
                SelectedIsPolaroidFrame = false;
            }
        }
        finally
        {
            _suppressInspectorSync = false;
        }
    }

    public void SetFramePosition(double xMm, double yMm, bool pushUndo = true)
    {
        if (PrimarySelectedFrame is not { } f) return;
        if (!double.IsFinite(xMm) || !double.IsFinite(yMm)) return;
        double clampedX = Math.Round(Math.Clamp(xMm, 0, Math.Max(0, PaperWidthMm - f.WidthMm)), 2);
        double clampedY = Math.Round(Math.Clamp(yMm, 0, Math.Max(0, PaperHeightMm - f.HeightMm)), 2);
        if (Math.Abs(clampedX - f.XMm) < 0.005 && Math.Abs(clampedY - f.YMm) < 0.005)
        {
            SyncInspectorFromPrimary();
            return;
        }
        if (pushUndo) PushUndo();
        f.XMm = clampedX;
        f.YMm = clampedY;
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    public void SetFrameSize(double widthMm, double heightMm, bool widthDriven, bool pushUndo = true)
    {
        if (PrimarySelectedFrame is not { } f) return;
        if (!double.IsFinite(widthMm) || !double.IsFinite(heightMm)) return;

        double minSize = Math.Min(FreeformLayoutMath.MinSlotSizeMm, Math.Min(PaperWidthMm, PaperHeightMm));
        double newW = Math.Clamp(widthMm, minSize, PaperWidthMm);
        double newH = Math.Clamp(heightMm, minSize, PaperHeightMm);

        if (LockAspectRatio && f.WidthMm > 0.01 && f.HeightMm > 0.01)
        {
            double aspect = f.WidthMm / f.HeightMm;
            double minWidth = Math.Max(minSize, minSize * aspect);
            double maxWidth = Math.Min(PaperWidthMm, PaperHeightMm * aspect);
            if (minWidth <= maxWidth)
            {
                newW = Math.Clamp(widthDriven ? newW : newH * aspect, minWidth, maxWidth);
                newH = newW / aspect;
            }
        }

        newW = Math.Round(newW, 2);
        newH = Math.Round(newH, 2);
        double newX = Math.Round(Math.Clamp(f.XMm, 0, Math.Max(0, PaperWidthMm - newW)), 2);
        double newY = Math.Round(Math.Clamp(f.YMm, 0, Math.Max(0, PaperHeightMm - newH)), 2);

        if (Math.Abs(newW - f.WidthMm) < 0.005 && Math.Abs(newH - f.HeightMm) < 0.005 &&
            Math.Abs(newX - f.XMm) < 0.005 && Math.Abs(newY - f.YMm) < 0.005)
        {
            SyncInspectorFromPrimary();
            return;
        }

        if (pushUndo) PushUndo();
        f.XMm = newX;
        f.YMm = newY;
        f.WidthMm = newW;
        f.HeightMm = newH;
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    public void SetFrameRightMargin(double rightMarginMm)
    {
        if (PrimarySelectedFrame is not { } f || !double.IsFinite(rightMarginMm)) return;
        SetFramePosition(PaperWidthMm - f.WidthMm - rightMarginMm, f.YMm);
    }

    public void SetFrameBottomMargin(double bottomMarginMm)
    {
        if (PrimarySelectedFrame is not { } f || !double.IsFinite(bottomMarginMm)) return;
        SetFramePosition(f.XMm, PaperHeightMm - f.HeightMm - bottomMarginMm);
    }

    partial void OnSelectedRotationDegreesChanged(double value)
    {
        if (_suppressInspectorSync || PrimarySelectedFrame is not { } f) return;
        double norm = DesignerFrameItem.NormalizeRotation(value);
        if (Math.Abs(f.SlotRotationDegrees - norm) < 0.1) return;
        PushUndo();
        foreach (var sel in GetSelectedFrames())
            sel.SlotRotationDegrees = norm;
        ValidateLayout();
        NotifyCanvasChanged();
    }

    partial void OnSelectedCornerRadiusMmChanged(double value)
    {
        if (_suppressInspectorSync || PrimarySelectedFrame is not { } f) return;
        double clamped = Math.Round(Math.Clamp(value, 0, 50), 2);
        if (Math.Abs(f.CornerRadiusMm - clamped) < 0.005) return;
        PushUndo();
        foreach (var sel in GetSelectedFrames())
            sel.CornerRadiusMm = clamped;
        NotifyCanvasChanged();
    }

    partial void OnSelectedBleedMmChanged(double value)
    {
        if (_suppressInspectorSync || PrimarySelectedFrame is not { } f) return;
        double clamped = Math.Round(Math.Clamp(value, 0, 10), 2);
        if (Math.Abs(f.BleedMm - clamped) < 0.005) return;
        PushUndo();
        foreach (var sel in GetSelectedFrames())
            sel.BleedMm = clamped;
        NotifyCanvasChanged();
    }

    partial void OnSelectedLabelChanged(string value)
    {
        if (_suppressInspectorSync || PrimarySelectedFrame is not { } f) return;
        string trimmed = value?.Trim() ?? "";
        if (string.Equals(f.Label, trimmed, StringComparison.Ordinal)) return;
        PushUndo();
        f.Label = trimmed;
        OnPropertyChanged(nameof(SelectedCountText));
        NotifyCanvasChanged();
    }

    partial void OnSelectedFitModeIndexChanged(int value)
    {
        if (_suppressInspectorSync || PrimarySelectedFrame is not { } f) return;
        FitMode? next = value switch
        {
            1 => FitMode.Cover,
            2 => FitMode.Contain,
            _ => null
        };
        if (f.FitModeOverride == next) return;
        PushUndo();
        foreach (var sel in GetSelectedFrames())
            sel.FitModeOverride = next;
        NotifyCanvasChanged();
    }

    partial void OnSelectedIsPolaroidFrameChanged(bool value)
    {
        if (_suppressInspectorSync || PrimarySelectedFrame is not { } f) return;
        bool currentlyPolaroid = f.PolaroidStyle != null;
        if (currentlyPolaroid == value) return;
        PushUndo();
        var spec = SelectedFramePolaroidSpec ?? PolaroidSpecs.FirstOrDefault() ?? PolaroidSpec.BuiltInSpecs[0];
        foreach (var sel in GetSelectedFrames())
        {
            sel.PolaroidStyle = value ? PolaroidSlotStyle.FromSpec(spec) : null;
            if (value && sel.CornerRadiusMm < 0.01)
                sel.CornerRadiusMm = spec.CornerRadiusMm;
        }
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    partial void OnSelectedFramePolaroidSpecChanged(PolaroidSpec? value)
    {
        if (_suppressInspectorSync || value == null || !SelectedIsPolaroidFrame) return;
        PushUndo();
        foreach (var sel in GetSelectedFrames().Where(s => s.PolaroidStyle != null))
        {
            var prev = sel.PolaroidStyle!;
            sel.PolaroidStyle = new PolaroidSlotStyle
            {
                PolaroidSpecId = value.Id,
                FrameColorHex = prev.FrameColorHex,
                TextColorHex = prev.TextColorHex,
                CaptionTemplate = prev.CaptionTemplate,
                FontFamily = prev.FontFamily,
                FontSizePt = prev.FontSizePt,
                TextAlignment = prev.TextAlignment,
                ShowInnerShadow = prev.ShowInnerShadow,
                ShowPaperGrain = prev.ShowPaperGrain,
                MarginTopMm = value.MarginTopMm,
                MarginLeftMm = value.MarginLeftMm,
                MarginRightMm = value.MarginRightMm,
                MarginBottomMm = value.MarginBottomMm
            };
        }
        ValidateLayout();
        NotifyCanvasChanged();
    }

    // === 交互拖拽（单步 Undo）===
    public void BeginInteractiveTransform()
    {
        _pendingInteractiveSnapshot = CaptureSnapshot();
        _interactiveMutated = false;
    }

    public void MarkInteractiveMutated()
    {
        _interactiveMutated = true;
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    public void EndInteractiveTransform()
    {
        if (_pendingInteractiveSnapshot != null && _interactiveMutated)
        {
            _undoStack.PushUndo(_pendingInteractiveSnapshot);
            RefreshUndoState();
        }
        _pendingInteractiveSnapshot = null;
        _interactiveMutated = false;
    }

    // === 图片框增删改操作 ===
    [RelayCommand]
    public void AddFrame()
    {
        PushUndo();
        var existing = Frames.Select(f => f.ToDefinition()).ToList();
        var rect = FreeformLayoutMath.ComputeNextSlotRect(
            PaperWidthMm, PaperHeightMm, existing, PrimarySelectedFrame?.ToDefinition());
        var def = new CustomSlotDefinition
        {
            XMm = rect.X,
            YMm = rect.Y,
            WidthMm = rect.Width,
            HeightMm = rect.Height,
            Label = $"图片框 {Frames.Count + 1}"
        };
        var item = new DesignerFrameItem(def, Frames.Count);
        Frames.Add(item);
        SelectSingleFrame(item);
        ValidateLayout();
        NotifyCanvasChanged();
    }

    public void AddFrameAt(double centerXMm, double centerYMm)
    {
        PushUndo();
        double w = PrimarySelectedFrame?.WidthMm ?? Math.Min(63.0, Math.Max(FreeformLayoutMath.MinSlotSizeMm, PaperWidthMm * 0.4));
        double h = PrimarySelectedFrame?.HeightMm ?? Math.Min(90.0, Math.Max(FreeformLayoutMath.MinSlotSizeMm, PaperHeightMm * 0.6));
        w = Math.Round(Math.Clamp(w, FreeformLayoutMath.MinSlotSizeMm, PaperWidthMm), 2);
        h = Math.Round(Math.Clamp(h, FreeformLayoutMath.MinSlotSizeMm, PaperHeightMm), 2);
        double x = Math.Round(Math.Clamp(centerXMm - w * 0.5, 0, Math.Max(0, PaperWidthMm - w)), 2);
        double y = Math.Round(Math.Clamp(centerYMm - h * 0.5, 0, Math.Max(0, PaperHeightMm - h)), 2);
        var def = new CustomSlotDefinition
        {
            XMm = x,
            YMm = y,
            WidthMm = w,
            HeightMm = h,
            Label = $"图片框 {Frames.Count + 1}"
        };
        var item = new DesignerFrameItem(def, Frames.Count);
        Frames.Add(item);
        SelectSingleFrame(item);
        ValidateLayout();
        NotifyCanvasChanged();
    }

    [RelayCommand]
    public void DuplicateSelectedFrames()
    {
        var selected = GetSelectedFrames();
        if (selected.Count == 0) return;
        PushUndo();

        var existingDefs = Frames.Select(f => f.ToDefinition()).ToList();
        var added = new List<DesignerFrameItem>(selected.Count);
        foreach (var src in selected)
        {
            var rect = FreeformLayoutMath.ComputeNextSlotRect(
                PaperWidthMm, PaperHeightMm, existingDefs, src.ToDefinition());
            var def = src.ToDefinition().Clone(preserveId: false);
            def.XMm = rect.X;
            def.YMm = rect.Y;
            def.WidthMm = rect.Width;
            def.HeightMm = rect.Height;
            def.Label = $"图片框 {Frames.Count + 1}";
            var item = new DesignerFrameItem(def, Frames.Count);
            Frames.Add(item);
            existingDefs.Add(def);
            added.Add(item);
        }

        SetSelectedFrames(added, added[^1]);
        ValidateLayout();
        NotifyCanvasChanged();
    }

    [RelayCommand]
    public void DeleteSelectedFrames()
    {
        var selected = GetSelectedFrames();
        if (selected.Count == 0) return;
        PushUndo();

        int firstIdx = Frames.IndexOf(selected[0]);
        foreach (var item in selected)
            Frames.Remove(item);
        ReindexFrames();

        var next = Frames.Count > 0 ? Frames[Math.Clamp(firstIdx, 0, Frames.Count - 1)] : null;
        SelectSingleFrame(next);
        ValidateLayout();
        NotifyCanvasChanged();
    }

    [RelayCommand]
    public void ClearAllFrames()
    {
        if (Frames.Count == 0) return;
        PushUndo();
        Frames.Clear();
        SelectSingleFrame(null);
        ValidateLayout();
        NotifyCanvasChanged();
    }

    [RelayCommand]
    public void RotateSelected90()
    {
        var selected = GetSelectedFrames();
        if (selected.Count == 0) return;
        PushUndo();
        foreach (var f in selected)
            f.SlotRotationDegrees = DesignerFrameItem.NormalizeRotation(f.SlotRotationDegrees + 90.0);
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    [RelayCommand]
    public void SwapSelectedWidthHeight()
    {
        var selected = GetSelectedFrames();
        if (selected.Count == 0) return;
        PushUndo();
        foreach (var f in selected)
        {
            double cx = f.XMm + f.WidthMm * 0.5;
            double cy = f.YMm + f.HeightMm * 0.5;
            double newW = Math.Round(Math.Clamp(f.HeightMm, FreeformLayoutMath.MinSlotSizeMm, PaperWidthMm), 2);
            double newH = Math.Round(Math.Clamp(f.WidthMm, FreeformLayoutMath.MinSlotSizeMm, PaperHeightMm), 2);
            f.WidthMm = newW;
            f.HeightMm = newH;
            f.XMm = Math.Round(Math.Clamp(cx - newW * 0.5, 0, Math.Max(0, PaperWidthMm - newW)), 2);
            f.YMm = Math.Round(Math.Clamp(cy - newH * 0.5, 0, Math.Max(0, PaperHeightMm - newH)), 2);
        }
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    [RelayCommand]
    public void FitSelectedToPaper()
    {
        var selected = GetSelectedFrames();
        if (selected.Count == 0) return;
        PushUndo();
        foreach (var f in selected)
        {
            f.XMm = 0;
            f.YMm = 0;
            f.WidthMm = Math.Round(PaperWidthMm, 2);
            f.HeightMm = Math.Round(PaperHeightMm, 2);
        }
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    [RelayCommand]
    public void ClampAllFramesIntoPaper()
    {
        if (Frames.Count == 0) return;
        PushUndo();
        double minSize = Math.Min(FreeformLayoutMath.MinSlotSizeMm, Math.Min(PaperWidthMm, PaperHeightMm));
        foreach (var f in Frames)
        {
            f.WidthMm = Math.Round(Math.Clamp(f.WidthMm, minSize, PaperWidthMm), 2);
            f.HeightMm = Math.Round(Math.Clamp(f.HeightMm, minSize, PaperHeightMm), 2);
            f.XMm = Math.Round(Math.Clamp(f.XMm, 0, Math.Max(0, PaperWidthMm - f.WidthMm)), 2);
            f.YMm = Math.Round(Math.Clamp(f.YMm, 0, Math.Max(0, PaperHeightMm - f.HeightMm)), 2);
        }
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    public void ApplyPresetSizeToSelected(FrameSizePreset preset)
    {
        var selected = GetSelectedFrames();
        if (selected.Count == 0) return;
        PushUndo();
        foreach (var f in selected)
        {
            double cx = f.XMm + f.WidthMm * 0.5;
            double cy = f.YMm + f.HeightMm * 0.5;
            double w = Math.Round(Math.Clamp(preset.WidthMm, FreeformLayoutMath.MinSlotSizeMm, PaperWidthMm), 2);
            double h = Math.Round(Math.Clamp(preset.HeightMm, FreeformLayoutMath.MinSlotSizeMm, PaperHeightMm), 2);
            f.WidthMm = w;
            f.HeightMm = h;
            f.XMm = Math.Round(Math.Clamp(cx - w * 0.5, 0, Math.Max(0, PaperWidthMm - w)), 2);
            f.YMm = Math.Round(Math.Clamp(cy - h * 0.5, 0, Math.Max(0, PaperHeightMm - h)), 2);
            f.IdPhotoSpecId = preset.IdPhotoSpecId;
            if (!string.IsNullOrEmpty(preset.PolaroidSpecId))
            {
                var pSpec = PolaroidSpecs.FirstOrDefault(p => p.Id == preset.PolaroidSpecId);
                if (pSpec != null)
                {
                    f.PolaroidStyle = PolaroidSlotStyle.FromSpec(pSpec);
                    f.CornerRadiusMm = pSpec.CornerRadiusMm;
                }
            }
            else
            {
                f.PolaroidStyle = null;
            }
        }
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    public void NudgeSelected(double dxMm, double dyMm)
    {
        var selected = GetSelectedFrames();
        if (selected.Count == 0) return;

        var bounds = FreeformLayoutMath.GetBoundingBox(selected.Select(s => s.ToRect()));
        double clampedDx = Math.Clamp(bounds.X + dxMm, 0, Math.Max(0, PaperWidthMm - bounds.Width)) - bounds.X;
        double clampedDy = Math.Clamp(bounds.Y + dyMm, 0, Math.Max(0, PaperHeightMm - bounds.Height)) - bounds.Y;
        if (Math.Abs(clampedDx) < 0.001 && Math.Abs(clampedDy) < 0.001) return;

        PushUndo();
        foreach (var f in selected)
        {
            f.XMm = Math.Round(f.XMm + clampedDx, 2);
            f.YMm = Math.Round(f.YMm + clampedDy, 2);
        }
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    // === 图层 / 顺序调整 ===
    [RelayCommand]
    public void MoveSelectedUp()
    {
        if (PrimarySelectedFrame is not { } f) return;
        int idx = Frames.IndexOf(f);
        if (idx <= 0) return;
        PushUndo();
        Frames.Move(idx, idx - 1);
        ReindexFrames();
        NotifyCanvasChanged();
    }

    [RelayCommand]
    public void MoveSelectedDown()
    {
        if (PrimarySelectedFrame is not { } f) return;
        int idx = Frames.IndexOf(f);
        if (idx < 0 || idx >= Frames.Count - 1) return;
        PushUndo();
        Frames.Move(idx, idx + 1);
        ReindexFrames();
        NotifyCanvasChanged();
    }

    public void BringSelectedToFront()
    {
        if (PrimarySelectedFrame is not { } f) return;
        int idx = Frames.IndexOf(f);
        if (idx < 0 || idx == Frames.Count - 1) return;
        PushUndo();
        Frames.Move(idx, Frames.Count - 1);
        ReindexFrames();
        NotifyCanvasChanged();
    }

    public void SendSelectedToBack()
    {
        if (PrimarySelectedFrame is not { } f) return;
        int idx = Frames.IndexOf(f);
        if (idx <= 0) return;
        PushUndo();
        Frames.Move(idx, 0);
        ReindexFrames();
        NotifyCanvasChanged();
    }

    // === 对齐、分布与等尺寸 ===
    public void AlignSelected(FrameAlignment alignment)
    {
        var selected = GetSelectedFrames();
        if (selected.Count == 0) return;
        PushUndo();
        var defs = selected.Select(f => f.ToDefinition()).ToList();
        FreeformLayoutMath.AlignSlots(defs, alignment, PaperWidthMm, PaperHeightMm);
        for (int i = 0; i < selected.Count; i++)
        {
            selected[i].XMm = defs[i].XMm;
            selected[i].YMm = defs[i].YMm;
        }
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    [RelayCommand] public void AlignLeft() => AlignSelected(FrameAlignment.Left);
    [RelayCommand] public void AlignCenterH() => AlignSelected(FrameAlignment.CenterHorizontal);
    [RelayCommand] public void AlignRight() => AlignSelected(FrameAlignment.Right);
    [RelayCommand] public void AlignTop() => AlignSelected(FrameAlignment.Top);
    [RelayCommand] public void AlignCenterV() => AlignSelected(FrameAlignment.CenterVertical);
    [RelayCommand] public void AlignBottom() => AlignSelected(FrameAlignment.Bottom);
    [RelayCommand] public void CenterOnPage() => AlignSelected(FrameAlignment.CenterOnPage);

    [RelayCommand]
    public void DistributeHorizontal()
    {
        var selected = GetSelectedFrames();
        if (selected.Count < 3) return;
        PushUndo();
        var defs = selected.Select(f => f.ToDefinition()).ToList();
        FreeformLayoutMath.DistributeSlots(defs, horizontal: true);
        for (int i = 0; i < selected.Count; i++)
            selected[i].XMm = defs[i].XMm;
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    [RelayCommand]
    public void DistributeVertical()
    {
        var selected = GetSelectedFrames();
        if (selected.Count < 3) return;
        PushUndo();
        var defs = selected.Select(f => f.ToDefinition()).ToList();
        FreeformLayoutMath.DistributeSlots(defs, horizontal: false);
        for (int i = 0; i < selected.Count; i++)
            selected[i].YMm = defs[i].YMm;
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    [RelayCommand]
    public void MatchSelectedWidth() => MatchSelectedSize(matchWidth: true, matchHeight: false);

    [RelayCommand]
    public void MatchSelectedHeight() => MatchSelectedSize(matchWidth: false, matchHeight: true);

    [RelayCommand]
    public void MatchSelectedBoth() => MatchSelectedSize(matchWidth: true, matchHeight: true);

    private void MatchSelectedSize(bool matchWidth, bool matchHeight)
    {
        var selected = GetSelectedFrames().ToList();
        if (selected.Count < 2 || PrimarySelectedFrame is not { } reference) return;
        int refIdx = selected.IndexOf(reference);
        if (refIdx < 0) return;
        PushUndo();
        var defs = selected.Select(f => f.ToDefinition()).ToList();
        var refDef = defs[refIdx];
        FreeformLayoutMath.MatchSlotSize(defs, refDef, matchWidth, matchHeight, PaperWidthMm, PaperHeightMm);
        for (int i = 0; i < selected.Count; i++)
        {
            selected[i].XMm = defs[i].XMm;
            selected[i].YMm = defs[i].YMm;
            selected[i].WidthMm = defs[i].WidthMm;
            selected[i].HeightMm = defs[i].HeightMm;
        }
        SyncInspectorFromPrimary();
        ValidateLayout();
        NotifyCanvasChanged();
    }

    // === 快速生成器与导入 ===
    [RelayCommand]
    public void ApplyQuickGrid()
    {
        var generated = FreeformLayoutMath.GenerateGridSlots(
            PaperWidthMm, PaperHeightMm, GridRows, GridCols, GridMarginMm, GridGapMm, GridCornerRadiusMm);
        if (generated.Count == 0)
        {
            ValidationMessage = "请填写非负的边距和间距，并确保当前纸张能放下所设行列。";
            return;
        }
        PushUndo();
        LoadSlots(generated);
        SelectSingleFrame(Frames.FirstOrDefault());
        ValidateLayout();
        NotifyCanvasChanged();
    }

    [RelayCommand]
    public void ApplyQuickFixedSizePack()
    {
        if (SelectedFixedTilePreset is not { } preset) return;
        var pSpec = !string.IsNullOrEmpty(preset.PolaroidSpecId)
            ? PolaroidSpecs.FirstOrDefault(p => p.Id == preset.PolaroidSpecId)
            : null;
        var generated = FreeformLayoutMath.GenerateFixedSizeGridSlots(
            PaperWidthMm, PaperHeightMm, preset.WidthMm, preset.HeightMm,
            GridMarginMm, GridGapMm, allowRotate: true, centerOnPage: true,
            idPhotoSpecId: preset.IdPhotoSpecId, polaroidSpec: pSpec);
        if (generated.Count == 0)
        {
            ValidationMessage = $"请填写非负的边距和间距，并确保当前纸张能放下「{preset.Name}」。";
            return;
        }
        PushUndo();
        LoadSlots(generated);
        SelectSingleFrame(Frames.FirstOrDefault());
        ValidateLayout();
        NotifyCanvasChanged();
    }

    public void ImportFromTemplate(LayoutTemplateDefinition template)
    {
        var built = LayoutEngine.BuildSheet(template, SelectedPaper, isLandscapeOverride: IsLandscape,
            idSpecOverride: _mainVm.IdPhotoSpecs.FirstOrDefault(s => s.Id == template.IdPhotoSpecId),
            polaroidSpecOverride: PolaroidSpecs.FirstOrDefault(s => s.Id == template.PolaroidSpecId));
        if (built.Slots.Count == 0) return;
        PushUndo();
        LoadSlots(built.Slots.Select(CustomSlotDefinition.FromLayoutSlot));
        SelectSingleFrame(Frames.FirstOrDefault());
        ValidateLayout();
        NotifyCanvasChanged();
    }

    // === 校验 ===
    public void ValidateLayout()
    {
        var defs = Frames.Select(f => f.ToDefinition()).ToList();
        var result = FreeformLayoutMath.ValidateSlots(defs, PaperWidthMm, PaperHeightMm);
        var oobSet = result.OutOfBoundsSlotIds.Concat(result.InvalidSizeSlotIds).ToHashSet();
        var overlapSet = new HashSet<string>();
        foreach (var (a, b) in result.OverlappingPairs)
        {
            overlapSet.Add(a);
            overlapSet.Add(b);
        }

        foreach (var f in Frames)
        {
            f.IsOutOfBounds = oobSet.Contains(f.Id);
            f.IsOverlapping = overlapSet.Contains(f.Id);
        }

        HasOutOfBounds = oobSet.Count > 0;
        HasOverlaps = result.OverlappingPairs.Count > 0;
        CanSave = result.IsValid;

        if (Frames.Count == 0)
            ValidationMessage = "画布为空，请点击「添加图片框」或使用「快速网格」添加至少 1 个图片框。";
        else if (HasOutOfBounds)
            ValidationMessage = $"有 {oobSet.Count} 个图片框超出纸张、尺寸过小或没有可用的照片区域，请调整后再应用。";
        else if (HasOverlaps)
            ValidationMessage = $"共 {Frames.Count} 个图片框（注意：有 {result.OverlappingPairs.Count} 处图片框重叠，允许保存但打印时会按顺序叠放）。";
        else
            ValidationMessage = $"{Frames.Count} 个图片框，纸张 {PaperWidthMm:0.#} × {PaperHeightMm:0.#} mm";
    }

    // === 撤销 / 重做 ===
    private EditorSnapshot CaptureSnapshot() => new(
        Frames.Select(f => f.ToDefinition()).ToList(),
        SelectedPaper,
        IsLandscape,
        GetSelectedFrames().Select(f => f.Id).ToList(),
        PrimarySelectedFrame?.Id);

    private void RestoreSnapshot(EditorSnapshot snap)
    {
        _suppressPaperChangeUndo = true;
        try
        {
            SelectedPaper = snap.Paper;
            IsLandscape = snap.IsLandscape;
            UpdatePaperDimensions();
            LoadSlots(snap.Slots);

            var selSet = snap.SelectedIds.ToHashSet();
            var selected = Frames.Where(f => selSet.Contains(f.Id)).ToList();
            var primary = Frames.FirstOrDefault(f => f.Id == snap.PrimarySelectedId) ?? selected.LastOrDefault();
            SetSelectedFrames(selected, primary);
        }
        finally
        {
            _suppressPaperChangeUndo = false;
        }
        ValidateLayout();
        NotifyCanvasChanged();
    }

    private void PushUndo()
    {
        _undoStack.PushUndo(CaptureSnapshot());
        RefreshUndoState();
    }

    private void RefreshUndoState()
    {
        CanUndo = _undoStack.CanUndo;
        CanRedo = _undoStack.CanRedo;
    }

    [RelayCommand]
    public void Undo()
    {
        if (!_undoStack.CanUndo) return;
        var prev = _undoStack.Undo(CaptureSnapshot());
        if (prev != null) RestoreSnapshot(prev);
        RefreshUndoState();
    }

    [RelayCommand]
    public void Redo()
    {
        if (!_undoStack.CanRedo) return;
        var next = _undoStack.Redo(CaptureSnapshot());
        if (next != null) RestoreSnapshot(next);
        RefreshUndoState();
    }

    // === 构建模板结果 ===
    public LayoutTemplateDefinition BuildTemplate(bool forceNewId = false)
    {
        var slots = Frames.Select(f => f.ToDefinition()).ToList();
        string id = !forceNewId && !string.IsNullOrWhiteSpace(EditingTemplateId)
            ? EditingTemplateId!
            : $"tpl_user_{Guid.NewGuid():N}";

        string name = string.IsNullOrWhiteSpace(TemplateName) ? "自定义自由版式" : TemplateName.Trim();
        string subtitle = string.IsNullOrWhiteSpace(TemplateSubtitle)
            ? BuildDefaultSubtitle(slots)
            : TemplateSubtitle.Trim();

        return new LayoutTemplateDefinition
        {
            Id = id,
            Name = name,
            Subtitle = subtitle,
            Scenario = Scenario,
            DefaultPaperId = SelectedPaper.Id,
            DefaultLandscape = IsLandscape,
            DefaultMarginMm = slots.Count > 0 ? Math.Round(slots.Min(s => Math.Min(s.XMm, s.YMm)), 1) : 0,
            DefaultGapMm = 0,
            DefaultBleedMm = slots.FirstOrDefault()?.BleedMm ?? 0,
            CustomCornerRadiusMm = slots.FirstOrDefault()?.CornerRadiusMm ?? 0,
            DefaultCropMarkStyle = CropMarkStyle,
            IdPhotoSpecId = slots.FirstOrDefault(s => !string.IsNullOrEmpty(s.IdPhotoSpecId))?.IdPhotoSpecId,
            PolaroidSpecId = slots.FirstOrDefault(s => s.PolaroidStyle != null)?.PolaroidStyle?.PolaroidSpecId,
            TemplateKind = LayoutTemplateDefinition.KindCustomFreeform,
            CustomPaperWidthMm = Math.Round(SelectedPaper.WidthMm, 2),
            CustomPaperHeightMm = Math.Round(SelectedPaper.HeightMm, 2),
            CustomCanvasBackgroundHex = CanvasBackgroundHex,
            CustomSlots = slots,
            IsBuiltIn = false
        };
    }

    private string BuildDefaultSubtitle(List<CustomSlotDefinition> slots)
    {
        if (slots.Count == 0) return "空白自由版式";
        if (slots.Count == 1)
            return $"{PaperWidthMm:0.#}×{PaperHeightMm:0.#} mm 纸张，单框 {slots[0].WidthMm:0.#}×{slots[0].HeightMm:0.#} mm";

        bool sameSize = slots.All(s =>
            Math.Abs(s.WidthMm - slots[0].WidthMm) < 0.2 &&
            Math.Abs(s.HeightMm - slots[0].HeightMm) < 0.2);
        return sameSize
            ? $"{slots.Count} 个图片框 ({slots[0].WidthMm:0.#}×{slots[0].HeightMm:0.#} mm)"
            : $"{slots.Count} 个自由尺寸图片框 ({PaperWidthMm:0.#}×{PaperHeightMm:0.#} mm)";
    }

    private void NotifyCanvasChanged() => CanvasVisualChanged?.Invoke();
}
