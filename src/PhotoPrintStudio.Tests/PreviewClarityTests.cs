using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.Tests;

public class PreviewClarityTests
{
    [Fact]
    public void CalculatePreviewDpi_ShouldProvideSharpResolutionForStandardAndZoomedViewports()
    {
        // 6寸相纸 (152.4 x 101.6 mm = 6 x 4 in): 默认 1920px 对应 320 DPI
        double sixInDefaultDpi = SheetRenderer.CalculatePreviewDpi(152.4, 101.6, 1920);
        Assert.Equal(320.0, sixInDefaultDpi, 1);
        Assert.Equal(1920, UnitConverter.MmToPixelsRounded(152.4, sixInDefaultDpi));
        Assert.Equal(1280, UnitConverter.MmToPixelsRounded(101.6, sixInDefaultDpi));

        // 放大到 ~2x (3200px 目标): 应提升到 ~533 DPI
        double sixInZoomDpi = SheetRenderer.CalculatePreviewDpi(152.4, 101.6, 3200);
        Assert.True(sixInZoomDpi > 500.0);

        // A4 (210 x 297 mm): 至少 164 DPI（显著高于原先糊掉的 110 DPI）
        double a4DefaultDpi = SheetRenderer.CalculatePreviewDpi(210.0, 297.0, 1920);
        Assert.True(a4DefaultDpi >= 164.0);
    }

    [Fact]
    public void ScreenPreview_MildDownscale_ShouldPreserveSharpEdgeContrastWithoutMipmapBlur()
    {
        // 创建 800x800 高频清晰黑白条纹样图，按约 0.72x 渲染到 6寸 2合1 预览上（320 DPI）
        using var src = new SKBitmap(800, 800, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(src))
        {
            c.Clear(SKColors.White);
            using var dark = new SKPaint { Color = SKColors.Black };
            for (int x = 0; x < 800; x += 4)
            {
                c.DrawRect(x, 0, 2, 800, dark);
            }
        }

        var sheet = new PrintSheet
        {
            PaperWidthMm = 152.4,
            PaperHeightMm = 101.6,
            CropMarkStyle = CropMarkStyle.None,
            Slots =
            [
                new LayoutSlot
                {
                    Index = 0,
                    XMm = 0,
                    YMm = 0,
                    WidthMm = 76.2,
                    HeightMm = 101.6,
                    PhotoAssetId = "stripes"
                }
            ]
        };

        double dpi = SheetRenderer.CalculatePreviewDpi(sheet.PaperWidthMm, sheet.PaperHeightMm, 1920);
        var asset = new PhotoAsset { Id = "stripes", PixelWidth = 800, PixelHeight = 800 };

        using var preview = SheetRenderer.RenderSheetBitmap(
            sheet,
            new Dictionary<string, PhotoAsset> { ["stripes"] = asset },
            (_, _) => src.Copy(),
            new SheetRenderOptions
            {
                Dpi = dpi,
                IsScreenPreview = true,
                ShowSafeMarginOverlay = false,
                ShowBleedOverlay = false,
                ShowIdGuidelinesOnSelected = false
            });

        Assert.Equal(1920, preview.Width);
        Assert.Equal(1280, preview.Height);

        // 检查清晰条纹保留的高峰值与低谷值反差（CatmullRom 下反差远大于 mipmap 平均灰糊）
        byte minLum = 255;
        byte maxLum = 0;
        int y = preview.Height / 2;
        for (int x = 100; x < 300; x++)
        {
            var p = preview.GetPixel(x, y);
            minLum = Math.Min(minLum, p.Red);
            maxLum = Math.Max(maxLum, p.Red);
        }

        Assert.True(maxLum - minLum >= 120, $"Expected sharp stripe contrast >= 120, got {maxLum - minLum} ({minLum}..{maxLum}).");
    }
}
