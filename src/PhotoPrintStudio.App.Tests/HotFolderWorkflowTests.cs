using System.IO;
using PhotoPrintStudio.App.Printing;
using PhotoPrintStudio.App.Services;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Persistence;
using PhotoPrintStudio.Core.Portable;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.App.Tests;

public class HotFolderWorkflowTests
{
    private sealed class StubDialogService : IDialogService
    {
        public string? OpenFolderPath { get; set; }
        public void ShowInfo(string message, string title) { }
        public void ShowError(string message, string title) { }
        public bool Confirm(string message, string title, bool isWarning = false) => true;
        public DialogChoice ConfirmYesNoCancel(string message, string title) => DialogChoice.No;
        public string? ShowSaveFileDialog(string title, string filter, string defaultExt, string fileName, string? initialDir = null) => null;
        public string? ShowOpenFileDialog(string title, string filter, string? initialDir = null) => null;
        public string? ShowOpenFolderDialog(string title, string? initialDir = null) => OpenFolderPath;
        public void OpenPathInShell(string path) { }
    }

    private sealed class StubPrintService : IPrintService
    {
        public int PrintSheetCalls { get; private set; }
        public bool HasSavedDriverPreferences(string printerName) => false;
        public string GetSavedDevModeBase64(string printerName) => "";
        public void LoadSavedDevModeBase64(string printerName, string? base64) { }
        public bool ShowPrinterPreferencesDialog(string printerName, IntPtr ownerHwnd) => false;
        public IReadOnlyList<string> GetInstalledPrinters() => ["Mock Printer"];
        public PrinterDeviceSummary? InspectPrinter(string printerName) => new(printerName, 0, 0, false, true, ["4x6"]);
        public void PrintSheets(IReadOnlyList<PrintSheet> sheets, IReadOnlyDictionary<string, PhotoAsset> assetsById,
            Func<PhotoAsset, PhotoEditState, SKBitmap> resolveFullResBitmap, string printerName, PrinterCalibrationProfile calibration,
            short copies = 1, int printDpi = 300, bool duplexMirrorBackSide = false, double outputSharpeningAmount = 0, CancellationToken cancellationToken = default)
            => PrintSheetCalls++;
        public void PrintCalibrationCard(double paperWidthMm, double paperHeightMm, string printerName, PrinterCalibrationProfile calibration, int dpi = 300) { }
    }

    private static string WriteSamplePng(string dir, string name, SKColor color)
    {
        string path = Path.Combine(dir, name);
        using var bmp = new SKBitmap(new SKImageInfo(160, 220, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        bmp.Erase(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public async Task ReplaceCurrentSheet_ShouldKeepAllSamplesInPool_InheritStyle_AndNeverSpawnExtraPrintPagesOrAutoPrint()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_hf_vm1_{Guid.NewGuid():N}");
        string camDir = Path.Combine(tempRoot, "cam");
        Directory.CreateDirectory(camDir);
        try
        {
            var paths = new PortablePaths(tempRoot);
            var store = new PortableDataStore(paths, new AppLogger(paths));
            var printSvc = new StubPrintService();
            using var vm = new MainViewModel(
                checkRecovery: false,
                paths: paths,
                dataStore: store,
                printService: printSvc,
                dialogs: new StubDialogService(),
                uiDispatcher: new InlineUiDispatcher(),
                loadBackgroundServices: false);

            // 默认就是 ReplaceCurrentSheet + CloneCurrentPhotoStyle
            Assert.Equal(HotFolderPlacementStrategy.ReplaceCurrentSheet, vm.HotFolderPlacementStrategy);
            Assert.Equal(HotFolderEditTemplateMode.CloneCurrentPhotoStyle, vm.HotFolderEditTemplateMode);

            // 拍第 1 张试拍样张
            string shot1 = WriteSamplePng(camDir, "shot1.png", new SKColor(180, 140, 120, 255));
            Assert.True(await vm.IngestHotFolderPhotoAsync(shot1));
            Assert.Single(vm.Assets);
            Assert.Single(vm.Sheets);

            // 摄影师对第 1 张微调曝光、开启影棚优化并选择电子正装
            vm.EditExposure = 0.35;
            vm.EditStudioPortraitEnhance = true;
            vm.EditStudioEnhanceStrength = 75.0;
            vm.SelectedFormalSuitOption = vm.FormalSuitOptions.First(o => o.Style == FormalSuitStyle.MenNavySuitTie);

            // 连拍第 2 张、第 3 张
            string shot2 = WriteSamplePng(camDir, "shot2.png", new SKColor(190, 150, 130, 255));
            string shot3 = WriteSamplePng(camDir, "shot3.png", new SKColor(200, 160, 140, 255));
            Assert.True(await vm.IngestHotFolderPhotoAsync(shot2));
            Assert.True(await vm.IngestHotFolderPhotoAsync(shot3));

            // 关键防误打断言：3 张样张均完整保存在素材库，但打印队列始终只有 1 页且全页为最新第 3 张！
            Assert.Equal(3, vm.Assets.Count);
            Assert.Single(vm.Sheets);
            Assert.Equal(3, vm.HotFolderImportedCount);
            Assert.Equal(0, printSvc.PrintSheetCalls);

            var latestAsset = vm.Assets[2];
            Assert.Equal(latestAsset.Id, vm.SelectedAsset!.Id);
            Assert.All(vm.ActiveSheet!.Slots, slot => Assert.Equal(latestAsset.Id, slot.PhotoAssetId));

            // 复刻样式验证：第 3 张自动继承第 1 张定版的曝光、影棚优化和正装
            Assert.Equal(0.35, latestAsset.Asset.EditState.Exposure, 3);
            Assert.True(latestAsset.Asset.EditState.EnableStudioPortraitEnhance);
            Assert.Equal(75.0, latestAsset.Asset.EditState.StudioEnhanceStrength, 1);
            Assert.Equal(FormalSuitStyle.MenNavySuitTie, latestAsset.Asset.EditState.FormalSuit);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task PoolOnlySelectLatest_ShouldNeverTouchCurrentPrintSheet()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_hf_vm2_{Guid.NewGuid():N}");
        string camDir = Path.Combine(tempRoot, "cam");
        Directory.CreateDirectory(camDir);
        try
        {
            var paths = new PortablePaths(tempRoot);
            using var vm = new MainViewModel(
                checkRecovery: false,
                paths: paths,
                dataStore: new PortableDataStore(paths, new AppLogger(paths)),
                printService: new StubPrintService(),
                dialogs: new StubDialogService(),
                uiDispatcher: new InlineUiDispatcher(),
                loadBackgroundServices: false);

            // 先放一张已定稿的照片在打印页上
            string approved = WriteSamplePng(camDir, "approved.png", new SKColor(100, 150, 200, 255));
            Assert.True(await vm.IngestHotFolderPhotoAsync(approved));
            string approvedId = vm.Assets[0].Id;
            Assert.All(vm.ActiveSheet!.Slots, s => Assert.Equal(approvedId, s.PhotoAssetId));

            // 切换为「仅入素材库并选中预览」
            vm.HotFolderPlacementStrategy = HotFolderPlacementStrategy.PoolOnlySelectLatest;
            vm.HotFolderAutoStudioEnhance = true;
            vm.HotFolderStudioEnhanceMode = StudioEnhanceMode.Natural;

            string candidate = WriteSamplePng(camDir, "candidate.png", new SKColor(150, 120, 110, 255));
            Assert.True(await vm.IngestHotFolderPhotoAsync(candidate));

            Assert.Equal(2, vm.Assets.Count);
            Assert.Single(vm.Sheets);
            // 画布选中了新样张供检查，但当前打印页上的所有格位仍然锁定为原定稿照片！
            Assert.Equal(vm.Assets[1].Id, vm.SelectedAsset!.Id);
            Assert.True(vm.Assets[1].Asset.EditState.EnableStudioPortraitEnhance);
            Assert.Equal(StudioEnhanceMode.Natural, vm.Assets[1].Asset.EditState.StudioEnhanceMode);
            Assert.All(vm.ActiveSheet.Slots, s => Assert.Equal(approvedId, s.PhotoAssetId));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task FillEmptySlotsOnly_ShouldFillCurrentSheetAndKeepOverflowInPoolWithoutAddingPages()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_hf_vm3_{Guid.NewGuid():N}");
        string camDir = Path.Combine(tempRoot, "cam");
        Directory.CreateDirectory(camDir);
        try
        {
            var paths = new PortablePaths(tempRoot);
            using var vm = new MainViewModel(
                checkRecovery: false,
                paths: paths,
                dataStore: new PortableDataStore(paths, new AppLogger(paths)),
                printService: new StubPrintService(),
                dialogs: new StubDialogService(),
                uiDispatcher: new InlineUiDispatcher(),
                loadBackgroundServices: false);

            vm.SetScenarioCommand.Execute(LayoutScenario.SixInchTwoInOne);
            Assert.Equal(2, vm.ActiveSheet!.Slots.Count);

            vm.HotFolderPlacementStrategy = HotFolderPlacementStrategy.FillEmptySlotsOnly;

            string p1 = WriteSamplePng(camDir, "p1.png", new SKColor(110, 120, 130, 255));
            string p2 = WriteSamplePng(camDir, "p2.png", new SKColor(120, 130, 140, 255));
            string p3 = WriteSamplePng(camDir, "p3.png", new SKColor(130, 140, 150, 255));

            Assert.True(await vm.IngestHotFolderPhotoAsync(p1));
            Assert.True(await vm.IngestHotFolderPhotoAsync(p2));
            Assert.True(await vm.IngestHotFolderPhotoAsync(p3));

            Assert.Equal(3, vm.Assets.Count);
            Assert.Single(vm.Sheets);
            Assert.Equal(vm.Assets[0].Id, vm.ActiveSheet.Slots[0].PhotoAssetId);
            Assert.Equal(vm.Assets[1].Id, vm.ActiveSheet.Slots[1].PhotoAssetId);
            Assert.Equal(0, vm.Assets[2].UsageCount);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task LiveHotFolderWatcher_WithColorPreset_ShouldAutoIngestDroppedFileEndToEnd()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_hf_live_{Guid.NewGuid():N}");
        string camDir = Path.Combine(tempRoot, "live_tether");
        Directory.CreateDirectory(camDir);
        try
        {
            var paths = new PortablePaths(tempRoot);
            using var vm = new MainViewModel(
                checkRecovery: false,
                paths: paths,
                dataStore: new PortableDataStore(paths, new AppLogger(paths)),
                printService: new StubPrintService(),
                dialogs: new StubDialogService(),
                uiDispatcher: new InlineUiDispatcher(),
                loadBackgroundServices: false);

            var preset = vm.Presets.First(p => Math.Abs(p.Parameters.Exposure) > 0.01 || Math.Abs(p.Parameters.Contrast) > 0.01 || Math.Abs(p.Parameters.Vibrance) > 0.01);
            vm.HotFolderWatchDirectory = camDir;
            vm.HotFolderEditTemplateMode = HotFolderEditTemplateMode.ApplyColorPreset;
            vm.HotFolderSelectedColorPreset = preset;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.HotFolderImportedCount) && vm.HotFolderImportedCount >= 1)
                    tcs.TrySetResult();
            };

            Assert.True(vm.StartHotFolderWatch());
            Assert.True(vm.IsHotFolderWatching);

            WriteSamplePng(camDir, "live_camera_001.png", new SKColor(170, 150, 140, 255));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Single(vm.Assets);
            Assert.Equal(preset.Parameters.Exposure, vm.Assets[0].Asset.EditState.Exposure, 3);
            Assert.Equal(preset.Parameters.Contrast, vm.Assets[0].Asset.EditState.Contrast, 3);
            Assert.Equal(preset.Parameters.Vibrance, vm.Assets[0].Asset.EditState.Vibrance, 3);

            vm.StopHotFolderWatch();
            Assert.False(vm.IsHotFolderWatching);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
