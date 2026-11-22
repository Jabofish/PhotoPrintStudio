using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.Helpers;

/// <summary>
/// 从生产入口 App.xaml.cs 剥离的独立 UI 冒烟截图运行器（--smoke-test）。
/// </summary>
internal static class SmokeTestRunner
{
    public static async Task RunAsync(MainWindow window, MainViewModel vm, string outDir, Action<string, Exception> onError)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            vm.Appearance = AppearanceMode.Dark;
            await Task.Delay(700);

            await vm.LoadSamplePhotosCommand.ExecuteAsync(null);
            vm.SetScenarioCommand.Execute(LayoutScenario.Polaroid);
            vm.RightTabIndex = 1;
            vm.EditBrightness = 8;
            vm.EditContrast = 14;
            vm.EditSaturation = 12;
            vm.OnEditMutated();
            await Task.Delay(900);
            SaveWpfVisual(window, Path.Combine(outDir, "ui_v2_dark_polaroid.png"));

            vm.RightTabIndex = 0;
            vm.ColorSubTabIndex = 1;
            vm.SetViewModeCommand.Execute(CanvasViewMode.SingleRetouchCrop);
            await Task.Delay(800);
            SaveWpfVisual(window, Path.Combine(outDir, "ui_v2_dark_curves_single.png"));

            vm.SetViewModeCommand.Execute(CanvasViewMode.SheetOverview);
            vm.SetScenarioCommand.Execute(LayoutScenario.SixInchTwoInOne);
            vm.RightTabIndex = 2;
            vm.SelectedPrinterName = "";
            vm.Appearance = AppearanceMode.Light;
            await Task.Delay(900);
            SaveWpfVisual(window, Path.Combine(outDir, "ui_v2_light_six2in1.png"));

            vm.Appearance = AppearanceMode.Dark;
            vm.RightTabIndex = 0;
            vm.ColorSubTabIndex = 0;
            vm.SetScenarioCommand.Execute(LayoutScenario.IdPhoto);
            vm.FillAllSlotsWithAsset(vm.Assets[0]);
            await Task.Delay(700);
            SaveWpfVisual(window, Path.Combine(outDir, "ui_v2_dark_idphoto.png"));

            vm.SaveConfig();
            window.Close();
        }
        catch (Exception ex)
        {
            onError("SmokeTest", ex);
            Application.Current?.Shutdown(-2);
        }
    }

    private static void SaveWpfVisual(Window window, string filePath)
    {
        window.UpdateLayout();
        var root = window.Content as FrameworkElement ?? window;
        int w = Math.Max(1, (int)Math.Round(root.ActualWidth));
        int h = Math.Max(1, (int)Math.Round(root.ActualHeight));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var bounds = new Rect(0, 0, w, h);
        var screenshot = new DrawingVisual();
        using (DrawingContext drawing = screenshot.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, bounds);
            drawing.DrawRectangle(new VisualBrush(root)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = bounds,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            }, null, bounds);
        }
        rtb.Render(screenshot);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        enc.Save(fs);
    }
}
