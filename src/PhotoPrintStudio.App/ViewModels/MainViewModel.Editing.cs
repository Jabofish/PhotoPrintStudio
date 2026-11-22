using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoPrintStudio.App.Helpers;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Portable;
using PhotoPrintStudio.Core.Preflight;
using PhotoPrintStudio.Core.Rendering;
using SkiaSharp;

namespace PhotoPrintStudio.App.ViewModels;

public partial class MainViewModel
{
    private PhotoEditState? CurrentEdit => SelectedAsset?.Asset.EditState;

    [ObservableProperty] private int _activeCurveChannel = 0; // 0=Master, 1=R, 2=G, 3=B
    [ObservableProperty] private int _activeHslChannel = 1;   // 默认选中橙色（肤色最常用）
    [ObservableProperty] private string _estimatedIdHeadInfo = "";
    [ObservableProperty] private string _detectedFaceSummaryText = "";
    [ObservableProperty] private bool _isAiFramingBusy;
    private CancellationTokenSource? _autoFrameCancellation;
    private CancellationTokenSource? _batchFramingCancellation;
    private readonly Dictionary<string, (CropGeometry Before, CropGeometry After)> _appliedAutoFrames = new();
    private bool CanRestoreAutoFrame => SelectedAsset is { } asset &&
        _appliedAutoFrames.TryGetValue(asset.Id, out var applied) && ReadCrop(asset.Asset.EditState) == applied.After;
    public bool CanAutoFramePhoto => _autoFrameCancellation != null || _batchFramingCancellation != null || CanRestoreAutoFrame ||
        (HasSelectedAsset && (ActiveScenario != LayoutScenario.IdPhoto || HasFaceDetectionModel));
    public string AutoFrameActionText => _autoFrameCancellation != null || _batchFramingCancellation != null
        ? "取消构图" : CanRestoreAutoFrame ? "还原构图" : "AI 智能构图";

    public ToneCurveData ActiveToneCurves => CurrentEdit?.ToneCurves ?? new ToneCurveData();

    // === 光影曝光 ===
    public double EditExposure { get => CurrentEdit?.Exposure ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Exposure - value) > 1e-9) { CurrentEdit.Exposure = value; OnEditMutated(); } } }
    public double EditBrightness { get => CurrentEdit?.Brightness ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Brightness - value) > 1e-9) { CurrentEdit.Brightness = value; OnEditMutated(); } } }
    public double EditContrast { get => CurrentEdit?.Contrast ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Contrast - value) > 1e-9) { CurrentEdit.Contrast = value; OnEditMutated(); } } }
    public double EditHighlights { get => CurrentEdit?.Highlights ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Highlights - value) > 1e-9) { CurrentEdit.Highlights = value; OnEditMutated(); } } }
    public double EditShadows { get => CurrentEdit?.Shadows ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Shadows - value) > 1e-9) { CurrentEdit.Shadows = value; OnEditMutated(); } } }
    public double EditWhites { get => CurrentEdit?.Whites ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Whites - value) > 1e-9) { CurrentEdit.Whites = value; OnEditMutated(); } } }
    public double EditBlacks { get => CurrentEdit?.Blacks ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Blacks - value) > 1e-9) { CurrentEdit.Blacks = value; OnEditMutated(); } } }
    public double EditGamma { get => CurrentEdit?.Gamma ?? 1.0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Gamma - value) > 1e-9) { CurrentEdit.Gamma = value; OnEditMutated(); } } }

    // === 色阶 ===
    public double EditInputBlack { get => CurrentEdit?.InputBlack ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.InputBlack - value) > 1e-9) { CurrentEdit.InputBlack = value; OnEditMutated(); } } }
    public double EditInputWhite { get => CurrentEdit?.InputWhite ?? 255; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.InputWhite - value) > 1e-9) { CurrentEdit.InputWhite = value; OnEditMutated(); } } }
    public double EditOutputBlack { get => CurrentEdit?.OutputBlack ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.OutputBlack - value) > 1e-9) { CurrentEdit.OutputBlack = value; OnEditMutated(); } } }
    public double EditOutputWhite { get => CurrentEdit?.OutputWhite ?? 255; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.OutputWhite - value) > 1e-9) { CurrentEdit.OutputWhite = value; OnEditMutated(); } } }

    // === 白平衡与色彩 ===
    public double EditTemperature { get => CurrentEdit?.Temperature ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Temperature - value) > 1e-9) { CurrentEdit.Temperature = value; OnEditMutated(); } } }
    public double EditTint { get => CurrentEdit?.Tint ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Tint - value) > 1e-9) { CurrentEdit.Tint = value; OnEditMutated(); } } }
    public double EditVibrance { get => CurrentEdit?.Vibrance ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Vibrance - value) > 1e-9) { CurrentEdit.Vibrance = value; OnEditMutated(); } } }
    public double EditSaturation { get => CurrentEdit?.Saturation ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Saturation - value) > 1e-9) { CurrentEdit.Saturation = value; OnEditMutated(); } } }

    // === HSL 八色相 ===
    public double HslHue
    {
        get => CurrentEdit?.Hsl.GetByIndex(ActiveHslChannel).Hue ?? 0;
        set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Hsl.GetByIndex(ActiveHslChannel).Hue - value) > 1e-9) { CurrentEdit.Hsl.GetByIndex(ActiveHslChannel).Hue = value; OnEditMutated(); } }
    }
    public double HslSaturation
    {
        get => CurrentEdit?.Hsl.GetByIndex(ActiveHslChannel).Saturation ?? 0;
        set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Hsl.GetByIndex(ActiveHslChannel).Saturation - value) > 1e-9) { CurrentEdit.Hsl.GetByIndex(ActiveHslChannel).Saturation = value; OnEditMutated(); } }
    }
    public double HslLightness
    {
        get => CurrentEdit?.Hsl.GetByIndex(ActiveHslChannel).Lightness ?? 0;
        set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Hsl.GetByIndex(ActiveHslChannel).Lightness - value) > 1e-9) { CurrentEdit.Hsl.GetByIndex(ActiveHslChannel).Lightness = value; OnEditMutated(); } }
    }

    partial void OnActiveHslChannelChanged(int value)
    {
        OnPropertyChanged(nameof(HslHue));
        OnPropertyChanged(nameof(HslSaturation));
        OnPropertyChanged(nameof(HslLightness));
    }

    // === 细节、柔肤与滤镜 ===
    public double EditClarity { get => CurrentEdit?.Clarity ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Clarity - value) > 1e-9) { CurrentEdit.Clarity = value; OnEditMutated(); } } }
    public double EditSharpening { get => CurrentEdit?.Sharpening ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Sharpening - value) > 1e-9) { CurrentEdit.Sharpening = value; OnEditMutated(); } } }
    public double EditSkinSoftening { get => CurrentEdit?.SkinSoftening ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.SkinSoftening - value) > 1e-9) { CurrentEdit.SkinSoftening = value; OnEditMutated(); } } }
    public double EditFilterIntensity { get => CurrentEdit?.FilterIntensity ?? 100; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.FilterIntensity - value) > 1e-9) { CurrentEdit.FilterIntensity = value; OnEditMutated(); } } }

    public string EditFilterId
    {
        get => CurrentEdit?.FilterPresetId ?? "none";
        set
        {
            if (CurrentEdit == null || string.IsNullOrWhiteSpace(value)) return;
            if (string.Equals(CurrentEdit.FilterPresetId, value, StringComparison.OrdinalIgnoreCase)) return;
            if (_pendingUndoSnapshot == null) PushUndoSnapshot();
            CurrentEdit.FilterPresetId = value;
            if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) && Math.Abs(CurrentEdit.FilterIntensity - 100.0) > 1e-3)
            {
                CurrentEdit.FilterIntensity = 100.0;
                OnPropertyChanged(nameof(EditFilterIntensity));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFilterActive));
            OnEditMutated();
        }
    }

    public bool IsFilterActive => !string.Equals(EditFilterId, "none", StringComparison.OrdinalIgnoreCase);

    // === 证件照背景 ===
    public double EditWhiteWallCleanup { get => CurrentEdit?.WhiteWallCleanup ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.WhiteWallCleanup - value) > 1e-9) { CurrentEdit.WhiteWallCleanup = value; OnEditMutated(); } } }
    public bool EditAiBackgroundReplace
    {
        get => CurrentEdit?.EnableAiBackgroundReplace ?? false;
        set
        {
            if (CurrentEdit == null || CurrentEdit.EnableAiBackgroundReplace == value) return;
            if (_pendingUndoSnapshot == null) PushUndoSnapshot();
            CurrentEdit.EnableAiBackgroundReplace = value;
            OnPropertyChanged();
            OnEditMutated();
        }
    }
    public string EditReplacementBgHex { get => CurrentEdit?.ReplacementBackgroundHex ?? "#FFFFFF"; set { if (CurrentEdit != null && !string.Equals(CurrentEdit.ReplacementBackgroundHex, value, StringComparison.Ordinal)) { if (_pendingUndoSnapshot == null) PushUndoSnapshot(); CurrentEdit.ReplacementBackgroundHex = value; OnPropertyChanged(); OnEditMutated(); } } }
    public bool EditUseGradientBg
    {
        get => CurrentEdit?.UseGradientBackground ?? false;
        set
        {
            if (CurrentEdit == null || CurrentEdit.UseGradientBackground == value) return;
            if (_pendingUndoSnapshot == null) PushUndoSnapshot();
            CurrentEdit.UseGradientBackground = value;
            OnPropertyChanged();
            OnEditMutated();
        }
    }
    public string EditReplacementBgSecondaryHex { get => CurrentEdit?.ReplacementBackgroundSecondaryHex ?? "#93C5FD"; set { if (CurrentEdit != null && !string.Equals(CurrentEdit.ReplacementBackgroundSecondaryHex, value, StringComparison.Ordinal)) { if (_pendingUndoSnapshot == null) PushUndoSnapshot(); CurrentEdit.ReplacementBackgroundSecondaryHex = value; OnPropertyChanged(); OnEditMutated(); } } }
    public double EditMattingFeather { get => CurrentEdit?.MattingFeather ?? 0.5; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.MattingFeather - value) > 1e-9) { CurrentEdit.MattingFeather = value; OnEditMutated(); } } }
    public double EditSpillSuppression { get => CurrentEdit?.SpillSuppression ?? 60.0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.SpillSuppression - value) > 1e-9) { CurrentEdit.SpillSuppression = value; OnEditMutated(); } } }
    public double EditMattingEdgeShift { get => CurrentEdit?.MattingEdgeShift ?? 0.0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.MattingEdgeShift - value) > 1e-9) { CurrentEdit.MattingEdgeShift = value; OnEditMutated(); } } }

    // === 一键影棚自动优化 ===
    public IReadOnlyDictionary<StudioEnhanceMode, string> StudioEnhanceModeOptions { get; } = new Dictionary<StudioEnhanceMode, string>
    {
        [StudioEnhanceMode.Natural] = "自然质感（轻度修饰）",
        [StudioEnhanceMode.Studio] = "影棚标准（推荐）",
        [StudioEnhanceMode.Rescue] = "暗光强力抢救"
    };

    public bool EditStudioPortraitEnhance
    {
        get => CurrentEdit?.EnableStudioPortraitEnhance ?? false;
        set
        {
            if (CurrentEdit == null || CurrentEdit.EnableStudioPortraitEnhance == value) return;
            PushUndoSnapshot();
            CurrentEdit.EnableStudioPortraitEnhance = value;
            OnEditMutated();
            OnPropertyChanged();
            OnPropertyChanged(nameof(StudioEnhanceActionText));
        }
    }

    public StudioEnhanceMode EditStudioEnhanceMode
    {
        get => CurrentEdit?.StudioEnhanceMode ?? StudioEnhanceMode.Studio;
        set
        {
            if (CurrentEdit == null || CurrentEdit.StudioEnhanceMode == value) return;
            PushUndoSnapshot();
            CurrentEdit.StudioEnhanceMode = value;
            OnEditMutated();
            OnPropertyChanged();
        }
    }

    public double EditStudioEnhanceStrength
    {
        get => CurrentEdit?.StudioEnhanceStrength ?? 70.0;
        set
        {
            if (CurrentEdit != null && CurrentEdit.StudioEnhanceStrength != value)
            {
                CurrentEdit.StudioEnhanceStrength = value;
                OnEditMutated();
            }
        }
    }

    public string StudioEnhanceActionText => EditStudioPortraitEnhance ? "影棚优化已开启" : "影棚自动优化";

    [RelayCommand]
    private void ToggleStudioPortraitEnhance()
    {
        if (CurrentEdit == null) return;
        bool next = !CurrentEdit.EnableStudioPortraitEnhance;
        PushUndoSnapshot();
        CurrentEdit.EnableStudioPortraitEnhance = next;
        OnEditMutated();
        OnPropertyChanged(nameof(EditStudioPortraitEnhance));
        OnPropertyChanged(nameof(StudioEnhanceActionText));
        ShowToast(next
            ? "已开启影棚自动优化"
            : "已关闭一键影棚自动优化");
    }

    // === 证件照矢量/PNG 电子正装叠加 ===
    public IReadOnlyList<FormalSuitPresetOption> FormalSuitOptions => AvailableFormalSuitOptions;
    public bool IsSuitOverlayActive => (CurrentEdit?.FormalSuit ?? FormalSuitStyle.None) != FormalSuitStyle.None;
    public FormalSuitPresetOption SelectedFormalSuitOption
    {
        get
        {
            if (FormalSuitOptions.Count == 0) return FormalSuitRenderer.BuiltInPresets[0];
            var style = CurrentEdit?.FormalSuit ?? FormalSuitStyle.None;
            if (style == FormalSuitStyle.CustomPng)
            {
                string customPath = CurrentEdit?.CustomSuitAssetPath ?? "";
                var hit = FormalSuitOptions.FirstOrDefault(p =>
                    p.Style == FormalSuitStyle.CustomPng &&
                    string.Equals(p.CustomPngPath, customPath, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return style == FormalSuitStyle.CustomPng ? FormalSuitOptions[0] :
                FormalSuitOptions.FirstOrDefault(p => p.Style == style) ?? FormalSuitOptions[0];
        }
        set
        {
            if (CurrentEdit == null || value == null) return;
            string nextCustomPath = value.CustomPngPath ?? "";
            if (CurrentEdit.FormalSuit == value.Style &&
                string.Equals(CurrentEdit.CustomSuitAssetPath, nextCustomPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            PushUndoSnapshot();
            bool shouldAutoSnap = CurrentEdit.FormalSuit == FormalSuitStyle.None
                && value.Style != FormalSuitStyle.None
                && Math.Abs(CurrentEdit.SuitScale - 1.0) < 0.01
                && Math.Abs(CurrentEdit.SuitNeckYRatio - 0.60) < 0.01;
            CurrentEdit.FormalSuit = value.Style;
            CurrentEdit.CustomSuitAssetPath = nextCustomPath;
            OnEditMutated();
            OnPropertyChanged(nameof(SelectedFormalSuitOption));
            OnPropertyChanged(nameof(IsSuitOverlayActive));
            if (shouldAutoSnap && FaceService.HasModelLoaded)
                _ = TryAutoAlignSuitInBackgroundAsync();
        }
    }
    public double EditSuitScale { get => CurrentEdit?.SuitScale ?? 1.0; set { if (CurrentEdit != null && CurrentEdit.SuitScale != value) { CurrentEdit.SuitScale = value; OnEditMutated(); } } }
    public double EditSuitNeckYRatio { get => CurrentEdit?.SuitNeckYRatio ?? 0.60; set { if (CurrentEdit != null && CurrentEdit.SuitNeckYRatio != value) { CurrentEdit.SuitNeckYRatio = value; OnEditMutated(); } } }
    public double EditSuitCenterXRatio { get => CurrentEdit?.SuitCenterXRatio ?? 0.50; set { if (CurrentEdit != null && CurrentEdit.SuitCenterXRatio != value) { CurrentEdit.SuitCenterXRatio = value; OnEditMutated(); } } }
    [RelayCommand]
    private void OpenSuitsDirectory()
    {
        Paths.EnsureDirectoriesExist();
        Dialogs.OpenPathInShell(Paths.SuitsDirectory);
    }

    [RelayCommand]
    private void RefreshFormalSuits()
    {
        RefreshFormalSuitOptions();
        int customCount = AvailableFormalSuitOptions.Count(o => o.Style == FormalSuitStyle.CustomPng);
        ShowToast(customCount > 0
            ? $"已刷新正装列表，发现 {customCount} 套自定义 PNG 正装"
            : "已刷新正装列表（可将透明 PNG 正装放入 portable_data\\suits 目录）");
    }

    // === 构图与裁剪 ===
    public double EditRotationDegrees { get => CurrentEdit?.RotationDegrees ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.RotationDegrees - value) > 1e-9) { CurrentEdit.RotationDegrees = value; OnEditMutated(); } } }
    public double EditZoom { get => CurrentEdit?.Zoom ?? 1.0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.Zoom - value) > 1e-9) { CurrentEdit.Zoom = value; OnEditMutated(); } } }
    public double EditPanX { get => CurrentEdit?.PanX ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.PanX - value) > 1e-9) { CurrentEdit.PanX = value; OnEditMutated(); } } }
    public double EditPanY { get => CurrentEdit?.PanY ?? 0; set { if (CurrentEdit != null && Math.Abs(CurrentEdit.PanY - value) > 1e-9) { CurrentEdit.PanY = value; OnEditMutated(); } } }
    public bool EditFlipHorizontal { get => CurrentEdit?.FlipHorizontal ?? false; set { if (CurrentEdit != null && CurrentEdit.FlipHorizontal != value) { if (_pendingUndoSnapshot == null) PushUndoSnapshot(); CurrentEdit.FlipHorizontal = value; OnPropertyChanged(); OnEditMutated(); } } }
    public bool EditFlipVertical { get => CurrentEdit?.FlipVertical ?? false; set { if (CurrentEdit != null && CurrentEdit.FlipVertical != value) { if (_pendingUndoSnapshot == null) PushUndoSnapshot(); CurrentEdit.FlipVertical = value; OnPropertyChanged(); OnEditMutated(); } } }
    private LayoutSlot? SelectedFitOverrideSlot => SelectedSlot is { EffectiveFitMode: not null } slot &&
        slot.PhotoAssetId == SelectedAsset?.Id ? slot : null;

    public bool IsCoverFitMode
    {
        get => (SelectedFitOverrideSlot?.EffectiveFitMode ?? CurrentEdit?.FitMode ?? FitMode.Cover) == FitMode.Cover;
        set
        {
            if (CurrentEdit is not { } edit || IsCoverFitMode == value) return;
            PushUndoSnapshot();
            ApplyFitMode(edit, value ? FitMode.Cover : FitMode.Contain);
            OnEditMutated();
            OnPropertyChanged(nameof(IsCoverFitMode));
        }
    }

    private void ApplyFitMode(PhotoEditState edit, FitMode fit)
    {
        if (SelectedFitOverrideSlot is { } slot) slot.FitModeOverride = fit;
        else edit.FitMode = fit;
        if (SelectedAsset is { } asset) _appliedAutoFrames.Remove(asset.Id);
    }

    public void OnEditMutated()
    {
        MarkContinuousEditMutated();
        SyncSelectedPresetToCurrentEdit();
        NotifyEditIndicatorPropertiesChanged();
        OnPropertyChanged(nameof(AutoFrameActionText));
        UpdateEstimatedHeadSize();
        UpdateActiveTargetContext();
        ScheduleRender(preserveContext: true);
    }

    private static bool IsCurveChannelModified(List<CurvePoint>? pts) =>
        pts != null && !(pts.Count == 2 &&
            Math.Abs(pts[0].X) < 1e-4 && Math.Abs(pts[0].Y) < 1e-4 &&
            Math.Abs(pts[1].X - 1.0) < 1e-4 && Math.Abs(pts[1].Y - 1.0) < 1e-4);

    public bool HasMasterCurveEdit => IsCurveChannelModified(CurrentEdit?.ToneCurves.Master);
    public bool HasRedCurveEdit => IsCurveChannelModified(CurrentEdit?.ToneCurves.Red);
    public bool HasGreenCurveEdit => IsCurveChannelModified(CurrentEdit?.ToneCurves.Green);
    public bool HasBlueCurveEdit => IsCurveChannelModified(CurrentEdit?.ToneCurves.Blue);

    public bool HasHslRedEdit => CurrentEdit is { } e && !e.Hsl.Red.IsZero;
    public bool HasHslOrangeEdit => CurrentEdit is { } e && !e.Hsl.Orange.IsZero;
    public bool HasHslYellowEdit => CurrentEdit is { } e && !e.Hsl.Yellow.IsZero;
    public bool HasHslGreenEdit => CurrentEdit is { } e && !e.Hsl.Green.IsZero;
    public bool HasHslCyanEdit => CurrentEdit is { } e && !e.Hsl.Cyan.IsZero;
    public bool HasHslBlueEdit => CurrentEdit is { } e && !e.Hsl.Blue.IsZero;
    public bool HasHslPurpleEdit => CurrentEdit is { } e && !e.Hsl.Purple.IsZero;
    public bool HasHslMagentaEdit => CurrentEdit is { } e && !e.Hsl.Magenta.IsZero;

    public bool HasLightingGroupEdits => CurrentEdit is { } e && (
        Math.Abs(e.Exposure) > 1e-3 || Math.Abs(e.Brightness) > 1e-3 || Math.Abs(e.Contrast) > 1e-3 ||
        Math.Abs(e.Highlights) > 1e-3 || Math.Abs(e.Shadows) > 1e-3 || Math.Abs(e.Whites) > 1e-3 ||
        Math.Abs(e.Blacks) > 1e-3 || Math.Abs(e.Gamma - 1.0) > 1e-3);

    public bool HasColorFilterGroupEdits => CurrentEdit is { } e && (
        Math.Abs(e.Temperature) > 1e-3 || Math.Abs(e.Tint) > 1e-3 ||
        Math.Abs(e.Vibrance) > 1e-3 || Math.Abs(e.Saturation) > 1e-3 ||
        IsFilterActive);

    public bool HasLightColorEdits => HasLightingGroupEdits || HasColorFilterGroupEdits;

    public bool HasCurvesLevelsEdits => CurrentEdit is { } e && (
        !e.ToneCurves.IsIdentity ||
        e.InputBlack > 0.5 || e.InputWhite < 254.5 ||
        e.OutputBlack > 0.5 || e.OutputWhite < 254.5);

    public bool HasHslEdits => CurrentEdit is { } e && !e.Hsl.IsZero;

    public bool HasCurveHslEdits => HasCurvesLevelsEdits || HasHslEdits;

    public bool HasCropGeometryEdits => CurrentEdit is { } e && (
        Math.Abs(e.RotationDegrees) > 1e-2 || e.QuarterTurns != 0 ||
        e.FlipHorizontal || e.FlipVertical ||
        Math.Abs(e.Zoom - 1.0) > 1e-3 || Math.Abs(e.PanX) > 1e-3 || Math.Abs(e.PanY) > 1e-3);

    public bool HasDetailBgEdits => CurrentEdit is { } e && (
        Math.Abs(e.Clarity) > 1e-3 || e.Sharpening > 0.5 || e.SkinSoftening > 0.5 ||
        e.WhiteWallCleanup > 0.5 || e.EnableAiBackgroundReplace || e.UseGradientBackground ||
        Math.Abs(e.MattingEdgeShift) > 1e-2 ||
        e.EnableStudioPortraitEnhance || e.StudioEnhanceMode != StudioEnhanceMode.Studio ||
        Math.Abs(e.StudioEnhanceStrength - 70.0) > 0.5);

    public bool HasCropPortraitEdits => HasCropGeometryEdits || HasDetailBgEdits ||
        (CurrentEdit is { FormalSuit: not FormalSuitStyle.None });

    public bool HasAnyColorEdits => HasLightColorEdits || HasCurveHslEdits || HasDetailBgEdits ||
        (CurrentEdit is { FormalSuit: not FormalSuitStyle.None });

    private void NotifyEditIndicatorPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasLightingGroupEdits));
        OnPropertyChanged(nameof(HasColorFilterGroupEdits));
        OnPropertyChanged(nameof(HasLightColorEdits));
        OnPropertyChanged(nameof(HasCurvesLevelsEdits));
        OnPropertyChanged(nameof(HasHslEdits));
        OnPropertyChanged(nameof(HasCurveHslEdits));
        OnPropertyChanged(nameof(HasCropGeometryEdits));
        OnPropertyChanged(nameof(HasDetailBgEdits));
        OnPropertyChanged(nameof(HasCropPortraitEdits));
        OnPropertyChanged(nameof(HasAnyColorEdits));
        OnPropertyChanged(nameof(HasMasterCurveEdit));
        OnPropertyChanged(nameof(HasRedCurveEdit));
        OnPropertyChanged(nameof(HasGreenCurveEdit));
        OnPropertyChanged(nameof(HasBlueCurveEdit));
        OnPropertyChanged(nameof(HasHslRedEdit));
        OnPropertyChanged(nameof(HasHslOrangeEdit));
        OnPropertyChanged(nameof(HasHslYellowEdit));
        OnPropertyChanged(nameof(HasHslGreenEdit));
        OnPropertyChanged(nameof(HasHslCyanEdit));
        OnPropertyChanged(nameof(HasHslBlueEdit));
        OnPropertyChanged(nameof(HasHslPurpleEdit));
        OnPropertyChanged(nameof(HasHslMagentaEdit));
    }

    public void NotifyAllEditPropertiesChanged()
    {
        OnPropertyChanged(nameof(ActiveToneCurves));
        OnPropertyChanged(nameof(EditExposure));
        OnPropertyChanged(nameof(EditBrightness));
        OnPropertyChanged(nameof(EditContrast));
        OnPropertyChanged(nameof(EditHighlights));
        OnPropertyChanged(nameof(EditShadows));
        OnPropertyChanged(nameof(EditWhites));
        OnPropertyChanged(nameof(EditBlacks));
        OnPropertyChanged(nameof(EditGamma));
        OnPropertyChanged(nameof(EditInputBlack));
        OnPropertyChanged(nameof(EditInputWhite));
        OnPropertyChanged(nameof(EditOutputBlack));
        OnPropertyChanged(nameof(EditOutputWhite));
        OnPropertyChanged(nameof(EditTemperature));
        OnPropertyChanged(nameof(EditTint));
        OnPropertyChanged(nameof(EditVibrance));
        OnPropertyChanged(nameof(EditSaturation));
        OnPropertyChanged(nameof(HslHue));
        OnPropertyChanged(nameof(HslSaturation));
        OnPropertyChanged(nameof(HslLightness));
        OnPropertyChanged(nameof(EditClarity));
        OnPropertyChanged(nameof(EditSharpening));
        OnPropertyChanged(nameof(EditSkinSoftening));
        OnPropertyChanged(nameof(EditFilterId));
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(EditFilterIntensity));
        OnPropertyChanged(nameof(EditWhiteWallCleanup));
        OnPropertyChanged(nameof(EditAiBackgroundReplace));
        OnPropertyChanged(nameof(EditReplacementBgHex));
        OnPropertyChanged(nameof(EditUseGradientBg));
        OnPropertyChanged(nameof(EditReplacementBgSecondaryHex));
        OnPropertyChanged(nameof(EditMattingFeather));
        OnPropertyChanged(nameof(EditSpillSuppression));
        OnPropertyChanged(nameof(EditMattingEdgeShift));
        OnPropertyChanged(nameof(EditStudioPortraitEnhance));
        OnPropertyChanged(nameof(EditStudioEnhanceMode));
        OnPropertyChanged(nameof(EditStudioEnhanceStrength));
        OnPropertyChanged(nameof(StudioEnhanceActionText));
        OnPropertyChanged(nameof(EditRotationDegrees));
        OnPropertyChanged(nameof(EditZoom));
        OnPropertyChanged(nameof(EditPanX));
        OnPropertyChanged(nameof(EditPanY));
        OnPropertyChanged(nameof(EditFlipHorizontal));
        OnPropertyChanged(nameof(EditFlipVertical));
        OnPropertyChanged(nameof(IsCoverFitMode));
        OnPropertyChanged(nameof(SelectedFormalSuitOption));
        OnPropertyChanged(nameof(IsSuitOverlayActive));
        OnPropertyChanged(nameof(EditSuitScale));
        OnPropertyChanged(nameof(EditSuitNeckYRatio));
        OnPropertyChanged(nameof(EditSuitCenterXRatio));
        SyncSelectedPresetToCurrentEdit();
        NotifyEditIndicatorPropertiesChanged();
        OnPropertyChanged(nameof(ShowCustomPresetPlaceholder));
        UpdateEstimatedHeadSize();
    }

    private void UpdateEstimatedHeadSize()
    {
        if (SelectedSlot?.IdPhotoSpecId is string specId)
        {
            var spec = IdPhotoSpec.BuiltInSpecs.FirstOrDefault(s => s.Id == specId);
            if (spec != null && spec.HeadHeightMaxMm > 0)
            {
                double estHead = spec.HeightMm * (spec.ChinLineRatioFromTop - 0.1) / Math.Max(0.4, EditZoom);
                EstimatedIdHeadInfo = $"规范参考头高 {spec.HeadHeightMinMm:0}~{spec.HeadHeightMaxMm:0} mm，顶部留白 {spec.TopMarginMm:0.#} mm";
                return;
            }
        }
        EstimatedIdHeadInfo = "";
    }

    private bool _suppressPresetApply;
    public bool IsCustomPresetSelected => SelectedPreset is { IsBuiltIn: false };
    public bool ShowCustomPresetPlaceholder => HasSelectedAsset && SelectedPreset == null;

    private void SyncSelectedPresetToCurrentEdit()
    {
        var edit = CurrentEdit;
        if (SelectedPreset is { } selected && Presets.Contains(selected) && selected.Parameters.ColorEquals(edit)) return;
        ColorPresetItem? matched = edit != null ? Presets.FirstOrDefault(p => p.Parameters.ColorEquals(edit)) : null;
        if (ReferenceEquals(SelectedPreset, matched)) return;
        _suppressPresetApply = true;
        try { SelectedPreset = matched; }
        finally { _suppressPresetApply = false; }
    }

    partial void OnSelectedPresetChanged(ColorPresetItem? value)
    {
        OnPropertyChanged(nameof(IsCustomPresetSelected));
        OnPropertyChanged(nameof(ShowCustomPresetPlaceholder));
        if (_suppressPresetApply || value == null || CurrentEdit == null) return;
        PushUndoSnapshot();
        SelectedAsset!.Asset.EditState = value.Parameters.CloneColorOnlyInto(CurrentEdit);
        InvalidateAssetPreviewCache(SelectedAsset.Id);
        NotifyAllEditPropertiesChanged();
        ScheduleRender();
        StatusText = $"已应用调色预设：{value.Name}";
    }

    [RelayCommand]
    private void RotateQuarterLeft()
    {
        if (CurrentEdit == null) return;
        PushUndoSnapshot();
        CurrentEdit.QuarterTurns = (CurrentEdit.QuarterTurns + 3) % 4;
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
    }

    [RelayCommand]
    private void RotateQuarterRight()
    {
        if (CurrentEdit == null) return;
        PushUndoSnapshot();
        CurrentEdit.QuarterTurns = (CurrentEdit.QuarterTurns + 1) % 4;
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
    }

    [RelayCommand]
    private void ToggleFlipHorizontal()
    {
        if (CurrentEdit == null) return;
        PushUndoSnapshot();
        CurrentEdit.FlipHorizontal = !CurrentEdit.FlipHorizontal;
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
    }

    [RelayCommand]
    private void ToggleFlipVertical()
    {
        if (CurrentEdit == null) return;
        PushUndoSnapshot();
        CurrentEdit.FlipVertical = !CurrentEdit.FlipVertical;
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
    }

    [RelayCommand]
    private void AutoOrientPhotoToSlot()
    {
        if (SelectedAsset == null || CurrentEdit == null) return;
        var geom = ResolveTargetSlotGeometry(SelectedSlot, SelectedAsset.Id);
        bool needsTurn = PortraitAutoFrameHelper.NeedsQuarterTurnForSlotAspect(
            SelectedAsset.Asset.PixelWidth,
            SelectedAsset.Asset.PixelHeight,
            CurrentEdit.QuarterTurns,
            geom.trimW,
            geom.trimH);

        if (!needsTurn && IsCoverFitMode)
        {
            ShowToast("照片方向已与当前格位长宽比匹配");
            return;
        }

        PushUndoSnapshot();
        if (needsTurn)
            CurrentEdit.QuarterTurns = (CurrentEdit.QuarterTurns + 1) % 4;
        ApplyFitMode(CurrentEdit, FitMode.Cover);
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
        ShowToast(needsTurn ? "已自动旋转 90° 并满幅适配格位方向" : "已切换为满幅裁切适应格位 (Cover)");
    }

    [RelayCommand]
    private void ResetCropGeometry()
    {
        if (CurrentEdit == null || !HasCropGeometryEdits) return;
        PushUndoSnapshot();
        CurrentEdit.RotationDegrees = 0;
        CurrentEdit.QuarterTurns = 0;
        CurrentEdit.FlipHorizontal = false;
        CurrentEdit.FlipVertical = false;
        CurrentEdit.Zoom = 1.0;
        CurrentEdit.PanX = 0;
        CurrentEdit.PanY = 0;
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
        ShowToast("已重置裁剪与旋转");
    }

    [RelayCommand]
    private void ResetColorAdjustments()
    {
        if (CurrentEdit == null || !HasAnyColorEdits) return;
        PushUndoSnapshot();
        CurrentEdit.ResetColor();
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
        ShowToast("已重置全部色彩与光影参数");
    }

    [RelayCommand]
    private void ResetLightingGroup()
    {
        if (CurrentEdit == null || !HasLightingGroupEdits) return;
        PushUndoSnapshot();
        CurrentEdit.Exposure = 0;
        CurrentEdit.Brightness = 0;
        CurrentEdit.Contrast = 0;
        CurrentEdit.Highlights = 0;
        CurrentEdit.Shadows = 0;
        CurrentEdit.Whites = 0;
        CurrentEdit.Blacks = 0;
        CurrentEdit.Gamma = 1.0;
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
        ShowToast("已重置光影曝光参数");
    }

    [RelayCommand]
    private void ResetWhiteBalanceGroup()
    {
        if (CurrentEdit == null || !HasColorFilterGroupEdits) return;
        PushUndoSnapshot();
        CurrentEdit.Temperature = 0;
        CurrentEdit.Tint = 0;
        CurrentEdit.Vibrance = 0;
        CurrentEdit.Saturation = 0;
        CurrentEdit.FilterPresetId = "none";
        CurrentEdit.FilterIntensity = 100;
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
        ShowToast("已重置白平衡与色彩");
    }

    [RelayCommand]
    private void ResetCurvesAndLevels()
    {
        if (CurrentEdit == null || !HasCurvesLevelsEdits) return;
        PushUndoSnapshot();
        CurrentEdit.ToneCurves = new ToneCurveData();
        CurrentEdit.InputBlack = 0;
        CurrentEdit.InputWhite = 255;
        CurrentEdit.OutputBlack = 0;
        CurrentEdit.OutputWhite = 255;
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
        ShowToast("已重置曲线与色阶");
    }

    [RelayCommand]
    private void ResetHslGroup()
    {
        if (CurrentEdit == null || !HasHslEdits) return;
        PushUndoSnapshot();
        CurrentEdit.Hsl = new HslAdjustmentSet();
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
        ShowToast("已重置全部 HSL 分色");
    }

    [RelayCommand]
    private void ResetDetailAndBgGroup()
    {
        if (CurrentEdit == null || !HasDetailBgEdits) return;
        PushUndoSnapshot();
        CurrentEdit.Clarity = 0;
        CurrentEdit.Sharpening = 0;
        CurrentEdit.SkinSoftening = 0;
        CurrentEdit.WhiteWallCleanup = 0;
        CurrentEdit.EnableAiBackgroundReplace = false;
        CurrentEdit.UseGradientBackground = false;
        CurrentEdit.MattingEdgeShift = 0.0;
        CurrentEdit.EnableStudioPortraitEnhance = false;
        CurrentEdit.StudioEnhanceMode = StudioEnhanceMode.Studio;
        CurrentEdit.StudioEnhanceStrength = 70.0;
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
        ShowToast("已重置细节与背景处理");
    }

    [RelayCommand]
    private void ApplyPreflightQuickFix(PreflightIssue? issue)
    {
        var slot = ActiveSheet?.Slots.FirstOrDefault(s => s.Id == issue?.SlotId);
        if (issue?.HasQuickFix != true || slot?.PhotoAssetId == null) return;

        SelectSlot(slot);
        if (SelectedAsset?.Id != slot.PhotoAssetId || CurrentEdit is not { } edit || edit.IsStudioEnhancementActive()) return;

        PushUndoSnapshot();
        edit.EnableStudioPortraitEnhance = true;
        if (edit.StudioEnhanceStrength < 1.0) edit.StudioEnhanceStrength = 70.0;
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
    }

    [RelayCommand]
    private void BatchEnhanceAllIdPhotos()
    {
        if (ActiveSheet?.Scenario != LayoutScenario.IdPhoto || CurrentEdit == null) return;

        var ids = Sheets.Where(s => s.Scenario == LayoutScenario.IdPhoto)
            .SelectMany(s => s.Slots)
            .Select(s => s.PhotoAssetId)
            .ToHashSet();
        var mode = CurrentEdit.StudioEnhanceMode;
        var strength = CurrentEdit.StudioEnhanceStrength >= 1.0 ? CurrentEdit.StudioEnhanceStrength : 70.0;
        var targets = Assets.Where(a => ids.Contains(a.Id) &&
            (a.Asset.EditState.StudioEnhanceMode != mode ||
             a.Asset.EditState.StudioEnhanceStrength != strength ||
             !a.Asset.EditState.EnableStudioPortraitEnhance)).ToArray();
        if (targets.Length == 0)
        {
            ShowToast("已排版的证件照均已应用当前影棚设置");
            return;
        }

        PushUndoSnapshot();
        foreach (var target in targets)
        {
            var edit = target.Asset.EditState;
            edit.EnableStudioPortraitEnhance = true;
            edit.StudioEnhanceMode = mode;
            edit.StudioEnhanceStrength = strength;
            InvalidateAssetPreviewCache(target.Id);
        }

        NotifyAllEditPropertiesChanged();
        PagePreviewsInvalidated?.Invoke();
        ScheduleRender();
        ShowToast($"已对 {targets.Length} 张已排版证件照应用影棚优化");
    }

    [RelayCommand]
    private void CopyColorParameters()
    {
        if (CurrentEdit == null) return;
        _copiedColorEdit = CurrentEdit.Clone();
        HasCopiedColorEdit = true;
        ShowToast($"已复制 {SelectedAsset!.DisplayName} 的调色参数");
    }

    [RelayCommand]
    private void PasteColorToSelected()
    {
        if (_copiedColorEdit == null || SelectedAsset == null) return;
        PushUndoSnapshot();
        SelectedAsset.Asset.EditState = _copiedColorEdit.CloneColorOnlyInto(SelectedAsset.Asset.EditState, includePortraitEffects: false);
        InvalidateAssetPreviewCache(SelectedAsset.Id);
        NotifyAllEditPropertiesChanged();
        ScheduleRender();
        ShowToast("已粘贴调色参数到当前照片");
    }

    [RelayCommand]
    private void PasteColorToAllAssets()
    {
        if (_copiedColorEdit == null || Assets.Count == 0) return;
        if (Assets.Count > 1 && !Dialogs.Confirm(
            $"将光影与色彩调整应用到素材池中的全部 {Assets.Count} 张照片。裁剪、换底和正装保持不变。是否继续？",
            "应用调色到全部照片")) return;
        PushUndoSnapshot();
        foreach (var vm in Assets)
        {
            vm.Asset.EditState = _copiedColorEdit.CloneColorOnlyInto(vm.Asset.EditState, includePortraitEffects: false);
            InvalidateAssetPreviewCache(vm.Id);
        }
        NotifyAllEditPropertiesChanged();
        PagePreviewsInvalidated?.Invoke();
        ScheduleRender();
        ShowToast($"已将调色批量应用到全部 {Assets.Count} 张照片");
    }

    public void PasteColorToAssets(IReadOnlyList<AssetItemViewModel> targets)
    {
        if (_copiedColorEdit == null || targets.Count == 0) return;
        PushUndoSnapshot();
        foreach (var vm in targets)
        {
            vm.Asset.EditState = _copiedColorEdit.CloneColorOnlyInto(vm.Asset.EditState, includePortraitEffects: false);
            InvalidateAssetPreviewCache(vm.Id);
        }
        NotifyAllEditPropertiesChanged();
        PagePreviewsInvalidated?.Invoke();
        ScheduleRender();
        ShowToast(targets.Count == 1
            ? "已粘贴调色参数到所选照片"
            : $"已将调色批量应用到所选 {targets.Count} 张照片，可撤销");
    }

    [RelayCommand]
    private void ApplyIdBackgroundColor(string hex)
    {
        if (CurrentEdit == null) return;
        PushUndoSnapshot();
        CurrentEdit.ReplacementBackgroundHex = hex;
        CurrentEdit.UseGradientBackground = false;
        if (HasAiMattingModel)
            CurrentEdit.EnableAiBackgroundReplace = true;
        else
            StatusText = "提示：放入任意 .onnx 人像分割模型到 portable_data\\models 可启用 AI 换底；当前可先使用“白墙提纯”滑块";
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
    }

    [RelayCommand]
    private void ApplyIdGradientBackground(string spec)
    {
        if (CurrentEdit == null || string.IsNullOrWhiteSpace(spec)) return;
        var parts = spec.Split('|');
        if (parts.Length < 2) return;
        PushUndoSnapshot();
        CurrentEdit.ReplacementBackgroundHex = parts[0].Trim();
        CurrentEdit.ReplacementBackgroundSecondaryHex = parts[1].Trim();
        CurrentEdit.UseGradientBackground = true;
        if (HasAiMattingModel)
            CurrentEdit.EnableAiBackgroundReplace = true;
        else
            StatusText = "提示：放入任意 .onnx 人像分割模型到 portable_data\\models 可启用 AI 换底";
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
    }

    public void SetCustomIdBackgroundColors(string primaryHex, string? secondaryHex = null, bool? useGradient = null)
    {
        if (CurrentEdit == null) return;
        PushUndoSnapshot();
        CurrentEdit.ReplacementBackgroundHex = primaryHex;
        if (!string.IsNullOrWhiteSpace(secondaryHex))
            CurrentEdit.ReplacementBackgroundSecondaryHex = secondaryHex;
        if (useGradient.HasValue)
            CurrentEdit.UseGradientBackground = useGradient.Value;
        if (HasAiMattingModel)
            CurrentEdit.EnableAiBackgroundReplace = true;
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
    }

    [RelayCommand]
    private void OpenModelsDirectory()
    {
        Paths.EnsureDirectoriesExist();
        Process.Start(new ProcessStartInfo("explorer.exe", Paths.ModelsDirectory) { UseShellExecute = true });
    }

    [RelayCommand]
    private void SetViewMode(CanvasViewMode mode)
    {
        if (mode != CanvasViewMode.SheetOverview && !HasSelectedAsset) return;
        ViewMode = mode;
        IsTwoPointStraightenActive = false;
        UpdateToolBanner();
        ScheduleRender();
    }

    [RelayCommand]
    private void ToggleTwoPointStraighten()
    {
        if (SelectedAsset == null) return;
        ViewMode = CanvasViewMode.SingleRetouchCrop;
        IsTwoPointStraightenActive = !IsTwoPointStraightenActive;
        UpdateToolBanner();
        StatusText = IsTwoPointStraightenActive
            ? "两点拉直：请在画布中依次点击左右眼瞳孔或水平线两端"
            : "已退出两点拉直";
        ScheduleRender();
    }

    public void ApplyTwoPointHorizon(Point p1, Point p2)
    {
        if (CurrentEdit == null) return;
        double dx = p2.X - p1.X;
        double dy = p2.Y - p1.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < 8) return;

        double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (angleDeg > 90) angleDeg -= 180;
        if (angleDeg < -90) angleDeg += 180;

        PushUndoSnapshot();
        CurrentEdit.RotationDegrees = Math.Clamp(CurrentEdit.RotationDegrees - angleDeg, -45, 45);
        IsTwoPointStraightenActive = false;
        UpdateToolBanner();
        OnEditMutated();
        NotifyAllEditPropertiesChanged();
        ShowToast($"已完成水平校正 ({CurrentEdit.RotationDegrees:+0.0;-0.0;0.0}°)");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AutoFrameWithFace()
    {
        if ((_batchFramingCancellation ?? _autoFrameCancellation) is { } running)
        {
            running.Cancel();
            ShowToast("已取消智能构图");
            return;
        }
        if (CanRestoreAutoFrame && SelectedAsset is { } asset)
        {
            PushUndoSnapshot();
            RestoreAutoFrame(asset.Asset.EditState, _appliedAutoFrames[asset.Id].Before);
            _appliedAutoFrames.Remove(asset.Id);
            OnEditMutated();
            NotifyAllEditPropertiesChanged();
            ShowToast("已还原构图");
            return;
        }
        if (IsAiFramingBusy) return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _autoFrameCancellation = cancellation;
        NotifyAutoFrameActionChanged();
        try
        {
            if (ActiveScenario == LayoutScenario.IdPhoto)
                await RunAutoFaceFramingAsync(adjustZoomAndPan: true, cancellation.Token);
            else
                await AutoFrameContentAsync(cancellation.Token);
        }
        finally
        {
            _autoFrameCancellation = null;
            NotifyAutoFrameActionChanged();
        }
    }

    private void NotifyAutoFrameActionChanged()
    {
        OnPropertyChanged(nameof(AutoFrameActionText));
        OnPropertyChanged(nameof(CanAutoFramePhoto));
    }

    [RelayCommand]
    private Task AutoLevelEyes() => RunAutoFaceFramingAsync(adjustZoomAndPan: false, _disposeCts.Token);

    private async Task AutoFrameContentAsync(CancellationToken token)
    {
        if (IsAiFramingBusy || SelectedAsset is not { } assetVm) return;
        var sheet = ActiveSheet;
        var slot = SelectedSlot;
        var asset = assetVm.Asset;
        var snapshot = asset.CloneMetadataOnly();
        var originalCrop = ReadCrop(snapshot.EditState);
        var geometry = ResolveTargetSlotGeometry(slot, asset.Id);
        IsAiFramingBusy = true;
        try
        {
            await Task.Run(() => ApplyContentFrame(snapshot, geometry.trimW, geometry.trimH, geometry.bleed,
                geometry.fitOverride, geometry.slotZoom, geometry.slotPanX, geometry.slotPanY, token), token);

            var edit = asset.EditState;
            if (token.IsCancellationRequested || SelectedAsset != assetVm || ActiveSheet != sheet || SelectedSlot != slot ||
                ReadCrop(edit) != originalCrop || ResolveTargetSlotGeometry(slot, asset.Id) != geometry) return;

            if ((edit.Zoom, edit.PanX, edit.PanY) != (snapshot.EditState.Zoom, snapshot.EditState.PanX, snapshot.EditState.PanY))
            {
                PushUndoSnapshot();
                (edit.Zoom, edit.PanX, edit.PanY) = (snapshot.EditState.Zoom, snapshot.EditState.PanX, snapshot.EditState.PanY);
                _appliedAutoFrames[asset.Id] = (originalCrop, ReadCrop(edit));
                OnEditMutated();
                NotifyAllEditPropertiesChanged();
                ShowToast("已优化裁剪，保留照片方向");
            }
            else ShowToast("当前构图已适合此格位");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Error("智能构图失败", ex);
            ShowToast($"智能构图失败：{ex.Message}");
        }
        finally { IsAiFramingBusy = false; }
    }

    private readonly record struct CropGeometry(int QuarterTurns, double Rotation, bool FlipHorizontal, bool FlipVertical,
        FitMode Fit, double Zoom, double PanX, double PanY);

    private static CropGeometry ReadCrop(PhotoEditState edit) =>
        new(edit.QuarterTurns, edit.RotationDegrees, edit.FlipHorizontal, edit.FlipVertical, edit.FitMode, edit.Zoom, edit.PanX, edit.PanY);

    private static void RestoreAutoFrame(PhotoEditState edit, CropGeometry before) =>
        (edit.QuarterTurns, edit.RotationDegrees, edit.Zoom, edit.PanX, edit.PanY) =
        (before.QuarterTurns, before.Rotation, before.Zoom, before.PanX, before.PanY);

    private void ApplyContentFrame(PhotoAsset asset, double trimW, double trimH, double bleed,
        FitMode? fitOverride, double slotZoom, double slotPanX, double slotPanY, CancellationToken token)
    {
        var edit = asset.EditState;
        var fit = fitOverride ?? edit.FitMode;
        double width = trimW + 2 * bleed, height = trimH + 2 * bleed;
        if (fit == FitMode.Contain && asset.PixelWidth > 0 && asset.PixelHeight > 0)
        {
            (edit.Zoom, edit.PanX, edit.PanY) = ContentAutoFrameHelper.Compute(asset.PixelWidth, asset.PixelHeight,
                edit, width, height, fit, slotZoom, slotPanX, slotPanY, [], null);
            return;
        }

        if (fit == FitMode.Cover && FaceService.HasModelLoaded)
        {
            var (proxyW, proxyH, faces) = GetOrDetectFacesCached(asset, token);
            if (faces.Count > 0)
            {
                (edit.Zoom, edit.PanX, edit.PanY) = ContentAutoFrameHelper.Compute(proxyW, proxyH,
                    edit, width, height, fit, slotZoom, slotPanX, slotPanY, faces, null);
                return;
            }
        }

        using var preview = ExifAndColorHelper.DecodeNormalizedSrgb(asset.FilePath, 192);
        (edit.Zoom, edit.PanX, edit.PanY) = ContentAutoFrameHelper.Compute(preview.Width, preview.Height,
            edit, width, height, fit, slotZoom, slotPanX, slotPanY, [], preview);
    }

    [RelayCommand]
    private Task AutoAlignFormalSuit() => TryAutoAlignSuitInBackgroundAsync(userInitiated: true);

    private async Task TryAutoAlignSuitInBackgroundAsync(bool userInitiated = false)
    {
        if (SelectedAsset == null)
        {
            if (userInitiated) ShowToast("请先选择照片");
            return;
        }
        if (!FaceService.HasModelLoaded)
        {
            if (userInitiated) ShowToast("未检测到人脸检测模型，可直接拖动位置与缩放滑块手动贴合");
            return;
        }

        var assetVm = SelectedAsset;
        var assetSnap = assetVm.Asset.CloneMetadataOnly();
        var edit = assetVm.Asset.EditState;
        var placement = (edit.FormalSuit, edit.SuitCenterXRatio, edit.SuitNeckYRatio, edit.SuitScale);
        try
        {
            var (pw, ph, faces) = await Task.Run(() => GetOrDetectFacesCached(assetSnap));
            if (SelectedAsset != assetVm || assetVm.Asset.EditState != edit ||
                placement != (edit.FormalSuit, edit.SuitCenterXRatio, edit.SuitNeckYRatio, edit.SuitScale)) return;
            if (faces.Count == 0)
            {
                if (userInitiated) ShowToast("未识别到清晰人脸，可用滑块微调正装衣领");
                return;
            }

            var (cx, ny, scale) = FormalSuitRenderer.ComputeSuitPlacement(faces, pw, ph);
            if (userInitiated) PushUndoSnapshot();
            edit.SuitCenterXRatio = cx;
            edit.SuitNeckYRatio = ny;
            edit.SuitScale = scale;
            OnEditMutated();
            NotifyAllEditPropertiesChanged();
            if (userInitiated) ShowToast("已根据下颌与五官位置自动贴合正装肩颈");
        }
        catch (Exception ex)
        {
            Logger.Error("正装自动贴合失败", ex);
        }
    }

    private async Task RunAutoFaceFramingAsync(bool adjustZoomAndPan, CancellationToken token)
    {
        if (IsAiFramingBusy) return;
        if (SelectedAsset == null)
        {
            ShowToast("请先选择需要智能构图的照片");
            return;
        }
        if (!FaceService.HasModelLoaded)
        {
            ShowToast("未检测到人脸与五官 ONNX 模型，请检查 portable_data\\models 目录");
            return;
        }

        IsAiFramingBusy = true;
        var assetVm = SelectedAsset;
        var assetSnap = assetVm.Asset.CloneMetadataOnly();
        var sheet = ActiveSheet;
        var slot = SelectedSlot;
        var crop = ReadCrop(assetSnap.EditState);

        try
        {
            var (proxyW, proxyH, faces) = await Task.Run(() => GetOrDetectFacesCached(assetSnap, token), token);
            if (token.IsCancellationRequested || SelectedAsset != assetVm || ActiveSheet != sheet || SelectedSlot != slot ||
                ReadCrop(assetVm.Asset.EditState) != crop) return;

            var (trimW, trimH, bleed, idSpec, fitOverride, slotZoom, slotPanX, slotPanY) =
                ResolveTargetSlotGeometry(SelectedSlot, assetSnap.Id);
            var effectiveFit = fitOverride ?? assetVm.Asset.EditState.FitMode;
            var res = PortraitAutoFrameHelper.ComputeAutoFrame(
                proxyW,
                proxyH,
                faces,
                assetVm.Asset.EditState,
                trimW,
                trimH,
                bleed,
                effectiveFit,
                idSpec,
                adjustZoomAndPan,
                slotZoom,
                slotPanX,
                slotPanY);

            if (!res.Success)
            {
                ShowToast(res.Message);
                return;
            }

            if (crop.Rotation == res.RotationDegrees && (!adjustZoomAndPan ||
                (crop.Zoom, crop.PanX, crop.PanY) == (res.Zoom, res.PanX, res.PanY)))
            {
                ShowToast("当前构图已适合此格位");
                return;
            }
            PushUndoSnapshot();
            assetVm.Asset.EditState.RotationDegrees = res.RotationDegrees;
            if (adjustZoomAndPan)
            {
                assetVm.Asset.EditState.Zoom = res.Zoom;
                assetVm.Asset.EditState.PanX = res.PanX;
                assetVm.Asset.EditState.PanY = res.PanY;
                _appliedAutoFrames[assetVm.Id] = (crop, ReadCrop(assetVm.Asset.EditState));
            }

            OnEditMutated();
            NotifyAllEditPropertiesChanged();
            ShowToast(res.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Error("AI 人脸与五官构图失败", ex);
            ShowToast($"AI 构图失败：{ex.Message}");
        }
        finally
        {
            IsAiFramingBusy = false;
        }
    }

    private (double trimW, double trimH, double bleed, IdPhotoSpec? idSpec, FitMode? fitOverride, double slotZoom, double slotPanX, double slotPanY)
        ResolveTargetSlotGeometry(LayoutSlot? slotSnap, string assetId)
    {
        LayoutSlot? effectiveSlot = slotSnap;
        if (effectiveSlot == null ||
            (!string.IsNullOrEmpty(effectiveSlot.PhotoAssetId) && effectiveSlot.PhotoAssetId != assetId))
        {
            effectiveSlot = ActiveSheet?.Slots.FirstOrDefault(s => s.PhotoAssetId == assetId)
                            ?? ActiveSheet?.Slots.FirstOrDefault();
        }

        if (effectiveSlot != null)
        {
            var (tw, th, bleed) = PortraitAutoFrameHelper.ResolveUprightSlotPhotoFrameMm(effectiveSlot);
            var idSpec = !string.IsNullOrEmpty(effectiveSlot.IdPhotoSpecId)
                ? IdPhotoSpec.BuiltInSpecs.FirstOrDefault(s => s.Id == effectiveSlot.IdPhotoSpecId)
                : (ActiveScenario == LayoutScenario.IdPhoto ? SelectedIdSpec : null);
            return (tw, th, bleed, idSpec, effectiveSlot.EffectiveFitMode, effectiveSlot.SlotZoom, effectiveSlot.SlotPanX, effectiveSlot.SlotPanY);
        }

        if (ActiveScenario == LayoutScenario.IdPhoto)
            return (SelectedIdSpec.WidthMm, SelectedIdSpec.HeightMm, 0.0, SelectedIdSpec, null, 1.0, 0.0, 0.0);

        if (ActiveScenario == LayoutScenario.Polaroid)
            return (SelectedPolaroidSpec.PhotoWidthMm, SelectedPolaroidSpec.PhotoHeightMm, 0.0, null, null, 1.0, 0.0, 0.0);

        return (100.0, 140.0, 0.0, null, null, 1.0, 0.0, 0.0);
    }

    private (int proxyW, int proxyH, IReadOnlyList<DetectedFace> faces) GetOrDetectFacesCached(PhotoAsset asset, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!FaceService.HasModelLoaded || string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath))
            return (0, 0, []);

        int qt = ((asset.EditState.QuarterTurns % 4) + 4) % 4;
        string key = $"{asset.FilePath}|{File.GetLastWriteTimeUtc(asset.FilePath).Ticks}|qt={qt}|{FaceService.ActiveModelName}";
        if (_faceDetectionCache.TryGetValue(asset.Id, out var hit) && hit.key == key)
            return (hit.width, hit.height, hit.faces);

        using var proxy = ImageCache.GetReadOnlyPreviewProxy(asset, 1300);
        var faces = FaceService.DetectFacesWithQuarterTurns(proxy, qt, token);
        token.ThrowIfCancellationRequested();
        _faceDetectionCache[asset.Id] = (key, proxy.Width, proxy.Height, faces);
        return (proxy.Width, proxy.Height, faces);
    }

    public void AdjustPanZoom(double deltaPanX, double deltaPanY, double zoomFactor)
    {
        if (CurrentEdit is not { } edit) return;
        double zoom = zoomFactor == 1.0 ? edit.Zoom : Math.Clamp(edit.Zoom * zoomFactor, 1.0, 4.0);
        double panX = Math.Clamp(edit.PanX + deltaPanX, -1.2, 1.2);
        double panY = Math.Clamp(edit.PanY + deltaPanY, -1.2, 1.2);
        if ((zoom, panX, panY) == (edit.Zoom, edit.PanX, edit.PanY)) return;
        (edit.Zoom, edit.PanX, edit.PanY) = (zoom, panX, panY);
        OnEditMutated();
        OnPropertyChanged(nameof(EditZoom));
        OnPropertyChanged(nameof(EditPanX));
        OnPropertyChanged(nameof(EditPanY));
    }

    partial void OnIsHoldingCompareChanged(bool value) { UpdateToolBanner(); ScheduleRender(); }
    partial void OnSplitCompareRatioChanged(double value) { if (ViewMode == CanvasViewMode.SplitCompare) ScheduleRender(); }
    partial void OnShowSafeMarginOverlayChanged(bool value) => ScheduleRender();
    partial void OnShowBleedOverlayChanged(bool value) => ScheduleRender();
    partial void OnShowIdGuidelinesChanged(bool value) => ScheduleRender();
    partial void OnShowFaceLandmarksOverlayChanged(bool value) => ScheduleRender();
    partial void OnShowClippingOverlayChanged(bool value) => ScheduleRender();
    partial void OnEnableSoftProofingChanged(bool value) => ScheduleRender();
    partial void OnSoftProofIntentChanged(SoftProofRenderingIntent value) { if (EnableSoftProofing) ScheduleRender(); }
    partial void OnSoftProofSimulatePaperWhiteChanged(bool value) { if (EnableSoftProofing) ScheduleRender(); }
    partial void OnSoftProofGamutWarningChanged(bool value) { if (EnableSoftProofing) ScheduleRender(); }
    partial void OnSelectedIccPathChanged(string value)
    {
        _cachedIccPath = null;
        _cachedIccBytes = null;
        SaveConfig();
        ScheduleRender();
    }

    private SoftProofOptions CurrentSoftProofOptions() => new(
        SoftProofIntent,
        SoftProofSimulatePaperWhite,
        SoftProofGamutWarning);

    private byte[]? GetSoftProofIccBytesCached()
    {
        if (!EnableSoftProofing || string.IsNullOrWhiteSpace(SelectedIccPath))
            return null;
        if (string.Equals(_cachedIccPath, SelectedIccPath, StringComparison.OrdinalIgnoreCase) && _cachedIccBytes != null)
            return _cachedIccBytes;

        try
        {
            if (!File.Exists(SelectedIccPath))
                return null;

            byte[] bytes = File.ReadAllBytes(SelectedIccPath);
            if (!ExifAndColorHelper.SupportsSoftProofIcc(bytes))
            {
                EnableSoftProofing = false;
                ShowToast("此色彩配置无法用于软打样，请检查是否为有效的 RGB 或 CMYK ICC 配置文件。");
                return null;
            }
            _cachedIccBytes = bytes;
            _cachedIccPath = SelectedIccPath;
            return _cachedIccBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"读取软打样 ICC 失败: {SelectedIccPath}", ex);
            EnableSoftProofing = false;
            ShowToast($"无法读取软打样 ICC 文件：{ex.Message}");
            return null;
        }
    }

    public void InvalidateAssetPreviewCache(string assetId) =>
        PreviewCache.InvalidateAsset(assetId);

    public void InvalidateAllMattingCaches()
    {
        _compactMatteCache.Clear();
        _relightShadingCache.Clear();
        _portraitDiagnosticsCache.Clear();
        _latestPortraitDiagnostics.Clear();
        PreviewCache.ClearAllCaches();
    }

    private int _previewTargetLongEdgePx = 1920;

    public static double CalculatePreviewDpi(double paperWidthMm, double paperHeightMm, int targetLongEdgePx)
        => SheetRenderer.CalculatePreviewDpi(paperWidthMm, paperHeightMm, targetLongEdgePx);

    public void UpdatePreviewViewportSize(double desiredPhysicalLongEdgePx)
    {
        if (double.IsNaN(desiredPhysicalLongEdgePx) || desiredPhysicalLongEdgePx <= 0) return;
        int clamped = Math.Clamp((int)Math.Ceiling(desiredPhysicalLongEdgePx), 1920, 3200);
        int previous = Volatile.Read(ref _previewTargetLongEdgePx);
        if (clamped > previous * 1.15 || clamped < previous * 0.70)
        {
            Volatile.Write(ref _previewTargetLongEdgePx, clamped);
            if (ActiveSheet != null && Assets.Count > 0)
                ScheduleRender(preserveContext: true);
        }
        else if (clamped > previous)
        {
            Volatile.Write(ref _previewTargetLongEdgePx, clamped);
        }
    }

    public void ScheduleRender(bool preserveContext = false)
    {
        if (!preserveContext) ChangeRenderContext();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && dispatcher.CheckAccess())
        {
            PagePreviewsInvalidated?.Invoke();
            if (_renderQueuedOnDispatcher) return;
            _renderQueuedOnDispatcher = true;
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, () =>
            {
                _renderQueuedOnDispatcher = false;
                StartBackgroundRender(forceHighQuality: false);
            });
            return;
        }

        StartBackgroundRender(forceHighQuality: false);
    }

    internal event Action? PagePreviewsInvalidated;

    private void ChangeRenderContext(bool closing = false) =>
        PreviewCache.AdvanceContext(closing);

    internal Func<System.Windows.Media.Imaging.BitmapSource> CreatePageThumbnailRenderer(PrintSheet page, CancellationToken token)
    {
        // UI 线程仅做轻量级元数据快照；磁盘时间戳读取与 JSON 状态序列化全部延后至后台工作线程执行
        var sheet = page.Clone(preserveId: true);
        var ids = sheet.Slots.Select(s => s.PhotoAssetId).OfType<string>().ToHashSet();
        var assets = Assets.Where(a => ids.Contains(a.Id)).ToDictionary(a => a.Id, a => a.Asset.CloneMetadataOnly());
        string? mattingModel = MattingService.ActiveModelName;
        string? faceModel = FaceService.ActiveModelName;
        string? landmarkModel = FaceService.ActiveLandmarkModelName;

        return () =>
        {
            string state = JsonSerializer.Serialize(new
            {
                Sheet = sheet,
                Assets = assets.Values.Select(asset => new
                {
                    Asset = asset,
                    FileStamp = File.Exists(asset.FilePath) ? File.GetLastWriteTimeUtc(asset.FilePath).Ticks : 0L
                }),
                Matting = mattingModel,
                Face = faceModel,
                Landmarks = landmarkModel
            });

            if (PreviewCache.TryGetPageThumbnail(sheet.Id, state, out var cached) && cached != null)
                return cached;

            double dpi = 256 * UnitConverter.MmPerInch / Math.Max(sheet.PaperWidthMm, sheet.PaperHeightMm);
            using var bitmap = new SKBitmap(UnitConverter.MmToPixelsRounded(sheet.PaperWidthMm, dpi),
                UnitConverter.MmToPixelsRounded(sheet.PaperHeightMm, dpi));
            using (var canvas = new SKCanvas(bitmap))
            {
                SheetRenderer.RenderToCanvas(canvas, sheet, assets, (asset, edit) =>
                {
                    using var source = ImageCache.GetReadOnlyThumbnail(asset);
                    return ProcessBitmapForExportWithCompactMatte(source, asset, edit, token);
                }, new SheetRenderOptions { Dpi = dpi, IsScreenPreview = false });
            }
            var thumbnail = SkiaWpfInterop.ToBitmapSource(bitmap);
            PreviewCache.StorePageThumbnail(sheet.Id, state, thumbnail);
            return thumbnail;
        };
    }

    private void StartBackgroundRender(bool forceHighQuality)
    {
        bool useAiModels = !IsAiModelLoading;
        if (!PreviewCache.TryGetCurrentContext(out long contextVersion, out CancellationToken contextToken))
            return;
        long renderSeq = Interlocked.Increment(ref _renderSequence);
        long nowTick = Environment.TickCount64;
        long prevTick = Interlocked.Exchange(ref _lastRenderScheduleTickMs, nowTick);
        bool isInteractiveDrag = !forceHighQuality && (_pendingUndoSnapshot != null || (prevTick > 0 && nowTick - prevTick < 150));

        var sheetSnapshot = ActiveSheet?.Clone();
        if (sheetSnapshot == null) return;
        _hqSettleCts?.Cancel();
        var cts = new CancellationTokenSource();
        var renderToken = cts.Token;
        var prevCts = Interlocked.Exchange(ref _renderCts, cts);
        if (prevCts != null)
        {
            try { prevCts.Cancel(); } catch (ObjectDisposedException) { }
            prevCts.Dispose();
        }
        int previewTargetLongEdgePx = Volatile.Read(ref _previewTargetLongEdgePx);
        int effectiveTargetLongEdgePx = isInteractiveDrag ? Math.Min(previewTargetLongEdgePx, 960) : previewTargetLongEdgePx;

        var selectedSlotId = SelectedSlot?.Id;
        var selectedSlotSnap = SelectedSlot?.Clone();
        var selectedAssetSnap = SelectedAsset?.Asset.CloneMetadataOnly();

        // 仅克隆当前页面实际引用的素材，避免图库有数十张照片时每次拖拽全量深拷贝
        var assetsMap = new Dictionary<string, PhotoAsset>();
        foreach (var sl in sheetSnapshot.Slots)
        {
            if (string.IsNullOrEmpty(sl.PhotoAssetId) || assetsMap.ContainsKey(sl.PhotoAssetId))
                continue;
            if (selectedAssetSnap != null && sl.PhotoAssetId == selectedAssetSnap.Id)
            {
                assetsMap[sl.PhotoAssetId] = selectedAssetSnap;
            }
            else
            {
                var found = Assets.FirstOrDefault(a => a.Id == sl.PhotoAssetId);
                if (found != null) assetsMap[found.Id] = found.Asset.CloneMetadataOnly();
            }
        }
        if (selectedAssetSnap != null && !assetsMap.ContainsKey(selectedAssetSnap.Id))
            assetsMap[selectedAssetSnap.Id] = selectedAssetSnap;

        var viewMode = ViewMode;
        var holdingCompare = IsHoldingCompare;
        var splitRatio = SplitCompareRatio;
        var showSafe = ShowSafeMarginOverlay;
        var showBleed = ShowBleedOverlay;
        var showGuide = ShowIdGuidelines;
        var showFaceOverlay = ShowFaceLandmarksOverlay;
        var showClip = ShowClippingOverlay;
        var calibration = CurrentCalibration;
        var safeMargin = !calibration.DefaultBorderless &&
            string.Equals(calibration.PrinterName, SelectedPrinterName, StringComparison.OrdinalIgnoreCase) &&
            PrinterChoices.Any(printer => !printer.IsVirtual && printer.Name == SelectedPrinterName)
            ? calibration.SafeMarginMm
            : 0.0;
        (double trimW, double trimH, double bleed, IdPhotoSpec? targetIdSpec, FitMode? fitOverride, double slotZoom, double slotPanX, double slotPanY) targetGeom =
            selectedAssetSnap != null
                ? ResolveTargetSlotGeometry(selectedSlotSnap, selectedAssetSnap.Id)
                : (100.0, 140.0, 0.0, null, null, 1.0, 0.0, 0.0);
        byte[]? softProofBytes = GetSoftProofIccBytesCached();
        string? softProofKey = softProofBytes != null ? SelectedIccPath : null;
        var softProofOpts = CurrentSoftProofOptions();

        _ = Task.Run(async () =>
        {
            bool acquiredGate = false;
            try
            {
                try
                {
                    await _renderGate.WaitAsync(renderToken);
                    acquiredGate = true;
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                if (renderToken.IsCancellationRequested) return;
                SKBitmap rendered;
                HistogramData? activeHist = null;
                var renderHistograms = new Dictionary<string, HistogramData>();
                IReadOnlyList<DetectedFace> detectedFaces = [];
                int faceProxyW = 0, faceProxyH = 0;
                FaceComplianceMetrics? faceMetrics = null;

                if (useAiModels && selectedAssetSnap != null && FaceService.HasModelLoaded)
                {
                    (faceProxyW, faceProxyH, detectedFaces) = GetOrDetectFacesCached(selectedAssetSnap, contextToken);
                    if (detectedFaces.Count > 0)
                    {
                        faceMetrics = PortraitAutoFrameHelper.MeasureCompliance(
                            faceProxyW,
                            faceProxyH,
                            detectedFaces,
                            selectedAssetSnap.EditState,
                            targetGeom.trimW,
                            targetGeom.trimH,
                            targetGeom.bleed,
                            targetGeom.fitOverride ?? selectedAssetSnap.EditState.FitMode,
                            targetGeom.targetIdSpec,
                            targetGeom.slotZoom,
                            targetGeom.slotPanX,
                            targetGeom.slotPanY);

                    }
                }

                if (renderToken.IsCancellationRequested) return;

                if (viewMode != CanvasViewMode.SheetOverview && selectedAssetSnap != null)
                {
                    rendered = RenderSingleEditView(
                        selectedAssetSnap,
                        selectedSlotSnap,
                        viewMode,
                        holdingCompare,
                        splitRatio,
                        showClip,
                        showGuide,
                        showFaceOverlay,
                        detectedFaces,
                        faceProxyW,
                        faceProxyH,
                        softProofBytes,
                        softProofKey,
                        softProofOpts,
                        effectiveTargetLongEdgePx,
                        isInteractiveDrag,
                        useAiModels,
                        renderHistograms,
                        out activeHist,
                        contextToken);
                }
                else
                {
                    var opts = new SheetRenderOptions
                    {
                        Dpi = SheetRenderer.CalculatePreviewDpi(
                            sheetSnapshot.PaperWidthMm,
                            sheetSnapshot.PaperHeightMm,
                            effectiveTargetLongEdgePx),
                        IsScreenPreview = true,
                        IsInteractiveDrag = isInteractiveDrag,
                        SelectedSlotId = selectedSlotId,
                        ShowSafeMarginOverlay = showSafe,
                        ShowBleedOverlay = showBleed,
                        ShowIdGuidelinesOnSelected = showGuide,
                        ShowClippingOverlay = showClip,
                        SafeMarginMm = safeMargin,
                        SoftProofIccBytes = softProofBytes,
                        SoftProofOptions = softProofOpts
                    };

                    rendered = SheetRenderer.RenderSheetBitmap(
                        sheetSnapshot,
                        assetsMap,
                        (asset, effEdit) =>
                        {
                            return ResolvePreviewBitmap(asset, holdingCompare ? new PhotoEditState() : effEdit, showClip, renderHistograms, contextToken, useAiModels, originalColors: holdingCompare);
                        },
                        opts);

                    if (selectedAssetSnap != null && renderHistograms.TryGetValue(selectedAssetSnap.Id, out var h))
                        activeHist = h;
                }

                if (contextVersion != PreviewCache.CurrentContextVersion)
                {
                    rendered.Dispose();
                    return;
                }

                UiDispatcher.BeginInvoke(() =>
                {
                    if (contextVersion != PreviewCache.CurrentContextVersion)
                    {
                        rendered.Dispose();
                        return;
                    }
                    using (rendered)
                    {
                        CanvasPreviewBitmap = SkiaWpfInterop.UpdateWriteableBitmap(rendered, CanvasPreviewBitmap);
                    }
                    if (renderToken.IsCancellationRequested) return;

                    foreach (var item in renderHistograms) _latestHistograms[item.Key] = item.Value;
                    if (activeHist != null)
                    {
                        ActiveHistogram = activeHist;
                        ClippingWarningText = (activeHist.HighlightClippedRatio > 0.05 || activeHist.ShadowClippedRatio > 0.05)
                            ? $"高光溢出 {activeHist.HighlightClippedRatio:P1}，暗部死黑 {activeHist.ShadowClippedRatio:P1}"
                            : "曝光分布正常";
                    }

                    if (faceMetrics != null)
                    {
                        DetectedFaceSummaryText =
                            $"AI 检出 {faceMetrics.FaceCount} 张人脸，瞳孔倾角 {faceMetrics.EyeTiltDegrees:+0.0;-0.0;0.0}°，偏移 {faceMetrics.HorizontalOffsetMm:+0.0;-0.0;0.0} mm";

                        if (targetGeom.targetIdSpec is { HeadHeightMaxMm: > 0 } idSpec)
                        {
                            string okTag = (faceMetrics.IsHeadHeightCompliant && faceMetrics.IsLevelCompliant && faceMetrics.IsCenteredCompliant)
                                ? "构图合规 ✓"
                                : "可点「AI智能构图」自动校准";
                            EstimatedIdHeadInfo =
                                $"AI 估算头高 {faceMetrics.EstimatedHeadHeightMm:0.0} mm (规范 {idSpec.HeadHeightMinMm:0}~{idSpec.HeadHeightMaxMm:0})，倾角 {faceMetrics.EyeTiltDegrees:+0.0;-0.0;0.0}°，{okTag}";
                        }
                    }
                    else
                    {
                        DetectedFaceSummaryText = useAiModels && selectedAssetSnap != null && FaceService.HasModelLoaded
                            ? "未检出清晰人脸（风景/静物可忽略）"
                            : "";
                        UpdateEstimatedHeadSize();
                    }

                    UpdatePreflight(assetsMap);

                    if (isInteractiveDrag && Volatile.Read(ref _renderSequence) == renderSeq)
                    {
                        ScheduleHqSettle(contextVersion, renderSeq);
                    }
                });

                if (useAiModels && !isInteractiveDrag && sheetSnapshot.Scenario == LayoutScenario.IdPhoto)
                {
                    using var diagnosticCancellation = CancellationTokenSource.CreateLinkedTokenSource(renderToken, contextToken);
                    var diagnostics = InspectPortraits([sheetSnapshot], assetsMap, diagnosticCancellation.Token);
                    UiDispatcher.BeginInvoke(() =>
                    {
                        if (renderToken.IsCancellationRequested ||
                            contextVersion != PreviewCache.CurrentContextVersion ||
                            Volatile.Read(ref _renderSequence) != renderSeq) return;
                        _latestPortraitDiagnostics = diagnostics;
                        UpdatePreflight(assetsMap);
                    });
                }
            }
            catch (OperationCanceledException) when (contextToken.IsCancellationRequested || renderToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Logger.Error("画布异步渲染异常", ex);
            }
            finally
            {
                if (acquiredGate)
                {
                    try { _renderGate.Release(); } catch (ObjectDisposedException) { }
                }
            }
        });
    }

    private void ScheduleHqSettle(long contextVersion, long expectedRenderSeq)
    {
        _hqSettleCts?.Cancel();
        _hqSettleCts?.Dispose();
        var settleCts = new CancellationTokenSource();
        _hqSettleCts = settleCts;
        var settleToken = settleCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(140, settleToken);
                if (settleToken.IsCancellationRequested) return;
                if (PreviewCache.CurrentContextVersion != contextVersion ||
                    Volatile.Read(ref _renderSequence) != expectedRenderSeq)
                    return;

                UiDispatcher.BeginInvoke(() =>
                {
                    if (PreviewCache.CurrentContextVersion == contextVersion &&
                        Volatile.Read(ref _renderSequence) == expectedRenderSeq &&
                        ActiveSheet != null && Assets.Count > 0)
                    {
                        StartBackgroundRender(forceHighQuality: true);
                    }
                });
            }
            catch (OperationCanceledException) { }
        }, settleToken);
    }

    private SKBitmap ResolvePreviewBitmap(PhotoAsset asset, PhotoEditState edit, bool showClipping, Dictionary<string, HistogramData> histograms, CancellationToken token, bool useAiModels, bool originalColors = false)
    {
        token.ThrowIfCancellationRequested();
        string sourceKey = MattingSourceKey(asset);
        string previewKey = !useAiModels ? $"{sourceKey}|loading"
            : edit.IsStudioEnhancementActive() ? $"{sourceKey}|{RelightingService.ActiveModelName}" : sourceKey;
        if (PreviewCache.TryGetProcessedPreview(asset.Id, originalColors, previewKey, edit, showClipping, out var cachedBmp, out var cachedHist) &&
            cachedBmp != null && cachedHist != null)
        {
            histograms[asset.Id] = cachedHist;
            return cachedBmp;
        }

        using var proxy = ImageCache.GetReadOnlyPreviewProxy(asset, 1300);
        var matte = useAiModels && (edit.EnableAiBackgroundReplace || edit.IsStudioEnhancementActive())
            ? ResolvePortraitMatte(asset, sourceKey, proxy, token)
            : null;
        var portraitCtx = useAiModels ? BuildPortraitProcessContext(asset, edit, proxy, token) : null;
        var res = FloatImageProcessor.Process(proxy, edit, matte, showClipping, portraitCtx);
        if (!_disposeCts.IsCancellationRequested)
        {
            PreviewCache.StoreProcessedPreview(asset.Id, originalColors, previewKey, edit, showClipping, res.Bitmap, res.Histogram);
        }
        histograms[asset.Id] = res.Histogram;
        return res.Bitmap;
    }

    private SKBitmap RenderSingleEditView(
        PhotoAsset asset,
        LayoutSlot? slot,
        CanvasViewMode mode,
        bool holdingCompare,
        double splitRatio,
        bool showClipping,
        bool showGuide,
        bool showFaceOverlay,
        IReadOnlyList<DetectedFace> detectedFaces,
        int faceProxyW,
        int faceProxyH,
        byte[]? softProofBytes,
        string? softProofKey,
        SoftProofOptions softProofOpts,
        int previewTargetLongEdgePx,
        bool isInteractiveDrag,
        bool useAiModels,
        Dictionary<string, HistogramData> histograms,
        out HistogramData hist,
        CancellationToken token)
    {
        double targetW = slot?.WidthMm ?? 100;
        double targetH = slot?.HeightMm ?? 140;
        if (slot is { IsQuarterTurnRotated: true })
            (targetW, targetH) = (targetH, targetW);
        if (slot?.PolaroidStyle != null)
        {
            targetW = Math.Max(10, targetW - slot.PolaroidStyle.MarginLeftMm - slot.PolaroidStyle.MarginRightMm);
            targetH = Math.Max(10, targetH - slot.PolaroidStyle.MarginTopMm - slot.PolaroidStyle.MarginBottomMm);
        }

        int targetLong = isInteractiveDrag ? previewTargetLongEdgePx : Math.Clamp(previewTargetLongEdgePx, 1500, 2600);
        double aspect = targetW / Math.Max(0.1, targetH);
        int outW, outH;
        if (aspect >= 1.0)
        {
            outW = targetLong;
            outH = Math.Clamp((int)Math.Round(outW / aspect), 480, targetLong);
        }
        else
        {
            outH = targetLong;
            outW = Math.Clamp((int)Math.Round(outH * aspect), 480, targetLong);
        }

        var singleSheet = new PrintSheet
        {
            PaperWidthMm = targetW,
            PaperHeightMm = targetH,
            CanvasBackgroundHex = "#181A20",
            CropMarkStyle = CropMarkStyle.None,
            Slots =
            [
                new LayoutSlot
                {
                    XMm = 0,
                    YMm = 0,
                    WidthMm = targetW,
                    HeightMm = targetH,
                    PhotoAssetId = asset.Id,
                    IdPhotoSpecId = slot?.IdPhotoSpecId,
                    FitModeOverride = slot?.EffectiveFitMode,
                    SlotZoom = slot?.SlotZoom ?? 1.0,
                    SlotPanX = slot?.SlotPanX ?? 0,
                    SlotPanY = slot?.SlotPanY ?? 0,
                    BleedMm = slot?.BleedMm ?? 0
                }
            ]
        };

        double dpi = outW / (targetW / UnitConverter.MmPerInch);
        var opts = new SheetRenderOptions
        {
            Dpi = dpi,
            IsScreenPreview = true,
            IsInteractiveDrag = isInteractiveDrag,
            SelectedSlotId = singleSheet.Slots[0].Id,
            ShowSafeMarginOverlay = false,
            ShowBleedOverlay = false,
            ShowIdGuidelinesOnSelected = showGuide,
            ShowFaceLandmarksOnSelected = mode == CanvasViewMode.SingleRetouchCrop && showFaceOverlay && detectedFaces.Count > 0,
            SingleViewDetectedFaces = detectedFaces,
            SingleViewSourceWidth = faceProxyW,
            SingleViewSourceHeight = faceProxyH,
            ShowClippingOverlay = showClipping,
            SoftProofIccBytes = softProofBytes,
            SoftProofOptions = softProofOpts
        };

        var afterBmp = SheetRenderer.RenderSheetBitmap(
            singleSheet,
            new Dictionary<string, PhotoAsset> { [asset.Id] = asset },
            (a, eff) => ResolvePreviewBitmap(a, holdingCompare ? new PhotoEditState() : eff, showClipping, histograms, token, useAiModels, originalColors: holdingCompare),
            opts);

        hist = histograms[asset.Id];

        if (mode != CanvasViewMode.SplitCompare)
            return afterBmp;
        using var compareAfter = afterBmp;

        // 分屏对比：左侧绘制原图（仅保留几何对齐，调色归零），并复用原图画布缓存避免滑块拖动期间双倍整页重绘
        var e = asset.EditState;
        string beforeKey = FormattableString.Invariant(
            $"{asset.Id}|{outW}x{outH}|{isInteractiveDrag}|{softProofKey}|{(int)softProofOpts.Intent}|{softProofOpts.SimulatePaperWhite}|{softProofOpts.ShowGamutWarning}|{e.RotationDegrees:F2}|{e.QuarterTurns}|{e.FlipHorizontal}|{e.FlipVertical}|{e.Zoom:F4}|{e.PanX:F4}|{e.PanY:F4}|{(int)e.FitMode}|{slot?.SlotZoom:F4}|{slot?.SlotPanX:F4}|{slot?.SlotPanY:F4}|{showGuide}|{(int)e.FormalSuit}|{e.SuitCenterXRatio:F4}|{e.SuitNeckYRatio:F4}|{e.SuitScale:F4}");

        SKBitmap? beforeBmp;
        if (!PreviewCache.TryGetSplitBefore(beforeKey, out beforeBmp) || beforeBmp == null)
        {
            var identityColor = asset.EditState.Clone();
            identityColor.ResetColor();
            beforeBmp = SheetRenderer.RenderSheetBitmap(
                singleSheet,
                new Dictionary<string, PhotoAsset> { [asset.Id] = asset },
                (a, _) => ResolvePreviewBitmap(a, identityColor, false, histograms, token, useAiModels, originalColors: true),
                opts);
            PreviewCache.StoreSplitBefore(beforeKey, beforeBmp);
        }
        histograms[asset.Id] = hist;

        var combined = new SKBitmap(afterBmp.Info);
        using var canvas = new SKCanvas(combined);
        int splitX = Math.Clamp((int)Math.Round(combined.Width * splitRatio), 2, combined.Width - 2);

        using (beforeBmp)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, splitX, combined.Height));
            using (var imgB = SKImage.FromBitmap(beforeBmp))
                canvas.DrawImage(imgB, 0, 0, SKSamplingOptions.Default);
            canvas.Restore();
        }

        canvas.Save();
        canvas.ClipRect(new SKRect(splitX, 0, combined.Width, combined.Height));
        using (var imgA = SKImage.FromBitmap(afterBmp))
            canvas.DrawImage(imgA, 0, 0, SKSamplingOptions.Default);
        canvas.Restore();

        using var linePaint = new SKPaint { Color = SKColors.White, StrokeWidth = 2.5f };
        canvas.DrawLine(splitX, 0, splitX, combined.Height, linePaint);

        var typeface = SheetRenderer.GetCachedTypeface("Microsoft YaHei UI");
        using var font = new SKFont(typeface, 14f, 1f, 0f);
        using var pillBg = new SKPaint { Color = new SKColor(0, 0, 0, 170), IsAntialias = true };
        using var pillFg = new SKPaint { Color = SKColors.White, IsAntialias = true };

        canvas.DrawRoundRect(new SKRect(14, 14, 102, 40), 6, 6, pillBg);
        canvas.DrawText("原图 Before", 58, 32, SKTextAlign.Center, font, pillFg);
        canvas.DrawRoundRect(new SKRect(combined.Width - 110, 14, combined.Width - 14, 40), 6, 6, pillBg);
        canvas.DrawText("调色 After", combined.Width - 62, 32, SKTextAlign.Center, font, pillFg);

        return combined;
    }

    internal SKBitmap ResolveFullResBitmapForExport(PhotoAsset asset, PhotoEditState edit)
    {
        using var full = ImageCache.DecodeFullResolution(asset);
        return ProcessBitmapForExportWithCompactMatte(full, asset, edit);
    }

    private Func<PhotoAsset, PhotoEditState, SKBitmap> CreateSheetScopedExportResolver(
        IReadOnlyList<PrintSheet> sheets,
        int dpi,
        CancellationToken token = default)
    {
        var slotsByAsset = sheets.SelectMany(sheet => sheet.Slots)
            .Where(slot => !string.IsNullOrEmpty(slot.PhotoAssetId))
            .ToLookup(slot => slot.PhotoAssetId!, StringComparer.Ordinal);
        return (asset, edit) =>
        {
            token.ThrowIfCancellationRequested();
            int targetLongEdge = ComputeRequiredExportLongEdge(asset, slotsByAsset[asset.Id], dpi);
            using var decoded = targetLongEdge > 0
                ? ExifAndColorHelper.DecodeNormalizedSrgb(asset.FilePath, targetLongEdge)
                : ImageCache.DecodeFullResolution(asset);
            return ProcessBitmapForExportWithCompactMatte(decoded, asset, edit, token);
        };
    }

    private SKBitmap ProcessBitmapForExportWithCompactMatte(SKBitmap decoded, PhotoAsset asset, PhotoEditState edit, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var compactMatte = (edit.EnableAiBackgroundReplace || edit.IsStudioEnhancementActive())
            ? ResolvePortraitMatte(asset, MattingSourceKey(asset), token: token)
            : null;
        var portraitCtx = BuildPortraitProcessContext(asset, edit, preview: null, token);
        var res = FloatImageProcessor.Process(decoded, edit, compactMatte, showClippingOverlay: false, portraitCtx);
        return res.Bitmap;
    }

    private PortraitProcessContext? BuildPortraitProcessContext(PhotoAsset asset, PhotoEditState edit, SKBitmap? preview, CancellationToken token)
    {
        bool studioActive = edit.IsStudioEnhancementActive();
        bool needsFace = studioActive || edit.SkinSoftening > 0.5;
        if (!needsFace) return null;

        DetectedFace? primaryFace = null;
        int fw = 0, fh = 0;
        if (FaceService.HasModelLoaded)
        {
            var (pw, ph, faces) = GetOrDetectFacesCached(asset, token);
            if (faces.Count > 0)
            {
                primaryFace = PortraitAutoFrameHelper.SelectFramingTarget(faces, pw, ph);
                fw = pw;
                fh = ph;
            }
        }

        CompactShadingMap? relightMap = null;
        if (studioActive && RelightingService.HasModelLoaded)
        {
            relightMap = ResolveRelightingShading(asset, preview, token);
        }

        if (primaryFace == null && relightMap == null)
            return null;

        return new PortraitProcessContext(primaryFace, fw, fh, relightMap);
    }

    private CompactShadingMap? ResolveRelightingShading(PhotoAsset asset, SKBitmap? preview, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!RelightingService.HasModelLoaded || string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath))
            return null;

        string key = $"{asset.FilePath}|{File.GetLastWriteTimeUtc(asset.FilePath).Ticks}|{RelightingService.ActiveModelName}";
        if (_relightShadingCache.TryGetValue(asset.Id, out var cached) && cached.key == key)
            return cached.shading;

        using var ownedProxy = preview == null ? ImageCache.GetReadOnlyPreviewProxy(asset, 1300) : null;
        var shading = RelightingService.ComputeStudioShadingMap(preview ?? ownedProxy!, token);
        token.ThrowIfCancellationRequested();
        _relightShadingCache[asset.Id] = (key, shading);
        return shading;
    }

    private string MattingSourceKey(PhotoAsset asset) =>
        $"{asset.FilePath}|{File.GetLastWriteTimeUtc(asset.FilePath).Ticks}|{MattingService.ActiveModelName}|{FaceService.ActiveModelName}|{FaceService.ActiveLandmarkModelName}|{asset.EditState.QuarterTurns}";

    private readonly record struct PortraitDiagnosticKey(
        string SourceKey, bool ReplaceBackground, string BackgroundColor, bool HasFormalSuit, bool StudioActive);

    private Dictionary<string, PortraitQualityDiagnostics> InspectPortraits(
        IEnumerable<PrintSheet> sheets, IReadOnlyDictionary<string, PhotoAsset> assets, CancellationToken token)
    {
        var diagnostics = new Dictionary<string, PortraitQualityDiagnostics>();
        if (!FaceService.HasModelLoaded) return diagnostics;

        var ids = sheets.Where(sheet => sheet.Scenario == LayoutScenario.IdPhoto)
            .SelectMany(sheet => sheet.Slots).Select(slot => slot.PhotoAssetId).OfType<string>().Distinct();
        foreach (string id in ids)
        {
            token.ThrowIfCancellationRequested();
            if (!assets.TryGetValue(id, out var asset)) continue;
            var edit = asset.EditState;
            var key = new PortraitDiagnosticKey(MattingSourceKey(asset), edit.EnableAiBackgroundReplace,
                edit.ReplacementBackgroundHex, edit.FormalSuit != FormalSuitStyle.None, edit.IsStudioEnhancementActive());
            if (!_portraitDiagnosticsCache.TryGetValue(id, out var entry) || entry.Key != key)
            {
                var (w, h, faces) = GetOrDetectFacesCached(asset, token);
                var face = PortraitAutoFrameHelper.SelectFramingTarget(faces, w, h);
                PortraitQualityDiagnostics? diagnosis = null;
                if (face != null)
                {
                    using var proxy = ImageCache.GetReadOnlyPreviewProxy(asset, 1300);
                    var matte = ResolvePortraitMatte(asset, key.SourceKey, proxy, token);
                    diagnosis = PortraitComplianceAnalyzer.Analyze(proxy, face, matte, edit);
                }
                token.ThrowIfCancellationRequested();
                entry = (key, diagnosis);
                _portraitDiagnosticsCache[id] = entry;
            }
            if (entry.Value != null) diagnostics.Add(id, entry.Value);
        }
        return diagnostics;
    }

    private CompactAlphaMatte? ResolvePortraitMatte(PhotoAsset asset, string sourceKey, SKBitmap? preview = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!MattingService.HasModelLoaded) return null;
        if (_compactMatteCache.TryGetValue(asset.Id, out var cached) && cached.key == sourceKey) return cached.matte;

        // Use the same source resolution even when the page thumbnail requests the matte first.
        using var proxy = preview == null ? ImageCache.GetReadOnlyPreviewProxy(asset, 1300) : null;
        var (_, _, faces) = GetOrDetectFacesCached(asset, token);
        var matte = MattingService.ComputeCompactAlphaMatte(preview ?? proxy!, faces, token);
        token.ThrowIfCancellationRequested();
        if (matte == null) return null;
        _compactMatteCache[asset.Id] = (sourceKey, matte);
        return matte;
    }

    private static int ComputeRequiredExportLongEdge(PhotoAsset asset, IEnumerable<LayoutSlot> slots, int dpi)
    {
        int rawLong = Math.Max(asset.PixelWidth, asset.PixelHeight);
        if (rawLong <= 1800) return 0;

        double maxNeededPx = 0;
        double srcLong = rawLong;
        double srcShort = Math.Max(1, Math.Min(asset.PixelWidth, asset.PixelHeight));
        double srcRatio = srcLong / srcShort;

        foreach (var slot in slots)
        {
            double wMm = slot.WidthMm + slot.BleedMm * 2.0;
            double hMm = slot.HeightMm + slot.BleedMm * 2.0;
            if (slot.PolaroidStyle != null)
            {
                bool rotated = slot.IsQuarterTurnRotated;
                wMm = Math.Max(10, slot.WidthMm - (rotated
                    ? slot.PolaroidStyle.MarginTopMm + slot.PolaroidStyle.MarginBottomMm
                    : slot.PolaroidStyle.MarginLeftMm + slot.PolaroidStyle.MarginRightMm));
                hMm = Math.Max(10, slot.HeightMm - (rotated
                    ? slot.PolaroidStyle.MarginLeftMm + slot.PolaroidStyle.MarginRightMm
                    : slot.PolaroidStyle.MarginTopMm + slot.PolaroidStyle.MarginBottomMm));
            }

            double slotLongPx = Math.Max(wMm, hMm) / UnitConverter.MmPerInch * dpi;
            double slotShortPx = Math.Max(1.0, Math.Min(wMm, hMm) / UnitConverter.MmPerInch * dpi);
            double zoom = Math.Clamp(asset.EditState.Zoom * slot.SlotZoom, 1.0, 8.0);
            double slotRatio = slotLongPx / slotShortPx;
            double cropFactor = Math.Max(1.0, Math.Max(srcRatio / slotRatio, slotRatio / srcRatio));

            // 保留 >= 2x 超采样与 >= 1800px 安全下限，仅当物理冲印格位远小于原图时按需解码以加速导出
            double req = slotLongPx * zoom * cropFactor * 2.0;
            if (req > maxNeededPx) maxNeededPx = req;
        }

        if (maxNeededPx <= 0) return 0;
        int target = (int)Math.Ceiling(Math.Max(1800.0, maxNeededPx));
        return target >= rawLong * 0.72 ? 0 : target;
    }
}
