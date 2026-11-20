using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MetadataExtractor.Formats.Icc;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Persistence;
using PhotoPrintStudio.Core.Portable;
using PhotoPrintStudio.Core.Preflight;
using PhotoPrintStudio.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.Tests;

public class ProfessionalQualityRegressionTests
{
    [Fact]
    public void FloatImageProcessor_LinearWhiteBalanceAndShadows_ShouldPreservePureBlackPoint()
    {
        using var src = new SKBitmap(new SKImageInfo(32, 32, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        src.Erase(new SKColor(128, 128, 128, 255));
        // 左上角纯黑，中间暗部，右下角中性灰
        src.SetPixel(2, 2, new SKColor(0, 0, 0, 255));
        src.SetPixel(10, 10, new SKColor(32, 32, 32, 255));
        src.SetPixel(20, 20, new SKColor(128, 128, 128, 255));

        var edit = new PhotoEditState
        {
            Temperature = 75,
            Tint = 30,
            Shadows = 80
        };

        using var res = FloatImageProcessor.Process(src, edit);
        var blackAfter = res.Bitmap.GetPixel(2, 2);
        var darkAfter = res.Bitmap.GetPixel(10, 10);
        var midAfter = res.Bitmap.GetPixel(20, 20);

        // 线性光乘性白平衡 + 黑场锚定阴影提亮：纯黑像素不应泛灰或偏色
        Assert.Equal(0, blackAfter.Red);
        Assert.Equal(0, blackAfter.Green);
        Assert.Equal(0, blackAfter.Blue);

        // 暗部细节应正常提亮
        Assert.True(darkAfter.Red > 45, $"Expected lifted dark shadow, got {darkAfter}");

        // 暖色温下中性灰红色高于蓝色
        Assert.True(midAfter.Red > midAfter.Blue + 18, $"Expected warm midgray R>B, got {midAfter}");
    }

    [Fact]
    public void FloatImageProcessor_DarkLinearToSrgbAndPoolSafety_ShouldStaySmoothUnderParallelFastPath()
    {
        using var src = new SKBitmap(new SKImageInfo(64, 32, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 64; x++)
            {
                byte v = (byte)x; // 0..63 暗部细渐变
                src.SetPixel(x, y, new SKColor(v, v, v, 255));
            }

        var edit = new PhotoEditState
        {
            Exposure = -1.0,
            Gamma = 1.05,
            Contrast = 12
        };

        // 并发多次运行快路径（带 PostLut 租借），确认无池泄漏或并发篡改
        var hashes = new string[16];
        Parallel.For(0, 16, i =>
        {
            using var res = FloatImageProcessor.Process(src, edit);
            Assert.Equal(0, res.Bitmap.GetPixel(0, 16).Red);
            // 检查 0..32 暗部渐变单调递增且相邻步跳变 <= 2
            int prev = res.Bitmap.GetPixel(0, 16).Red;
            for (int x = 1; x <= 32; x++)
            {
                int cur = res.Bitmap.GetPixel(x, 16).Red;
                Assert.True(cur >= prev - 1 && cur - prev <= 2, $"Shadow jump at x={x}: {prev} -> {cur}");
                prev = cur;
            }
            hashes[i] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(res.Bitmap.GetPixelSpan()));
        });

        Assert.All(hashes, h => Assert.Equal(hashes[0], h));
    }

    [Fact]
    public void FloatImageProcessor_ClarityGaussianAndOutputSharpening_ShouldEnhanceEdgeContrastCleanly()
    {
        using var src = new SKBitmap(new SKImageInfo(240, 240, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        using (var c = new SKCanvas(src))
        {
            c.Clear(new SKColor(96, 96, 96));
            using var p = new SKPaint { Color = new SKColor(160, 160, 160) };
            c.DrawRect(80, 80, 80, 80, p);
        }

        using var clarityRes = FloatImageProcessor.Process(src, new PhotoEditState { Clarity = 60, Sharpening = 40 });
        byte leftOfEdge = clarityRes.Bitmap.GetPixel(79, 120).Red;
        byte rightOfEdge = clarityRes.Bitmap.GetPixel(80, 120).Red;
        Assert.True(rightOfEdge - leftOfEdge > 160 - 96 + 10,
            $"Expected enhanced local edge contrast, got {leftOfEdge} vs {rightOfEdge}");

        using var copy = src.Copy();
        FloatImageProcessor.ApplyOutputSharpeningInPlace(copy, amount: 70, dpi: 300);
        byte outLeft = copy.GetPixel(79, 120).Red;
        byte outRight = copy.GetPixel(80, 120).Red;
        Assert.True(outRight - outLeft > 160 - 96 + 10,
            $"Expected output sharpening to boost edge contrast, got {outLeft} vs {outRight}");
    }

    [Fact]
    public void FormalSuitRenderer_WhenFlippedAndRotated_ShouldKeepShouldersLevelAndSymmetric()
    {
        using var portrait = new SKBitmap(new SKImageInfo(400, 560, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        portrait.Erase(new SKColor(20, 30, 50));

        var asset = new PhotoAsset
        {
            Id = "flip_rot_suit",
            PixelWidth = 400,
            PixelHeight = 560,
            EditState = new PhotoEditState
            {
                FlipHorizontal = true,
                RotationDegrees = 20.0,
                FormalSuit = FormalSuitStyle.ClassicWhiteShirt,
                SuitCenterXRatio = 0.50,
                SuitNeckYRatio = 0.55,
                SuitScale = 1.0
            }
        };

        var sheet = new PrintSheet
        {
            PaperWidthMm = 40,
            PaperHeightMm = 56,
            CanvasBackgroundHex = "#101828", // 深色画布，避免旋转露角被误判为白色衬衫
            CropMarkStyle = CropMarkStyle.None,
            Slots = [new LayoutSlot { Index = 0, XMm = 0, YMm = 0, WidthMm = 40, HeightMm = 56, PhotoAssetId = asset.Id }]
        };

        using var rendered = SheetRenderer.RenderSheetBitmap(
            sheet,
            new Dictionary<string, PhotoAsset> { [asset.Id] = asset },
            (_, _) => portrait.Copy(),
            new SheetRenderOptions { Dpi = 150, IsScreenPreview = false, ShowSafeMarginOverlay = false, ShowBleedOverlay = false, ShowIdGuidelinesOnSelected = false });

        // 在左右对称的肩部水平采样，找到最顶部的白色衬衫像素 Y 坐标；若矩阵逆序正确则左右肩高度几乎相等
        int FindTopWhiteY(int x)
        {
            for (int y = 0; y < rendered.Height; y++)
            {
                var p = rendered.GetPixel(x, y);
                if (p.Red > 200 && p.Green > 200 && p.Blue > 200) return y;
            }
            return -1;
        }

        int leftShoulderY = FindTopWhiteY(rendered.Width / 4);
        int rightShoulderY = FindTopWhiteY(rendered.Width * 3 / 4);
        Assert.True(leftShoulderY > 0 && rightShoulderY > 0, $"Expected shirt at both shoulders, got L={leftShoulderY}, R={rightShoulderY}");
        Assert.True(Math.Abs(leftShoulderY - rightShoulderY) <= 4,
            $"Expected horizontal shoulders under Flip+Rotate, got L={leftShoulderY}, R={rightShoulderY} (diff={Math.Abs(leftShoulderY - rightShoulderY)})");
    }

    [Fact]
    public void WindowsIccEngine_ShouldSupportMinimalSrgbAndSystemCmykSoftProofingWithGamutWarning()
    {
        byte[] synthSrgb = WindowsIccEngine.BuildMinimalSrgbIccV2();
        var synthInfo = WindowsIccEngine.InspectProfileHeader(synthSrgb);
        Assert.True(synthInfo.IsValid);
        Assert.Equal("RGB", synthInfo.ColorSpace);
        Assert.True(ExifAndColorHelper.SupportsSoftProofIcc(synthSrgb));

        using var vividBmp = new SKBitmap(new SKImageInfo(32, 32, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        vividBmp.Erase(new SKColor(0, 255, 40, 255)); // 超出典型 CMYK 印刷色域的高饱和亮绿
        vividBmp.SetPixel(2, 2, new SKColor(128, 128, 128, 255)); // 色域内中性灰
        vividBmp.SetPixel(4, 4, new SKColor(255, 255, 255, 255)); // 纸白测试像素

        string cmykPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "RSWOP.icm");
        if (OperatingSystem.IsWindows() && File.Exists(cmykPath))
        {
            byte[] cmykBytes = File.ReadAllBytes(cmykPath);
            var cmykInfo = WindowsIccEngine.InspectProfileHeader(cmykBytes);
            Assert.True(cmykInfo.IsValid);
            Assert.Equal("CMYK", cmykInfo.ColorSpace);
            Assert.True(ExifAndColorHelper.SupportsSoftProofIcc(cmykBytes), "CMYK CLUT ICC profile should be supported by Windows WCS/ICM");

            var proofOpts = new SoftProofOptions(SoftProofRenderingIntent.RelativeColorimetric, SimulatePaperWhite: false, ShowGamutWarning: false);
            using var proofed = ExifAndColorHelper.ApplySoftProofIcc(vividBmp, cmykBytes, fastPreviewLut: true, proofOpts);
            var pProof = proofed.GetPixel(16, 16);
            // CMYK 无法还原纯荧光绿 (0,255,40)，软打样后绿色饱和度显著收窄
            Assert.True(pProof.Green < 230 || pProof.Red > 15, $"Expected CMYK gamut compression on vivid green, got {pProof}");

            var gamutOpts = proofOpts with { ShowGamutWarning = true };
            using var gamutProof = ExifAndColorHelper.ApplySoftProofIcc(vividBmp, cmykBytes, fastPreviewLut: true, gamutOpts);
            var pGamut = gamutProof.GetPixel(16, 16);
            var pGrayGamut = gamutProof.GetPixel(2, 2);
            // 超色域提示叠加洋红色（R 和 B 显著升高），而色域内中性灰不应误报洋红
            Assert.True(pGamut.Red > pProof.Red + 50 && pGamut.Blue > pProof.Blue + 50,
                $"Expected magenta gamut warning overlay, got proof={pProof}, gamut={pGamut}");
            Assert.True(Math.Abs(pGrayGamut.Red - pGrayGamut.Green) < 12 && Math.Abs(pGrayGamut.Blue - pGrayGamut.Green) < 12,
                $"In-gamut mid-gray should stay neutral without false gamut alert, got {pGrayGamut}");

            // 模拟纸白（Absolute Colorimetric 回显）应将纯白拉向 SWOP 印刷纸张微暖纸白
            var paperWhiteOpts = new SoftProofOptions(SoftProofRenderingIntent.RelativeColorimetric, SimulatePaperWhite: true, ShowGamutWarning: false);
            using var paperProof = ExifAndColorHelper.ApplySoftProofIcc(vividBmp, cmykBytes, fastPreviewLut: true, paperWhiteOpts);
            var pPaperWhite = paperProof.GetPixel(4, 4);
            Assert.True(pPaperWhite.Blue < 245 && pPaperWhite.Red > pPaperWhite.Blue + 10,
                $"Expected warm paper-white simulation on pure white, got {pPaperWhite}");
        }
    }

    [Fact]
    public void ExportService_ShouldEmbedValidIccProfileInJpegAndPng()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"pps_icc_exp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var src = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
            src.Erase(new SKColor(60, 140, 210));
            var asset = new PhotoAsset { Id = "a1", PixelWidth = 200, PixelHeight = 200 };
            var sheet = new PrintSheet
            {
                PaperWidthMm = 50,
                PaperHeightMm = 50,
                CropMarkStyle = CropMarkStyle.None,
                Slots = [new LayoutSlot { Index = 0, XMm = 0, YMm = 0, WidthMm = 50, HeightMm = 50, PhotoAssetId = asset.Id }]
            };
            var map = new Dictionary<string, PhotoAsset> { [asset.Id] = asset };

            string jpgPath = Path.Combine(tempDir, "icc_300.jpg");
            ExportService.ExportSingleSheetImage(jpgPath, sheet, map, (_, _) => src.Copy(),
                ExportImageFormat.Jpeg, dpi: 300, jpegQuality: 96, outputSharpeningAmount: 25, embedIccProfile: true);

            byte[] jpgBytes = File.ReadAllBytes(jpgPath);
            Assert.True(IndexOfAscii(jpgBytes, "ICC_PROFILE\0") > 0, "Exported JPEG should contain APP2 ICC_PROFILE marker");
            var jpgMeta = MetadataExtractor.ImageMetadataReader.ReadMetadata(jpgPath);
            Assert.Contains(jpgMeta, d => d is IccDirectory);
            using (var decJpg = SKBitmap.Decode(jpgPath))
            {
                Assert.NotNull(decJpg);
                Assert.Equal(UnitConverter.MmToPixelsRounded(50, 300), decJpg.Width);
            }

            string pngPath = Path.Combine(tempDir, "icc_300.png");
            ExportService.ExportSingleSheetImage(pngPath, sheet, map, (_, _) => src.Copy(),
                ExportImageFormat.Png, dpi: 300, embedIccProfile: true);

            byte[] pngBytes = File.ReadAllBytes(pngPath);
            Assert.True(IndexOfAscii(pngBytes, "iCCP") > 8, "Exported PNG should contain iCCP chunk");
            Assert.True(IndexOfAscii(pngBytes, "pHYs") > 8, "Exported PNG should contain pHYs chunk");
            using (var decPng = SKBitmap.Decode(pngPath))
            {
                Assert.NotNull(decPng);
                Assert.Equal(UnitConverter.MmToPixelsRounded(50, 300), decPng.Width);
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LayoutEngine_FixedTemplatesOnUndersizedPaper_ShouldNotEmitNegativeOrOverflowingSlots()
    {
        var tinyPaper = new PaperSizeSpec("paper_tiny", "Tiny (60x60 mm)", 60, 60, "Custom", IsBuiltIn: false);
        foreach (string id in new[] { "tpl_id_6in_mixed_1_2", "tpl_id_6in_mixed_split", "tpl_6in_2in1_card_duplex" })
        {
            var tpl = LayoutEngine.BuiltInTemplates.First(t => t.Id == id);
            var sheet = LayoutEngine.BuildSheet(tpl, tinyPaper);
            Assert.Empty(sheet.Slots);
        }

        var twoInOne = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_6in_2in1_border");
        var excessiveMarginSheet = LayoutEngine.BuildSheet(twoInOne, tinyPaper, marginOverride: 35, gapOverride: 10);
        Assert.Empty(excessiveMarginSheet.Slots);
    }

    [Fact]
    public void PreflightChecker_ShouldPointToWorstDpiSlot_AndWarnOnUnknownResolution()
    {
        var asset = new PhotoAsset { Id = "p1", DisplayName = "Portrait", PixelWidth = 400, PixelHeight = 400 };
        var unknownAsset = new PhotoAsset { Id = "p2", DisplayName = "BadHeader", PixelWidth = 0, PixelHeight = 0 };
        var sheet = new PrintSheet
        {
            PaperWidthMm = 200,
            PaperHeightMm = 200,
            Slots =
            [
                new LayoutSlot { Id = "slot_small", Index = 0, XMm = 10, YMm = 10, WidthMm = 50, HeightMm = 50, PhotoAssetId = "p1" }, // ~203 DPI (Warning)
                new LayoutSlot { Id = "slot_large", Index = 1, XMm = 70, YMm = 10, WidthMm = 120, HeightMm = 120, PhotoAssetId = "p1" }, // ~85 DPI (Error)
                new LayoutSlot { Id = "slot_unknown", Index = 2, XMm = 10, YMm = 70, WidthMm = 50, HeightMm = 50, PhotoAssetId = "p2" }
            ]
        };

        var report = PreflightChecker.InspectSheet(
            sheet,
            new Dictionary<string, PhotoAsset> { ["p1"] = asset, ["p2"] = unknownAsset });

        var lowRes = Assert.Single(report.Issues, i => i.Category == "分辨率过低");
        Assert.Equal("slot_large", lowRes.SlotId);
        Assert.Contains(report.Issues, i => i.Category == "分辨率未知" && i.SlotId == "slot_unknown");
    }

    [Fact]
    public void ImageCacheManager_ThumbnailHandleShouldRemainValidAfterEviction_AndAtomicStorePreservesDevMode()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_cache_store_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string imgPath = Path.Combine(tempRoot, "sample.png");
            using (var src = new SKBitmap(new SKImageInfo(128, 128, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
            {
                src.Erase(new SKColor(220, 80, 40));
                using var img = SKImage.FromBitmap(src);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(imgPath, data.ToArray());
            }

            var asset = ExifAndColorHelper.InspectAsset(imgPath);
            using (var cache = new ImageCacheManager())
            {
                using var thumb = cache.GetThumbnail(asset);
                cache.RemoveAsset(asset.Id);
                // 即使缓存已驱逐该素材，返回的共享不可变句柄仍可安全读取像素
                Assert.NotEqual(IntPtr.Zero, thumb.Handle);
                var px = thumb.GetPixel(64, 64);
                Assert.Equal(220, px.Red);
            }

            // 验证 DEVMODE Base64 持久化且原子写入不残留 .tmp 文件
            var paths = new PortablePaths(tempRoot);
            var store = new PortableDataStore(paths, new AppLogger(paths));
            string fakeDevMode = Convert.ToBase64String(Enumerable.Range(0, 96).Select(i => (byte)i).ToArray());
            store.SavePrinterProfile(new PrinterCalibrationProfile
            {
                PrinterName = "Epson L8050",
                SavedDevModeBase64 = fakeDevMode,
                OverscanCompensationPercent = 98.0
            });
            var loaded = store.LoadPrinterProfile("Epson L8050");
            Assert.Equal(fakeDevMode, loaded.SavedDevModeBase64);
            Assert.Empty(Directory.GetFiles(paths.PrintersDirectory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void ContentAutoFrameHelper_ZeroCopySampler_ShouldCenterHighDetailRegion()
    {
        // 创建宽幅图：左侧纯灰，右侧非周期高对比度彩色块（避免与步长 step=8 采样网格发生混叠）
        using var bmp = new SKBitmap(new SKImageInfo(320, 160, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(128, 128, 128));
            using var dark = new SKPaint { Color = SKColors.Black };
            using var light = new SKPaint { Color = SKColors.White };
            for (int row = 0; row < 10; row++)
                for (int col = 0; col < 8; col++)
                    c.DrawRect(210 + col * 13, 15 + row * 13, 13, 13, (row + col) % 2 == 0 ? dark : light);
        }

        var (zoom, panX, panY) = ContentAutoFrameHelper.Compute(
            bmp.Width, bmp.Height, new PhotoEditState { FitMode = FitMode.Cover },
            frameWidth: 100, frameHeight: 100, FitMode.Cover, 1.0, 0, 0, [], bmp);

        Assert.InRange(zoom, 1.0, 4.0);
        // 高对比度主体在右侧，因此画面应向左平移 (panX < 0) 把右侧主体拉入中心窗
        Assert.True(panX < -0.1, $"Expected panX < -0.1 to frame right-side detail, got panX={panX}, panY={panY}");
    }

    [Fact]
    public void ModelManifestLoader_And_ProjectSchemaMigration_ShouldResolveSpecsAndUpgradeLegacyVersion()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_manifest_schema_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var paths = new PortablePaths(tempRoot);
            paths.EnsureDirectoriesExist();

            // 1. 验证自定义 models.manifest.json 驱动优先级与预处理参数
            string manifestJson = """
            {
              "schemaVersion": "1.0",
              "models": [
                {
                  "fileName": "custom_hq_matting.onnx",
                  "role": "matting",
                  "priority": -5,
                  "resizeMode": "stretch",
                  "normalization": "imagenet"
                },
                {
                  "fileName": "custom_face.onnx",
                  "role": "face_detection",
                  "priority": 0
                }
              ]
            }
            """;
            File.WriteAllText(Path.Combine(paths.ModelsDirectory, ModelManifestLoader.ManifestFileName), manifestJson);
            var manifest = ModelManifestLoader.LoadDirectoryManifest(paths.ModelsDirectory);
            Assert.Equal(2, manifest.Count);
            Assert.True(ModelManifestLoader.IsMattingModel("custom_hq_matting.onnx", manifest));
            Assert.False(ModelManifestLoader.IsMattingModel("custom_face.onnx", manifest));

            var spec = ModelManifestLoader.ResolveMattingSpec("custom_hq_matting.onnx", manifest);
            Assert.True(spec.ResizeToSquare);
            Assert.Equal(-5, spec.Priority);
            Assert.True(Math.Abs(spec.RgbOffset.R - (0.485f / 0.229f)) < 0.001f);

            // 2. 验证旧版工程 (1.0) 自动迁移升级为 2.0
            var store = new PortableDataStore(paths, new AppLogger(paths));
            string legacyFile = Path.Combine(paths.ProjectsDirectory, "legacy.ppsproj");
            File.WriteAllText(legacyFile, """{"version":"1.0","projectName":"Legacy","activeSheetIndex":0,"assets":[],"sheets":[]}""");
            var loaded = store.LoadProject(legacyFile);
            Assert.Equal(PortableDataStore.CurrentProjectSchemaVersion, loaded.Version);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static int IndexOfAscii(byte[] data, string ascii)
    {
        byte[] pat = Encoding.ASCII.GetBytes(ascii);
        for (int i = 0; i <= data.Length - pat.Length; i++)
        {
            if (data.AsSpan(i, pat.Length).SequenceEqual(pat)) return i;
        }
        return -1;
    }
}
