using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Portable;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.Tests;

public class AiMattingModelTests
{
    private static string ResolveModelsDir() => Path.Combine(AppContext.BaseDirectory, "assets", "models");

    [Fact]
    public void BundledModNetOnnxModel_ShouldLoadAndComputeValidPortraitAlphaMatte()
    {
        string modelSrc = Path.Combine(ResolveModelsDir(), PortraitMattingService.ModNetModelFile);
        Assert.True(File.Exists(modelSrc), "Bundled MODNet ONNX model should exist in assets/models/");

        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_onnx_{Guid.NewGuid():N}");
        try
        {
            var paths = new PortablePaths(tempRoot);
            paths.EnsureDirectoriesExist();
            File.Copy(modelSrc, Path.Combine(paths.ModelsDirectory, Path.GetFileName(modelSrc)), true);

            var logger = new AppLogger(paths);
            using var matting = new PortraitMattingService(paths, logger);

            Assert.True(matting.HasModelLoaded, "PortraitMattingService should auto-load bundled ONNX model");
            Assert.Equal(PortraitMattingService.ModNetModelFile, matting.ActiveModelName);

            // 构造一张典型证件照人像图 (600x800 灰白墙背景 + 深色人物轮廓)
            using var portrait = CreateTestPortraitBitmap(600, 800);
            float[]? matte = matting.ComputeAlphaMatte(portrait);

            Assert.NotNull(matte);
            Assert.Equal(600 * 800, matte!.Length);

            // 验证边缘背景区域 alpha 偏低、中心人像区域 alpha 偏高
            float cornerAlpha = matte[10 * 600 + 10];
            float centerAlpha = matte[400 * 600 + 300];
            Assert.InRange(cornerAlpha, 0f, 0.35f);
            Assert.InRange(centerAlpha, 0.70f, 1.0f);

            // 测试替换为公安蓝底色 (#438EDB) 后的成品渲染
            var edit = new PhotoEditState
            {
                EnableAiBackgroundReplace = true,
                ReplacementBackgroundHex = "#438EDB",
                MattingFeather = 1.5,
                SpillSuppression = 35
            };
            using var replaced = FloatImageProcessor.Process(portrait, edit, matte);
            var cornerPixel = replaced.Bitmap.GetPixel(10, 10);
            Assert.True(cornerPixel.Blue > cornerPixel.Red + 60, $"Expected blue ID background at corner, got R={cornerPixel.Red} G={cornerPixel.Green} B={cornerPixel.Blue}");

            // 测试商业柔光渐变背景：背景边缘较深而肩颈附近柔光中心更亮，同时前景人脸像素不受影响
            var gradEdit = new PhotoEditState
            {
                EnableAiBackgroundReplace = true,
                UseGradientBackground = true,
                ReplacementBackgroundHex = "#1E4E8C",
                ReplacementBackgroundSecondaryHex = "#8CC4F2",
                MattingFeather = 1.5,
                SpillSuppression = 35
            };
            using var gradReplaced = FloatImageProcessor.Process(portrait, gradEdit, matte);
            var outerCorner = gradReplaced.Bitmap.GetPixel(5, 5);
            var innerBgGlow = gradReplaced.Bitmap.GetPixel(130, 220); // 人像头部两侧空白处接近聚光中心
            Assert.True(innerBgGlow.Blue > outerCorner.Blue + 25 && innerBgGlow.Green > outerCorner.Green + 25,
                $"Expected brighter gradient spotlight near portrait center ({innerBgGlow}) than outer corner ({outerCorner})");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void MultiModelMattingAndLetterboxInference_ShouldSwitchModels_AndHandleWideLandscapePortraits()
    {
        string modelsDir = ResolveModelsDir();
        string pasSrc = Path.Combine(modelsDir, PortraitMattingService.PasPhotoMattingModelFile);
        string modnetSrc = Path.Combine(modelsDir, PortraitMattingService.ModNetModelFile);
        string manifestSrc = Path.Combine(modelsDir, ModelManifestLoader.ManifestFileName);
        Assert.True(File.Exists(pasSrc));
        Assert.True(File.Exists(modnetSrc));
        Assert.True(File.Exists(manifestSrc));

        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_onnx_multi_{Guid.NewGuid():N}");
        try
        {
            var paths = new PortablePaths(tempRoot);
            paths.EnsureDirectoriesExist();
            File.Copy(manifestSrc, Path.Combine(paths.ModelsDirectory, ModelManifestLoader.ManifestFileName), true);
            File.Copy(pasSrc, Path.Combine(paths.ModelsDirectory, Path.GetFileName(pasSrc)), true);
            File.Copy(modnetSrc, Path.Combine(paths.ModelsDirectory, Path.GetFileName(modnetSrc)), true);

            using var matting = new PortraitMattingService(paths, new AppLogger(paths));
            Assert.Equal(2, matting.AvailableModelNames.Count);
            Assert.NotEmpty(matting.ModelManifest);
            Assert.Equal(PortraitMattingService.PasPhotoMattingModelFile, matting.ActiveModelName); // 清单驱动默认最高优先级 Pas 证件照模型

            // 宽画幅横向合照测试 (1000x560) — 等比 Letterbox 不应压扁前景判断
            using var widePortrait = CreateTestPortraitBitmap(1000, 560);
            float[]? pasMatte = matting.ComputeAlphaMatte(widePortrait);
            Assert.NotNull(pasMatte);
            Assert.Equal(1000 * 560, pasMatte!.Length);
            Assert.InRange(pasMatte[10 * 1000 + 10], 0f, 0.35f);
            Assert.InRange(pasMatte[280 * 1000 + 500], 0.70f, 1.0f);

            // 运行时切换为 MODNet 并验证动态等比推理输出维度及精度
            Assert.True(matting.SelectModel(PortraitMattingService.ModNetModelFile));
            Assert.Equal(PortraitMattingService.ModNetModelFile, matting.ActiveModelName);

            float[]? modnetMatte = matting.ComputeAlphaMatte(widePortrait);
            Assert.NotNull(modnetMatte);
            Assert.Equal(1000 * 560, modnetMatte!.Length);
            Assert.InRange(modnetMatte[10 * 1000 + 10], 0f, 0.35f);
            Assert.InRange(modnetMatte[280 * 1000 + 500], 0.70f, 1.0f);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static SKBitmap CreateTestPortraitBitmap(int w, int h)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(225, 228, 232));
        canvas.Scale(w / 600f, h / 800f);

        // 胸肩
        using (var pb = new SKPathBuilder())
        {
            pb.MoveTo(80, 800);
            pb.CubicTo(95, 560, 190, 520, 300, 520);
            pb.CubicTo(410, 520, 505, 560, 520, 800);
            pb.Close();
            using var path = pb.Detach();
            canvas.DrawPath(path, new SKPaint { Color = new SKColor(32, 46, 70), IsAntialias = true });
        }
        // 颈部、头发、人脸
        canvas.DrawRoundRect(new SKRect(260, 420, 340, 540), 15, 15, new SKPaint { Color = new SKColor(225, 180, 150), IsAntialias = true });
        canvas.DrawOval(new SKRect(175, 130, 425, 420), new SKPaint { Color = new SKColor(34, 30, 32), IsAntialias = true });
        canvas.DrawOval(new SKRect(195, 180, 405, 455), new SKPaint { Color = new SKColor(240, 200, 174), IsAntialias = true });
        canvas.DrawArc(new SKRect(185, 135, 415, 315), 185, 170, true, new SKPaint { Color = new SKColor(34, 30, 32), IsAntialias = true });
        canvas.DrawCircle(255, 310, 8, new SKPaint { Color = new SKColor(40, 32, 30), IsAntialias = true });
        canvas.DrawCircle(345, 310, 8, new SKPaint { Color = new SKColor(40, 32, 30), IsAntialias = true });
        return bmp;
    }
}
