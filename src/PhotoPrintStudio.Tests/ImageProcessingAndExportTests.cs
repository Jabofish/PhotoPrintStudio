using System.Buffers.Binary;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.Tests;

public class ImageProcessingAndExportTests
{
    [Fact]
    public void FloatImageProcessor_ContrastSaturationBrightness_ShouldModifyPixelsAccurately()
    {
        using var src = CreateGradientTestBitmap(120, 120);
        var edit = new PhotoEditState
        {
            Brightness = 25,
            Contrast = 30,
            Saturation = 40,
            Temperature = 15,
            Sharpening = 25
        };

        using var res = FloatImageProcessor.Process(src, edit);
        Assert.Equal(120, res.Bitmap.Width);
        Assert.Equal(120, res.Bitmap.Height);
        Assert.Equal(120 * 120, res.Histogram.TotalPixels);

        var centerBefore = src.GetPixel(60, 60);
        var centerAfter = res.Bitmap.GetPixel(60, 60);
        Assert.NotEqual(centerBefore, centerAfter);
        Assert.True(centerAfter.Red > centerBefore.Red); // 提亮+暖色温使红色分量升高
    }

    [Fact]
    public void ToneCurve_MonotonicSpline_ShouldStayBoundedBetweenZeroAndOne()
    {
        var pts = new List<CurvePoint> { new(0, 0), new(0.25, 0.18), new(0.75, 0.88), new(1, 1) };
        var lut = ToneCurveData.BuildLut(pts, 1024);

        Assert.Equal(1024, lut.Length);
        Assert.InRange(lut[0], 0f, 0.01f);
        Assert.InRange(lut[^1], 0.99f, 1.0f);
        for (int i = 1; i < lut.Length; i++)
        {
            Assert.True(lut[i] >= lut[i - 1] - 1e-4f, "Monotonic curve lut should remain non-decreasing");
        }
    }

    [Fact]
    public void ExportService_JpegPngPdf_ShouldProduceExactDimensionsAndDpiHeaders()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"pps_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string srcPath = Path.Combine(tempDir, "src.png");
            using (var src = CreateGradientTestBitmap(800, 1000))
            using (var img = SKImage.FromBitmap(src))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            {
                File.WriteAllBytes(srcPath, data.ToArray());
            }

            var asset = ExifAndColorHelper.InspectAsset(srcPath);
            var paper6 = PaperSizeSpec.BuiltInPapers.First(p => p.Id == "paper_6inch");
            var tpl = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_pol_6in_mini_2");
            var sheet = LayoutEngine.BuildSheet(tpl, paper6);
            sheet.Slots[0].PhotoAssetId = asset.Id;
            sheet.Slots[1].PhotoAssetId = asset.Id;

            var map = new Dictionary<string, PhotoAsset> { [asset.Id] = asset };

            string jpgOut = Path.Combine(tempDir, "out_300.jpg");
            ExportService.ExportSingleSheetImage(jpgOut, sheet, map,
                (a, e) =>
                {
                    using var raw = ExifAndColorHelper.DecodeNormalizedSrgb(a.FilePath);
                    using var r = FloatImageProcessor.Process(raw, e);
                    return r.Bitmap.Copy();
                },
                ExportImageFormat.Jpeg, dpi: 300, jpegQuality: 95);

            byte[] jpgBytes = File.ReadAllBytes(jpgOut);
            // 验证 JFIF APP0 DPI = 300
            Assert.Equal(0xFF, jpgBytes[0]);
            Assert.Equal(0xD8, jpgBytes[1]);
            Assert.Equal(1, jpgBytes[13]); // inch units
            Assert.Equal(300, BinaryPrimitives.ReadUInt16BigEndian(jpgBytes.AsSpan(14, 2)));

            using (var decoded = SKBitmap.Decode(jpgOut))
            {
                Assert.Equal(1800, decoded.Width);
                Assert.Equal(1200, decoded.Height);
            }

            string pngOut = Path.Combine(tempDir, "out_300.png");
            ExportService.ExportSingleSheetImage(pngOut, sheet, map,
                (a, e) =>
                {
                    using var raw = ExifAndColorHelper.DecodeNormalizedSrgb(a.FilePath);
                    using var r = FloatImageProcessor.Process(raw, e);
                    return r.Bitmap.Copy();
                },
                ExportImageFormat.Png, dpi: 300);

            byte[] pngBytes = File.ReadAllBytes(pngOut);
            // 验证 PNG pHYs 块写入 (300 DPI ≈ 11811 px/m)
            int physIdx = IndexOfAscii(pngBytes, "pHYs");
            Assert.True(physIdx > 8);
            uint ppm = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(physIdx + 4, 4));
            Assert.InRange((int)ppm, 11805, 11815);

            string pdfOut = Path.Combine(tempDir, "out_300.pdf");
            ExportService.ExportSheetsToPdf(pdfOut, [sheet], map,
                (a, e) =>
                {
                    using var raw = ExifAndColorHelper.DecodeNormalizedSrgb(a.FilePath);
                    using var r = FloatImageProcessor.Process(raw, e);
                    return r.Bitmap.Copy();
                }, dpi: 300);

            Assert.True(File.Exists(pdfOut));
            Assert.True(new FileInfo(pdfOut).Length > 5000);

            // 批量导出全部页序列 JPG 与进度回调验证
            var sheet2 = sheet.Clone();
            sheet2.SheetName = "第 2 页";
            string batchDir = Path.Combine(tempDir, "batch_out");
            var progressReports = new List<(int current, int total, string sheetName)>();
            var batchFiles = ExportService.ExportSheetsBatchImages(
                batchDir,
                "冲印批量",
                [sheet, sheet2],
                map,
                (a, e) =>
                {
                    using var raw = ExifAndColorHelper.DecodeNormalizedSrgb(a.FilePath);
                    using var r = FloatImageProcessor.Process(raw, e);
                    return r.Bitmap.Copy();
                },
                ExportImageFormat.Jpeg,
                dpi: 300,
                jpegQuality: 92,
                progress: new Progress<(int current, int total, string sheetName)>(p =>
                {
                    lock (progressReports) progressReports.Add(p);
                }));

            Assert.Equal(2, batchFiles.Count);
            Assert.Contains("_P01_", batchFiles[0]);
            Assert.Contains("_P02_", batchFiles[1]);
            Assert.All(batchFiles, f => Assert.True(File.Exists(f)));

            // 已取消的令牌应立即中止批量导出
            using var canceledCts = new CancellationTokenSource();
            canceledCts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                ExportService.ExportSheetsBatchImages(
                    batchDir,
                    "取消测试",
                    [sheet],
                    map,
                    (a, e) => throw new InvalidOperationException("Should not resolve after cancel"),
                    ExportImageFormat.Png,
                    cancellationToken: canceledCts.Token));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static int IndexOfAscii(byte[] data, string ascii)
    {
        byte[] pat = System.Text.Encoding.ASCII.GetBytes(ascii);
        for (int i = 0; i <= data.Length - pat.Length; i++)
        {
            if (data.AsSpan(i, pat.Length).SequenceEqual(pat)) return i;
        }
        return -1;
    }

    private static SKBitmap CreateGradientTestBitmap(int w, int h)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte r = (byte)(60 + x * 140 / w);
                byte g = (byte)(50 + y * 130 / h);
                byte b = (byte)(70 + (x + y) * 100 / (w + h));
                bmp.SetPixel(x, y, new SKColor(r, g, b));
            }
        }
        return bmp;
    }
}
