using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoPrintStudio.App.Helpers;
using PhotoPrintStudio.App.Printing;
using PhotoPrintStudio.App.Services;
using PhotoPrintStudio.App.Themes;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Persistence;
using PhotoPrintStudio.Core.Portable;
using PhotoPrintStudio.Core.Preflight;
using PhotoPrintStudio.Core.Rendering;
using SkiaSharp;

namespace PhotoPrintStudio.App.ViewModels;

public enum CanvasViewMode
{
    SheetOverview,
    SingleRetouchCrop,
    SplitCompare
}

public enum ImportPlacement
{
    PoolOnly,
    FillEmptySlots,
    Paginate,
    OnePhotoPerPage,
    SmartPaginate
}

public readonly record struct ImportArrangement(ImportPlacement Placement,
    SlotFillOrder Order = SlotFillOrder.Template, FitMode? Fit = null, bool AutoRotate = false);

public sealed record ProjectSnapshot(
    List<PhotoAsset> Assets,
    List<PrintSheet> Sheets,
    int ActiveSheetIndex,
    string? SelectedSlotId,
    string? SelectedAssetId);

public partial class MainViewModel : ObservableObject, IDisposable
{
    public PortablePaths Paths { get; }
    public AppLogger Logger { get; }
    public IWorkspaceStore DataStore { get; }
    public ImageCacheManager ImageCache { get; }
    public PortraitMattingService MattingService { get; }
    public FaceDetectionService FaceService { get; }
    public PortraitRelightingService RelightingService { get; }
    public IPrintService PrintService { get; }
    public IDialogService Dialogs { get; }
    public IUiDispatcher UiDispatcher { get; }
    public PreviewCacheCoordinator PreviewCache { get; } = new();

    private readonly UndoRedoStack<ProjectSnapshot> _undoStack = new(60);
    private readonly ConcurrentDictionary<string, (string key, CompactAlphaMatte matte)> _compactMatteCache = new();
    private readonly ConcurrentDictionary<string, (string key, int width, int height, IReadOnlyList<DetectedFace> faces)> _faceDetectionCache = new();
    private readonly ConcurrentDictionary<string, (string key, CompactShadingMap? shading)> _relightShadingCache = new();
    private readonly Dictionary<string, HistogramData> _latestHistograms = new();
    private readonly ConcurrentDictionary<string, (PortraitDiagnosticKey Key, PortraitQualityDiagnostics? Value)> _portraitDiagnosticsCache = new();
    private Dictionary<string, PortraitQualityDiagnostics> _latestPortraitDiagnostics = new();
    private readonly DispatcherTimer? _autoRecoveryTimer;
    private readonly DispatcherTimer? _toastTimer;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _autoRecoveryInFlight;
    private readonly object _autoRecoveryGate = new();
    private long _autoRecoveryVersion;
    private Task? _startupInitTask;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _hqSettleCts;
    private CancellationTokenSource? _workspaceThumbnailCts;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private long _renderSequence;
    private long _lastRenderScheduleTickMs;
    private bool _renderQueuedOnDispatcher;
    private string? _cachedIccPath;
    private byte[]? _cachedIccBytes;
    private PhotoEditState? _copiedColorEdit;
    private ProjectSnapshot? _pendingUndoSnapshot;
    private bool _suppressLayoutRebuild;

    // === 主题与全局状态 ===
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private AppearanceMode _appearance;
    public IReadOnlyDictionary<AppearanceMode, string> AppearanceOptions { get; } = new Dictionary<AppearanceMode, string>
    {
        [AppearanceMode.System] = "跟随系统",
        [AppearanceMode.Light] = "浅色",
        [AppearanceMode.Dark] = "深色"
    };
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _portablePathDisplay = "";
    [ObservableProperty] private int _rightTabIndex = 0;
    [ObservableProperty] private int _colorSubTabIndex = 0; // 0=基础光影色彩, 1=曲线与HSL, 2=裁剪细节与证件照

    // === 清晰编辑上下文与 Toast 反馈 ===
    [ObservableProperty] private string _activeEditTargetTitle = "尚未选择编辑对象";
    [ObservableProperty] private string _activeEditTargetMeta = "请导入照片或点击下方“样例图”体验";
    [ObservableProperty] private bool _isEditingUnassignedAsset;
    [ObservableProperty] private bool _canForkIndependentAsset;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAutoFramePhoto))]
    private bool _hasSelectedAsset;
    [ObservableProperty] private bool _hasSelectedSlot;
    [ObservableProperty] private bool _hasPreflightIssues;
    [ObservableProperty] private string _activeToolBannerText = "";
    [ObservableProperty] private bool _hasActiveToolBanner;
    [ObservableProperty] private string _toastMessage = "";
    [ObservableProperty] private bool _isToastVisible;

    // === 画布预览与交互状态 ===
    [ObservableProperty] private WriteableBitmap? _canvasPreviewBitmap;
    [ObservableProperty] private double _canvasZoom = 1.0;
    [ObservableProperty] private CanvasViewMode _viewMode = CanvasViewMode.SheetOverview;
    [ObservableProperty] private bool _isHoldingCompare;
    [ObservableProperty] private double _splitCompareRatio = 0.5;
    [ObservableProperty] private bool _showRulers = true;
    [ObservableProperty] private bool _showSafeMarginOverlay = true;
    [ObservableProperty] private bool _showBleedOverlay = true;
    [ObservableProperty] private bool _showIdGuidelines = true;
    [ObservableProperty] private bool _showFaceLandmarksOverlay;
    [ObservableProperty] private bool _showClippingOverlay = false;
    [ObservableProperty] private bool _isTwoPointStraightenActive = false;

    // === 素材池与多页队列 ===
    public ObservableCollection<AssetItemViewModel> Assets { get; } = [];
    public ObservableCollection<PrintSheet> Sheets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoToPreviousPage))]
    [NotifyPropertyChangedFor(nameof(CanGoToNextPage))]
    private int _activeSheetIndex;
    public bool HasMultiplePages => Sheets.Count > 1;
    public bool CanGoToPreviousPage => HasMultiplePages && ActiveSheetIndex > 0;
    public bool CanGoToNextPage => ActiveSheetIndex < Sheets.Count - 1;
    [ObservableProperty] private PrintSheet? _activeSheet;
    [ObservableProperty] private LayoutSlot? _selectedSlot;
    [ObservableProperty] private AssetItemViewModel? _selectedAsset;
    [ObservableProperty] private HistogramData? _activeHistogram;
    [ObservableProperty] private string _clippingWarningText = "";

    // === 场景、纸张与模板库 ===
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDailyPrintScenario))]
    [NotifyPropertyChangedFor(nameof(IsSinglePhotoLayout))]
    [NotifyPropertyChangedFor(nameof(CanAutoFramePhoto))]
    private LayoutScenario _activeScenario = LayoutScenario.IdPhoto;
    public bool IsDailyPrintScenario => TemplateCategory(ActiveScenario) == LayoutScenario.SinglePhoto;
    public bool IsSinglePhotoLayout => ActiveScenario == LayoutScenario.SinglePhoto;
    public ObservableCollection<LayoutTemplateDefinition> FilteredTemplates { get; } = [];
    private List<LayoutTemplateDefinition> _allTemplates = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditGridSize))]
    [NotifyPropertyChangedFor(nameof(IsAutomaticPolaroidLayout))]
    private LayoutTemplateDefinition? _selectedTemplate;
    public bool CanEditGridSize => SelectedTemplate?.TemplateKind is "free_grid" or "polaroid_fixed_grid";
    public bool IsAutomaticPolaroidLayout => SelectedTemplate?.TemplateKind == "polaroid_grid";

    private readonly List<PaperSizeSpec> _customPaperSizes = [];
    private readonly List<PolaroidSpec> _customPolaroidSpecs = [];

    public ObservableCollection<PaperSizeSpec> PaperSizes { get; } = [];
    public IReadOnlyList<IdPhotoSpec> IdPhotoSpecs => IdPhotoSpec.BuiltInSpecs;
    public ObservableCollection<PolaroidSpec> PolaroidSpecs { get; } = [];
    public IReadOnlyDictionary<CropMarkStyle, string> CropMarkStyles { get; } = new Dictionary<CropMarkStyle, string>
    {
        [CropMarkStyle.None] = "无边缘标记",
        [CropMarkStyle.OuterCornerTicks] = "四角短线",
        [CropMarkStyle.DashedBorder] = "虚线裁切框",
        [CropMarkStyle.SolidThinBorder] = "细实线裁切框",
        [CropMarkStyle.WhiteBackgroundGuide] = "白底裁切角标"
    };
    public IReadOnlyDictionary<PolaroidTextAlign, string> PolaroidTextAlignments { get; } = new Dictionary<PolaroidTextAlign, string>
    {
        [PolaroidTextAlign.Left] = "左对齐",
        [PolaroidTextAlign.Center] = "居中",
        [PolaroidTextAlign.Right] = "右对齐"
    };
    [ObservableProperty] private int _unplacedPhotoCount;
    public IReadOnlyList<int> DpiOptions { get; } = [300, 600];

    // === 排版控制属性 ===
    [ObservableProperty] private PaperSizeSpec _selectedPaper = PaperSizeSpec.BuiltInPapers[0];
    [ObservableProperty] private bool _isLandscape = true;
    [ObservableProperty] private double _layoutMarginMm = 6.0;
    [ObservableProperty] private double _layoutGapMm = 4.0;
    [ObservableProperty] private double _layoutBleedMm = 0.0;
    [ObservableProperty] private double _layoutCornerRadiusMm = 0.0;
    [ObservableProperty] private CropMarkStyle _selectedCropMarkStyle = CropMarkStyle.OuterCornerTicks;
    [ObservableProperty] private bool _showCenterFoldTick = false;
    [ObservableProperty] private bool _showSlugFooter = false;
    [ObservableProperty] private string _canvasBackgroundHex = "#FFFFFF";
    [ObservableProperty] private IdPhotoSpec _selectedIdSpec = IdPhotoSpec.BuiltInSpecs[0];
    [ObservableProperty] private string _autoPackInfoText = "";
    [ObservableProperty] private int _customGridRows = 2;
    [ObservableProperty] private int _customGridCols = 2;

    // === 拍立得样式专属属性 ===
    [ObservableProperty] private PolaroidSpec _selectedPolaroidSpec = PolaroidSpec.BuiltInSpecs[0];
    [ObservableProperty] private bool _polaroidSafeFit = false;
    [ObservableProperty] private string _polaroidFrameColorHex = "#FFFFFF";
    [ObservableProperty] private string _polaroidTextColorHex = "#374151";
    [ObservableProperty] private string _polaroidCaptionTemplate = "{Date}";
    [ObservableProperty] private string _polaroidFontFamily = "Microsoft YaHei UI";
    [ObservableProperty] private double _polaroidFontSizePt = 9.0;
    [ObservableProperty] private PolaroidTextAlign _polaroidTextAlign = PolaroidTextAlign.Center;
    [ObservableProperty] private bool _polaroidInnerShadow = true;
    [ObservableProperty] private bool _polaroidPaperGrain = false;
    [ObservableProperty] private bool _isPolaroidControlsVisible = false;
    public IReadOnlyList<string> AvailableFonts { get; } =
        System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(n => n)
            .ToList();

    // === 印前预检、打印校准与导出 ===
    [ObservableProperty] private PreflightReport _preflightReport = new(PreflightSeverity.Ok, []);
    public ObservableCollection<string> InstalledPrinters { get; } = [];
    public sealed record PrinterChoice(string Name, bool IsVirtual)
    {
        public string Group => IsVirtual ? "虚拟打印机" : "打印机";
    }
    public ObservableCollection<PrinterChoice> PrinterChoices { get; } = [];
    public IReadOnlyDictionary<string, string> DefaultPrinterChoices => MakeDefaultPrinterChoices();
    [ObservableProperty] private string _selectedPrinterName = "";
    [ObservableProperty] private bool _useLastPrintedPrinter = true;
    [ObservableProperty] private bool _showVirtualPrinters;
    private string _defaultPrinterName = "";
    private string _lastPrintedPrinterName = "";
    private IReadOnlyList<string> _allPrinterNames = [];
    private string? _windowsDefaultPrinterName;

    public string DefaultPrinterName
    {
        get => _defaultPrinterName;
        set
        {
            // 设备列表更新时，下拉框会暂时清除选中项。空字符串是可选的 Windows 默认项。
            if (value is not null) SetProperty(ref _defaultPrinterName, value);
        }
    }
    [ObservableProperty] private PrinterCalibrationProfile _currentCalibration = new();
    public bool PrinterBorderless
    {
        get => CurrentCalibration.DefaultBorderless;
        set
        {
            if (CurrentCalibration.DefaultBorderless == value) return;
            CurrentCalibration.DefaultBorderless = value;
            OnPropertyChanged();
            DataStore.SavePrinterProfile(CurrentCalibration);
            UpdatePreflight();
            ScheduleRender();
        }
    }
    [ObservableProperty] private int _printCopies = 1;
    [ObservableProperty] private int _printDpi = AppPortableConfig.StandardOutputDpi;
    [ObservableProperty] private int _exportDpi = AppPortableConfig.StandardOutputDpi;
    [ObservableProperty] private int _jpegQuality = AppPortableConfig.StandardJpegQuality;
    [ObservableProperty] private double _outputSharpeningAmount = 0.0;
    [ObservableProperty] private bool _embedIccProfileOnExport = true;
    [ObservableProperty] private bool _enableSoftProofing = false;
    [ObservableProperty] private SoftProofRenderingIntent _softProofIntent = SoftProofRenderingIntent.RelativeColorimetric;
    [ObservableProperty] private bool _softProofSimulatePaperWhite = false;
    [ObservableProperty] private bool _softProofGamutWarning = false;
    [ObservableProperty] private string _selectedIccPath = "";
    private int _autoRecoverySeconds = 30;
    public IReadOnlyDictionary<SoftProofRenderingIntent, string> SoftProofIntentOptions { get; } = new Dictionary<SoftProofRenderingIntent, string>
    {
        [SoftProofRenderingIntent.Perceptual] = "感知 (Perceptual)",
        [SoftProofRenderingIntent.RelativeColorimetric] = "相对比色 (Relative)",
        [SoftProofRenderingIntent.Saturation] = "饱和度 (Saturation)",
        [SoftProofRenderingIntent.AbsoluteColorimetric] = "绝对比色 (Absolute)"
    };
    public ObservableCollection<string> AvailableIccFiles { get; } = [];
    [ObservableProperty] private string _printerHardwareSummary = "未选择打印机";

    public ObservableCollection<ColorPresetItem> Presets { get; } = [];
    [ObservableProperty] private ColorPresetItem? _selectedPreset;
    public ObservableCollection<FormalSuitPresetOption> AvailableFormalSuitOptions { get; } = [];
    public ObservableCollection<string> AvailableAiModels { get; } = [];
    [ObservableProperty] private string? _selectedAiModelName;
    private bool _suppressAiModelSwitch;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectAiModel))]
    [NotifyCanExecuteChangedFor(nameof(RefreshAiModelsCommand))]
    private bool _isAiModelLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectAiModel))]
    private bool _hasAiMattingModel;
    [ObservableProperty] private string _aiMattingStatusText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAutoFramePhoto))]
    private bool _hasFaceDetectionModel;
    [ObservableProperty] private string _faceDetectionStatusText = "";
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private bool _hasCopiedColorEdit;
    internal bool IsStartupCancelled { get; }

    static MainViewModel() => ExifAndColorHelper.TiffDecoder = SkiaWpfInterop.DecodeTiff;

    public MainViewModel(
        bool checkRecovery = true,
        PortablePaths? paths = null,
        IWorkspaceStore? dataStore = null,
        IPrintService? printService = null,
        IDialogService? dialogs = null,
        IUiDispatcher? uiDispatcher = null,
        bool loadBackgroundServices = true)
    {
        Paths = paths ?? new PortablePaths();
        Logger = new AppLogger(Paths);
        DataStore = dataStore ?? new PortableDataStore(Paths, Logger);
        ImageCache = new ImageCacheManager();
        MattingService = new PortraitMattingService(Paths, Logger, autoLoad: false);
        FaceService = new FaceDetectionService(Paths, Logger, autoLoad: false);
        RelightingService = new PortraitRelightingService(Paths, Logger, autoLoad: false);
        RefreshFormalSuitOptions();
        PrintService = printService ?? new WindowsPrintService();
        Dialogs = dialogs ?? new WpfDialogService();
        UiDispatcher = uiDispatcher ?? new WpfUiDispatcher();

        PortablePathDisplay = Paths.DataDirectory;

        var cfg = DataStore.LoadConfig();
        _lastPrintedPrinterName = cfg.LastPrinterName;
        _defaultPrinterName = cfg.DefaultPrinterName;
        _useLastPrintedPrinter = cfg.UseLastPrintedPrinter;
        _showVirtualPrinters = cfg.ShowVirtualPrinters;
        _appearance = cfg.Appearance ?? (cfg.IsDarkTheme switch
        {
            true => AppearanceMode.Dark,
            false => AppearanceMode.Light,
            null => AppearanceMode.System
        });
        RefreshAppearance();
        PrintDpi = cfg.DefaultPrintDpi;
        ExportDpi = cfg.DefaultExportDpi;
        JpegQuality = cfg.DefaultJpegQuality;
        OutputSharpeningAmount = Math.Clamp(cfg.OutputSharpeningAmount, 0, 100);
        EmbedIccProfileOnExport = cfg.EmbedIccProfileOnExport;
        ShowRulers = cfg.ShowRulers;
        ShowSafeMarginOverlay = cfg.ShowSafeMarginOverlay;
        ShowBleedOverlay = cfg.ShowBleedOverlay;
        ShowIdGuidelines = cfg.ShowIdGuidelines;
        ShowFaceLandmarksOverlay = cfg.ShowFaceLandmarksOverlay;
        ShowClippingOverlay = cfg.ShowClippingOverlay;
        EnableSoftProofing = cfg.EnableSoftProofing;
        SoftProofIntent = cfg.SoftProofIntent;
        SoftProofSimulatePaperWhite = cfg.SoftProofSimulatePaperWhite;
        SoftProofGamutWarning = cfg.SoftProofGamutWarning;
        _autoRecoverySeconds = Math.Clamp(cfg.AutoRecoverySeconds, 10, 600);

        if (!File.Exists(Paths.ConfigFilePath))
            DataStore.SaveConfig(cfg);

        Logger.Info("光影印坊 Portable 启动完成");

        Sheets.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMultiplePages));
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));
        };

        _suppressLayoutRebuild = true;
        try
        {
            ReloadCustomPhysicalSpecs(cfg);
            _allTemplates = [.. LayoutEngine.BuiltInTemplates, .. DataStore.LoadCustomTemplates()];
            ReloadPresets();
            if (Presets.Count > 0) SelectedPreset = Presets[0];
            ReloadIccProfiles(cfg.SoftProofIccFile);
            InitializeHotFolderService(cfg.HotFolderWatch);
        }
        finally
        {
            _suppressLayoutRebuild = false;
        }

        AiMattingStatusText = "正在后台初始化 AI 抠图引擎...";
        FaceDetectionStatusText = "正在后台加载人脸关键点模型...";

        // 初始默认页：6寸 8张一寸证件照（仅执行一次初始排版构建）
        FilterTemplatesByScenario(LayoutScenario.IdPhoto, selectDefault: true);
        _savedWorkspace = WorkspaceContent();

        if (loadBackgroundServices)
        {
            IsAiModelLoading = true;
            _startupInitTask = InitializeBackgroundServicesAsync(
                string.IsNullOrWhiteSpace(cfg.PreferredMattingModel) ? PortraitMattingService.PasPhotoMattingModelFile : cfg.PreferredMattingModel,
                _disposeCts.Token);
        }

        UpdateActiveTargetContext();
        UpdateToolBanner();

        IsStartupCancelled = checkRecovery && !ChooseStartupWorkspace();
        if (!IsStartupCancelled && Application.Current != null)
        {
            _autoRecoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_autoRecoverySeconds) };
            _autoRecoveryTimer.Tick += (_, _) => SaveAutoRecoverySnapshot();
            _autoRecoveryTimer.Start();

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                IsToastVisible = false;
            };
        }
    }

    public void ShowToast(string message)
    {
        StatusText = message;
        ToastMessage = message;
        IsToastVisible = true;
        _toastTimer?.Stop();
        _toastTimer?.Start();
    }

    [RelayCommand]
    private void OpenPortableDataFolder()
    {
        Paths.EnsureDirectoriesExist();
        Dialogs.OpenPathInShell(Paths.DataDirectory);
    }

    [RelayCommand]
    private void AssignSelectedAssetToSelectedSlot()
    {
        if (SelectedAsset == null) return;
        var slot = SelectedSlot ?? ActiveSheet?.Slots.FirstOrDefault();
        if (slot == null) return;
        AssignAssetToSlot(SelectedAsset, slot);
        ShowToast($"已将 {SelectedAsset.DisplayName} 放入格位 #{slot.Index + 1}");
    }

    [RelayCommand]
    private void ForkIndependentAssetForSelectedSlot()
    {
        if (SelectedSlot == null || string.IsNullOrEmpty(SelectedSlot.PhotoAssetId)) return;
        var origVm = Assets.FirstOrDefault(a => a.Id == SelectedSlot.PhotoAssetId);
        if (origVm == null) return;

        PushUndoSnapshot();
        var clonedAsset = origVm.Asset.CloneMetadataOnly();
        clonedAsset.Id = Guid.NewGuid().ToString("N");
        clonedAsset.DisplayName = $"{origVm.DisplayName} (格位#{SelectedSlot.Index + 1}副本)";

        var newVm = new AssetItemViewModel(clonedAsset, origVm.Thumbnail);
        Assets.Add(newVm);
        SelectedSlot.PhotoAssetId = clonedAsset.Id;
        UpdateAssetUsageCounts();
        SelectSlot(SelectedSlot);
        UpdatePreflight();
        ScheduleRender();
        ShowToast($"已为格位 #{SelectedSlot.Index + 1} 拆分独立副本，可单独调色裁剪");
    }

    public void ReloadTemplates()
    {
        _allTemplates = [.. LayoutEngine.BuiltInTemplates, .. DataStore.LoadCustomTemplates()];
        FilterTemplatesByScenario(ActiveScenario, selectDefault: false);
    }

    public void ReloadPresets()
    {
        Presets.Clear();
        foreach (var p in DataStore.LoadAllPresets())
            Presets.Add(p);
    }

    public void ReloadIccProfiles(string? preferredPath = null)
    {
        AvailableIccFiles.Clear();
        foreach (var f in DataStore.ListIccProfiles())
            AvailableIccFiles.Add(f);
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
            SelectedIccPath = preferredPath;
    }

    public void ImportIccProfile(string sourcePath)
    {
        try
        {
            byte[] iccBytes = File.ReadAllBytes(sourcePath);
            if (!ExifAndColorHelper.SupportsSoftProofIcc(iccBytes))
            {
                ShowToast("此色彩配置无法用于软打样，请检查是否为有效的 RGB 或 CMYK ICC 配置文件。");
                return;
            }
            var info = WindowsIccEngine.InspectProfileHeader(iccBytes);
            Paths.EnsureDirectoriesExist();
            string target = Path.Combine(Paths.IccDirectory, Path.GetFileName(sourcePath));
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(target) && !Dialogs.Confirm("已有同名色彩配置，是否替换？", "导入相纸色彩配置")) return;
                File.Copy(sourcePath, target, overwrite: true);
            }
            ReloadIccProfiles(target);
            ShowToast($"已导入 {info.ColorSpace} 色彩配置（{info.EngineName}），可开启软打样预览。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"无法导入色彩配置：{ex.Message}");
        }
    }

    private async Task InitializeBackgroundServicesAsync(string? preferredMattingModel, CancellationToken token)
    {
        void FinishStartupModels()
        {
            if (token.IsCancellationRequested) return;
            ApplyAiModelStatusesToUi();
            IsAiModelLoading = false;
            if (Assets.Count > 0) ScheduleRender();
        }

        try
        {
            IReadOnlyList<string> printers = [];
            string? windowsDefault = null;

            await Task.Run(() =>
            {
                if (token.IsCancellationRequested) return;
                Parallel.Invoke(
                    () =>
                    {
                        if (!token.IsCancellationRequested)
                            MattingService.RefreshAvailableModels(preferredMattingModel);
                    },
                    () =>
                    {
                        if (!token.IsCancellationRequested)
                            FaceService.RefreshAvailableModels();
                    },
                    () =>
                    {
                        if (!token.IsCancellationRequested)
                            RelightingService.RefreshAvailableModels();
                    },
                    () =>
                    {
                        if (token.IsCancellationRequested) return;
                        printers = PrintService.GetInstalledPrinters();
                        if (printers.Count > 0) windowsDefault = PrintService.GetDefaultPrinterName();
                    });
            }, token);

            if (token.IsCancellationRequested) return;

            var dispatcher = Application.Current?.Dispatcher;
            void ApplyStartupResults()
            {
                if (token.IsCancellationRequested) return;

                _allPrinterNames = printers;
                _windowsDefaultPrinterName = windowsDefault;
                ApplyPrinterChoices(SelectedPrinterName);

                FinishStartupModels();
            }

            if (dispatcher != null && !dispatcher.CheckAccess())
                _ = dispatcher.BeginInvoke(ApplyStartupResults);
            else
                ApplyStartupResults();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("后台初始化打印与 AI 模型服务异常", ex);
            UiDispatcher.BeginInvoke(FinishStartupModels);
        }
    }

    private bool _suppressPrinterInspectOnSelect;

    public void RefreshPrinters(string? preferredPrinter = null)
    {
        _allPrinterNames = PrintService.GetInstalledPrinters();
        _windowsDefaultPrinterName = _allPrinterNames.Count > 0 ? PrintService.GetDefaultPrinterName() : null;
        ApplyPrinterChoices(preferredPrinter);
    }

    partial void OnShowVirtualPrintersChanged(bool value) => ApplyPrinterChoices(SelectedPrinterName);

    private void ApplyPrinterChoices(string? preferredPrinter)
    {
        var devices = _allPrinterNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new PrinterChoice(name, WindowsPrintService.IsKnownVirtualPrinter(name)))
            .ToList();
        var visible = devices.Where(device => ShowVirtualPrinters || !device.IsVirtual)
            .OrderBy(device => device.IsVirtual)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        string? Resolve(string? candidate) => visible.FirstOrDefault(
            device => string.Equals(device.Name, candidate, StringComparison.OrdinalIgnoreCase))?.Name;
        string selection = Resolve(preferredPrinter)
            ?? (UseLastPrintedPrinter ? Resolve(_lastPrintedPrinterName) : null)
            ?? Resolve(DefaultPrinterName)
            ?? Resolve(_windowsDefaultPrinterName)
            ?? visible.FirstOrDefault()?.Name
            ?? "";
        string previous = SelectedPrinterName;

        _suppressPrinterInspectOnSelect = true;
        try
        {
            InstalledPrinters.Clear();
            PrinterChoices.Clear();
            foreach (var device in visible)
            {
                InstalledPrinters.Add(device.Name);
                PrinterChoices.Add(device);
            }

            OnPropertyChanged(nameof(DefaultPrinterChoices));
            OnPropertyChanged(nameof(DefaultPrinterName));
            SelectedPrinterName = selection;
        }
        finally
        {
            _suppressPrinterInspectOnSelect = false;
        }

        if (selection.Length == 0)
        {
            CurrentCalibration = new();
            OnPropertyChanged(nameof(PrinterBorderless));
            PrinterHardwareSummary = devices.Count > 0
                ? "仅检测到虚拟打印机，可在设置中显示"
                : "未检测到打印机，可刷新设备";
            UpdatePreflight();
        }
        else if (!string.Equals(previous, selection, StringComparison.Ordinal) ||
                 !string.Equals(CurrentCalibration.PrinterName, selection, StringComparison.Ordinal))
        {
            PrinterHardwareSummary = "正在读取打印机设置…";
            OnSelectedPrinterNameChanged(selection);
        }
    }

    private IReadOnlyDictionary<string, string> MakeDefaultPrinterChoices()
    {
        var choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [""] = "跟随 Windows 默认打印机" };
        foreach (var device in PrinterChoices)
            choices.Add(device.Name, device.IsVirtual ? $"{device.Name}（虚拟）" : device.Name);
        if (DefaultPrinterName.Length > 0 && !choices.ContainsKey(DefaultPrinterName))
            choices.Add(DefaultPrinterName, $"{DefaultPrinterName}（当前不可用）");
        return choices;
    }

    partial void OnSelectedPrinterNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || _suppressPrinterInspectOnSelect) return;

        var token = _disposeCts.Token;
        _ = Task.Run(() =>
        {
            try
            {
                if (token.IsCancellationRequested) return;
                bool hasSaved = DataStore.HasSavedPrinterProfile(value);
                var profile = DataStore.LoadPrinterProfile(value);
                if (!string.IsNullOrWhiteSpace(profile.SavedDevModeBase64))
                    PrintService.LoadSavedDevModeBase64(value, profile.SavedDevModeBase64);
                var info = PrintService.InspectPrinter(value);
                if (token.IsCancellationRequested) return;

                var dispatcher = Application.Current?.Dispatcher;
                void Apply()
                {
                    if (token.IsCancellationRequested || !string.Equals(SelectedPrinterName, value, StringComparison.Ordinal))
                        return;
                    ApplyPrinterHardwareInfoToUi(value, hasSaved, profile, info);
                    ScheduleRender();
                }
                if (dispatcher != null && !dispatcher.CheckAccess())
                    _ = dispatcher.BeginInvoke(Apply);
                else
                    Apply();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error($"读取打印机配置失败: {value}", ex);
            }
        }, token);
    }

    private void ApplyPrinterHardwareInfoToUi(string printerName, bool hasSaved, PrinterCalibrationProfile profile, PrinterDeviceSummary? info)
    {
        if (info != null)
        {
            if (!hasSaved)
            {
                if (info.IsVirtualPrinter || (info.HardMarginLeftMm <= 0.05 && info.HardMarginTopMm <= 0.05))
                {
                    profile.SafeMarginMm = 0.0;
                    profile.DefaultBorderless = true;
                }
                else
                {
                    double hwMargin = Math.Max(info.HardMarginLeftMm, info.HardMarginTopMm);
                    profile.SafeMarginMm = hwMargin > 0.2 ? hwMargin : 4.0;
                }
            }
            PrinterHardwareSummary = info.IsVirtualPrinter
                ? $"虚拟打印/无边距输出 (0mm 硬件边距)，支持 {info.PaperNames.Count} 种纸张"
                : $"硬件硬边距: 左 {info.HardMarginLeftMm:0.#}mm / 上 {info.HardMarginTopMm:0.#}mm，支持 {info.PaperNames.Count} 种纸张";
        }
        else
        {
            if (!hasSaved)
            {
                profile.SafeMarginMm = 0.0;
                profile.DefaultBorderless = true;
            }
            PrinterHardwareSummary = "虚拟或离线打印队列";
        }

        CurrentCalibration = profile;
        if (!string.IsNullOrWhiteSpace(profile.CustomIccProfilePath) &&
            File.Exists(profile.CustomIccProfilePath) &&
            !string.Equals(SelectedIccPath, profile.CustomIccProfilePath, StringComparison.OrdinalIgnoreCase))
        {
            if (!AvailableIccFiles.Contains(profile.CustomIccProfilePath))
                AvailableIccFiles.Add(profile.CustomIccProfilePath);
            SelectedIccPath = profile.CustomIccProfilePath;
        }
        OnPropertyChanged(nameof(PrinterBorderless));
        UpdatePreflight();
    }

    public bool CanSelectAiModel => HasAiMattingModel && CanRefreshAiModels();

    private bool CanRefreshAiModels() => !IsExportBusy && !IsAiModelLoading;

    [RelayCommand(CanExecute = nameof(CanRefreshAiModels))]
    private async Task RefreshAiModelsAsync()
    {
        if (!CanRefreshAiModels()) return;
        string? preferred = SelectedAiModelName;
        await UpdateAiModelsAsync(() =>
        {
            MattingService.RefreshAvailableModels(preferred);
            FaceService.RefreshAvailableModels();
            RelightingService.RefreshAvailableModels();
            return true;
        });
    }

    public void RefreshFormalSuitOptions()
    {
        AvailableFormalSuitOptions.Clear();
        foreach (var option in FormalSuitRenderer.DiscoverAllPresets(Paths.SuitsDirectory))
            AvailableFormalSuitOptions.Add(option);
        OnPropertyChanged(nameof(SelectedFormalSuitOption));
    }

    private void ApplyAiModelStatusesToUi()
    {
        _suppressAiModelSwitch = true;
        try
        {
            AvailableAiModels.Clear();
            foreach (var name in MattingService.AvailableModelNames)
                AvailableAiModels.Add(name);
            SelectedAiModelName = MattingService.ActiveModelName;
        }
        finally
        {
            _suppressAiModelSwitch = false;
        }

        HasAiMattingModel = MattingService.HasModelLoaded;
        AiMattingStatusText = HasAiMattingModel
            ? $"已加载 AI 模型: {MattingService.ActiveModelName}"
            : "未放入 .onnx 模型（当前白墙提纯可免模型直接使用）";

        HasFaceDetectionModel = FaceService.HasModelLoaded;
        FaceDetectionStatusText = HasFaceDetectionModel
            ? $"已加载人脸/五官检测 ONNX: {FaceService.ActiveModelName}" +
              (FaceService.ActiveLandmarkModelName == null ? "" : $" + {FaceService.ActiveLandmarkModelName}")
            : "未加载人脸模型。日常打印和拍立得仍可按照片内容智能构图。";
    }

    partial void OnSelectedAiModelNameChanged(string? value)
    {
        if (_suppressAiModelSwitch || string.IsNullOrWhiteSpace(value)) return;
        if (string.Equals(value, MattingService.ActiveModelName, StringComparison.OrdinalIgnoreCase)) return;

        if (CanRefreshAiModels()) _ = UpdateAiModelsAsync(() => MattingService.SelectModel(value), value);
    }

    private async Task UpdateAiModelsAsync(Func<bool> update, string? requestedModel = null)
    {
        IsAiModelLoading = true;
        ChangeRenderContext();
        AiMattingStatusText = "正在加载离线模型…";
        var token = _disposeCts.Token;
        try
        {
            bool loaded = await Task.Run(update, token);
            if (token.IsCancellationRequested) return;
            if (requestedModel == null)
            {
                _faceDetectionCache.Clear();
                _relightShadingCache.Clear();
            }
            InvalidateAllMattingCaches();
            ApplyAiModelStatusesToUi();
            if (requestedModel != null)
            {
                if (loaded) SaveConfig();
                ShowToast(loaded ? $"已切换人像抠图模型：{MattingService.ActiveModelName}" : $"无法加载人像抠图模型：{requestedModel}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Error("更新离线模型失败", ex);
            ApplyAiModelStatusesToUi();
            ShowToast("更新离线模型失败");
        }
        finally
        {
            IsAiModelLoading = false;
            if (!token.IsCancellationRequested) ScheduleRender();
        }
    }

    partial void OnAppearanceChanged(AppearanceMode value) => RefreshAppearance();

    public void RefreshAppearance()
    {
        bool isDark = Appearance == AppearanceMode.Dark ||
            (Appearance == AppearanceMode.System && ThemeManager.SystemUsesDarkTheme());
        ThemeManager.ApplyTheme(isDark);
        IsDarkTheme = isDark;
    }

    [RelayCommand]
    private void SetScenario(LayoutScenario scenario)
    {
        if (ActiveScenario == scenario && FilteredTemplates.Count > 0) return;
        ActiveScenario = scenario;
        FilterTemplatesByScenario(scenario, selectDefault: true);
    }

    private static LayoutScenario TemplateCategory(LayoutScenario scenario) => scenario switch
    {
        LayoutScenario.SixInchTwoInOne or LayoutScenario.FreeGrid => LayoutScenario.SinglePhoto,
        _ => scenario
    };

    private void FilterTemplatesByScenario(LayoutScenario scenario, bool selectDefault)
    {
        var previous = SelectedTemplate;
        var category = TemplateCategory(scenario);
        FilteredTemplates.Clear();
        foreach (var t in _allTemplates.Where(t => TemplateCategory(t.Scenario) == category)
                     .OrderBy(t => t.Scenario == LayoutScenario.SinglePhoto ? 0 : 1))
            FilteredTemplates.Add(t);

        IsPolaroidControlsVisible = scenario == LayoutScenario.Polaroid || SelectedSlot?.PolaroidStyle != null;

        if (selectDefault && FilteredTemplates.Count > 0)
        {
            SelectedTemplate = FilteredTemplates.FirstOrDefault(t => t.Scenario == scenario);
        }
        else if (previous != null)
        {
            var replacement = FilteredTemplates.FirstOrDefault(t => t.Id == previous.Id);
            bool wasSuppressed = _suppressLayoutRebuild;
            _suppressLayoutRebuild = true;
            try { SelectedTemplate = replacement; }
            finally { _suppressLayoutRebuild = wasSuppressed; }
        }
    }

    [RelayCommand]
    private async Task LoadSamplePhotosAsync()
    {
        var paths = await Task.Run(() => SampleStudioGenerator.EnsureSampleImages(Paths));
        await ImportFilesAsync(paths);
        StatusText = "已加载 3 张内置高分辨率测试样片（含证件照白墙样片与拍立得风景样片）";
    }

    public void ImportFiles(IEnumerable<string> filePaths)
    {
        if (Application.Current?.Dispatcher != null)
            _ = ImportFilesAsync(filePaths);
        else
            ImportFilesAsync(filePaths).GetAwaiter().GetResult();
    }

    public async Task ImportFilesAsync(IEnumerable<string> filePaths,
        Func<int, ImportArrangement?>? chooseArrangement = null, LayoutSlot? dropTarget = null)
    {
        var validExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".tif" };

        var targets = new List<string>();
        var requested = filePaths.ToList();
        foreach (var file in requested)
        {
            if (File.Exists(file) && validExts.Contains(Path.GetExtension(file)))
                targets.Add(file);
        }
        if (targets.Count == 0)
        {
            ShowToast("没有找到可导入的照片。");
            return;
        }

        StatusText = targets.Count > 1
            ? $"正在导入 {targets.Count} 张照片..."
            : "正在导入照片...";

        var token = _disposeCts.Token;
        var decoded = new (PhotoAsset? Asset, System.Windows.Media.Imaging.BitmapSource? Thumb)[targets.Count];

        try
        {
            await Task.Run(() =>
            {
                var opts = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6),
                    CancellationToken = token
                };
                Parallel.For(0, targets.Count, opts, i =>
                {
                    string file = targets[i];
                    try
                    {
                        var asset = ExifAndColorHelper.InspectAsset(file);
                        using var thumbSk = ImageCache.GetReadOnlyThumbnail(asset);
                        var thumbWpf = SkiaWpfInterop.ToBitmapSource(thumbSk); // 已 Freeze，可安全跨线程传递
                        decoded[i] = (asset, thumbWpf);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"导入照片失败: {file}", ex);
                    }
                });
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested) return;

        var added = new List<AssetItemViewModel>(targets.Count);
        for (int i = 0; i < decoded.Length; i++)
        {
            var item = decoded[i];
            if (item.Asset == null) continue;
            var vm = new AssetItemViewModel(item.Asset, item.Thumb);
            added.Add(vm);
        }

        if (added.Count == 0)
        {
            ShowToast("这些文件无法作为照片打开，未导入任何素材。");
            return;
        }

        var arrangement = chooseArrangement != null ? chooseArrangement(added.Count) :
            new ImportArrangement(ActiveSheet?.Slots.All(s => string.IsNullOrEmpty(s.PhotoAssetId)) == true
                ? ActiveSheet.Scenario == LayoutScenario.IdPhoto && added.Count == 1
                    ? ImportPlacement.OnePhotoPerPage : ImportPlacement.FillEmptySlots
                : ImportPlacement.PoolOnly);
        if (arrangement is not { } options)
        {
            foreach (var photo in added) ImageCache.RemoveAsset(photo.Id);
            StatusText = "已取消导入";
            return;
        }

        var placement = options.Placement;
        int framingFailures = 0;
        List<PrintSheet>? preparedPages = null;
        if (placement == ImportPlacement.SmartPaginate && ActiveSheet is { Slots.Count: > 0 } prototype)
        {
            preparedPages = LayoutEngine.BuildPaginatedSheets(prototype, added.Select(item => item.Asset).ToList(),
                options.Order, options.Fit, orientSinglePhotoPages: true);
            var slots = preparedPages.SelectMany(page => page.Slots).Where(slot => slot.PhotoAssetId != null)
                .ToDictionary(slot => slot.PhotoAssetId!);
            var originalCrops = added.Select(item => ReadCrop(item.Asset.EditState)).ToArray();
            using var framing = CancellationTokenSource.CreateLinkedTokenSource(token);
            _batchFramingCancellation = framing;
            NotifyAutoFrameActionChanged();
            StatusText = $"正在为 {added.Count} 张照片智能构图…";
            try
            {
                framingFailures = await Task.Run(() =>
                {
                    int failures = 0;
                    for (int i = 0; i < added.Count; i++)
                    {
                        framing.Token.ThrowIfCancellationRequested();
                        var slot = slots[added[i].Id];
                        var (w, h, bleed) = PortraitAutoFrameHelper.ResolveUprightSlotPhotoFrameMm(slot);
                        var asset = added[i].Asset;
                        var edit = asset.EditState;
                        try
                        {
                            if (options.AutoRotate && PortraitAutoFrameHelper.NeedsQuarterTurnForSlotAspect(
                                asset.PixelWidth, asset.PixelHeight, edit.QuarterTurns, w, h))
                                edit.QuarterTurns = (edit.QuarterTurns + 1) % 4;
                            ApplyContentFrame(asset, w, h, bleed, options.Fit ?? slot.FitModeOverride,
                                slot.SlotZoom, slot.SlotPanX, slot.SlotPanY, framing.Token);
                        }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException)
                        {
                            RestoreAutoFrame(edit, originalCrops[i]);
                            failures++;
                            Logger.Error($"无法为 {added[i].Asset.DisplayName} 智能构图", ex);
                        }
                    }
                    return failures;
                }, framing.Token);
            }
            catch (OperationCanceledException) when (framing.IsCancellationRequested) { }
            finally
            {
                _batchFramingCancellation = null;
                NotifyAutoFrameActionChanged();
            }

            if (token.IsCancellationRequested) return;
            if (framing.IsCancellationRequested || ActiveSheet != prototype)
            {
                for (int i = 0; i < added.Count; i++) RestoreAutoFrame(added[i].Asset.EditState, originalCrops[i]);
                placement = ActiveSheet == prototype ? ImportPlacement.Paginate : ImportPlacement.PoolOnly;
            }
            else
            {
                for (int i = 0; i < added.Count; i++)
                {
                    var after = ReadCrop(added[i].Asset.EditState);
                    if (after != originalCrops[i]) _appliedAutoFrames[added[i].Id] = (originalCrops[i], after);
                }
            }
        }

        PushUndoSnapshot();
        foreach (var item in added) Assets.Add(item);
        int pages = 0;
        int placed = 0;
        if (placement == ImportPlacement.PoolOnly && dropTarget != null &&
            ActiveSheet?.Slots.Contains(dropTarget) == true && added.Count == 1)
        {
            dropTarget.PhotoAssetId = added[0].Id;
            placed = 1;
        }
        else if (placement == ImportPlacement.FillEmptySlots && ActiveSheet is { } sheet)
        {
            var emptySlots = sheet.Slots.Where(s => string.IsNullOrEmpty(s.PhotoAssetId));
            foreach (var slot in LayoutEngine.OrderSlots(emptySlots, options.Order).Take(added.Count))
            {
                slot.PhotoAssetId = added[placed++].Id;
                if (options.Fit.HasValue) slot.FitModeOverride = options.Fit;
            }
        }
        else if (placement is ImportPlacement.Paginate or ImportPlacement.OnePhotoPerPage or ImportPlacement.SmartPaginate &&
                 ActiveSheet is { Slots.Count: > 0 })
        {
            pages = AppendPhotoPages(added, options with { Placement = placement }, preparedPages, orientSinglePhotoPages: true);
            placed = added.Count;
        }

        UpdateAssetUsageCounts();
        if (pages == 0)
        {
            var firstPlaced = ActiveSheet?.Slots.FirstOrDefault(s => s.PhotoAssetId == added[0].Id);
            if (firstPlaced != null) SelectSlot(firstPlaced);
            else SelectAsset(added[0]);
            UpdatePreflight();
            ScheduleRender();
        }
        int failures = requested.Count - added.Count;
        string result = pages > 0 ? $"，排成 {pages} 页" : placed > 0 ? $"，已放入 {placed} 张" : "";
        if (added.Count > placed) result += $"，{added.Count - placed} 张待排版";
        if (placement == ImportPlacement.SmartPaginate && framingFailures > 0) result += $"，{framingFailures} 张保留原裁剪";
        if (failures > 0) result += $"，{failures} 个文件未导入";
        ShowToast($"已导入 {added.Count} 张照片{result}。");
    }

    public void PaginateSelectedPhotos(IReadOnlyList<AssetItemViewModel> photos, ImportArrangement? arrangement = null)
    {
        if (photos.Count == 0 || ActiveSheet is not { Slots.Count: > 0 }) return;
        PushUndoSnapshot();
        int pages = AppendPhotoPages(photos, arrangement ?? new ImportArrangement(ImportPlacement.Paginate));
        ShowToast($"已将所选 {photos.Count} 张照片排成 {pages} 页。");
    }

    private int AppendPhotoPages(IReadOnlyList<AssetItemViewModel> photos, ImportArrangement options,
        List<PrintSheet>? preparedPages = null, bool orientSinglePhotoPages = false, bool selectFirstPage = true)
    {
        var prototype = ActiveSheet!;
        var assets = photos.Select(p => p.Asset).ToList();
        var pages = preparedPages ?? (options.Placement == ImportPlacement.OnePhotoPerPage
            ? assets.Select(asset =>
            {
                var page = prototype.Clone();
                foreach (var slot in page.Slots)
                {
                    slot.PhotoAssetId = asset.Id;
                    if (options.Fit.HasValue) slot.FitModeOverride = options.Fit;
                }
                return page;
            }).ToList()
            : LayoutEngine.BuildPaginatedSheets(prototype, assets, options.Order, options.Fit, orientSinglePhotoPages));
        int insertAt = Sheets.Count;
        if (selectFirstPage && Sheets.Count == 1 && prototype.Slots.All(s => string.IsNullOrEmpty(s.PhotoAssetId)))
        {
            Sheets.Clear();
            insertAt = 0;
        }
        foreach (var page in pages)
        {
            page.SheetName = $"第 {Sheets.Count + 1} 页";
            Sheets.Add(page);
        }
        if (selectFirstPage) SwitchToSheet(insertAt);
        return pages.Count;
    }

    public void SelectAsset(AssetItemViewModel? vm, bool syncSlotOnSheet = false)
    {
        if (SelectedAsset != null) SelectedAsset.IsSelected = false;
        SelectedAsset = vm;
        if (SelectedAsset != null) SelectedAsset.IsSelected = true;

        if (syncSlotOnSheet && vm != null && ActiveSheet != null && SelectedSlot?.PhotoAssetId != vm.Id)
        {
            var matchingSlot = ActiveSheet.Slots.FirstOrDefault(s => s.PhotoAssetId == vm.Id);
            if (matchingSlot != null)
            {
                SelectedSlot = matchingSlot;
                HasSelectedSlot = true;
            }
        }

        HasSelectedAsset = SelectedAsset != null;
        NotifyAutoFrameActionChanged();
        NotifyAllEditPropertiesChanged();
        UpdateActiveTargetContext();
        ScheduleRender();
    }

    public void SelectSlot(LayoutSlot? slot)
    {
        SelectedSlot = slot;
        HasSelectedSlot = slot != null;
        IsPolaroidControlsVisible = ActiveScenario == LayoutScenario.Polaroid || slot?.PolaroidStyle != null;

        if (slot?.PolaroidStyle != null)
        {
            bool wasSuppressed = _suppressLayoutRebuild;
            _suppressLayoutRebuild = true;
            PolaroidFrameColorHex = slot.PolaroidStyle.FrameColorHex;
            PolaroidTextColorHex = slot.PolaroidStyle.TextColorHex;
            PolaroidCaptionTemplate = slot.CustomCaption ?? slot.PolaroidStyle.CaptionTemplate;
            PolaroidFontFamily = slot.PolaroidStyle.FontFamily;
            PolaroidFontSizePt = slot.PolaroidStyle.FontSizePt;
            PolaroidTextAlign = slot.PolaroidStyle.TextAlignment;
            PolaroidInnerShadow = slot.PolaroidStyle.ShowInnerShadow;
            PolaroidPaperGrain = slot.PolaroidStyle.ShowPaperGrain;
            _suppressLayoutRebuild = wasSuppressed;
        }

        if (!string.IsNullOrEmpty(slot?.PhotoAssetId))
        {
            var found = Assets.FirstOrDefault(a => a.Id == slot.PhotoAssetId);
            if (found != null) SelectAsset(found);
        }
        else
        {
            UpdateActiveTargetContext();
            ScheduleRender();
        }
    }

    public void UpdateActiveTargetContext()
    {
        if (SelectedSlot != null && !string.IsNullOrEmpty(SelectedSlot.PhotoAssetId))
        {
            var assetVm = Assets.FirstOrDefault(a => a.Id == SelectedSlot.PhotoAssetId);
            if (assetVm != null)
            {
                if (SelectedAsset != null && SelectedAsset.Id != assetVm.Id)
                {
                    ActiveEditTargetTitle = $"预调素材: {SelectedAsset.DisplayName}";
                    ActiveEditTargetMeta = $"{SelectedAsset.ResolutionText}，未放入当前格位 #{SelectedSlot.Index + 1}";
                    IsEditingUnassignedAsset = true;
                    return;
                }

                bool rotated = Math.Abs(Math.Abs(SelectedSlot.SlotRotationDegrees) - 90.0) < 1.0;
                double targetW = SelectedSlot.PolaroidStyle != null
                    ? Math.Max(1, SelectedSlot.WidthMm - (rotated
                        ? SelectedSlot.PolaroidStyle.MarginTopMm + SelectedSlot.PolaroidStyle.MarginBottomMm
                        : SelectedSlot.PolaroidStyle.MarginLeftMm + SelectedSlot.PolaroidStyle.MarginRightMm))
                    : SelectedSlot.WidthMm + SelectedSlot.BleedMm * 2;
                double targetH = SelectedSlot.PolaroidStyle != null
                    ? Math.Max(1, SelectedSlot.HeightMm - (rotated
                        ? SelectedSlot.PolaroidStyle.MarginLeftMm + SelectedSlot.PolaroidStyle.MarginRightMm
                        : SelectedSlot.PolaroidStyle.MarginTopMm + SelectedSlot.PolaroidStyle.MarginBottomMm))
                    : SelectedSlot.HeightMm + SelectedSlot.BleedMm * 2;

                bool swapped = (assetVm.Asset.EditState.QuarterTurns % 2 != 0) ^
                               rotated;
                double srcW = swapped ? assetVm.Asset.PixelHeight : assetVm.Asset.PixelWidth;
                double srcH = swapped ? assetVm.Asset.PixelWidth : assetVm.Asset.PixelHeight;
                double zoom = Math.Clamp(assetVm.Asset.EditState.Zoom * SelectedSlot.SlotZoom, 0.5, 8.0);
                double dpiW = (srcW / zoom) / (targetW / UnitConverter.MmPerInch);
                double dpiH = (srcH / zoom) / (targetH / UnitConverter.MmPerInch);
                double effDpi = (SelectedSlot.FitModeOverride ?? assetVm.Asset.EditState.FitMode) == FitMode.Cover
                    ? Math.Min(dpiW, dpiH) : Math.Max(dpiW, dpiH);
                string dpiBadge = effDpi >= 300 ? $"{effDpi:0} DPI 极清" : (effDpi >= 220 ? $"{effDpi:0} DPI 合格" : $"{effDpi:0} DPI 偏低");
                string syncHint = assetVm.UsageCount > 1 ? $"，同步 {assetVm.UsageCount} 格" : "";

                ActiveEditTargetTitle = $"格位 #{SelectedSlot.Index + 1}：{assetVm.DisplayName}";
                ActiveEditTargetMeta = $"{SelectedSlot.WidthMm:0.#}×{SelectedSlot.HeightMm:0.#}mm，{dpiBadge}{syncHint}";
                IsEditingUnassignedAsset = false;
                CanForkIndependentAsset = assetVm.UsageCount > 1;
                return;
            }
        }

        CanForkIndependentAsset = false;

        if (SelectedSlot != null)
        {
            ActiveEditTargetTitle = $"格位 #{SelectedSlot.Index + 1}（空置）";
            ActiveEditTargetMeta = SelectedAsset != null
                ? $"待放置素材: {SelectedAsset.DisplayName}"
                : "从下方胶片条拖入照片或点击导入";
            IsEditingUnassignedAsset = SelectedAsset != null;
            return;
        }

        ActiveEditTargetTitle = SelectedAsset != null ? $"素材: {SelectedAsset.DisplayName}" : "工作区就绪";
        ActiveEditTargetMeta = SelectedAsset != null ? SelectedAsset.ResolutionText : "导入照片后点击模板即可开始排版";
        IsEditingUnassignedAsset = SelectedAsset != null;
    }

    public void UpdateToolBanner()
    {
        if (IsHoldingCompare)
        {
            ActiveToolBannerText = "正在对比原始未调色图像（松开空格或按钮恢复效果图）";
            HasActiveToolBanner = true;
        }
        else if (IsTwoPointStraightenActive)
        {
            ActiveToolBannerText = "两点水平拉直模式：请在画布照片上依次点击两个水平参考点（如左右眼瞳孔）";
            HasActiveToolBanner = true;
        }
        else if (ViewMode == CanvasViewMode.SingleRetouchCrop)
        {
            ActiveToolBannerText = "单图裁剪精修模式：画布内按住拖动调整构图，滚轮调整裁剪缩放，按 F6 返回总览";
            HasActiveToolBanner = true;
        }
        else if (ViewMode == CanvasViewMode.SplitCompare)
        {
            ActiveToolBannerText = "分屏对比模式：在画布上左右拖动分界线对比「原图 Before | 调色 After」";
            HasActiveToolBanner = true;
        }
        else
        {
            ActiveToolBannerText = "";
            HasActiveToolBanner = false;
        }
    }

    public void AssignAssetToSlot(AssetItemViewModel assetVm, LayoutSlot slot)
    {
        PushUndoSnapshot();
        slot.PhotoAssetId = assetVm.Id;
        UpdateAssetUsageCounts();
        SelectSlot(slot);
        UpdatePreflight();
        ScheduleRender();
    }

    [RelayCommand]
    private void FillAllSlotsWithSelectedAsset()
    {
        if (SelectedAsset != null) FillAllSlotsWithAsset(SelectedAsset);
    }

    public void FillAllSlotsWithAsset(AssetItemViewModel assetVm)
    {
        if (ActiveSheet == null) return;
        PushUndoSnapshot();
        foreach (var s in ActiveSheet.Slots)
            s.PhotoAssetId = assetVm.Id;
        UpdateAssetUsageCounts();
        SelectSlot(ActiveSheet.Slots.FirstOrDefault());
        UpdatePreflight();
        ScheduleRender();
    }

    [RelayCommand]
    private void SequentialFillAllAssets() => SequentialFillCurrentSheet(Assets);

    private void SequentialFillCurrentSheet(IReadOnlyList<AssetItemViewModel> pool)
    {
        if (ActiveSheet == null || pool.Count == 0) return;
        PushUndoSnapshot();
        for (int i = 0; i < ActiveSheet.Slots.Count; i++)
        {
            ActiveSheet.Slots[i].PhotoAssetId = pool[i % pool.Count].Id;
        }
        UpdateAssetUsageCounts();
        SelectSlot(ActiveSheet.Slots.FirstOrDefault());
        UpdatePreflight();
        ScheduleRender();
    }

    [RelayCommand]
    private void ClearSelectedSlot()
    {
        if (SelectedSlot == null) return;
        PushUndoSnapshot();
        SelectedSlot.PhotoAssetId = null;
        UpdateAssetUsageCounts();
        UpdatePreflight();
        ScheduleRender();
    }

    public void RemovePhotos(IReadOnlyList<AssetItemViewModel> photos)
    {
        if (photos.Count == 0) return;
        PushUndoSnapshot();
        var ids = photos.Select(p => p.Id).ToHashSet();
        foreach (var slot in Sheets.SelectMany(s => s.Slots))
        {
            if (slot.PhotoAssetId is { } id && ids.Contains(id)) slot.PhotoAssetId = null;
        }
        foreach (var photo in photos)
        {
            Assets.Remove(photo);
            ImageCache.RemoveAsset(photo.Id);
            InvalidateAssetPreviewCache(photo.Id);
            _compactMatteCache.TryRemove(photo.Id, out _);
            _faceDetectionCache.TryRemove(photo.Id, out _);
            _relightShadingCache.TryRemove(photo.Id, out _);
            _portraitDiagnosticsCache.TryRemove(photo.Id, out _);
            _latestPortraitDiagnostics.Remove(photo.Id);
            _appliedAutoFrames.Remove(photo.Id);
            _latestHistograms.Remove(photo.Id);
        }
        SelectAsset(Assets.FirstOrDefault());
        UpdateAssetUsageCounts();
        UpdatePreflight();
        ScheduleRender();
    }

    private void UpdateAssetUsageCounts()
    {
        var counts = Sheets.SelectMany(s => s.Slots)
            .Where(s => !string.IsNullOrEmpty(s.PhotoAssetId))
            .GroupBy(s => s.PhotoAssetId!)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var a in Assets)
            a.UsageCount = counts.TryGetValue(a.Id, out int c) ? c : 0;
        UnplacedPhotoCount = Assets.Count(a => a.UsageCount == 0);
    }

    // === 撤销 / 重做 ===
    public void BeginContinuousEdit()
    {
        _pendingUndoSnapshot ??= CaptureSnapshot();
    }

    public void EndContinuousEdit()
    {
        if (_pendingUndoSnapshot != null)
        {
            _undoStack.PushUndo(_pendingUndoSnapshot);
            _pendingUndoSnapshot = null;
            SyncUndoRedoFlags();
        }
    }

    public void PushUndoSnapshot()
    {
        _undoStack.PushUndo(CaptureSnapshot());
        SyncUndoRedoFlags();
    }

    [RelayCommand]
    private void Undo()
    {
        var current = CaptureSnapshot();
        var prev = _undoStack.Undo(current);
        if (prev != null) RestoreSnapshot(prev);
        SyncUndoRedoFlags();
    }

    [RelayCommand]
    private void Redo()
    {
        var current = CaptureSnapshot();
        var next = _undoStack.Redo(current);
        if (next != null) RestoreSnapshot(next);
        SyncUndoRedoFlags();
    }

    private void SyncUndoRedoFlags()
    {
        CanUndo = _undoStack.CanUndo;
        CanRedo = _undoStack.CanRedo;
    }

    private ProjectSnapshot CaptureSnapshot() => new(
        Assets.Select(a => a.Asset.CloneMetadataOnly()).ToList(),
        Sheets.Select(s => s.Clone(preserveId: true)).ToList(),
        ActiveSheetIndex,
        SelectedSlot?.Id,
        SelectedAsset?.Id);

    private void RestoreSnapshot(ProjectSnapshot snap)
    {
        _suppressLayoutRebuild = true;
        try
        {
            var existingThumbs = Assets.ToDictionary(a => a.Id, a => a.Thumbnail);
            var missingThumbItems = new List<AssetItemViewModel>();
            Assets.Clear();
            foreach (var a in snap.Assets)
            {
                BitmapSource? thumb = existingThumbs.TryGetValue(a.Id, out var t) ? t : null;
                var item = new AssetItemViewModel(a.CloneMetadataOnly(), thumb);
                Assets.Add(item);
                if (thumb == null && File.Exists(a.FilePath))
                    missingThumbItems.Add(item);
            }

            Sheets.Clear();
            foreach (var sh in snap.Sheets) Sheets.Add(sh.Clone(preserveId: true));

            ActiveSheetIndex = Math.Clamp(snap.ActiveSheetIndex, 0, Math.Max(0, Sheets.Count - 1));
            ActiveSheet = Sheets.ElementAtOrDefault(ActiveSheetIndex);
            UpdateAssetUsageCounts();

            var slot = ActiveSheet?.Slots.FirstOrDefault(s => s.Id == snap.SelectedSlotId)
                       ?? ActiveSheet?.Slots.FirstOrDefault();
            SelectSlot(slot);

            var asset = Assets.FirstOrDefault(a => a.Id == snap.SelectedAssetId) ?? Assets.FirstOrDefault();
            SelectAsset(asset);

            PreviewCache.ClearProcessedAndSplitCaches();
            UpdatePreflight();
            ScheduleRender();
            if (missingThumbItems.Count > 0) _ = LoadWorkspaceThumbnailsAsync(missingThumbItems);
        }
        finally
        {
            _suppressLayoutRebuild = false;
        }
    }

    public void SaveConfig()
    {
        var cfg = new AppPortableConfig
        {
            Appearance = Appearance,
            IsDarkTheme = IsDarkTheme,
            DefaultPrintDpi = PrintDpi,
            DefaultExportDpi = ExportDpi,
            DefaultJpegQuality = JpegQuality,
            OutputSharpeningAmount = Math.Clamp(OutputSharpeningAmount, 0, 100),
            EmbedIccProfileOnExport = EmbedIccProfileOnExport,
            LastPrinterName = _lastPrintedPrinterName,
            DefaultPrinterName = DefaultPrinterName,
            UseLastPrintedPrinter = UseLastPrintedPrinter,
            ShowVirtualPrinters = ShowVirtualPrinters,
            SoftProofIccFile = SelectedIccPath,
            EnableSoftProofing = EnableSoftProofing,
            SoftProofIntent = SoftProofIntent,
            SoftProofSimulatePaperWhite = SoftProofSimulatePaperWhite,
            SoftProofGamutWarning = SoftProofGamutWarning,
            AutoRecoverySeconds = _autoRecoverySeconds,
            ShowRulers = ShowRulers,
            ShowSafeMarginOverlay = ShowSafeMarginOverlay,
            ShowBleedOverlay = ShowBleedOverlay,
            ShowIdGuidelines = ShowIdGuidelines,
            ShowFaceLandmarksOverlay = ShowFaceLandmarksOverlay,
            ShowClippingOverlay = ShowClippingOverlay,
            PreferredMattingModel = MattingService.ActiveModelName ?? SelectedAiModelName ?? "",
            HotFolderWatch = _hotFolderSettings.Clone(),
            CustomPaperSizes = [.. _customPaperSizes],
            CustomPolaroidSpecs = [.. _customPolaroidSpecs]
        };
        DataStore.SaveConfig(cfg);
    }

    private void SaveAutoRecoverySnapshot() => _ = SaveAutoRecoverySnapshotAsync();

    private void ClearAutoRecovery()
    {
        Interlocked.Increment(ref _autoRecoveryVersion);
        lock (_autoRecoveryGate)
            DataStore.ClearAutoRecovery();
    }

    internal Task SaveAutoRecoverySnapshotAsync()
    {
        if (Interlocked.Exchange(ref _autoRecoveryInFlight, 1) == 1)
            return Task.CompletedTask;

        // 仅在 UI 线程做轻量级对象克隆，将 JSON 序列化、脏检测与原子写盘彻底移出 UI 线程
        var snapshotProject = new PrintProject
        {
            ProjectName = "自动恢复工程",
            Assets = Assets.Select(a => a.Asset.CloneMetadataOnly()).ToList(),
            Sheets = Sheets.Select(s => s.Clone(preserveId: true)).ToList(),
            ActiveSheetIndex = ActiveSheetIndex
        };
        string savedWorkspace = _savedWorkspace;
        long version = Volatile.Read(ref _autoRecoveryVersion);
        var token = _disposeCts.Token;

        return Task.Run(() =>
        {
            try
            {
                if (token.IsCancellationRequested) return;
                string serialized = System.Text.Json.JsonSerializer.Serialize(
                    new { snapshotProject.Assets, snapshotProject.Sheets },
                    PortablePaths.JsonOptions);
                bool hasUnsaved = !string.Equals(serialized, savedWorkspace, StringComparison.Ordinal);
                lock (_autoRecoveryGate)
                {
                    if (token.IsCancellationRequested || version != Volatile.Read(ref _autoRecoveryVersion)) return;
                    if (hasUnsaved)
                        DataStore.SaveAutoRecovery(snapshotProject);
                    else
                        DataStore.ClearAutoRecovery();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("后台自动恢复快照写入失败", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _autoRecoveryInFlight, 0);
            }
        });
    }

    private bool ChooseStartupWorkspace()
    {
        try
        {
            var recovery = DataStore.TryLoadAutoRecovery();
            var project = recovery ?? DataStore.LoadTemporaryWorkspace();
            if (project == null) return true;

            string source = recovery != null ? "自动恢复快照" : "临时工程";
            var choice = Dialogs.ChooseWorkspaceRestore(
                $"找到上次的{source}，共 {project.Assets.Count} 张照片、{project.Sheets.Count} 页排版。");
            if (choice == DialogChoice.Cancel) return false;
            if (choice == DialogChoice.No)
            {
                DataStore.ClearTemporaryWorkspace();
                ClearAutoRecovery();
                return true;
            }

            bool missing = project.Assets.Any(a => !File.Exists(a.FilePath));
            if (missing && !RelinkMissingPhotos(project)) return false;
            LoadProjectIntoWorkspace(project);
            if (recovery != null)
                TrySaveCurrentWorkspace();
            else if (!missing)
                _savedWorkspace = WorkspaceContent();
            StatusText = $"已恢复上次的{source}";
        }
        catch (Exception ex)
        {
            Logger.Error("处理启动暂存工程失败", ex);
            StatusText = "无法处理上次的暂存工程";
        }
        return true;
    }

    public void LoadProjectIntoWorkspace(PrintProject project)
    {
        if (IsHotFolderWatching) StopHotFolderWatch();
        _workspaceThumbnailCts?.Cancel();
        var loadedAssets = project.Assets.Where(a => File.Exists(a.FilePath))
            .Select(a => new AssetItemViewModel(a, null)).ToList();

        _renderCts?.Cancel();
        _undoStack.Clear();
        _pendingUndoSnapshot = null;
        _appliedAutoFrames.Clear();
        SyncUndoRedoFlags();
        _compactMatteCache.Clear();
        _faceDetectionCache.Clear();
        _relightShadingCache.Clear();
        _latestHistograms.Clear();
        _portraitDiagnosticsCache.Clear();
        _latestPortraitDiagnostics.Clear();
        PreviewCache.ClearAllCaches();
        Assets.Clear();
        foreach (var asset in loadedAssets) Assets.Add(asset);
        Sheets.Clear();
        foreach (var sh in project.Sheets) Sheets.Add(sh);
        if (Sheets.Count == 0)
        {
            ActiveSheet = null;
            ActiveScenario = LayoutScenario.IdPhoto;
            FilterTemplatesByScenario(LayoutScenario.IdPhoto, selectDefault: true);
        }

        ActiveSheetIndex = Math.Clamp(project.ActiveSheetIndex, 0, Math.Max(0, Sheets.Count - 1));
        ActiveSheet = Sheets.ElementAtOrDefault(ActiveSheetIndex);
        UpdateAssetUsageCounts();
        SelectSlot(ActiveSheet?.Slots.FirstOrDefault());
        if (SelectedSlot?.PhotoAssetId is not { } firstId || !Assets.Any(a => a.Id == firstId))
            SelectAsset(Assets.FirstOrDefault());
        UpdatePreflight();
        ScheduleRender();
        if (loadedAssets.Count > 0) _ = LoadWorkspaceThumbnailsAsync(loadedAssets);
    }

    private async Task LoadWorkspaceThumbnailsAsync(List<AssetItemViewModel> assets)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _workspaceThumbnailCts = cancellation;
        var token = cancellation.Token;
        try
        {
            foreach (var item in assets)
            {
                if (token.IsCancellationRequested) break;
                BitmapSource thumbnail;
                try
                {
                    thumbnail = await Task.Run(() =>
                    {
                        using var bitmap = ImageCache.GetReadOnlyThumbnail(item.Asset);
                        return SkiaWpfInterop.ToBitmapSource(bitmap);
                    }, token);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    Logger.Error($"生成照片缩略图失败: {item.Asset.FilePath}", ex);
                    continue;
                }
                if (!token.IsCancellationRequested && Assets.Contains(item))
                {
                    UiDispatcher.BeginInvoke(() =>
                    {
                        if (!token.IsCancellationRequested && Assets.Contains(item))
                            item.Thumbnail = thumbnail;
                    });
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (_workspaceThumbnailCts == cancellation) _workspaceThumbnailCts = null;
        }
    }

    public void Dispose()
    {
        ChangeRenderContext(closing: true);
        _autoRecoveryTimer?.Stop();
        _toastTimer?.Stop();
        _workspaceThumbnailCts?.Cancel();
        _hqSettleCts?.Cancel();
        _hqSettleCts?.Dispose();
        _hqSettleCts = null;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;
        _disposeCts.Cancel();
        HotFolderService?.Dispose();
        _renderGate.Wait();
        _renderGate.Release();
        try { _startupInitTask?.Wait(500); } catch { }
        PreviewCache.Dispose();
        ImageCache.Dispose();
        MattingService.Dispose();
        FaceService.Dispose();
        RelightingService.Dispose();
    }
}
