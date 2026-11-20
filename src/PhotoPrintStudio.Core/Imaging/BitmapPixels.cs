using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

public static class BitmapPixels
{
    // The returned bitmap owns a reference to the same read-only pixels.
    public static SKBitmap ShareImmutable(SKBitmap bitmap)
    {
        bitmap.SetImmutable();
        var shared = new SKBitmap();
        if (bitmap.ExtractSubset(shared, new SKRectI(0, 0, bitmap.Width, bitmap.Height)))
            return shared;

        shared.Dispose();
        throw new InvalidOperationException("无法引用位图像素。");
    }
}
