using System.IO;
using PhotoPrintStudio.App.Printing;
using PhotoPrintStudio.App.Services;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Persistence;
using PhotoPrintStudio.Core.Portable;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.App.Tests;

public class CustomLayoutEditorTests
{
    private sealed class StubDialogService : IDialogService
    {
        public void ShowInfo(string message, string title) { }
        public void ShowError(string message, string title) { }
        public bool Confirm(string message, string title, bool isWarning = false) => true;
        public DialogChoice ConfirmYesNoCancel(string message, string title) => DialogChoice.No;
        public string? ShowSaveFileDialog(string title, string filter, string defaultExt, string fileName, string? initialDir = null) => null;
        public string? ShowOpenFileDialog(string title, string filter, string? initialDir = null) => null;
        public string? ShowOpenFolderDialog(string title, string? initialDir = null) => null;
        public void OpenPathInShell(string path) { }
        public void RevealFileInShell(string filePath) { }
    }

    private sealed class StubPrintService : IPrintService
    {
        public bool HasSavedDriverPreferences(string printerName) => false;
        public string GetSavedDevModeBase64(string printerName) => "";
        public void LoadSavedDevModeBase64(string printerName, string? base64) { }
        public bool ShowPrinterPreferencesDialog(string printerName, IntPtr ownerHwnd) => false;
        public IReadOnlyList<string> GetInstalledPrinters() => ["Mock Printer"];
        public PrinterDeviceSummary? InspectPrinter(string printerName) => new(printerName, 0, 0, false, true, ["4x6"]);
        public void PrintSheets(IReadOnlyList<PrintSheet> sheets, IReadOnlyDictionary<string, PhotoAsset> assetsById,
            Func<PhotoAsset, PhotoEditState, SKBitmap> resolveFullResBitmap, string printerName, PrinterCalibrationProfile calibration,
            short copies = 1, int printDpi = 300, bool duplexMirrorBackSide = false, double outputSharpeningAmount = 0, CancellationToken cancellationToken = default) { }
        public void PrintCalibrationCard(double paperWidthMm, double paperHeightMm, string printerName, PrinterCalibrationProfile calibration, int dpi = 300) { }
    }

    private static MainViewModel CreateVm(string root)
    {
        var paths = new PortablePaths(root);
        return new MainViewModel(
            checkRecovery: false,
            paths: paths,
            dataStore: new PortableDataStore(paths, new AppLogger(paths)),
            printService: new StubPrintService(),
            dialogs: new StubDialogService(),
            uiDispatcher: new InlineUiDispatcher(),
            loadBackgroundServices: false);
    }

    private static AssetItemViewModel AddSampleAsset(MainViewModel vm, string dir, string name, SKColor color)
    {
        string path = Path.Combine(dir, name);
        using (var bmp = new SKBitmap(new SKImageInfo(240, 320, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
        {
            bmp.Erase(color);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        var asset = ExifAndColorHelper.InspectAsset(path);
        var item = new AssetItemViewModel(asset, null);
        vm.Assets.Add(item);
        return item;
    }

    [Fact]
    public void CustomLayoutEditorViewModel_EditOperations_And_UndoRedo_ShouldWorkEndToEnd()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_editor_vm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var mainVm = CreateVm(tempRoot);
            var editor = new CustomLayoutEditorViewModel(mainVm);

            // 清空后应不可保存
            editor.ClearAllFrames();
            Assert.Empty(editor.Frames);
            Assert.False(editor.CanSave);

            // 添加 1 个图片框并精确设定位置与大小
            editor.AddFrame();
            Assert.Single(editor.Frames);
            Assert.True(editor.CanSave);

            editor.SetFrameSize(63.1, 90.0, widthDriven: true);
            editor.SetFramePosition(4.0, 6.0);
            Assert.Equal(4.0, editor.SelectedXMm, 2);
            Assert.Equal(6.0, editor.SelectedYMm, 2);
            Assert.Equal(63.1, editor.SelectedWidthMm, 2);
            Assert.Equal(90.0, editor.SelectedHeightMm, 2);
            Assert.Equal(Math.Round(editor.PaperWidthMm - 4.0 - 63.1, 2), editor.SelectedRightMarginMm, 2);
            Assert.Equal(Math.Round(editor.PaperHeightMm - 6.0 - 90.0, 2), editor.SelectedBottomMarginMm, 2);

            // 修改右边距应反向推动 X
            editor.SetFrameRightMargin(10.0);
            Assert.Equal(Math.Round(editor.PaperWidthMm - 63.1 - 10.0, 2), editor.SelectedXMm, 2);

            // 锁定宽高比缩放
            editor.LockAspectRatio = true;
            editor.SetFrameSize(31.55, 90.0, widthDriven: true);
            Assert.Equal(45.0, editor.SelectedHeightMm, 1);
            editor.LockAspectRatio = false;

            // 旋转 90° 与复制
            editor.RotateSelected90();
            Assert.Equal(90.0, editor.SelectedRotationDegrees, 1);
            editor.DuplicateSelectedFrames();
            Assert.Equal(2, editor.Frames.Count);

            // 撤销复制 -> 回到 1 个框；重做 -> 恢复 2 个框
            Assert.True(editor.CanUndo);
            editor.Undo();
            Assert.Single(editor.Frames);
            Assert.True(editor.CanRedo);
            editor.Redo();
            Assert.Equal(2, editor.Frames.Count);

            // 快速生成 2x2 网格
            editor.GridRows = 2;
            editor.GridCols = 2;
            editor.GridMarginMm = 5;
            editor.GridGapMm = 4;
            editor.ApplyQuickGrid();
            Assert.Equal(4, editor.Frames.Count);
            Assert.True(editor.CanSave);
            Assert.False(editor.HasOutOfBounds);
            Assert.False(editor.HasOverlaps);

            // 构建模板校验
            editor.TemplateName = "测试自由四格";
            var tpl = editor.BuildTemplate();
            Assert.True(tpl.IsCustomFreeform);
            Assert.Equal("测试自由四格", tpl.Name);
            Assert.Equal(4, tpl.CustomSlots!.Count);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void MainViewModel_ApplyCustomFreeformTemplate_SaveUpdateAndApplyOnly_ShouldPreservePhotosAndSupportUndo()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_editor_main_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var mainVm = CreateVm(tempRoot);
            mainVm.SetScenarioCommand.Execute(LayoutScenario.FreeGrid);
            var p1 = AddSampleAsset(mainVm, tempRoot, "a.png", SKColors.Red);
            var p2 = AddSampleAsset(mainVm, tempRoot, "b.png", SKColors.Blue);
            mainVm.AssignAssetToSlot(p1, mainVm.ActiveSheet!.Slots[0]);
            mainVm.AssignAssetToSlot(p2, mainVm.ActiveSheet.Slots[1]);

            // 1. 通过编辑器创建自由版式并保存应用
            var editor = new CustomLayoutEditorViewModel(mainVm);
            editor.ClearAllFrames();
            editor.AddFrame();
            editor.SetFrameSize(63.0, 90.0, widthDriven: true);
            editor.SetFramePosition(5.0, 5.8);
            editor.AddFrame();
            editor.SetFrameSize(70.0, 70.0, widthDriven: true);
            editor.SetFramePosition(76.0, 15.8);
            editor.TemplateName = "左右非对称版式";
            editor.Scenario = LayoutScenario.FreeGrid;
            var tpl = editor.BuildTemplate();

            mainVm.ApplyCustomFreeformTemplate(tpl, saveToStore: true);

            Assert.True(mainVm.IsCustomFreeformLayout);
            Assert.False(mainVm.CanEditAutoLayoutSliders);
            Assert.Equal(tpl.Id, mainVm.SelectedTemplate?.Id);
            Assert.Equal(2, mainVm.ActiveSheet!.Slots.Count);
            Assert.Equal(63.0, mainVm.ActiveSheet.Slots[0].WidthMm, 2);
            Assert.Equal(70.0, mainVm.ActiveSheet.Slots[1].WidthMm, 2);
            // 保留已放置的照片
            Assert.Equal(p1.Id, mainVm.ActiveSheet.Slots[0].PhotoAssetId);
            Assert.Equal(p2.Id, mainVm.ActiveSheet.Slots[1].PhotoAssetId);

            // 2. 二次打开该模板进行编辑并覆盖保存，应实时更新当前页
            var reEditor = new CustomLayoutEditorViewModel(mainVm, mainVm.SelectedTemplate);
            Assert.True(reEditor.IsEditingExistingTemplate);
            Assert.Equal(2, reEditor.Frames.Count);
            reEditor.AddFrame();
            var updatedTpl = reEditor.BuildTemplate(forceNewId: false);
            Assert.Equal(tpl.Id, updatedTpl.Id);

            mainVm.ApplyCustomFreeformTemplate(updatedTpl, saveToStore: true);
            Assert.Equal(3, mainVm.ActiveSheet.Slots.Count);
            Assert.Equal(p1.Id, mainVm.ActiveSheet.Slots[0].PhotoAssetId);
            Assert.Equal(p2.Id, mainVm.ActiveSheet.Slots[1].PhotoAssetId);

            // 3. “仅应用到当前页” 不新增模板文件，但立即更新当前页且可撤销
            int templateCountBefore = mainVm.DataStore.LoadCustomTemplates().Count;
            var adHocEditor = new CustomLayoutEditorViewModel(mainVm);
            adHocEditor.ClearAllFrames();
            adHocEditor.AddFrame();
            adHocEditor.SetFrameSize(100, 80, widthDriven: true);
            adHocEditor.SetFramePosition(10, 10);
            var adHocTpl = adHocEditor.BuildTemplate(forceNewId: true);

            mainVm.ApplyCustomFreeformTemplate(adHocTpl, saveToStore: false);
            Assert.Equal(templateCountBefore, mainVm.DataStore.LoadCustomTemplates().Count);
            Assert.Single(mainVm.ActiveSheet.Slots);
            Assert.Equal(100, mainVm.ActiveSheet.Slots[0].WidthMm, 2);
            Assert.Equal(p1.Id, mainVm.ActiveSheet.Slots[0].PhotoAssetId);

            // 撤销回到 3 格状态
            mainVm.UndoCommand.Execute(null);
            Assert.Equal(3, mainVm.ActiveSheet.Slots.Count);

            // 纸张换向后若放不下格位，保留原版式。
            adHocTpl.Id = $"tpl_adhoc_{Guid.NewGuid():N}";
            mainVm.ApplyCustomFreeformTemplate(adHocTpl, saveToStore: false);
            Assert.True(mainVm.IsCustomFreeformLayout);
            bool prevLandscape = mainVm.IsLandscape;
            mainVm.IsLandscape = !prevLandscape;
            Assert.Equal(prevLandscape, mainVm.IsLandscape);
            Assert.Equal(prevLandscape, mainVm.ActiveSheet.IsLandscape);
            Assert.Single(mainVm.ActiveSheet.Slots);
            Assert.Equal(100, mainVm.ActiveSheet.Slots[0].WidthMm, 2);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void CustomLayoutEditorControls_ShouldMeasureArrangeAndRenderWithoutExceptions()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_editor_ui_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            try
            {
                using var mainVm = CreateVm(tempRoot);
                var editorVm = new CustomLayoutEditorViewModel(mainVm);
                editorVm.ClearAllFrames();
                editorVm.AddFrame();
                editorVm.SetFrameSize(63.1, 90.0, widthDriven: true);
                editorVm.SetFramePosition(4.0, 6.0);
                editorVm.RotateSelected90();

                var canvas = new PhotoPrintStudio.App.Controls.LayoutDesignerCanvas
                {
                    ViewModel = editorVm,
                    Width = 600,
                    Height = 400
                };
                canvas.Measure(new System.Windows.Size(600, 400));
                canvas.Arrange(new System.Windows.Rect(0, 0, 600, 400));
                var rtbCanvas = new System.Windows.Media.Imaging.RenderTargetBitmap(600, 400, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtbCanvas.Render(canvas);

                var diagram = new PhotoPrintStudio.App.Controls.MarginDiagramControl
                {
                    HasSelection = true,
                    PaperWidthMm = editorVm.PaperWidthMm,
                    PaperHeightMm = editorVm.PaperHeightMm,
                    FrameXMm = editorVm.SelectedXMm,
                    FrameYMm = editorVm.SelectedYMm,
                    FrameWidthMm = editorVm.SelectedWidthMm,
                    FrameHeightMm = editorVm.SelectedHeightMm,
                    Width = 260,
                    Height = 158
                };
                diagram.Measure(new System.Windows.Size(260, 158));
                diagram.Arrange(new System.Windows.Rect(0, 0, 260, 158));
                var rtbDiagram = new System.Windows.Media.Imaging.RenderTargetBitmap(260, 158, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtbDiagram.Render(diagram);

                var thumb = new PhotoPrintStudio.App.Controls.FrameMiniThumbControl
                {
                    PaperWidthMm = editorVm.PaperWidthMm,
                    PaperHeightMm = editorVm.PaperHeightMm,
                    FrameXMm = editorVm.SelectedXMm,
                    FrameYMm = editorVm.SelectedYMm,
                    FrameWidthMm = editorVm.SelectedWidthMm,
                    FrameHeightMm = editorVm.SelectedHeightMm,
                    Width = 36,
                    Height = 26
                };
                thumb.Measure(new System.Windows.Size(36, 26));
                thumb.Arrange(new System.Windows.Rect(0, 0, 36, 26));
                var rtbThumb = new System.Windows.Media.Imaging.RenderTargetBitmap(36, 26, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtbThumb.Render(thumb);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
            finally
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(threadEx);
    }
}
