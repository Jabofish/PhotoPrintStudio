namespace PhotoPrintStudio.Core.Models;

public sealed class PhotoAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public int ExifOrientation { get; set; } = 1;
    public DateTime? DateTaken { get; set; }
    public string CameraModel { get; set; } = "";
    public string ColorProfileName { get; set; } = "sRGB";
    public PhotoEditState EditState { get; set; } = new();

    public PhotoAsset CloneMetadataOnly() => new()
    {
        Id = Id,
        FilePath = FilePath,
        DisplayName = DisplayName,
        PixelWidth = PixelWidth,
        PixelHeight = PixelHeight,
        ExifOrientation = ExifOrientation,
        DateTaken = DateTaken,
        CameraModel = CameraModel,
        ColorProfileName = ColorProfileName,
        EditState = EditState.Clone()
    };
}

public enum PolaroidTextAlign
{
    Center,
    Left,
    Right
}

public sealed class PolaroidSlotStyle
{
    public string PolaroidSpecId { get; set; } = "pol_instax_mini";
    public string FrameColorHex { get; set; } = "#FFFFFF";
    public string TextColorHex { get; set; } = "#374151";
    public string CaptionTemplate { get; set; } = "{Date}";
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSizePt { get; set; } = 9.0;
    public PolaroidTextAlign TextAlignment { get; set; } = PolaroidTextAlign.Center;
    public bool ShowInnerShadow { get; set; } = true;
    public bool ShowPaperGrain { get; set; } = false;
    public double MarginTopMm { get; set; } = 4.0;
    public double MarginLeftMm { get; set; } = 4.0;
    public double MarginRightMm { get; set; } = 4.0;
    public double MarginBottomMm { get; set; } = 20.0;

    public static PolaroidSlotStyle FromSpec(PolaroidSpec spec) => new()
    {
        PolaroidSpecId = spec.Id,
        MarginTopMm = spec.MarginTopMm,
        MarginLeftMm = spec.MarginLeftMm,
        MarginRightMm = spec.MarginRightMm,
        MarginBottomMm = spec.MarginBottomMm
    };

    public bool HasPhotoArea(double widthMm, double heightMm, bool rotated)
    {
        if (rotated) (widthMm, heightMm) = (heightMm, widthMm);
        return widthMm > MarginLeftMm + MarginRightMm && heightMm > MarginTopMm + MarginBottomMm;
    }

    public PolaroidSlotStyle Clone() => new()
    {
        PolaroidSpecId = PolaroidSpecId,
        FrameColorHex = FrameColorHex,
        TextColorHex = TextColorHex,
        CaptionTemplate = CaptionTemplate,
        FontFamily = FontFamily,
        FontSizePt = FontSizePt,
        TextAlignment = TextAlignment,
        ShowInnerShadow = ShowInnerShadow,
        ShowPaperGrain = ShowPaperGrain,
        MarginTopMm = MarginTopMm,
        MarginLeftMm = MarginLeftMm,
        MarginRightMm = MarginRightMm,
        MarginBottomMm = MarginBottomMm
    };
}

public sealed class LayoutSlot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Index { get; set; }
    public double XMm { get; set; }
    public double YMm { get; set; }
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }
    public double BleedMm { get; set; }
    public double CornerRadiusMm { get; set; }
    public double SlotRotationDegrees { get; set; } // 0, 90, 180, 270
    public string Label { get; set; } = "";
    public string? PhotoAssetId { get; set; }
    public string? CustomCaption { get; set; }
    public string? IdPhotoSpecId { get; set; }
    public PolaroidSlotStyle? PolaroidStyle { get; set; }
    public FitMode? FitModeOverride { get; set; }
    public FitMode? TemplateFitMode { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public FitMode? EffectiveFitMode => FitModeOverride ?? TemplateFitMode;

    public double SlotZoom { get; set; } = 1.0;
    public double SlotPanX { get; set; }
    public double SlotPanY { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsQuarterTurnRotated => IsQuarterTurnRotation(SlotRotationDegrees);

    public static bool IsQuarterTurnRotation(double degrees)
    {
        double norm = ((degrees % 360.0) + 360.0) % 360.0;
        return Math.Abs(norm - 90.0) < 1.0 || Math.Abs(norm - 270.0) < 1.0;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidPhotoArea => PolaroidStyle?.HasPhotoArea(WidthMm, HeightMm, IsQuarterTurnRotated) ?? true;

    public LayoutSlot Clone() => new()
    {
        Id = Id,
        Index = Index,
        XMm = XMm,
        YMm = YMm,
        WidthMm = WidthMm,
        HeightMm = HeightMm,
        BleedMm = BleedMm,
        CornerRadiusMm = CornerRadiusMm,
        SlotRotationDegrees = SlotRotationDegrees,
        Label = Label,
        PhotoAssetId = PhotoAssetId,
        CustomCaption = CustomCaption,
        IdPhotoSpecId = IdPhotoSpecId,
        PolaroidStyle = PolaroidStyle?.Clone(),
        FitModeOverride = FitModeOverride,
        TemplateFitMode = TemplateFitMode,
        SlotZoom = SlotZoom,
        SlotPanX = SlotPanX,
        SlotPanY = SlotPanY
    };
}

public sealed class PrintSheet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SheetName { get; set; } = "第 1 页";
    public string PaperId { get; set; } = "paper_6inch";
    public double PaperWidthMm { get; set; } = 152.4;
    public double PaperHeightMm { get; set; } = 101.6;
    public bool IsLandscape { get; set; } = true;
    public LayoutScenario Scenario { get; set; } = LayoutScenario.IdPhoto;
    public string TemplateId { get; set; } = "";
    public bool IsCustomFreeform { get; set; }
    public CropMarkStyle CropMarkStyle { get; set; } = CropMarkStyle.OuterCornerTicks;
    public string CropMarkColorHex { get; set; } = "#888888";
    public double CropMarkThicknessMm { get; set; } = 0.15;
    public bool ShowCenterFoldTick { get; set; }
    public bool ShowSlugFooter { get; set; }
    public string SlugCustomText { get; set; } = "";
    public string CanvasBackgroundHex { get; set; } = "#FFFFFF";
    public bool AlignFlushToEdge { get; set; }
    public SheetLayoutSettings? LayoutSettings { get; set; }
    public List<LayoutSlot> Slots { get; set; } = [];

    public PrintSheet Clone(bool preserveId = false) => new()
    {
        Id = preserveId ? Id : Guid.NewGuid().ToString("N"),
        SheetName = SheetName,
        PaperId = PaperId,
        PaperWidthMm = PaperWidthMm,
        PaperHeightMm = PaperHeightMm,
        IsLandscape = IsLandscape,
        Scenario = Scenario,
        TemplateId = TemplateId,
        IsCustomFreeform = IsCustomFreeform,
        CropMarkStyle = CropMarkStyle,
        CropMarkColorHex = CropMarkColorHex,
        CropMarkThicknessMm = CropMarkThicknessMm,
        ShowCenterFoldTick = ShowCenterFoldTick,
        ShowSlugFooter = ShowSlugFooter,
        SlugCustomText = SlugCustomText,
        CanvasBackgroundHex = CanvasBackgroundHex,
        AlignFlushToEdge = AlignFlushToEdge,
        LayoutSettings = LayoutSettings,
        Slots = Slots.Select(s => s.Clone()).ToList()
    };
}

public sealed record SheetLayoutSettings(
    double MarginMm, double GapMm, double BleedMm, double CornerRadiusMm,
    int Rows, int Columns, string? IdSpecId, string? PolaroidSpecId, bool PolaroidSafeFit);

public sealed class CustomSlotDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double XMm { get; set; }
    public double YMm { get; set; }
    public double WidthMm { get; set; } = 60.0;
    public double HeightMm { get; set; } = 90.0;
    public double BleedMm { get; set; }
    public double CornerRadiusMm { get; set; }
    public double SlotRotationDegrees { get; set; } // 0, 90, 180, 270
    public string Label { get; set; } = "";
    public FitMode? FitModeOverride { get; set; }
    public string? IdPhotoSpecId { get; set; }
    public PolaroidSlotStyle? PolaroidStyle { get; set; }

    public CustomSlotDefinition Clone(bool preserveId = true) => new()
    {
        Id = preserveId ? Id : Guid.NewGuid().ToString("N"),
        XMm = XMm,
        YMm = YMm,
        WidthMm = WidthMm,
        HeightMm = HeightMm,
        BleedMm = BleedMm,
        CornerRadiusMm = CornerRadiusMm,
        SlotRotationDegrees = SlotRotationDegrees,
        Label = Label,
        FitModeOverride = FitModeOverride,
        IdPhotoSpecId = IdPhotoSpecId,
        PolaroidStyle = PolaroidStyle?.Clone()
    };

    public static CustomSlotDefinition FromLayoutSlot(LayoutSlot slot) => new()
    {
        Id = string.IsNullOrWhiteSpace(slot.Id) ? Guid.NewGuid().ToString("N") : slot.Id,
        XMm = Math.Round(slot.XMm, 3),
        YMm = Math.Round(slot.YMm, 3),
        WidthMm = Math.Round(slot.WidthMm, 3),
        HeightMm = Math.Round(slot.HeightMm, 3),
        BleedMm = Math.Round(slot.BleedMm, 3),
        CornerRadiusMm = Math.Round(slot.CornerRadiusMm, 3),
        SlotRotationDegrees = ((Math.Round(slot.SlotRotationDegrees / 90.0) * 90.0) % 360.0 + 360.0) % 360.0,
        Label = slot.Label,
        FitModeOverride = slot.TemplateFitMode,
        IdPhotoSpecId = slot.IdPhotoSpecId,
        PolaroidStyle = slot.PolaroidStyle?.Clone()
    };
}

public sealed class LayoutTemplateDefinition
{
    public const string KindCustomFreeform = "custom_freeform";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public LayoutScenario Scenario { get; set; }
    public string DefaultPaperId { get; set; } = "paper_6inch";
    public bool DefaultLandscape { get; set; } = true;
    public double DefaultMarginMm { get; set; } = 4.0;
    public double DefaultGapMm { get; set; } = 3.0;
    public double DefaultBleedMm { get; set; } = 0.0;
    public CropMarkStyle DefaultCropMarkStyle { get; set; } = CropMarkStyle.OuterCornerTicks;
    public string? IdPhotoSpecId { get; set; }
    public string? PolaroidSpecId { get; set; }
    public int CustomRows { get; set; } = 2;
    public int CustomCols { get; set; } = 2;
    public double CustomItemWidthMm { get; set; } = 60;
    public double CustomItemHeightMm { get; set; } = 40;
    public double CustomCornerRadiusMm { get; set; } = 0;
    public bool PolaroidSafeFit { get; set; } = false;
    public bool IsBuiltIn { get; set; } = true;
    public string TemplateKind { get; set; } = "auto_grid"; // auto_grid, id_mixed_1_2, id_mixed_split, six_2in1_full, six_2in1_border, six_2in1_left, six_2in1_vert, six_2in1_card_duplex, polaroid_grid, free_grid, custom_freeform
    public double? CustomPaperWidthMm { get; set; }
    public double? CustomPaperHeightMm { get; set; }
    public string? CustomCanvasBackgroundHex { get; set; }
    public List<CustomSlotDefinition>? CustomSlots { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCustomFreeform => string.Equals(TemplateKind, KindCustomFreeform, StringComparison.OrdinalIgnoreCase);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowCenterFoldTickByDefault => DefaultCropMarkStyle == CropMarkStyle.CenterFoldTicks ||
        (!IsCustomFreeform && Scenario == LayoutScenario.SixInchTwoInOne && TemplateKind != "six_2in1_left");

    public LayoutTemplateDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        Subtitle = Subtitle,
        Scenario = Scenario,
        DefaultPaperId = DefaultPaperId,
        DefaultLandscape = DefaultLandscape,
        DefaultMarginMm = DefaultMarginMm,
        DefaultGapMm = DefaultGapMm,
        DefaultBleedMm = DefaultBleedMm,
        DefaultCropMarkStyle = DefaultCropMarkStyle,
        IdPhotoSpecId = IdPhotoSpecId,
        PolaroidSpecId = PolaroidSpecId,
        CustomRows = CustomRows,
        CustomCols = CustomCols,
        CustomItemWidthMm = CustomItemWidthMm,
        CustomItemHeightMm = CustomItemHeightMm,
        CustomCornerRadiusMm = CustomCornerRadiusMm,
        PolaroidSafeFit = PolaroidSafeFit,
        IsBuiltIn = IsBuiltIn,
        TemplateKind = TemplateKind,
        CustomPaperWidthMm = CustomPaperWidthMm,
        CustomPaperHeightMm = CustomPaperHeightMm,
        CustomCanvasBackgroundHex = CustomCanvasBackgroundHex,
        CustomSlots = CustomSlots?.Select(s => s.Clone()).ToList()
    };

    public override string ToString() => Name;
}

public sealed record PrinterGeometryCalibration(double OffsetX = 0, double OffsetY = 0,
    double ScaleX = 100, double ScaleY = 100, double Overscan = 100, double DuplexX = 0, double DuplexY = 0,
    bool HasMeasuredScale = false);

public sealed class PrinterCalibrationProfile
{
    public PrinterCalibrationProfile Clone()
    {
        var copy = (PrinterCalibrationProfile)MemberwiseClone();
        copy.GeometryByOutput = new(GeometryByOutput);
        return copy;
    }

    public Dictionary<string, PrinterGeometryCalibration> GeometryByOutput { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string? GeometryTarget { get; set; }

    public PrinterGeometryCalibration CaptureGeometry() => new(OffsetXMm, OffsetYMm,
        ScaleXPercent, ScaleYPercent, OverscanCompensationPercent, DuplexBackOffsetXMm, DuplexBackOffsetYMm);

    public PrinterCalibrationProfile ForOutput(string key)
    {
        var result = Clone();
        var geometry = GeometryByOutput.GetValueOrDefault(key) ?? new PrinterGeometryCalibration();
        result.OffsetXMm = geometry.OffsetX;
        result.OffsetYMm = geometry.OffsetY;
        result.ScaleXPercent = geometry.ScaleX;
        result.ScaleYPercent = geometry.ScaleY;
        result.OverscanCompensationPercent = geometry.Overscan;
        result.DuplexBackOffsetXMm = geometry.DuplexX;
        result.DuplexBackOffsetYMm = geometry.DuplexY;
        result.GeometryTarget = key;
        return result;
    }

    public (double ScaleX, double ScaleY, double OffsetX, double OffsetY) GetGeometryTransform(bool backSide = false)
    {
        double overscan = Math.Clamp(OverscanCompensationPercent / 100.0, 0.85, 1.15);
        return (ScaleXPercent / 100.0 * overscan, ScaleYPercent / 100.0 * overscan,
            OffsetXMm + (backSide ? DuplexBackOffsetXMm : 0), OffsetYMm + (backSide ? DuplexBackOffsetYMm : 0));
    }
    public string PrinterName { get; set; } = "默认打印机";
    public string PrinterKey { get; set; } = "default";
    public double OffsetXMm { get; set; } = 0.0;
    public double OffsetYMm { get; set; } = 0.0;
    public double ScaleXPercent { get; set; } = 100.0;
    public double ScaleYPercent { get; set; } = 100.0;
    public double OverscanCompensationPercent { get; set; } = 100.0; // 热升华无边距过扫描补偿 e.g. 97.5%
    public double DuplexBackOffsetXMm { get; set; } = 0.0;
    public double DuplexBackOffsetYMm { get; set; } = 0.0;
    public double BrightnessCompensation { get; set; } = 0.0;
    public double GammaCompensation { get; set; } = 1.0;
    public double TemperatureCompensation { get; set; } = 0.0;
    public bool DefaultBorderless { get; set; } = false;
    public string CustomIccProfilePath { get; set; } = "";
    public string SavedDevModeBase64 { get; set; } = "";
    public double SafeMarginMm { get; set; } = 4.0;
}

public enum SoftProofRenderingIntent
{
    Perceptual = 0,
    RelativeColorimetric = 1,
    Saturation = 2,
    AbsoluteColorimetric = 3
}

public enum AppearanceMode
{
    System,
    Light,
    Dark
}

public sealed class AppPortableConfig
{
    public AppearanceMode? Appearance { get; set; }
    public bool? IsDarkTheme { get; set; }
    public const int StandardOutputDpi = 300;
    public const int StandardJpegQuality = 100;
    public int DefaultExportDpi { get; set; } = StandardOutputDpi;
    public int DefaultPrintDpi { get; set; } = StandardOutputDpi;
    public int DefaultJpegQuality { get; set; } = StandardJpegQuality;
    public double OutputSharpeningAmount { get; set; } = 0.0; // 0 .. 100（打印/导出最终像素尺度输出锐化）
    public string LastPrinterName { get; set; } = "";
    public string DefaultPrinterName { get; set; } = "";
    public bool UseLastPrintedPrinter { get; set; } = true;
    public bool ShowVirtualPrinters { get; set; }
    public string SoftProofIccFile { get; set; } = "";
    public bool EnableSoftProofing { get; set; } = false;
    public SoftProofRenderingIntent SoftProofIntent { get; set; } = SoftProofRenderingIntent.RelativeColorimetric;
    public bool SoftProofSimulatePaperWhite { get; set; } = false;
    public bool SoftProofGamutWarning { get; set; } = false;
    public bool EmbedIccProfileOnExport { get; set; } = true;
    public bool ShowRulers { get; set; } = true;
    public bool ShowSafeMarginOverlay { get; set; } = true;
    public bool ShowBleedOverlay { get; set; } = true;
    public bool ShowIdGuidelines { get; set; } = true;
    public bool ShowFaceLandmarksOverlay { get; set; }
    public bool ShowClippingOverlay { get; set; }
    public int AutoRecoverySeconds { get; set; } = 30;
    public string PreferredMattingModel { get; set; } = "";
    public HotFolderWatchSettings HotFolderWatch { get; set; } = new();
    public List<PaperSizeSpec> CustomPaperSizes { get; set; } = [];
    public List<PolaroidSpec> CustomPolaroidSpecs { get; set; } = [];
}

public sealed class PrintProject
{
    public string Version { get; set; } = "2.1";
    public string ProjectName { get; set; } = "未命名冲印工程";
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public List<PhotoAsset> Assets { get; set; } = [];
    public List<PrintSheet> Sheets { get; set; } = [];
    public int ActiveSheetIndex { get; set; } = 0;
}
