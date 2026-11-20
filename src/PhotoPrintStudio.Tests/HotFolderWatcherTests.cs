using System.Collections.Concurrent;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Persistence;
using PhotoPrintStudio.Core.Portable;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.Tests;

public class HotFolderWatcherTests
{
    private static byte[] CreateValidPngBytes(SKColor color, int w = 64, int h = 80)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        bmp.Erase(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task HotFolderWatcher_ShouldIgnoreExistingOnStart_AndEmitNewImageWhenCreatedOrRenamedFromTmp()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_hf_core_{Guid.NewGuid():N}");
        string watchDir = Path.Combine(tempRoot, "camera_out");
        Directory.CreateDirectory(watchDir);
        try
        {
            byte[] pngBytes = CreateValidPngBytes(new SKColor(120, 160, 210, 255));
            string oldPhoto = Path.Combine(watchDir, "old_existing.png");
            File.WriteAllBytes(oldPhoto, pngBytes);

            var paths = new PortablePaths(tempRoot);
            var logger = new AppLogger(paths);
            using var watcher = new HotFolderWatcherService(logger);

            var received = new ConcurrentQueue<string>();
            var tcsFirst = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tcsSecond = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            watcher.PhotoReady += path =>
            {
                received.Enqueue(path);
                if (!tcsFirst.Task.IsCompleted) tcsFirst.TrySetResult(path);
                else tcsSecond.TrySetResult(path);
            };

            Assert.True(watcher.Start(watchDir, includeSubdirectories: false, ignoreExistingOnStart: true, out string err), err);
            Assert.True(watcher.IsWatching);

            // 非图片与隐藏临时文件不应触发
            File.WriteAllText(Path.Combine(watchDir, "readme.txt"), "ignore me");
            File.WriteAllBytes(Path.Combine(watchDir, "~$temp.png"), pngBytes);
            File.WriteAllBytes(Path.Combine(watchDir, ".hidden.jpg"), pngBytes);

            // 1. 直接写入新照片
            string shot1 = Path.Combine(watchDir, "IMG_0001.png");
            File.WriteAllBytes(shot1, pngBytes);

            string got1 = await tcsFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Path.GetFullPath(shot1), Path.GetFullPath(got1), StringComparer.OrdinalIgnoreCase);

            // 2. 模拟相机软件先写 .tmp 再原子重命名为 .png
            string tmpFile = Path.Combine(watchDir, "IMG_0002.tmp");
            string shot2 = Path.Combine(watchDir, "IMG_0002.png");
            File.WriteAllBytes(tmpFile, pngBytes);
            File.Move(tmpFile, shot2);

            string got2 = await tcsSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Path.GetFullPath(shot2), Path.GetFullPath(got2), StringComparer.OrdinalIgnoreCase);

            await Task.Delay(350);
            var all = received.ToArray();
            Assert.Equal(2, all.Length);
            Assert.DoesNotContain(all, p => p.EndsWith("old_existing.png", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task HotFolderWatcher_ShouldWaitForSlowIncrementalWriteToFinish_BeforeEmitting()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_hf_slow_{Guid.NewGuid():N}");
        string watchDir = Path.Combine(tempRoot, "slow_camera");
        Directory.CreateDirectory(watchDir);
        try
        {
            byte[] fullPng = CreateValidPngBytes(new SKColor(200, 140, 100, 255), 180, 220);
            int half = fullPng.Length / 2;

            var paths = new PortablePaths(tempRoot);
            using var watcher = new HotFolderWatcherService(new AppLogger(paths));
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.PhotoReady += path => tcs.TrySetResult(path);

            Assert.True(watcher.Start(watchDir, includeSubdirectories: false, ignoreExistingOnStart: true, out _));

            string target = Path.Combine(watchDir, "slow_shot.png");
            await using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await fs.WriteAsync(fullPng.AsMemory(0, half));
                await fs.FlushAsync();
                await Task.Delay(320); // 模拟无线传图缓慢写入中
                Assert.False(tcs.Task.IsCompleted, "PhotoReady must wait until the exclusive write completes");
                await fs.WriteAsync(fullPng.AsMemory(half));
                await fs.FlushAsync();
            }

            string emitted = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(emitted), StringComparer.OrdinalIgnoreCase);

            using var decoded = SKBitmap.Decode(emitted);
            Assert.NotNull(decoded);
            Assert.Equal(180, decoded!.Width);
            Assert.Equal(220, decoded.Height);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void HotFolderWatchSettings_ShouldRoundTripThroughAppPortableConfig()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_hf_cfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var paths = new PortablePaths(tempRoot);
            var store = new PortableDataStore(paths, new AppLogger(paths));
            var cfg = new AppPortableConfig
            {
                HotFolderWatch = new HotFolderWatchSettings
                {
                    WatchDirectory = @"C:\Example\Camera",
                    IncludeSubdirectories = true,
                    IgnoreExistingOnStart = false,
                    PlacementStrategy = HotFolderPlacementStrategy.FillEmptySlotsOnly,
                    EditTemplateMode = HotFolderEditTemplateMode.ApplyColorPreset,
                    ColorPresetId = "preset_id_clean",
                    AutoStudioEnhance = true,
                    StudioEnhanceStrength = 75,
                    StudioEnhanceMode = StudioEnhanceMode.Rescue,
                    AutoBackgroundReplace = true,
                    ReplacementBackgroundHex = "#FFFFFF",
                    AutoFaceFrame = true,
                    AutoSelectLatest = false
                }
            };

            store.SaveConfig(cfg);
            var loaded = store.LoadConfig();
            var hf = loaded.HotFolderWatch;
            Assert.Equal(@"C:\Example\Camera", hf.WatchDirectory);
            Assert.True(hf.IncludeSubdirectories);
            Assert.False(hf.IgnoreExistingOnStart);
            Assert.Equal(HotFolderPlacementStrategy.FillEmptySlotsOnly, hf.PlacementStrategy);
            Assert.Equal(HotFolderEditTemplateMode.ApplyColorPreset, hf.EditTemplateMode);
            Assert.Equal("preset_id_clean", hf.ColorPresetId);
            Assert.True(hf.AutoStudioEnhance);
            Assert.Equal(75, hf.StudioEnhanceStrength);
            Assert.Equal(StudioEnhanceMode.Rescue, hf.StudioEnhanceMode);
            Assert.True(hf.AutoBackgroundReplace);
            Assert.Equal("#FFFFFF", hf.ReplacementBackgroundHex);
            Assert.True(hf.AutoFaceFrame);
            Assert.False(hf.AutoSelectLatest);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
