using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoPrintStudio.App.Helpers;
using PhotoPrintStudio.App.Printing;
using PhotoPrintStudio.App.Services;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Persistence;
using PhotoPrintStudio.Core.Portable;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.App.Tests;

public class AppViewModelAndInteropTests
{
    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public DialogChoice YesNoCancelResult { get; set; } = DialogChoice.No;
        public string? SaveFilePath { get; set; }
        public string? OpenFilePath { get; set; }
        public string? OpenFolderPath { get; set; }
        public List<string> InfoMessages { get; } = [];
        public List<string> ErrorMessages { get; } = [];

        public void ShowInfo(string message, string title) => InfoMessages.Add($"{title}:{message}");
        public void ShowError(string message, string title) => ErrorMessages.Add($"{title}:{message}");
        public bool Confirm(string message, string title, bool isWarning = false) => ConfirmResult;
        public DialogChoice ConfirmYesNoCancel(string message, string title) => YesNoCancelResult;
        public string? ShowSaveFileDialog(string title, string filter, string defaultExt, string fileName, string? initialDir = null) => SaveFilePath;
        public string? ShowOpenFileDialog(string title, string filter, string? initialDir = null) => OpenFilePath;
        public string? ShowOpenFolderDialog(string title, string? initialDir = null) => OpenFolderPath;
        public void OpenPathInShell(string path) { }
    }

    private sealed class FakePrintService : IPrintService
    {
        private readonly Dictionary<string, string> _devModes = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Printers { get; set; } = ["Mock Thermal Printer"];
        public int PrintSheetCalls { get; private set; }

        public bool HasSavedDriverPreferences(string printerName) => _devModes.ContainsKey(printerName);
        public string GetSavedDevModeBase64(string printerName) => _devModes.TryGetValue(printerName, out var b64) ? b64 : "";
        public void LoadSavedDevModeBase64(string printerName, string? base64)
        {
            if (!string.IsNullOrWhiteSpace(base64)) _devModes[printerName] = base64;
        }
        public bool ShowPrinterPreferencesDialog(string printerName, IntPtr ownerHwnd) => false;
        public IReadOnlyList<string> GetInstalledPrinters() => Printers;
        public PrinterDeviceSummary? InspectPrinter(string printerName) =>
            new(printerName, 2.0, 2.0, false, false, ["4x6 in (102×152mm)"]);
        public void PrintSheets(IReadOnlyList<PrintSheet> sheets, IReadOnlyDictionary<string, PhotoAsset> assetsById,
            Func<PhotoAsset, PhotoEditState, SKBitmap> resolveFullResBitmap, string printerName, PrinterCalibrationProfile calibration,
            short copies = 1, int printDpi = 300, bool duplexMirrorBackSide = false, double outputSharpeningAmount = 0, CancellationToken cancellationToken = default)
            => PrintSheetCalls++;
        public void PrintCalibrationCard(double paperWidthMm, double paperHeightMm, string printerName, PrinterCalibrationProfile calibration, int dpi = 300) { }
    }

    [Fact]
    public void PreviewCacheCoordinator_ShouldCancelStaleContextAndKeepSharedBitmapValidAfterEviction()
    {
        using var coord = new PreviewCacheCoordinator();
        Assert.True(coord.TryGetCurrentContext(out long v0, out var tok0));
        Assert.False(tok0.IsCancellationRequested);

        long v1 = coord.AdvanceContext();
        Assert.True(v1 > v0);
        Assert.True(tok0.IsCancellationRequested);

        var edit = new PhotoEditState { Exposure = 0.5 };
        using var src = new SKBitmap(new SKImageInfo(32, 32, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        src.Erase(new SKColor(180, 90, 40, 255));
        coord.StoreProcessedPreview("a1", false, "src1", edit, false, src, new HistogramData());

        Assert.True(coord.TryGetProcessedPreview("a1", false, "src1", edit, false, out var shared, out var hist));
        Assert.NotNull(shared);
        Assert.NotNull(hist);

        // 即使缓存被主动失效，已取出的不可变共享句柄仍可安全读取像素
        coord.InvalidateAsset("a1");
        Assert.False(coord.TryGetProcessedPreview("a1", false, "src1", edit, false, out _, out _));
        using (shared!)
        {
            Assert.Equal(180, shared.GetPixel(10, 10).Red);
        }

        // 关闭上下文后不再发放有效上下文
        coord.AdvanceContext(closing: true);
        Assert.False(coord.TryGetCurrentContext(out _, out var closedTok));
        Assert.True(closedTok.IsCancellationRequested);
    }

    [Fact]
    public void SkiaWpfInterop_PremulBitmapAndTiffDecoder_ShouldUsePbgra32WithoutDarkHalo()
    {
        // 半透明红色 (alpha=128) 在 Premul 下像素应存储为 R≈128, G=0, B=0, A=128，
        // WPF BitmapSource 必须为 Pbgra32，避免二次预乘发黑。
        using var premul = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        premul.Erase(new SKColor(255, 0, 0, 128));

        var wpfSrc = SkiaWpfInterop.ToBitmapSource(premul);
        Assert.Equal(PixelFormats.Pbgra32, wpfSrc.Format);
        byte[] pixels = new byte[16 * 16 * 4];
        wpfSrc.CopyPixels(pixels, 16 * 4, 0);
        Assert.Equal(128, pixels[3]); // Alpha
        Assert.InRange(pixels[2], 125, 130); // Premultiplied Red ~ 128

        var wb = SkiaWpfInterop.UpdateWriteableBitmap(premul, null);
        Assert.Equal(PixelFormats.Pbgra32, wb.Format);

        // 验证 TIFF 解码器往返（8-bit TIFF）
        string tempTiff = Path.Combine(Path.GetTempPath(), $"pps_tiff_{Guid.NewGuid():N}.tif");
        try
        {
            var enc = new TiffBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(wpfSrc));
            using (var fs = File.Create(tempTiff)) enc.Save(fs);

            var (decoded, origin) = SkiaWpfInterop.DecodeTiff(tempTiff);
            using (decoded)
            {
                Assert.Equal(16, decoded.Width);
                Assert.Equal(16, decoded.Height);
                Assert.Equal(SKEncodedOrigin.TopLeft, origin);
            }
        }
        finally
        {
            if (File.Exists(tempTiff)) File.Delete(tempTiff);
        }
    }

    [Fact]
    public async Task MainViewModel_HeadlessUndoRedoAndBackgroundAutoRecovery_ShouldWorkWithoutUiDialogs()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_vm_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string photoPath = Path.Combine(tempRoot, "sample.png");
            using (var bmp = new SKBitmap(new SKImageInfo(200, 260, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
            {
                bmp.Erase(new SKColor(70, 130, 190, 255));
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(photoPath, data.ToArray());
            }

            var paths = new PortablePaths(tempRoot);
            var logger = new AppLogger(paths);
            var store = new PortableDataStore(paths, logger);
            var dialogs = new FakeDialogService { ConfirmResult = true };
            var printSvc = new FakePrintService();

            using (var vm = new MainViewModel(
                       checkRecovery: false,
                       paths: paths,
                       dataStore: store,
                       printService: printSvc,
                       dialogs: dialogs,
                       uiDispatcher: new InlineUiDispatcher(),
                       loadBackgroundServices: false))
            {
                var asset = ExifAndColorHelper.InspectAsset(photoPath);
                var item = new AssetItemViewModel(asset, null);
                vm.Assets.Add(item);
                vm.FillAllSlotsWithAsset(item);
                Assert.NotNull(vm.SelectedAsset);

                // 连续拖动编辑合并为单步 Undo
                vm.BeginContinuousEdit();
                vm.EditExposure = 0.4;
                vm.EditExposure = 0.8;
                vm.EditExposure = 1.2;
                vm.EndContinuousEdit();

                Assert.True(vm.CanUndo);
                Assert.Equal(1.2, vm.EditExposure, 3);

                vm.UndoCommand.Execute(null);
                Assert.Equal(0.0, vm.EditExposure, 3);
                Assert.True(vm.CanRedo);

                vm.RedoCommand.Execute(null);
                Assert.Equal(1.2, vm.EditExposure, 3);

                // 后台自动恢复快照（脱离 UI 线程序列化与落盘）
                await vm.SaveAutoRecoverySnapshotAsync();
                var recovered = store.TryLoadAutoRecovery();
                Assert.NotNull(recovered);
                Assert.Single(recovered!.Assets);
                Assert.Equal(1.2, recovered.Assets[0].EditState.Exposure, 3);
            }

            // 重新构造 ViewModel 验证启动自动恢复链路（通过 FakeDialogService 无需真实弹窗）
            using (var vmRecovered = new MainViewModel(
                       checkRecovery: true,
                       paths: paths,
                       dataStore: store,
                       printService: printSvc,
                       dialogs: dialogs,
                       uiDispatcher: new InlineUiDispatcher(),
                       loadBackgroundServices: false))
            {
                Assert.Single(vmRecovered.Assets);
                Assert.Equal(1.2, vmRecovered.Assets[0].Asset.EditState.Exposure, 3);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void MainViewModel_PrinterCalibration_ShouldPersistCustomIccPathAndDevMode()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_vm_icc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var paths = new PortablePaths(tempRoot);
            paths.EnsureDirectoriesExist();
            string iccPath = Path.Combine(paths.IccDirectory, "srgb_paper.icc");
            File.WriteAllBytes(iccPath, WindowsIccEngine.BuildMinimalSrgbIccV2());

            var store = new PortableDataStore(paths, new AppLogger(paths));
            var printSvc = new FakePrintService();
            string fakeDevMode = Convert.ToBase64String(Enumerable.Range(0, 80).Select(i => (byte)i).ToArray());
            printSvc.LoadSavedDevModeBase64("Mock Thermal Printer", fakeDevMode);

            using var vm = new MainViewModel(
                checkRecovery: false,
                paths: paths,
                dataStore: store,
                printService: printSvc,
                dialogs: new FakeDialogService(),
                uiDispatcher: new InlineUiDispatcher(),
                loadBackgroundServices: false);

            vm.InstalledPrinters.Add("Mock Thermal Printer");
            vm.SelectedPrinterName = "Mock Thermal Printer";
            vm.CurrentCalibration.PrinterName = "Mock Thermal Printer";
            vm.SelectedIccPath = iccPath;
            vm.SaveCurrentCalibrationCommand.Execute(null);

            var persisted = store.LoadPrinterProfile("Mock Thermal Printer");
            Assert.Equal(iccPath, persisted.CustomIccProfilePath);
            Assert.Equal(fakeDevMode, persisted.SavedDevModeBase64);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void WindowsPrintService_SelectPaperSize_ShouldMatchMetricAndBorderlessPapersWithoutFalseMatches()
    {
        var a4 = new PaperSize("A4", 827, 1169);
        var metric102x152 = new PaperSize("4x6 (102x152mm)", 402, 598);
        var metric102x152Borderless = new PaperSize("4x6 无边距 (102x152mm)", 402, 598);
        var metric10x15 = new PaperSize("10 x 15 cm", 394, 591);
        var a6 = new PaperSize("A6", 413, 583);
        var postcard = new PaperSize("日式明信片", 394, 583);

        // 1. 满幅无边距打印时：102x152mm 应命中驱动预设而非回退 CustomPhoto，且优先选中无边距纸型
        var pickedBorderless = WindowsPrintService.SelectPaperSize(
            [a4, metric102x152, metric102x152Borderless], 101.6, 152.4,
            savedPreferredPaper: null, systemDefaultPaper: a4, preferBorderless: true);
        Assert.Same(metric102x152Borderless, pickedBorderless);

        // 2. 非无边距打印（如证件照留白/测试卡）：应优先选标准纸型避免过扫描拉伸
        var pickedStandard = WindowsPrintService.SelectPaperSize(
            [a4, metric102x152Borderless, metric102x152], 101.6, 152.4,
            savedPreferredPaper: null, systemDefaultPaper: a4, preferBorderless: false);
        Assert.Same(metric102x152, pickedStandard);

        // 3. 公制 10x15cm (100x150mm) 应能匹配 6寸 (101.6x152.4mm) 而不是回退 CustomPhoto
        var pickedMetric = WindowsPrintService.SelectPaperSize(
            [a4, metric10x15], 101.6, 152.4,
            savedPreferredPaper: null, systemDefaultPaper: a4, preferBorderless: true);
        Assert.Same(metric10x15, pickedMetric);

        // 4. 仅存在 A6 (105x148) / 明信片 (100x148) 时不应误匹配 6寸，保持回退 CustomPhoto
        var fallback = WindowsPrintService.SelectPaperSize(
            [a4, a6, postcard], 101.6, 152.4,
            savedPreferredPaper: null, systemDefaultPaper: a4, preferBorderless: true);
        Assert.Equal("CustomPhoto", fallback.PaperName);
        Assert.Equal(400, fallback.Width);
        Assert.Equal(600, fallback.Height);

        // 5. 明信片 (100x148) 应精确命中明信片而不是 A6
        var pickedPostcard = WindowsPrintService.SelectPaperSize(
            [a4, a6, postcard], 100.0, 148.0,
            savedPreferredPaper: null, systemDefaultPaper: a4, preferBorderless: false);
        Assert.Same(postcard, pickedPostcard);
    }

    [Fact]
    public void WindowsPrintService_SelectPaperSize_ShouldPreserveSavedAndSystemDefaultBorderlessPreferences()
    {
        var a4 = new PaperSize("A4", 827, 1169);
        var std4x6 = new PaperSize("4 x 6 in", 400, 600);
        var borderless4x6 = new PaperSize("4 x 6 in (Borderless)", 400, 600);
        var overscan4x6 = new PaperSize("4 x 6 Borderless", 412, 616);

        // 1. 系统默认已选无边距 4x6（即便列表里普通 4x6 排在前面），不应被覆盖回普通 4x6
        var keptSysDefault = WindowsPrintService.SelectPaperSize(
            [a4, std4x6, borderless4x6], 101.6, 152.4,
            savedPreferredPaper: null, systemDefaultPaper: borderless4x6, preferBorderless: true);
        Assert.Same(borderless4x6, keptSysDefault);

        // 2. 软件内已保存过扫描无边距 DEVMODE（尺寸略大于 400x600），应原样保留
        var keptSavedOverscan = WindowsPrintService.SelectPaperSize(
            [a4, std4x6], 101.6, 152.4,
            savedPreferredPaper: overscan4x6, systemDefaultPaper: null, preferBorderless: true);
        Assert.Same(overscan4x6, keptSavedOverscan);

        // 3. 软件内保存的是 A4 但当前打印 6寸，应正常切换到 6寸纸型
        var switchedFromA4 = WindowsPrintService.SelectPaperSize(
            [a4, std4x6, borderless4x6], 101.6, 152.4,
            savedPreferredPaper: a4, systemDefaultPaper: null, preferBorderless: true);
        Assert.Same(borderless4x6, switchedFromA4);
    }

    [Fact]
    public void WindowsPrintService_ComputePrintDrawRect_ShouldKeepExactSizeForEveryPaper()
    {
        foreach (var paper in PaperSizeSpec.BuiltInPapers)
        {
            var portrait = WindowsPrintService.ComputePrintDrawRect(paper.WidthMm, paper.HeightMm, 0f, 0f);
            Assert.Equal(paper.WidthMm, portrait.Width * 25.4, 3);
            Assert.Equal(paper.HeightMm, portrait.Height * 25.4, 3);

            var landscape = WindowsPrintService.ComputePrintDrawRect(paper.HeightMm, paper.WidthMm, 12f, 9f);
            Assert.Equal(-0.12f, landscape.X, 3);
            Assert.Equal(-0.09f, landscape.Y, 3);
            Assert.Equal(paper.HeightMm, landscape.Width * 25.4, 3);
            Assert.Equal(paper.WidthMm, landscape.Height * 25.4, 3);
        }
    }

    [Fact]
    public void MainViewModel_StudioPortraitEnhanceToggle_ShouldToggleUndoRedoAndUpdateActionText()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_vm_studio_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string imgPath = Path.Combine(tempRoot, "portrait.png");
            using (var bmp = new SKBitmap(new SKImageInfo(240, 320, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
            {
                bmp.Erase(new SKColor(180, 150, 130, 255));
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(imgPath, data.ToArray());
            }

            var paths = new PortablePaths(tempRoot);
            var store = new PortableDataStore(paths, new AppLogger(paths));
            using var vm = new MainViewModel(
                checkRecovery: false,
                paths: paths,
                dataStore: store,
                printService: new FakePrintService(),
                dialogs: new FakeDialogService(),
                uiDispatcher: new InlineUiDispatcher(),
                loadBackgroundServices: false);

            var asset = ExifAndColorHelper.InspectAsset(imgPath);
            var item = new AssetItemViewModel(asset, null);
            vm.Assets.Add(item);
            vm.FillAllSlotsWithAsset(item);
            Assert.NotNull(vm.SelectedAsset);
            Assert.False(vm.EditStudioPortraitEnhance);
            Assert.Equal("影棚自动优化", vm.StudioEnhanceActionText);

            vm.ToggleStudioPortraitEnhanceCommand.Execute(null);
            Assert.True(vm.EditStudioPortraitEnhance);
            Assert.Equal("影棚优化已开启", vm.StudioEnhanceActionText);
            Assert.True(vm.CanUndo);

            vm.UndoCommand.Execute(null);
            Assert.False(vm.EditStudioPortraitEnhance);
            Assert.Equal("影棚自动优化", vm.StudioEnhanceActionText);

            vm.RedoCommand.Execute(null);
            Assert.True(vm.EditStudioPortraitEnhance);

            vm.ToggleStudioPortraitEnhanceCommand.Execute(null);
            Assert.False(vm.EditStudioPortraitEnhance);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void MainViewModel_StudioModes_QuickFixAndCustomSuit_ShouldWorkEndToEnd()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_vm_p2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string imgPath = Path.Combine(tempRoot, "lowres_portrait.png");
            using (var bmp = new SKBitmap(new SKImageInfo(200, 280, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
            {
                bmp.Erase(new SKColor(190, 155, 132, 255));
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(imgPath, data.ToArray());
            }

            var paths = new PortablePaths(tempRoot);
            paths.EnsureDirectoriesExist();
            string customSuitPng = Path.Combine(paths.SuitsDirectory, "custom_suit.png");
            using (var sBmp = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul)))
            {
                sBmp.Erase(new SKColor(20, 35, 65, 255));
                using var data = sBmp.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(customSuitPng, data.ToArray());
            }

            var store = new PortableDataStore(paths, new AppLogger(paths));
            using var vm = new MainViewModel(
                checkRecovery: false,
                paths: paths,
                dataStore: store,
                printService: new FakePrintService(),
                dialogs: new FakeDialogService(),
                uiDispatcher: new InlineUiDispatcher(),
                loadBackgroundServices: false);

            var asset = ExifAndColorHelper.InspectAsset(imgPath);
            var item = new AssetItemViewModel(asset, null);
            vm.Assets.Add(item);
            vm.FillAllSlotsWithAsset(item);

            // 自定义 PNG 正装目录扫描与选中
            vm.RefreshFormalSuitsCommand.Execute(null);
            var customOption = Assert.Single(vm.FormalSuitOptions, o => o.Style == FormalSuitStyle.CustomPng);
            vm.SelectedFormalSuitOption = customOption;
            Assert.Equal(FormalSuitStyle.CustomPng, vm.SelectedAsset!.Asset.EditState.FormalSuit);
            Assert.Equal(customSuitPng, vm.SelectedAsset.Asset.EditState.CustomSuitAssetPath);

            // 影棚模式切换与撤销
            vm.EditStudioEnhanceMode = StudioEnhanceMode.Rescue;
            Assert.Equal(StudioEnhanceMode.Rescue, vm.EditStudioEnhanceMode);
            vm.UndoCommand.Execute(null);
            Assert.Equal(StudioEnhanceMode.Studio, vm.EditStudioEnhanceMode);

            // 4. 预检一键修复命令
            vm.EditStudioPortraitEnhance = false;
            var quickFixIssue = new PhotoPrintStudio.Core.Preflight.PreflightIssue(
                PhotoPrintStudio.Core.Preflight.PreflightSeverity.Warning,
                "光照不均",
                "面部存在阴阳脸",
                vm.SelectedSlot?.Id,
                "一键影棚补光");
            vm.ApplyPreflightQuickFixCommand.Execute(quickFixIssue);
            Assert.True(vm.EditStudioPortraitEnhance);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
