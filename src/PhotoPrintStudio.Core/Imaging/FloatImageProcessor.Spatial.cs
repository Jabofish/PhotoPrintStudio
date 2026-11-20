using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

public static unsafe partial class FloatImageProcessor
{
    /// <summary>
    /// 印前输出锐化（针对最终 300/600 DPI 光栅化成品执行纯亮度高斯 USM，补偿相纸油墨/热升华网点扩散且避免色边）。
    /// </summary>
    public static void ApplyOutputSharpeningInPlace(SKBitmap bitmap, double amount, int dpi)
    {
        if (amount <= 0.5 || bitmap.Width < 16 || bitmap.Height < 16 ||
            bitmap.ColorType != SKColorType.Bgra8888 || bitmap.RowBytes % 4 != 0)
            return;

        int w = bitmap.Width;
        int h = bitmap.Height;
        int pixelCount = w * h;
        int radius = dpi >= 500 ? 2 : 1;
        float gain = (float)(Math.Clamp(amount, 0.0, 100.0) / 100.0) * 0.95f;
        const float threshold = 0.015f;

        float[] luma = FloatPool.Rent(pixelCount);
        float[] blur = FloatPool.Rent(pixelCount);
        try
        {
            byte* ptr = (byte*)bitmap.GetPixels().ToPointer();
            int stride = bitmap.RowBytes;
            float[] byteLut = ByteToUnitLut;
            int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));

            Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    byte* row = ptr + (long)y * stride;
                    int off = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i4 = x * 4;
                        luma[off + x] = 0.2126f * byteLut[row[i4 + 2]] + 0.7152f * byteLut[row[i4 + 1]] + 0.0722f * byteLut[row[i4]];
                    }
                }
            });

            GaussianBlurPlaneInto(luma, blur, w, h, radius);

            Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    byte* row = ptr + (long)y * stride;
                    int off = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        float dl = luma[off + x] - blur[off + x];
                        if (MathF.Abs(dl) <= threshold) continue;
                        float add = dl * gain * 255f;
                        int i4 = x * 4;
                        row[i4] = (byte)Math.Clamp((int)MathF.Round(row[i4] + add), 0, 255);
                        row[i4 + 1] = (byte)Math.Clamp((int)MathF.Round(row[i4 + 1] + add), 0, 255);
                        row[i4 + 2] = (byte)Math.Clamp((int)MathF.Round(row[i4 + 2] + add), 0, 255);
                    }
                }
            });
            GC.KeepAlive(bitmap);
        }
        finally
        {
            FloatPool.Return(luma);
            FloatPool.Return(blur);
        }
    }

    /// <summary>
    /// 升级版高低频分离保边人像柔肤：
    /// 有 478 点人脸掩码时严格保护双眼/眉毛/嘴唇/头发，平滑低频肤色不均并保留高频皮肤微纹理；
    /// 无人脸掩码时平滑回退至皮肤色相范围与梯度方差保护。
    /// </summary>
    private static void ApplyEdgePreservingSkinSoften(
        float[] rBuf, float[] gBuf, float[] bBuf,
        float[] blurR, float[] blurG, float[] blurB,
        int w, int h, int bandRows, float strength,
        PortraitMaskSet? masks = null)
    {
        int radius = Math.Max(2, (int)MathF.Round(Math.Min(w, h) * 0.006f));
        const float edgeThreshold = 0.075f;
        bool hasMasks = masks != null;
        var maskSet = masks.GetValueOrDefault();

        FilterColorBands(rBuf, gBuf, bBuf, blurR, blurG, blurB, w, h, bandRows, radius, (start, end) =>
        {
            int firstPixel = start * w;
            int rowChunk = Math.Max(4, (end - start) / (PixelParallelism.MaxDegreeOfParallelism * 2));
            Parallel.ForEach(Partitioner.Create(start, end, rowChunk), PixelParallelism, range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        int bandPixel = i - firstPixel;
                        float r = rBuf[i], g = gBuf[i], b = bBuf[i];
                        float br = blurR[bandPixel], bg = blurG[bandPixel], bb = blurB[bandPixel];
                        float diff = (MathF.Abs(r - br) + MathF.Abs(g - bg) + MathF.Abs(b - bb)) * 0.333f;

                        float edgeKeep = 1f - SmoothStep(0.01f, edgeThreshold, diff);
                        bool skinLike = r > g && g > b * 0.85f && (r - b) > 0.03f;

                        float regionWeight;
                        if (hasMasks)
                        {
                            float sw = maskSet.SampleMask(maskSet.SkinMask, x, y, w, h);
                            if (sw <= 0.01f) continue;
                            regionWeight = skinLike ? sw : sw * 0.25f;
                        }
                        else
                        {
                            regionWeight = skinLike ? 1.0f : 0.35f;
                        }

                        float mix = strength * 0.68f * edgeKeep * regionWeight;
                        if (mix <= 0.001f) continue;

                        // 保留部分高频微亮度纹理，减少过度磨皮导致的假面感
                        float origL = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                        float blurL = 0.2126f * br + 0.7152f * bg + 0.0722f * bb;
                        float highTex = (origL - blurL) * 0.32f;

                        rBuf[i] = Math.Clamp(r + (br + highTex - r) * mix, 0f, 1f);
                        gBuf[i] = Math.Clamp(g + (bg + highTex - g) * mix, 0f, 1f);
                        bBuf[i] = Math.Clamp(b + (bb + highTex - b) * mix, 0f, 1f);
                    }
                }
            });
        });
    }

    private static void ApplyClarity(
        float[] rBuf, float[] gBuf, float[] bBuf,
        int w, int h, float amount,
        bool fuseQuantize, byte* srcPtr, int srcRowBytes, byte* dstPtr, int dstRowBytes, bool showClippingOverlay,
        out HistogramData? fusedHist,
        float[]? finalBgAlpha = null, in AiBgStyle finalBgStyle = default)
    {
        // 按物理画面短边比例计算分辨率无关局部反差半径，保证 1400px 预览与 24MP 导出视觉一致
        int targetRadius = Math.Max(4, (int)MathF.Round(Math.Min(w, h) * 0.022f));
        float gain = amount * 0.55f;

        // 大半径时降采样做 2-pass 高斯近似平滑再双线性上采样：消除方形振铃且大幅降低 24MP 内存与耗时
        int ds = targetRadius > 14 ? Math.Clamp(targetRadius / 10, 2, 6) : 1;
        int dw = Math.Max(4, (w + ds - 1) / ds);
        int dh = Math.Max(4, (h + ds - 1) / ds);
        int subRadius = Math.Max(2, (int)MathF.Round(targetRadius / (float)ds));
        int subPixels = dw * dh;

        float[] lowR = FloatPool.Rent(subPixels);
        float[] lowG = FloatPool.Rent(subPixels);
        float[] lowB = FloatPool.Rent(subPixels);
        float[] tmpR = FloatPool.Rent(subPixels);
        float[] tmpG = FloatPool.Rent(subPixels);
        float[] tmpB = FloatPool.Rent(subPixels);
        try
        {
            if (ds == 1)
            {
                BoxBlurFloatInto(rBuf, tmpR, subPixels, dw, dh, subRadius);
                BoxBlurFloatInto(tmpR, lowR, subPixels, dw, dh, subRadius);
                BoxBlurFloatInto(gBuf, tmpG, subPixels, dw, dh, subRadius);
                BoxBlurFloatInto(tmpG, lowG, subPixels, dw, dh, subRadius);
                BoxBlurFloatInto(bBuf, tmpB, subPixels, dw, dh, subRadius);
                BoxBlurFloatInto(tmpB, lowB, subPixels, dw, dh, subRadius);
            }
            else
            {
                DownsampleBox3(rBuf, gBuf, bBuf, w, h, lowR, lowG, lowB, dw, dh, ds);
                BoxBlurFloatInto(lowR, tmpR, subPixels, dw, dh, subRadius);
                BoxBlurFloatInto(tmpR, lowR, subPixels, dw, dh, subRadius);
                BoxBlurFloatInto(lowG, tmpG, subPixels, dw, dh, subRadius);
                BoxBlurFloatInto(tmpG, lowG, subPixels, dw, dh, subRadius);
                BoxBlurFloatInto(lowB, tmpB, subPixels, dw, dh, subRadius);
                BoxBlurFloatInto(tmpB, lowB, subPixels, dw, dh, subRadius);
            }

            float scaleX = dw / (float)w;
            float scaleY = dh / (float)h;
            int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));

            if (!fuseQuantize)
            {
                fusedHist = null;
                Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
                {
                    for (int y = range.Item1; y < range.Item2; y++)
                    {
                        int rowOffset = y * w;
                        if (ds == 1)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                int i = rowOffset + x;
                                float r = rBuf[i], g = gBuf[i], b = bBuf[i];
                                float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                                float midWeight = 1f - MathF.Abs(2f * l - 1f);
                                float k = gain * (0.4f + 0.6f * midWeight);
                                rBuf[i] = Math.Clamp(r + (r - lowR[i]) * k, 0f, 1f);
                                gBuf[i] = Math.Clamp(g + (g - lowG[i]) * k, 0f, 1f);
                                bBuf[i] = Math.Clamp(b + (b - lowB[i]) * k, 0f, 1f);
                            }
                        }
                        else
                        {
                            float sy = (y + 0.5f) * scaleY - 0.5f;
                            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, dh - 1);
                            int y1 = y0 + 1 < dh ? y0 + 1 : y0;
                            float fy = Math.Clamp(sy - y0, 0f, 1f);
                            int r0 = y0 * dw, r1 = y1 * dw;

                            for (int x = 0; x < w; x++)
                            {
                                int i = rowOffset + x;
                                float sx = (x + 0.5f) * scaleX - 0.5f;
                                int x0 = Math.Clamp((int)MathF.Floor(sx), 0, dw - 1);
                                int x1 = x0 + 1 < dw ? x0 + 1 : x0;
                                float fx = Math.Clamp(sx - x0, 0f, 1f);

                                float br = Bilerp(lowR, r0, r1, x0, x1, fx, fy);
                                float bg = Bilerp(lowG, r0, r1, x0, x1, fx, fy);
                                float bb = Bilerp(lowB, r0, r1, x0, x1, fx, fy);

                                float r = rBuf[i], g = gBuf[i], b = bBuf[i];
                                float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                                float midWeight = 1f - MathF.Abs(2f * l - 1f);
                                float k = gain * (0.4f + 0.6f * midWeight);
                                rBuf[i] = Math.Clamp(r + (r - br) * k, 0f, 1f);
                                gBuf[i] = Math.Clamp(g + (g - bg) * k, 0f, 1f);
                                bBuf[i] = Math.Clamp(b + (b - bb) * k, 0f, 1f);
                            }
                        }
                    }
                });
                return;
            }

            int pixelCount = w * h;
            var hist = new HistogramData { TotalPixels = pixelCount };
            var histLock = new object();
            int borderX = Math.Max(1, (int)(w * 0.12));
            int borderY = Math.Max(1, (int)(h * 0.12));
            int totalBorderPx = 0, totalBorderWhite = 0, totalInteriorPx = 0, totalInteriorHi = 0;
            bool doFinalBg = finalBgAlpha != null;
            var bgStyle = finalBgStyle;
            float invW = w > 1 ? 1f / (w - 1) : 1f;
            float invH = h > 1 ? 1f / (h - 1) : 1f;

            Parallel.ForEach(
                Partitioner.Create(0, h, rowChunk),
                PixelParallelism,
                () => new HistAccum(),
                (range, _, local) =>
                {
                    for (int y = range.Item1; y < range.Item2; y++)
                    {
                        byte* sRow = srcPtr + y * srcRowBytes;
                        byte* dRow = dstPtr + y * dstRowBytes;
                        int rowOffset = y * w;
                        bool isBorderRow = y < borderY || y >= h - borderY;
                        int ditherY = (y & 3);
                        float ny = (y * invH - 0.38f) * 1.15f;

                        if (ds == 1)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                int i = rowOffset + x;
                                float r = rBuf[i], g = gBuf[i], b = bBuf[i];
                                float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                                float midWeight = 1f - MathF.Abs(2f * l - 1f);
                                float k = gain * (0.4f + 0.6f * midWeight);
                                r += (r - lowR[i]) * k;
                                g += (g - lowG[i]) * k;
                                b += (b - lowB[i]) * k;

                                int idx4 = x * 4;
                                byte alphaByte = sRow[idx4 + 3];
                                if (doFinalBg)
                                    CompositeBackgroundPixel(ref r, ref g, ref b, finalBgAlpha![i], ref alphaByte, x, invW, ny, in bgStyle);

                                QuantizeAndAccumulatePixel(
                                    r, g, b, alphaByte,
                                    x, ditherY, w, isBorderRow, borderX, showClippingOverlay,
                                    dRow + idx4, local);
                            }
                        }
                        else
                        {
                            float sy = (y + 0.5f) * scaleY - 0.5f;
                            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, dh - 1);
                            int y1 = y0 + 1 < dh ? y0 + 1 : y0;
                            float fy = Math.Clamp(sy - y0, 0f, 1f);
                            int r0 = y0 * dw, r1 = y1 * dw;

                            for (int x = 0; x < w; x++)
                            {
                                int i = rowOffset + x;
                                float sx = (x + 0.5f) * scaleX - 0.5f;
                                int x0 = Math.Clamp((int)MathF.Floor(sx), 0, dw - 1);
                                int x1 = x0 + 1 < dw ? x0 + 1 : x0;
                                float fx = Math.Clamp(sx - x0, 0f, 1f);

                                float br = Bilerp(lowR, r0, r1, x0, x1, fx, fy);
                                float bg = Bilerp(lowG, r0, r1, x0, x1, fx, fy);
                                float bb = Bilerp(lowB, r0, r1, x0, x1, fx, fy);

                                float r = rBuf[i], g = gBuf[i], b = bBuf[i];
                                float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                                float midWeight = 1f - MathF.Abs(2f * l - 1f);
                                float k = gain * (0.4f + 0.6f * midWeight);
                                r += (r - br) * k;
                                g += (g - bg) * k;
                                b += (b - bb) * k;

                                int idx4 = x * 4;
                                byte alphaByte = sRow[idx4 + 3];
                                if (doFinalBg)
                                    CompositeBackgroundPixel(ref r, ref g, ref b, finalBgAlpha![i], ref alphaByte, x, invW, ny, in bgStyle);

                                QuantizeAndAccumulatePixel(
                                    r, g, b, alphaByte,
                                    x, ditherY, w, isBorderRow, borderX, showClippingOverlay,
                                    dRow + idx4, local);
                            }
                        }
                    }
                    return local;
                },
                local => MergeHistogram(hist, histLock, local, ref totalBorderPx, ref totalBorderWhite, ref totalInteriorPx, ref totalInteriorHi));

            FinalizeHistogramRatios(hist, pixelCount, totalBorderPx, totalBorderWhite, totalInteriorPx, totalInteriorHi);
            fusedHist = hist;
        }
        finally
        {
            FloatPool.Return(tmpB);
            FloatPool.Return(tmpG);
            FloatPool.Return(tmpR);
            FloatPool.Return(lowB);
            FloatPool.Return(lowG);
            FloatPool.Return(lowR);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Bilerp(float[] src, int r0, int r1, int x0, int x1, float fx, float fy)
    {
        float top = src[r0 + x0] + (src[r0 + x1] - src[r0 + x0]) * fx;
        float bot = src[r1 + x0] + (src[r1 + x1] - src[r1 + x0]) * fx;
        return top + (bot - top) * fy;
    }

    private static void DownsampleBox3(
        float[] rSrc, float[] gSrc, float[] bSrc, int sw, int sh,
        float[] rDst, float[] gDst, float[] bDst, int dw, int dh, int ds)
    {
        int rowChunk = Math.Max(4, dh / (PixelParallelism.MaxDegreeOfParallelism * 4));
        Parallel.ForEach(Partitioner.Create(0, dh, rowChunk), PixelParallelism, range =>
        {
            for (int dy = range.Item1; dy < range.Item2; dy++)
            {
                int sy0 = dy * ds;
                int sy1 = Math.Min(sh, sy0 + ds);
                int dstRow = dy * dw;
                for (int dx = 0; dx < dw; dx++)
                {
                    int sx0 = dx * ds;
                    int sx1 = Math.Min(sw, sx0 + ds);
                    float sr = 0f, sg = 0f, sb = 0f;
                    int count = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        int off = sy * sw;
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int idx = off + sx;
                            sr += rSrc[idx];
                            sg += gSrc[idx];
                            sb += bSrc[idx];
                            count++;
                        }
                    }
                    float inv = 1f / Math.Max(1, count);
                    rDst[dstRow + dx] = sr * inv;
                    gDst[dstRow + dx] = sg * inv;
                    bDst[dstRow + dx] = sb * inv;
                }
            }
        });
    }

    private static HistogramData ApplyUnsharpMaskAndQuantizeToBitmap(
        float[] rBuf, float[] gBuf, float[] bBuf,
        float[] blurR, float[] blurG, float[] blurB,
        byte* srcPtr, int srcRowBytes,
        byte* dstPtr, int dstRowBytes,
        int w, int h, int bandRows, float strength, bool showClippingOverlay,
        float[]? finalBgAlpha = null, in AiBgStyle finalBgStyle = default)
    {
        // 分辨率比例缩放的高斯锐化核（1400px 约 2px，24MP 约 6px），避免高分辨率导出锐化感偏弱及方框振铃
        int radius = Math.Clamp((int)MathF.Round(Math.Min(w, h) / 550f), 1, 6);
        float gain = strength * 1.15f;
        const float threshold = 0.012f;

        int pixelCount = w * h;
        var hist = new HistogramData { TotalPixels = pixelCount };
        var histLock = new object();
        int borderX = Math.Max(1, (int)(w * 0.12));
        int borderY = Math.Max(1, (int)(h * 0.12));
        int totalBorderPx = 0, totalBorderWhite = 0, totalInteriorPx = 0, totalInteriorHi = 0;
        bool doFinalBg = finalBgAlpha != null;
        var bgStyle = finalBgStyle;
        float invW = w > 1 ? 1f / (w - 1) : 1f;
        float invH = h > 1 ? 1f / (h - 1) : 1f;

        int rowChunk = Math.Max(8, bandRows / (PixelParallelism.MaxDegreeOfParallelism * 4));
        FilterColorBandsGaussian(rBuf, gBuf, bBuf, blurR, blurG, blurB, w, h, bandRows, radius, (start, end) => Parallel.ForEach(
            Partitioner.Create(start, end, rowChunk),
            PixelParallelism,
            () => new HistAccum(),
            (range, _, local) =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    byte* sRow = srcPtr + y * srcRowBytes;
                    byte* dRow = dstPtr + y * dstRowBytes;
                    int rowOffset = y * w;
                    bool isBorderRow = y < borderY || y >= h - borderY;
                    int ditherY = (y & 3);
                    float ny = (y * invH - 0.38f) * 1.15f;

                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOffset + x;
                        float r = rBuf[i], g = gBuf[i], b = bBuf[i];
                        int bandPixel = i - start * w;
                        float dr = r - blurR[bandPixel];
                        float dg = g - blurG[bandPixel];
                        float db = b - blurB[bandPixel];
                        float lumDiff = MathF.Abs(0.2126f * dr + 0.7152f * dg + 0.0722f * db);
                        if (lumDiff > threshold)
                        {
                            r += dr * gain;
                            g += dg * gain;
                            b += db * gain;
                        }

                        int idx4 = x * 4;
                        byte alphaByte = sRow[idx4 + 3];
                        if (doFinalBg)
                            CompositeBackgroundPixel(ref r, ref g, ref b, finalBgAlpha![i], ref alphaByte, x, invW, ny, in bgStyle);

                        QuantizeAndAccumulatePixel(
                            r, g, b, alphaByte,
                            x, ditherY, w, isBorderRow, borderX, showClippingOverlay,
                            dRow + idx4, local);
                    }
                }
                return local;
            },
            local => MergeHistogram(hist, histLock, local, ref totalBorderPx, ref totalBorderWhite, ref totalInteriorPx, ref totalInteriorHi)));

        FinalizeHistogramRatios(hist, pixelCount, totalBorderPx, totalBorderWhite, totalInteriorPx, totalInteriorHi);
        return hist;
    }

    private static float[] BuildGaussianKernel(int radius)
    {
        float sigma = Math.Max(0.6f, radius * 0.55f);
        float invTwoSigmaSq = 1f / (2f * sigma * sigma);
        var kernel = new float[radius * 2 + 1];
        float sum = 0f;
        for (int k = -radius; k <= radius; k++)
        {
            float v = MathF.Exp(-(k * k) * invTwoSigmaSq);
            kernel[k + radius] = v;
            sum += v;
        }
        float invSum = 1f / sum;
        for (int i = 0; i < kernel.Length; i++) kernel[i] *= invSum;
        return kernel;
    }

    private static void GaussianBlurPlaneInto(float[] src, float[] dst, int width, int height, int radius)
    {
        float[] kernel = BuildGaussianKernel(radius);
        float[] tmp = FloatPool.Rent(width * height);
        try
        {
            int rowChunk = Math.Max(8, height / (PixelParallelism.MaxDegreeOfParallelism * 4));
            Parallel.ForEach(Partitioner.Create(0, height, rowChunk), PixelParallelism, range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                    HorizontalGaussianRow(src, tmp, y * width, y * width, width, radius, kernel);
            });
            Parallel.ForEach(Partitioner.Create(0, height, rowChunk), PixelParallelism, range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    int outOff = y * width;
                    for (int x = 0; x < width; x++) dst[outOff + x] = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        float kw = kernel[k + radius];
                        int sy = Math.Clamp(y + k, 0, height - 1);
                        int inOff = sy * width;
                        for (int x = 0; x < width; x++) dst[outOff + x] += tmp[inOff + x] * kw;
                    }
                }
            });
        }
        finally
        {
            FloatPool.Return(tmp);
        }
    }

    private static void FilterColorBandsGaussian(
        float[] r, float[] g, float[] b, float[] blurR, float[] blurG, float[] blurB,
        int width, int height, int bandRows, int radius, Action<int, int> applyBand)
    {
        float[] kernel = BuildGaussianKernel(radius);
        int ringRows = Math.Min(height, bandRows + 2 * radius + 1);
        var pool = ArrayPool<float>.Shared;
        var redRows = pool.Rent(width * ringRows);
        var greenRows = pool.Rent(width * ringRows);
        var blueRows = pool.Rent(width * ringRows);
        try
        {
            int prepared = 0;
            for (int start = 0; start < height; start += bandRows)
            {
                int end = Math.Min(height, start + bandRows);
                int needed = Math.Min(height, end + radius);
                Parallel.For(prepared, needed, PixelParallelism, y =>
                {
                    int input = y * width;
                    int output = (y % ringRows) * width;
                    HorizontalGaussianRow(r, redRows, input, output, width, radius, kernel);
                    HorizontalGaussianRow(g, greenRows, input, output, width, radius, kernel);
                    HorizontalGaussianRow(b, blueRows, input, output, width, radius, kernel);
                });
                prepared = needed;

                int rowChunk = Math.Max(4, (end - start) / (PixelParallelism.MaxDegreeOfParallelism * 2));
                Parallel.ForEach(Partitioner.Create(start, end, rowChunk), PixelParallelism, range =>
                {
                    for (int y = range.Item1; y < range.Item2; y++)
                    {
                        int dstOff = (y - start) * width;
                        Array.Clear(blurR, dstOff, width);
                        Array.Clear(blurG, dstOff, width);
                        Array.Clear(blurB, dstOff, width);
                        for (int k = -radius; k <= radius; k++)
                        {
                            float kw = kernel[k + radius];
                            int sy = Math.Clamp(y + k, 0, height - 1);
                            int srcOff = (sy % ringRows) * width;
                            for (int x = 0; x < width; x++)
                            {
                                blurR[dstOff + x] += redRows[srcOff + x] * kw;
                                blurG[dstOff + x] += greenRows[srcOff + x] * kw;
                                blurB[dstOff + x] += blueRows[srcOff + x] * kw;
                            }
                        }
                    }
                });
                applyBand(start, end);
            }
        }
        finally
        {
            pool.Return(redRows);
            pool.Return(greenRows);
            pool.Return(blueRows);
        }
    }

    private static void HorizontalGaussianRow(float[] src, float[] dst, int input, int output, int w, int radius, float[] kernel)
    {
        int innerStart = Math.Min(radius, w);
        int innerEnd = Math.Max(innerStart, w - radius);
        for (int x = 0; x < innerStart; x++)
        {
            float sum = 0f;
            for (int k = -radius; k <= radius; k++)
                sum += src[input + Math.Clamp(x + k, 0, w - 1)] * kernel[k + radius];
            dst[output + x] = sum;
        }
        for (int x = innerStart; x < innerEnd; x++)
        {
            float sum = 0f;
            int baseIdx = input + x - radius;
            for (int i = 0; i < kernel.Length; i++)
                sum += src[baseIdx + i] * kernel[i];
            dst[output + x] = sum;
        }
        for (int x = innerEnd; x < w; x++)
        {
            float sum = 0f;
            for (int k = -radius; k <= radius; k++)
                sum += src[input + Math.Clamp(x + k, 0, w - 1)] * kernel[k + radius];
            dst[output + x] = sum;
        }
    }

    private static void FilterColorBands(
        float[] r, float[] g, float[] b, float[] blurR, float[] blurG, float[] blurB,
        int width, int height, int bandRows, int radius, Action<int, int> applyBand)
    {
        if (bandRows == height)
        {
            int pixels = width * height;
            BoxBlurFloatInto(r, blurR, pixels, width, height, radius);
            BoxBlurFloatInto(g, blurG, pixels, width, height, radius);
            BoxBlurFloatInto(b, blurB, pixels, width, height, radius);
            applyBand(0, height);
            return;
        }

        // Save old horizontal rows before updating the source. Vertical sums carry across
        // bands so the filter uses the same neighbors and operation order at every boundary.
        int ringRows = Math.Min(height, bandRows + 2 * radius + 1);
        var pool = ArrayPool<float>.Shared;
        var redRows = pool.Rent(width * ringRows);
        var greenRows = pool.Rent(width * ringRows);
        var blueRows = pool.Rent(width * ringRows);
        var sums = new float[width * 3];
        float invWin = 1f / (radius * 2 + 1);
        int stripWidth = Vector<float>.Count * 8;
        try
        {
            int prepared = 0;
            for (int start = 0; start < height; start += bandRows)
            {
                int end = Math.Min(height, start + bandRows);
                int needed = Math.Min(height, end + radius + 1);
                Parallel.For(prepared, needed, PixelParallelism, y =>
                {
                    int input = y * width;
                    int output = (y % ringRows) * width;
                    HorizontalBlurRow(r, redRows, input, output, width, radius, invWin);
                    HorizontalBlurRow(g, greenRows, input, output, width, radius, invWin);
                    HorizontalBlurRow(b, blueRows, input, output, width, radius, invWin);
                });
                prepared = needed;

                Parallel.For(0, (width + stripWidth - 1) / stripWidth, PixelParallelism, strip =>
                {
                    int x = strip * stripWidth;
                    int count = Math.Min(stripWidth, width - x);
                    VerticalBlurBand(redRows, blurR, sums, x, x, count, start, end, width, height, ringRows, radius, invWin);
                    VerticalBlurBand(greenRows, blurG, sums, width + x, x, count, start, end, width, height, ringRows, radius, invWin);
                    VerticalBlurBand(blueRows, blurB, sums, 2 * width + x, x, count, start, end, width, height, ringRows, radius, invWin);
                });
                applyBand(start, end);
            }
        }
        finally
        {
            pool.Return(redRows);
            pool.Return(greenRows);
            pool.Return(blueRows);
        }
    }

    private static void VerticalBlurBand(float[] rows, float[] output, float[] sums, int sumOffset,
        int x, int count, int firstRow, int endRow, int width, int height, int ringRows, int radius, float invWin)
    {
        int lanes = Vector<float>.Count;
        int vectorEnd = count / lanes * lanes;
        bool fullStrip = count == lanes * 8;
        var inverse = new Vector<float>(invWin);
        fixed (float* source = rows)
        fixed (float* destination = output)
        fixed (float* sumBase = sums)
        {
            float* sum = sumBase + sumOffset;
            float* input = source + x;
            if (firstRow == 0)
            {
                for (int col = 0; col < count; col++) sum[col] = input[col] * (radius + 1);
                for (int k = 1; k <= radius; k++)
                {
                    float* row = input + Math.Min(k, height - 1) * width;
                    for (int col = 0; col < count; col++) sum[col] += row[col];
                }
            }
            for (int y = firstRow; y < endRow; y++)
            {
                float* dest = destination + (y - firstRow) * width + x;
                float* add = input + (Math.Min(y + radius + 1, height - 1) % ringRows) * width;
                float* sub = input + (Math.Max(y - radius, 0) % ringRows) * width;
                for (int col = 0; col < vectorEnd; col += lanes)
                {
                    var current = LoadVec(sum, col);
                    var entering = LoadVec(add, col);
                    var leaving = LoadVec(sub, col);
                    StoreVec(dest, col, current * inverse);
                    // Match VerticalStripInPlace's accumulation order for full and partial strips.
                    StoreVec(sum, col, fullStrip ? current + (entering - leaving) : (current + entering) - leaving);
                }
                for (int col = vectorEnd; col < count; col++)
                {
                    dest[col] = sum[col] * invWin;
                    sum[col] += add[col] - sub[col];
                }
            }
        }
    }

    /// <summary>
    /// 快速 O(N) 可分离滑动均值滤波：
    /// 水平阶段顺序写入 dst；垂直阶段在每个工作线程内维护 L1 缓存友好的环形行滑窗，
    /// 利用 Vector&lt;float&gt; 对多列就地写回 dst，彻底消除额外全图 temp 数组与跨行 Cache Miss。
    /// </summary>
    private static void BoxBlurFloatInto(float[] src, float[] dst, int pixelCount, int w, int h, int radius)
    {
        if (radius <= 0)
        {
            src.AsSpan(0, pixelCount).CopyTo(dst.AsSpan(0, pixelCount));
            return;
        }

        float invWin = 1f / (radius * 2 + 1);
        int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));

        // 1. 水平滤波 src -> dst
        Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
        {
            for (int y = range.Item1; y < range.Item2; y++)
                HorizontalBlurRow(src, dst, y * w, y * w, w, radius, invWin);
        });

        // 2. 垂直就地条带滤波（使用 Vector<float> 宽带 + 线程本地 (radius+1)*stripW 环形输入历史窗）
        int vecLen = Vector<float>.Count;
        int stripW = vecLen * 8; // AVX2 下为 64 列 (256 字节/行)
        int stripCount = (w + stripW - 1) / stripW;
        int ringSlots = radius + 1;
        int ringCapacity = ringSlots * stripW;

        Parallel.For(0, stripCount, PixelParallelism,
            () => ArrayPool<float>.Shared.Rent(ringCapacity),
            (stripIdx, _, ring) =>
            {
                int x0 = stripIdx * stripW;
                int cols = Math.Min(stripW, w - x0);
                VerticalStripInPlace(dst, w, h, x0, cols, radius, invWin, ring, stripW);
                return ring;
            },
            ring => ArrayPool<float>.Shared.Return(ring));
    }

    private static void HorizontalBlurRow(float[] src, float[] dst, int input, int output, int w, int radius, float invWin)
    {
        float sum = src[input] * (radius + 1);
        for (int x = 1; x <= radius; x++)
            sum += src[input + Math.Min(x, w - 1)];

        if (w >= radius * 2 + 2)
        {
            for (int x = 0; x < radius; x++)
            {
                dst[output + x] = sum * invWin;
                sum += src[input + x + radius + 1] - src[input];
            }
            int innerEnd = w - radius - 1;
            for (int x = radius; x < innerEnd; x++)
            {
                dst[output + x] = sum * invWin;
                sum += src[input + x + radius + 1] - src[input + x - radius];
            }
            for (int x = innerEnd; x < w; x++)
            {
                dst[output + x] = sum * invWin;
                int addX = Math.Min(x + radius + 1, w - 1);
                sum += src[input + addX] - src[input + x - radius];
            }
        }
        else
        {
            for (int x = 0; x < w; x++)
            {
                dst[output + x] = sum * invWin;
                int addX = Math.Min(x + radius + 1, w - 1);
                int subX = Math.Max(x - radius, 0);
                sum += src[input + addX] - src[input + subX];
            }
        }
    }

    private static void VerticalStripInPlace(
        float[] dst, int w, int h, int x0, int cols, int radius, float invWin, float[] ring, int stripStride)
    {
        int vecLen = Vector<float>.Count;
        int vecCount = cols / vecLen;
        int tailOffset = vecCount * vecLen;
        int ringSlots = radius + 1;
        var vInvWin = new Vector<float>(invWin);
        var vTopMul = new Vector<float>(radius + 1);

        fixed (float* pDstBase = dst)
        fixed (float* pRing = ring)
        {
            // 若能占满全部 8 个向量（典型内区条带 64 列），用寄存器数组避开多余堆读写
            if (vecCount == 8)
            {
                float* pCol0 = pDstBase + x0;
                Vector<float> s0 = LoadVec(pCol0, 0) * vTopMul;
                Vector<float> s1 = LoadVec(pCol0, vecLen * 1) * vTopMul;
                Vector<float> s2 = LoadVec(pCol0, vecLen * 2) * vTopMul;
                Vector<float> s3 = LoadVec(pCol0, vecLen * 3) * vTopMul;
                Vector<float> s4 = LoadVec(pCol0, vecLen * 4) * vTopMul;
                Vector<float> s5 = LoadVec(pCol0, vecLen * 5) * vTopMul;
                Vector<float> s6 = LoadVec(pCol0, vecLen * 6) * vTopMul;
                Vector<float> s7 = LoadVec(pCol0, vecLen * 7) * vTopMul;

                for (int k = 1; k <= radius; k++)
                {
                    float* pk = pDstBase + Math.Min(k, h - 1) * w + x0;
                    s0 += LoadVec(pk, 0);
                    s1 += LoadVec(pk, vecLen * 1);
                    s2 += LoadVec(pk, vecLen * 2);
                    s3 += LoadVec(pk, vecLen * 3);
                    s4 += LoadVec(pk, vecLen * 4);
                    s5 += LoadVec(pk, vecLen * 5);
                    s6 += LoadVec(pk, vecLen * 6);
                    s7 += LoadVec(pk, vecLen * 7);
                }

                for (int y = 0; y < h; y++)
                {
                    float* rowPtr = pDstBase + y * w + x0;
                    float* wSlot = pRing + (y % ringSlots) * stripStride;
                    Buffer.MemoryCopy(rowPtr, wSlot, stripStride * sizeof(float), cols * sizeof(float));

                    StoreVec(rowPtr, 0, s0 * vInvWin);
                    StoreVec(rowPtr, vecLen * 1, s1 * vInvWin);
                    StoreVec(rowPtr, vecLen * 2, s2 * vInvWin);
                    StoreVec(rowPtr, vecLen * 3, s3 * vInvWin);
                    StoreVec(rowPtr, vecLen * 4, s4 * vInvWin);
                    StoreVec(rowPtr, vecLen * 5, s5 * vInvWin);
                    StoreVec(rowPtr, vecLen * 6, s6 * vInvWin);
                    StoreVec(rowPtr, vecLen * 7, s7 * vInvWin);

                    float* addPtr = pDstBase + Math.Min(y + radius + 1, h - 1) * w + x0;
                    int subIdx = y > radius ? (y - radius) % ringSlots : 0;
                    float* subPtr = pRing + subIdx * stripStride;

                    s0 += LoadVec(addPtr, 0) - LoadVec(subPtr, 0);
                    s1 += LoadVec(addPtr, vecLen * 1) - LoadVec(subPtr, vecLen * 1);
                    s2 += LoadVec(addPtr, vecLen * 2) - LoadVec(subPtr, vecLen * 2);
                    s3 += LoadVec(addPtr, vecLen * 3) - LoadVec(subPtr, vecLen * 3);
                    s4 += LoadVec(addPtr, vecLen * 4) - LoadVec(subPtr, vecLen * 4);
                    s5 += LoadVec(addPtr, vecLen * 5) - LoadVec(subPtr, vecLen * 5);
                    s6 += LoadVec(addPtr, vecLen * 6) - LoadVec(subPtr, vecLen * 6);
                    s7 += LoadVec(addPtr, vecLen * 7) - LoadVec(subPtr, vecLen * 7);
                }
                return;
            }

            // 尾条带：先初始化 sum 行缓冲（复借 stackalloc）
            float* colSums = stackalloc float[stripStride];
            float* pFirst = pDstBase + x0;
            for (int c = 0; c < cols; c++)
                colSums[c] = pFirst[c] * (radius + 1);

            for (int k = 1; k <= radius; k++)
            {
                float* pk = pDstBase + Math.Min(k, h - 1) * w + x0;
                for (int c = 0; c < cols; c++)
                    colSums[c] += pk[c];
            }

            for (int y = 0; y < h; y++)
            {
                float* rowPtr = pDstBase + y * w + x0;
                float* wSlot = pRing + (y % ringSlots) * stripStride;
                Buffer.MemoryCopy(rowPtr, wSlot, stripStride * sizeof(float), cols * sizeof(float));

                for (int v = 0; v < vecCount; v++)
                {
                    int off = v * vecLen;
                    StoreVec(rowPtr, off, LoadVec(colSums, off) * vInvWin);
                }
                for (int c = tailOffset; c < cols; c++)
                    rowPtr[c] = colSums[c] * invWin;

                float* addPtr = pDstBase + Math.Min(y + radius + 1, h - 1) * w + x0;
                int subIdx = y > radius ? (y - radius) % ringSlots : 0;
                float* subPtr = pRing + subIdx * stripStride;

                for (int v = 0; v < vecCount; v++)
                {
                    int off = v * vecLen;
                    var vs = LoadVec(colSums, off) + LoadVec(addPtr, off) - LoadVec(subPtr, off);
                    StoreVec(colSums, off, vs);
                }
                for (int c = tailOffset; c < cols; c++)
                    colSums[c] += addPtr[c] - subPtr[c];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> LoadVec(float* ptr, int floatOffset) =>
        Unsafe.ReadUnaligned<Vector<float>>(ptr + floatOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreVec(float* ptr, int floatOffset, Vector<float> value) =>
        Unsafe.WriteUnaligned(ptr + floatOffset, value);
}
