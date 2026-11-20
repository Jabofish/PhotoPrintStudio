using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Portable;
using PhotoPrintStudio.Core.Preflight;
using PhotoPrintStudio.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.Tests;

public class PortraitStudioQualityTests
{
    [Fact]
    public void MattingDecontamination_WhiteWallToBlue_ShouldEliminateWhiteHalo_AndKeepBackgroundPureUnderGrading()
    {
        // 构造 320x320 白墙黑发测试图（含 3px 半透明抗锯齿白边过渡，以及轻微外扩的神经网络 Alpha）
        const int w = 320, h = 320;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        var rawAlpha = new float[w * h];

        float cx = w * 0.5f, cy = h * 0.52f, rad = 95f;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dist = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                // 真实物理覆盖度（3px 柔和抗锯齿过渡）
                float trueCover = Math.Clamp((rad - dist) / 3.5f + 0.5f, 0f, 1f);
                // 原图颜色：深黑头发 (22,22,24) 与纯白墙 (246,246,248) 物理混合
                byte r = (byte)MathF.Round(22f * trueCover + 246f * (1f - trueCover));
                byte g = (byte)MathF.Round(22f * trueCover + 246f * (1f - trueCover));
                byte b = (byte)MathF.Round(24f * trueCover + 248f * (1f - trueCover));
                bmp.SetPixel(x, y, new SKColor(r, g, b, 255));

                // 模拟神经网络轻微外扩 1.5px 的 Alpha 预测（典型白边诱因）
                rawAlpha[y * w + x] = Math.Clamp((rad + 1.5f - dist) / 4.5f + 0.5f, 0f, 1f);
            }
        }

        // 同时开启换底 + 曝光/色温/柔肤/锐化：验证边缘无白边峰，且标准蓝底不受调色污染
        var edit = new PhotoEditState
        {
            EnableAiBackgroundReplace = true,
            ReplacementBackgroundHex = "#2B6CB0", // R=43, G=108, B=176
            MattingFeather = 0.5,
            SpillSuppression = 65.0,
            Exposure = 0.55,
            Temperature = 28,
            SkinSoftening = 35,
            Sharpening = 45
        };

        using var res = FloatImageProcessor.Process(bmp, edit, rawAlpha);

        // 1. 纯背景角点必须严格保持标准蓝 #2B6CB0 (43,108,176)，不被 +0.55 EV / +28 色温污染
        var bgPx = res.Bitmap.GetPixel(10, 10);
        Assert.InRange(bgPx.Red, 41, 45);
        Assert.InRange(bgPx.Green, 106, 110);
        Assert.InRange(bgPx.Blue, 174, 178);

        // 2. 沿垂直扫描线穿过头发顶部边界，验证不存在泛白峰值（R 不得显著高于背景 R=43，旧算法此处会飙升至 100+）
        int maxRimRed = 0;
        for (int y = (int)(cy - rad - 8); y <= (int)(cy - rad + 8); y++)
        {
            var px = res.Bitmap.GetPixel((int)cx, y);
            if (px.Red > maxRimRed) maxRimRed = px.Red;
        }
        Assert.True(maxRimRed <= 58, $"Expected halo-free dark-hair-to-blue edge with max R <= 58, but got {maxRimRed}");
    }

    [Fact]
    public void StudioPortraitEnhance_SplitLightingAndDarkCast_ShouldBalanceCheeksAndLiftExposure()
    {
        const int w = 320, h = 400;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        var alpha = new float[w * h];

        // 构造偏暗且左右光照明显失衡（右脸暗、左脸亮）的人像图 + 灰白背景墙
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dx = (x - 160f) / 72f;
                float dy = (y - 190f) / 92f;
                bool inFace = dx * dx + dy * dy <= 1f;
                if (inFace)
                {
                    alpha[y * w + x] = 1f;
                    // 左脸亮 (x > 160)，右脸明显偏暗 (x < 160)，带轻微黄绿偏色
                    float sideFactor = x < 160 ? 0.52f : 0.80f;
                    byte r = (byte)Math.Clamp((int)(190 * sideFactor), 0, 255);
                    byte g = (byte)Math.Clamp((int)(164 * sideFactor), 0, 255);
                    byte b = (byte)Math.Clamp((int)(122 * sideFactor), 0, 255);
                    bmp.SetPixel(x, y, new SKColor(r, g, b, 255));
                }
                else
                {
                    alpha[y * w + x] = 0f;
                    bmp.SetPixel(x, y, new SKColor(218, 224, 210, 255)); // 略偏黄绿白墙
                }
            }
        }

        var face = new DetectedFace(
            X: 88, Y: 98, Width: 144, Height: 184,
            RightEye: new SKPoint(130, 168),
            LeftEye: new SKPoint(190, 168),
            NoseTip: new SKPoint(160, 202),
            RightMouth: new SKPoint(138, 238),
            LeftMouth: new SKPoint(182, 238),
            Confidence: 0.98f);

        var ctx = new PortraitProcessContext(face, w, h);

        var editOff = new PhotoEditState
        {
            EnableAiBackgroundReplace = true,
            ReplacementBackgroundHex = "#438EDB",
            EnableStudioPortraitEnhance = false
        };
        var editOn = editOff.Clone();
        editOn.EnableStudioPortraitEnhance = true;
        editOn.StudioEnhanceStrength = 85.0;

        using var offRes = FloatImageProcessor.Process(bmp, editOff, alpha, false, ctx);
        using var onRes = FloatImageProcessor.Process(bmp, editOn, alpha, false, ctx);

        float MeasureLuma(SKBitmap b, int x0, int x1, int y0, int y1)
        {
            double sum = 0;
            int cnt = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var c = b.GetPixel(x, y);
                    sum += 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue;
                    cnt++;
                }
            return (float)(sum / Math.Max(1, cnt));
        }

        float offRight = MeasureLuma(offRes.Bitmap, 108, 145, 185, 225);
        float offLeft = MeasureLuma(offRes.Bitmap, 175, 212, 185, 225);
        float onRight = MeasureLuma(onRes.Bitmap, 108, 145, 185, 225);
        float onLeft = MeasureLuma(onRes.Bitmap, 175, 212, 185, 225);

        float offAsym = MathF.Abs(offLeft - offRight) / MathF.Max(1f, offLeft);
        float onAsym = MathF.Abs(onLeft - onRight) / MathF.Max(1f, onLeft);

        Assert.True(onRight > offRight + 22f, $"Expected darker cheek lifted significantly: off={offRight:F1}, on={onRight:F1}");
        Assert.True(onAsym < offAsym * 0.75f, $"Expected left-right lighting asymmetry reduced: off={offAsym:F3}, on={onAsym:F3}");

        // 换底后的标准公安蓝底色保持纯净不被影棚提亮影响
        var bgPx = onRes.Bitmap.GetPixel(8, 8);
        Assert.InRange(bgPx.Red, 65, 69);
        Assert.InRange(bgPx.Green, 140, 144);
        Assert.InRange(bgPx.Blue, 217, 221);
    }

    [Fact]
    public void StudioPortraitEnhance_WithCompactOnnxShadingMap_ShouldFuseRelightingCleanly()
    {
        const int w = 160, h = 160;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        bmp.Erase(new SKColor(120, 100, 88, 255));

        var gain = new float[16 * 16];
        Array.Fill(gain, 1.35f);
        var shading = new CompactShadingMap(gain, 16, 16, 0, 0, 16, 16);

        var edit = new PhotoEditState { EnableStudioPortraitEnhance = true, StudioEnhanceStrength = 80 };
        using var withoutShading = FloatImageProcessor.Process(bmp, edit);
        using var withShading = FloatImageProcessor.Process(bmp, edit, (CompactAlphaMatte?)null, false, new PortraitProcessContext(null, w, h, shading));

        var cBase = withoutShading.Bitmap.GetPixel(80, 80);
        var cRelit = withShading.Bitmap.GetPixel(80, 80);
        Assert.True(cRelit.Red > cBase.Red + 8, $"Expected ONNX shading map to brighten portrait: base={cBase.Red}, relit={cRelit.Red}");
    }

    [Fact]
    public void PortraitComplianceAndPreflight_ShouldWarnWhiteOnWhiteConflict_AndUnevenLighting()
    {
        const int w = 300, h = 400;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        bmp.Erase(new SKColor(245, 245, 245, 255));

        var matteData = new float[w * h];
        // 白衬衫肩膀（与白底 #FFFFFF 顺色冲突） + 左右不均暗脸
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (y >= 240 && x >= 40 && x <= 260)
                {
                    matteData[y * w + x] = 1f;
                    bmp.SetPixel(x, y, new SKColor(248, 248, 250, 255)); // 白衣
                }
                else if (y >= 90 && y < 240 && x >= 90 && x <= 210)
                {
                    matteData[y * w + x] = 1f;
                    byte l = x < 150 ? (byte)95 : (byte)155;
                    bmp.SetPixel(x, y, new SKColor(l, (byte)(l * 0.80f), (byte)(l * 0.65f), 255));
                }
            }
        }

        var face = new DetectedFace(90, 90, 120, 150, new SKPoint(125, 145), new SKPoint(175, 145), new SKPoint(150, 175), new SKPoint(132, 205), new SKPoint(168, 205), 0.96f);
        var matte = new CompactAlphaMatte(matteData, w, h, 0, 0, w, h);
        var edit = new PhotoEditState
        {
            EnableAiBackgroundReplace = true,
            ReplacementBackgroundHex = "#FFFFFF",
            EnableStudioPortraitEnhance = false
        };

        var diag = PortraitComplianceAnalyzer.Analyze(bmp, face, matte, edit);
        Assert.NotNull(diag);
        Assert.True(diag!.HasClothingBackgroundConflict, "White shirt on white background should trigger clothing conflict");
        Assert.True(diag.HasUnevenLighting, "Split-lit face should trigger uneven lighting diagnostic");

        var asset = new PhotoAsset { Id = "p1", DisplayName = "证件照", PixelWidth = 1200, PixelHeight = 1600, EditState = edit };
        var sheet = new PrintSheet
        {
            Scenario = LayoutScenario.IdPhoto,
            PaperWidthMm = 102,
            PaperHeightMm = 152,
            Slots = [new LayoutSlot { Index = 0, XMm = 10, YMm = 10, WidthMm = 25, HeightMm = 35, PhotoAssetId = "p1" }]
        };
        var report = PreflightChecker.InspectSheet(
            sheet,
            new Dictionary<string, PhotoAsset> { ["p1"] = asset },
            new PrinterCalibrationProfile { DefaultBorderless = true },
            portraitDiagnosticsByAssetId: new Dictionary<string, PortraitQualityDiagnostics> { ["p1"] = diag });

        Assert.Contains(report.Issues, i => i.Category == "衣底顺色");
        Assert.Contains(report.Issues, i => i.Category == "光照不均");
    }

    [Fact]
    public void ModelManifestAndRelightingService_ShouldRecognizeRelightingRole_AndDegradeGracefully()
    {
        var manifest = new Dictionary<string, ModelManifestEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["studio_relight_fp16.onnx"] = new() { FileName = "studio_relight_fp16.onnx", Role = "relighting" }
        };

        Assert.True(ModelManifestLoader.IsRelightingModel("studio_relight_fp16.onnx", manifest));
        Assert.False(ModelManifestLoader.IsMattingModel("studio_relight_fp16.onnx", manifest));
        Assert.True(ModelManifestLoader.IsRelightingModel("dpr_portrait_relight.onnx", new Dictionary<string, ModelManifestEntry>()));
        Assert.False(ModelManifestLoader.IsMattingModel("dpr_portrait_relight.onnx", new Dictionary<string, ModelManifestEntry>()));

        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_relight_{Guid.NewGuid():N}");
        try
        {
            var paths = new PortablePaths(tempRoot);
            using var svc = new PortraitRelightingService(paths, new AppLogger(paths));
            Assert.False(svc.HasModelLoaded);
            using var dummy = new SKBitmap(64, 64);
            Assert.Null(svc.ComputeStudioShadingMap(dummy));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void MattingEdgeShift_ShouldAdjustForegroundBoundary()
    {
        const int w = 96, h = 96;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        var alpha = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool fg = x >= 24 && x < 72 && y >= 20 && y < 76;
                // 前景为深色发丝，背景为纯白墙
                bmp.SetPixel(x, y, fg ? new SKColor(28, 26, 25, 255) : new SKColor(250, 250, 250, 255));
                float dist = MathF.Min(MathF.Min(x - 24, 71 - x), MathF.Min(y - 20, 75 - y));
                alpha[y * w + x] = Math.Clamp((dist + 1.5f) / 3.0f, 0f, 1f);
            }
        }

        var matte = new CompactAlphaMatte(alpha, w, h, 0, 0, w, h);
        var baseEdit = new PhotoEditState
        {
            EnableAiBackgroundReplace = true,
            ReplacementBackgroundHex = "#438EDB",
            MattingFeather = 0.5,
            SpillSuppression = 60.0,
            MattingEdgeShift = 0.0
        };
        var shrinkEdit = baseEdit.Clone();
        shrinkEdit.MattingEdgeShift = -1.6;
        var expandEdit = baseEdit.Clone();
        expandEdit.MattingEdgeShift = 0.9;

        using var resBase = FloatImageProcessor.Process(bmp, baseEdit, matte);
        using var resShrink = FloatImageProcessor.Process(bmp, shrinkEdit, matte);
        using var resExpand = FloatImageProcessor.Process(bmp, expandEdit, matte);

        static int CountDarkForegroundPixels(SKBitmap b)
        {
            int count = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                {
                    var p = b.GetPixel(x, y);
                    if (p.Red < 60 && p.Green < 80 && p.Blue < 120) count++;
                }
            return count;
        }

        int nShrink = CountDarkForegroundPixels(resShrink.Bitmap);
        int nBase = CountDarkForegroundPixels(resBase.Bitmap);
        int nExpand = CountDarkForegroundPixels(resExpand.Bitmap);
        Assert.True(nShrink < nBase, $"Expected shrink ({nShrink}) < base ({nBase})");
        Assert.True(nExpand > nBase, $"Expected expand ({nExpand}) > base ({nBase})");

    }

    [Fact]
    public void FormalSuit_ShouldDiscoverNamedCustomPng()
    {
        string tempSuits = Path.Combine(Path.GetTempPath(), $"pps_suits_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempSuits);
        try
        {
            string pngPath = Path.Combine(tempSuits, "custom_navy.png");
            using (var suitBmp = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Bgra8888, SKAlphaType.Premul)))
            {
                suitBmp.Erase(new SKColor(18, 36, 74, 255));
                using var data = suitBmp.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(pngPath, data.ToArray());
            }
            File.WriteAllText(Path.Combine(tempSuits, "custom_navy.json"), "{\"name\":\"影楼藏蓝高定\",\"neckAnchorX\":0.5,\"neckAnchorY\":0.15,\"shoulderWidthRatio\":0.8}");

            var presets = FormalSuitRenderer.DiscoverAllPresets(tempSuits);
            var custom = Assert.Single(presets, p => p.Style == FormalSuitStyle.CustomPng);
            Assert.Equal("影楼藏蓝高定", custom.Name);
            Assert.Equal(pngPath, custom.CustomPngPath);

        }
        finally
        {
            if (Directory.Exists(tempSuits)) Directory.Delete(tempSuits, true);
        }
    }

    [Fact]
    public void ExportDigitalIdPhoto_ShouldProduceExactSpecPixels_AndMeetStrictKbCap()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"pps_digid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string outPath = Path.Combine(tempDir, "id_1inch_30kb.jpg");
            using var src = new SKBitmap(new SKImageInfo(600, 840, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
            var rnd = new Random(42);
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                    src.SetPixel(x, y, new SKColor(
                        (byte)(60 + (x + rnd.Next(40)) % 140),
                        (byte)(110 + (y + rnd.Next(40)) % 120),
                        (byte)(160 + ((x ^ y) % 80)),
                        255));

            var asset = new PhotoAsset { Id = "id_a", DisplayName = "一寸证件照", PixelWidth = 600, PixelHeight = 840 };
            var spec = IdPhotoSpec.BuiltInSpecs.First(s => s.WidthMm == 25.0 && s.HeightMm == 35.0); // 1-inch -> 295×413 @ 300 DPI

            var result = ExportService.ExportDigitalIdPhoto(
                outPath,
                asset,
                spec,
                (_, _) => src.Copy(),
                dpi: 300,
                maxFileSizeKb: 30,
                outputSharpeningAmount: 20,
                embedIccProfile: true);

            Assert.True(File.Exists(outPath));
            Assert.Equal(295, result.PixelWidth);
            Assert.Equal(413, result.PixelHeight);
            Assert.True(result.MetTargetSizeLimit, $"Expected file size <= 30KB, actual={result.FileSizeBytes} bytes");
            Assert.True(result.FileSizeBytes <= 30 * 1024);

            using var decoded = SKBitmap.Decode(outPath);
            Assert.NotNull(decoded);
            Assert.Equal(295, decoded!.Width);
            Assert.Equal(413, decoded.Height);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
