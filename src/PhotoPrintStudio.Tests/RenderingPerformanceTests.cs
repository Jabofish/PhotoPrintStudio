using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Rendering;
using SkiaSharp;

namespace PhotoPrintStudio.Tests;

public class RenderingPerformanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedPhotoIsProcessedOnceWhileEverySlotIsRendered(bool polaroid)
    {
        var asset = new PhotoAsset();
        var sheet = CreateSheet(asset.Id, polaroid);
        int calls = 0;
        SKBitmap? owned = null;
        using var rendered = SheetRenderer.RenderSheetBitmap(sheet,
            new Dictionary<string, PhotoAsset> { [asset.Id] = asset }, (_, _) =>
            {
                calls++;
                owned = new SKBitmap(20, 20);
                owned.Erase(SKColors.Red);
                return owned;
            }, new SheetRenderOptions { Dpi = 25.4, IsScreenPreview = false });

        Assert.Equal(1, calls);
        foreach (var slot in sheet.Slots)
            Assert.Equal(SKColors.Red, rendered.GetPixel((int)slot.XMm + 12, (int)slot.YMm + 15));
        Assert.Equal(IntPtr.Zero, owned!.Handle);
    }

    [Fact]
    public void ChangingAssetsDisposesPreviousBitmapAndKeepsColorsDistinct()
    {
        var red = new PhotoAsset();
        var blue = new PhotoAsset();
        var sheet = CreateSheet(red.Id, false);
        sheet.Slots[1].PhotoAssetId = blue.Id;
        sheet.Slots = sheet.Slots.Take(3).ToList();
        var owned = new List<SKBitmap>();
        using var rendered = SheetRenderer.RenderSheetBitmap(sheet,
            new Dictionary<string, PhotoAsset> { [red.Id] = red, [blue.Id] = blue }, (a, _) =>
            {
                var bitmap = new SKBitmap(20, 20);
                bitmap.Erase(a.Id == red.Id ? SKColors.Red : SKColors.Blue);
                owned.Add(bitmap);
                return bitmap;
            }, new SheetRenderOptions { Dpi = 25.4, IsScreenPreview = false });
        Assert.Equal(SKColors.Red, rendered.GetPixel(12, 15));
        Assert.Equal(SKColors.Blue, rendered.GetPixel(37, 15));
        Assert.Equal(SKColors.Red, rendered.GetPixel(62, 15));
        Assert.All(owned, bitmap => Assert.Equal(IntPtr.Zero, bitmap.Handle));
    }

    [Fact]
    public void SharedPixelsPreserveIndependentSlotPanning()
    {
        var asset = new PhotoAsset();
        var sheet = CreateSheet(asset.Id, false);
        sheet.Slots = sheet.Slots.Take(2).ToList();
        sheet.Slots[0].SlotPanX = .5;
        sheet.Slots[1].SlotPanX = -.5;
        using var rendered = SheetRenderer.RenderSheetBitmap(sheet,
            new Dictionary<string, PhotoAsset> { [asset.Id]=asset }, (_,_) =>
            {
                var bitmap = new SKBitmap(20,20);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Red);
                using var paint = new SKPaint { Color=SKColors.Blue };
                canvas.DrawRect(10,0,10,20,paint);
                return bitmap;
            }, new SheetRenderOptions { Dpi=25.4, IsScreenPreview=false });
        Assert.Equal(SKColors.Red,rendered.GetPixel(12,20));
        Assert.Equal(SKColors.Blue,rendered.GetPixel(37,20));
    }

    [Fact]
    public void LaterProcessingFailureStillDisposesThePreviouslyRenderedBitmap()
    {
        var first = new PhotoAsset();
        var second = new PhotoAsset();
        var sheet = CreateSheet(first.Id,false);
        sheet.Slots[1].PhotoAssetId = second.Id;
        SKBitmap? owned = null;
        Assert.Throws<InvalidOperationException>(() => SheetRenderer.RenderSheetBitmap(sheet,
            new Dictionary<string,PhotoAsset> { [first.Id]=first,[second.Id]=second }, (asset,_) =>
            {
                if(asset.Id==second.Id) throw new InvalidOperationException("processing failure");
                return owned = new SKBitmap(20,20);
            },new SheetRenderOptions { Dpi=25.4,IsScreenPreview=false }));
        Assert.Equal(IntPtr.Zero,owned!.Handle);
    }

    private static PrintSheet CreateSheet(string assetId, bool polaroid) => new()
    {
        PaperWidthMm = 100,
        PaperHeightMm = 80,
        CropMarkStyle = CropMarkStyle.None,
        Slots = Enumerable.Range(0, 8).Select(i => new LayoutSlot
        {
            XMm = (i % 4) * 25,
            YMm = (i / 4) * 40,
            WidthMm = 25,
            HeightMm = 40,
            PhotoAssetId = assetId,
            PolaroidStyle = polaroid ? new PolaroidSlotStyle
            {
                MarginTopMm = 2, MarginLeftMm = 2, MarginRightMm = 2, MarginBottomMm = 8,
                ShowPaperGrain = false, ShowInnerShadow = false
            } : null
        }).ToList()
    };
}
