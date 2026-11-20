using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace PhotoPrintStudio.App.Helpers;

public static unsafe class SkiaWpfInterop
{
    public static (SKBitmap Bitmap, SKEncodedOrigin Origin) DecodeTiff(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var decoder = new TiffBitmapDecoder(stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var metadata = frame.Metadata as BitmapMetadata;
        BitmapSource pixels = frame;
        if (metadata?.ContainsQuery("/ifd/{ushort=34675}") == true && frame.ColorContexts is { Count: > 0 } colors)
            pixels = new ColorConvertedBitmap(frame, colors[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
        else if (frame.Format != PixelFormats.Bgra32)
            pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var origin = metadata?.GetQuery("/ifd/{ushort=274}") is ushort rotation
            ? (SKEncodedOrigin)rotation : SKEncodedOrigin.TopLeft;
        using var srgb = SKColorSpace.CreateSrgb();
        var bitmap = new SKBitmap(new SKImageInfo(pixels.PixelWidth, pixels.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul, srgb));
        try
        {
            pixels.CopyPixels(Int32Rect.Empty, bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
            return (bitmap, origin);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public static BitmapSource ToBitmapSource(SKBitmap skBitmap)
    {
        bool canZeroCopy = CanReadAsBgraPremul(skBitmap);
        using SKBitmap? converted = canZeroCopy ? null : ConvertToBgraPremul(skBitmap);
        SKBitmap src = converted ?? skBitmap;

        var wb = new WriteableBitmap(src.Width, src.Height, 96, 96, PixelFormats.Pbgra32, null);
        WriteToWriteableBitmap(src, wb);
        wb.Freeze();
        return wb;
    }

    public static WriteableBitmap UpdateWriteableBitmap(SKBitmap skBitmap, WriteableBitmap? existing)
    {
        bool canZeroCopy = CanReadAsBgraPremul(skBitmap);
        using SKBitmap? converted = canZeroCopy ? null : ConvertToBgraPremul(skBitmap);
        SKBitmap src = converted ?? skBitmap;

        if (existing == null || existing.PixelWidth != src.Width || existing.PixelHeight != src.Height || existing.Format != PixelFormats.Pbgra32)
        {
            existing = new WriteableBitmap(src.Width, src.Height, 96, 96, PixelFormats.Pbgra32, null);
        }
        WriteToWriteableBitmap(src, existing);
        return existing;
    }

    private static bool CanReadAsBgraPremul(SKBitmap src) =>
        src.ColorType == SKColorType.Bgra8888 &&
        (src.AlphaType == SKAlphaType.Premul || src.AlphaType == SKAlphaType.Opaque);

    private static void WriteToWriteableBitmap(SKBitmap bgra, WriteableBitmap wb)
    {
        int w = bgra.Width;
        int h = bgra.Height;
        wb.Lock();
        try
        {
            byte* srcPtr = (byte*)bgra.GetPixels().ToPointer();
            byte* dstPtr = (byte*)wb.BackBuffer.ToPointer();
            int dstStride = wb.BackBufferStride;
            int srcStride = bgra.RowBytes;

            if (dstStride == srcStride)
            {
                long totalBytes = (long)srcStride * h;
                Buffer.MemoryCopy(srcPtr, dstPtr, totalBytes, totalBytes);
            }
            else
            {
                int copyRow = Math.Min(srcStride, dstStride);
                for (int y = 0; y < h; y++)
                {
                    Buffer.MemoryCopy(srcPtr + (long)y * srcStride, dstPtr + (long)y * dstStride, dstStride, copyRow);
                }
            }
            wb.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            wb.Unlock();
            GC.KeepAlive(bgra);
        }
    }

    private static SKBitmap ConvertToBgraPremul(SKBitmap src)
    {
        var converted = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(converted);
        canvas.Clear(SKColors.Transparent);
        using var img = SKImage.FromBitmap(src);
        canvas.DrawImage(img, 0, 0, SKSamplingOptions.Default);
        return converted;
    }
}
