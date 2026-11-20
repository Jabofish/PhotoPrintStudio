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
    public double SlotRotationDegrees { get; set; } // 0 or 90
    public string Label { get; set; } = "";
    public string? PhotoAssetId { get; set; }
    public string? CustomCaption { get; set; }
    public string? IdPhotoSpecId { get; set; }
    public PolaroidSlotStyle? PolaroidStyle { get; set; }
    public FitMode? FitModeOverride { get; set; }
    public double SlotZoom { get; set; } = 1.0;
    public double SlotPanX { get; set; }
    public double SlotPanY { get; set; }

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

public sealed class LayoutTemplateDefinition
{
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
    public string TemplateKind { get; set; } = "auto_grid"; // auto_grid, id_mixed_1_2, id_mixed_split, six_2in1_full, six_2in1_border, six_2in1_vert, six_2in1_card_duplex, six_2in1_compare, polaroid_grid, free_grid

    public override string ToString() => Name;
}

public sealed class PrinterCalibrationProfile
{
    public PrinterCalibrationProfile Clone() => (PrinterCalibrationProfile)MemberwiseClone();
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
    public string Version { get; set; } = "2.0";
    public string ProjectName { get; set; } = "未命名冲印工程";
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public List<PhotoAsset> Assets { get; set; } = [];
    public List<PrintSheet> Sheets { get; set; } = [];
    public int ActiveSheetIndex { get; set; } = 0;
}
