using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Portable;
using PhotoPrintStudio.Core.Rendering;
using SkiaSharp;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static readonly List<string> Failures = [];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: PerformanceTests <photo> <report.json> <models-directory>");
            return 2;
        }
        var paths = new PortablePaths();
        paths.EnsureDirectoriesExist();
        foreach (string model in new[] { ModelManifestLoader.ManifestFileName, PortraitMattingService.PasPhotoMattingModelFile, "face_detection_yunet_2023mar.onnx", "face_landmarks.onnx" })
        {
            string src = Path.Combine(args[2], model);
            if (File.Exists(src)) File.Copy(src, Path.Combine(paths.ModelsDirectory, model), true);
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.BeginInvoke(async () =>
        {
            int exitCode = 0;
            try { await RunAsync(args[0]); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { exitCode, processorCount=Environment.ProcessorCount,
                framework=RuntimeInformation.FrameworkDescription, failures=Failures, results = Results }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(exitCode);
        });
        return app.Run();
    }

    private static async Task RunAsync(string photo)
    {
        using var vm = new MainViewModel(checkRecovery: false);
        var asset = ExifAndColorHelper.InspectAsset(photo);
        var item = new AssetItemViewModel(asset, null);
        vm.Assets.Add(item);
        await FrameAsync(vm, "first_import", () => vm.FillAllSlotsWithAsset(item));
        await Task.Delay(300);

        foreach (var mode in new[] { CanvasViewMode.SheetOverview, CanvasViewMode.SingleRetouchCrop, CanvasViewMode.SplitCompare })
        {
            await FrameAsync(vm, mode + ":switch", () => vm.SetViewModeCommand.Execute(mode));
            for (int i = 0; i < 3; i++)
            {
                double exposure = i * .2 + .25;
                await FrameAsync(vm, mode + ":exposure", () => vm.EditExposure = exposure);
            }
            await FrameAsync(vm, mode + ":burst", () =>
            {
                for (int i = 0; i < 20; i++) vm.EditExposure = .8 + i * .025;
            });
        }

        await FrameAsync(vm, "matting:first", () => vm.EditAiBackgroundReplace = true);
        await FrameAsync(vm, "matting:cached_exposure", () => vm.EditExposure = .4);
        await FrameAsync(vm, "skin", () => vm.EditSkinSoftening = 35);
        await FrameAsync(vm, "clarity", () => vm.EditClarity = 25);
        await FrameAsync(vm, "sharpen", () => vm.EditSharpening = 30);
        await FrameAsync(vm, "combined:exposure", () => vm.EditExposure = .65);
        int dragFrames = 0;
        void OnPixelsChanged(object? _, EventArgs __) => dragFrames++;
        WriteableBitmap? observed = null;
        void ObserveCurrentBitmap(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(vm.CanvasPreviewBitmap)) return;
            if (observed != null) observed.Changed -= OnPixelsChanged;
            observed = vm.CanvasPreviewBitmap;
            if (observed == null) return;
            observed.Changed += OnPixelsChanged;
            dragFrames++;
        }
        observed = vm.CanvasPreviewBitmap!;
        observed.Changed += OnPixelsChanged;
        vm.PropertyChanged += ObserveCurrentBitmap;
        await MeasureFrameAsync(vm, "combined:drag", async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(16);
                vm.EditExposure = .8 + i * .025;
            }
        });
        vm.PropertyChanged -= ObserveCurrentBitmap;
        if (observed != null) observed.Changed -= OnPixelsChanged;
        Results.Add(new { name="combined:drag_frames", dragFrames });
        Verify(dragFrames > 1, "continuous dragging publishes intermediate preview frames");
        var colorHistogram = vm.ActiveHistogram;
        await FrameAsync(vm, "geometry:pan", () => vm.EditPanX = .1);
        Results.Add(new { name="geometry:cache", reused=ReferenceEquals(colorHistogram, vm.ActiveHistogram) });
        Verify(ReferenceEquals(colorHistogram, vm.ActiveHistogram), "pan reuses processed colors and histogram");

        using var proxy = vm.ImageCache.GetReadOnlyPreviewProxy(asset, 1300);
        var faces = vm.FaceService.DetectFacesWithQuarterTurns(proxy, asset.EditState.QuarterTurns);
        var alpha = vm.MattingService.ComputeCompactAlphaMatte(proxy, faces);
        using (var expected = FloatImageProcessor.Process(proxy, asset.EditState, alpha))
            Verify(expected.Histogram.Red.SequenceEqual(vm.ActiveHistogram!.Red) &&
                expected.Histogram.Green.SequenceEqual(vm.ActiveHistogram.Green) &&
                expected.Histogram.Blue.SequenceEqual(vm.ActiveHistogram.Blue), "latest edit histogram matches direct processing");

        int resolveCalls = 0;
        var exportClock = Stopwatch.StartNew();
        ExportService.ExportSingleSheetImage(Path.Combine(AppContext.BaseDirectory,"performance-output.jpg"), vm.ActiveSheet!,
            new Dictionary<string, PhotoAsset> { [asset.Id]=asset },
            (a,e) => { resolveCalls++; return vm.ResolveFullResBitmapForExport(a,e); }, ExportImageFormat.Jpeg);
        var exportMeasurement = new { name="export:repeated_asset", ms=exportClock.Elapsed.TotalMilliseconds, resolveCalls, slots=vm.ActiveSheet!.Slots.Count };
        Results.Add(exportMeasurement);
        Console.WriteLine(JsonSerializer.Serialize(exportMeasurement));
        Verify(resolveCalls == 1, "repeated ID photo is processed once for export");

        foreach (var edit in new[] {
            new PhotoEditState(), new PhotoEditState { Exposure=.7 },
            new PhotoEditState { SkinSoftening=35 }, new PhotoEditState { Clarity=25 },
            new PhotoEditState { Sharpening=30 } })
        {
            var clock = Stopwatch.StartNew();
            using var processed = FloatImageProcessor.Process(proxy, edit);
            var pixels = new byte[processed.Bitmap.RowBytes*processed.Bitmap.Height];
            Marshal.Copy(processed.Bitmap.GetPixels(),pixels,0,pixels.Length);
            var measurement = new { name="processor", edit.Exposure, edit.SkinSoftening, edit.Clarity, edit.Sharpening,
                ms=clock.Elapsed.TotalMilliseconds, sha256=Convert.ToHexString(SHA256.HashData(pixels)) };
            Results.Add(measurement);
            Console.WriteLine(JsonSerializer.Serialize(measurement));
        }
        await Task.Delay(200);
        var otherAsset = ExifAndColorHelper.InspectAsset(photo);
        var otherItem = new AssetItemViewModel(otherAsset,null);
        await FrameAsync(vm,"context:change_asset",()=>
        {
            vm.EditExposure=-1;
            vm.Assets.Add(otherItem);
            vm.FillAllSlotsWithAsset(otherItem);
        });
        using (var expected = FloatImageProcessor.Process(proxy,new PhotoEditState()))
            Verify(vm.SelectedAsset==otherItem && expected.Histogram.Red.SequenceEqual(vm.ActiveHistogram!.Red),
                "changing the selected asset discards the previous rendering context");
        var measured = Results.Select(r=>JsonSerializer.SerializeToElement(r)).ToList();
        double singleBytes = measured.Where(r=>r.GetProperty("name").GetString()=="SheetOverview:exposure")
            .Max(r=>r.GetProperty("allocatedMiB").GetDouble());
        double burstBytes = measured.Single(r=>r.GetProperty("name").GetString()=="SheetOverview:burst").GetProperty("allocatedMiB").GetDouble();
        Verify(burstBytes < singleBytes*4, "20 edits have bounded rendering allocations");
        if (Failures.Count>0) throw new InvalidOperationException(string.Join("; ",Failures));
    }

    private static void Verify(bool condition, string description)
    {
        Console.WriteLine((condition?"PASS ":"FAIL ")+description);
        if(!condition) Failures.Add(description);
    }

    private static Task FrameAsync(MainViewModel vm, string name, Action change) =>
        MeasureFrameAsync(vm, name, () => { change(); return Task.CompletedTask; });

    private static async Task MeasureFrameAsync(MainViewModel vm, string name, Func<Task> change)
    {
        vm.ActiveHistogram = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool applyingChanges = true;
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!applyingChanges && e.PropertyName == nameof(vm.ActiveHistogram) && vm.ActiveHistogram != null)
                completion.TrySetResult();
        }
        vm.PropertyChanged += OnChanged;
        long beforeBytes = GC.GetTotalAllocatedBytes(true);
        using var process = Process.GetCurrentProcess();
        var beforeCpu = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        try
        {
            await change();
            applyingChanges = false;
            double setterMs = clock.Elapsed.TotalMilliseconds;
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var measurement = new { name, ms=clock.Elapsed.TotalMilliseconds, setterMs, tailMs=clock.Elapsed.TotalMilliseconds-setterMs,
                cpuMs=(process.TotalProcessorTime-beforeCpu).TotalMilliseconds,
                allocatedMiB=(GC.GetTotalAllocatedBytes(true)-beforeBytes)/1048576.0 };
            Results.Add(measurement);
            Console.WriteLine(JsonSerializer.Serialize(measurement));
        }
        finally { vm.PropertyChanged -= OnChanged; }
    }
}
