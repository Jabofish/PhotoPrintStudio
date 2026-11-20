using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

public static unsafe partial class FloatImageProcessor
{
    private const float MatteEdgeOpacityLimit = 0.97f;

    private readonly struct LocalColorField(
        float[] fgR, float[] fgG, float[] fgB,
        float[] bgR, float[] bgG, float[] bgB,
        float[] interiorAlpha,
        int sw, int sh, float scaleX, float scaleY) : IDisposable
    {
        public readonly float[] FgR = fgR;
        public readonly float[] FgG = fgG;
        public readonly float[] FgB = fgB;
        public readonly float[] BgR = bgR;
        public readonly float[] BgG = bgG;
        public readonly float[] BgB = bgB;
        public readonly float[] InteriorAlpha = interiorAlpha;
        public readonly int Sw = sw;
        public readonly int Sh = sh;
        public readonly float ScaleX = scaleX;
        public readonly float ScaleY = scaleY;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Sample(
            int x, int y,
            out float fR, out float fG, out float fB,
            out float bR, out float bG, out float bB,
            out float innerA)
        {
            float sx = (x + 0.5f) * ScaleX - 0.5f;
            float sy = (y + 0.5f) * ScaleY - 0.5f;
            int x0 = Math.Clamp((int)MathF.Floor(sx), 0, Sw - 1);
            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, Sh - 1);
            int x1 = x0 + 1 < Sw ? x0 + 1 : x0;
            int y1 = y0 + 1 < Sh ? y0 + 1 : y0;
            float fx = Math.Clamp(sx - x0, 0f, 1f);
            float fy = Math.Clamp(sy - y0, 0f, 1f);
            int r0 = y0 * Sw;
            int r1 = y1 * Sw;

            fR = Bilerp(FgR, r0, r1, x0, x1, fx, fy);
            fG = Bilerp(FgG, r0, r1, x0, x1, fx, fy);
            fB = Bilerp(FgB, r0, r1, x0, x1, fx, fy);
            bR = Bilerp(BgR, r0, r1, x0, x1, fx, fy);
            bG = Bilerp(BgG, r0, r1, x0, x1, fx, fy);
            bB = Bilerp(BgB, r0, r1, x0, x1, fx, fy);
            innerA = Bilerp(InteriorAlpha, r0, r1, x0, x1, fx, fy);
        }

        public void Dispose()
        {
            FloatPool.Return(FgR);
            FloatPool.Return(FgG);
            FloatPool.Return(FgB);
            FloatPool.Return(BgR);
            FloatPool.Return(BgG);
            FloatPool.Return(BgB);
            FloatPool.Return(InteriorAlpha);
        }
    }

    private static void UnpackWithWhiteWall(
        byte* srcPtr, int srcRowBytes,
        float[] rBuf, float[] gBuf, float[] bBuf,
        int w, int h, float whiteStrength)
    {
        bool doWhiteWall = whiteStrength > 0.001f;
        float wwThreshold = 0.72f - whiteStrength * 0.16f;
        float[] byteLut = ByteToUnitLut;
        int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));

        Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
        {
            for (int y = range.Item1; y < range.Item2; y++)
            {
                byte* sRow = srcPtr + y * srcRowBytes;
                int rowOffset = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx4 = x * 4;
                    float b = byteLut[sRow[idx4]];
                    float g = byteLut[sRow[idx4 + 1]];
                    float r = byteLut[sRow[idx4 + 2]];

                    if (doWhiteWall)
                        ApplyWhiteWallPixel(ref r, ref g, ref b, wwThreshold, whiteStrength);

                    int i = rowOffset + x;
                    rBuf[i] = r;
                    gBuf[i] = g;
                    bBuf[i] = b;
                }
            }
        });
    }

    /// <summary>
    /// 上采样模型遮罩，去除背景残余底噪并用原图亮度对齐半透明边缘。
    /// </summary>
    private static void PrepareRefinedAlpha(
        float[] rBuf, float[] gBuf, float[] bBuf,
        float[] alphaOut,
        int w, int h,
        float[]? fullAiMatte,
        CompactAlphaMatte? compactAlphaMatte)
    {
        int pixelCount = w * h;
        int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));

        // 1. 原始 Alpha 上采样 + 背景微弱底噪线性软截断
        if (fullAiMatte != null)
        {
            int chunk = Math.Max(4096, pixelCount / (PixelParallelism.MaxDegreeOfParallelism * 4));
            Parallel.ForEach(Partitioner.Create(0, pixelCount, chunk), PixelParallelism, range =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                    alphaOut[i] = Math.Clamp((fullAiMatte[i] - 0.02f) * (1f / 0.96f), 0f, 1f);
            });
        }
        else if (compactAlphaMatte != null)
        {
            using var sampler = new CompactMatteSampler(
                compactAlphaMatte.Data,
                compactAlphaMatte.Width,
                compactAlphaMatte.Height,
                compactAlphaMatte.CropX,
                compactAlphaMatte.CropY,
                compactAlphaMatte.CropW / Math.Max(1, w),
                compactAlphaMatte.CropH / Math.Max(1, h),
                rentedBuffer: null);

            Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    int rowOffset = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        float raw = sampler.SampleBilinear(x, y);
                        alphaOut[rowOffset + x] = Math.Clamp((raw - 0.02f) * (1f / 0.96f), 0f, 1f);
                    }
                }
            });
        }

        // 2. Fast Guided Filter（以高分辨率原图亮度作为引导图，在过渡带内对齐发丝边缘）
        if (w >= 32 && h >= 32)
        {
            RefineAlphaWithFastGuidedFilter(rBuf, gBuf, bBuf, alphaOut, w, h);
        }

    }

    private static void RefineMatteEdges(
        float[] rBuf, float[] gBuf, float[] bBuf, float[] alphaOut,
        int w, int h, LocalColorField colorField, double featherAmount, float spillStrength, double edgeShift)
    {
        int pixelCount = w * h;
        int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));
        // 0. 可选亚像素边缘微缩/微扩（Edge Shift：负值内缩收边，正值微扩保细发）
        if (Math.Abs(edgeShift) > 0.05 && w >= 16 && h >= 16)
        {
            ApplySubpixelEdgeShift(alphaOut, w, h, (float)Math.Clamp(edgeShift, -2.5, 1.5));
        }

        // 只在半透明轮廓带内用颜色估计覆盖率。实心发丝、衣料亮纹与耳缘的颜色变化不能当成露底。
        float decontamCurve = spillStrength > 0.001f ? MathF.Pow(Math.Clamp(spillStrength, 0f, 1f), 0.50f) : 0f;
        if (decontamCurve > 0.02f)
        {
            Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    int rowOffset = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOffset + x;
                        float a = alphaOut[i];
                        if (a <= 0.004f || a >= MatteEdgeOpacityLimit) continue;

                        colorField.Sample(x, y,
                            out float fR, out float fG, out float fB,
                            out float bR, out float bG, out float bB,
                            out float innerA);

                        // 仅在距离轮廓边界数像素内的空间外缘带（或半透明区）执行收敛，深内区 100% 锁定不动
                        float rimBand = MathF.Max(1f - SmoothStep(0.55f, 0.985f, innerA), 1f - SmoothStep(0.55f, 0.96f, a));
                        if (rimBand <= 0.01f) continue;

                        float dR = fR - bR, dG = fG - bG, dB = fB - bB;
                        float distSq = dR * dR + dG * dG + dB * dB;
                        if (distSq < 0.0484f) continue; // 前背景色差 < 0.22（如白衣对白墙）时完全信任模型 Alpha

                        float oR = rBuf[i] - bR, oG = gBuf[i] - bG, oB = bBuf[i] - bB;
                        float aColor = Math.Clamp((oR * dR + oG * dG + oB * dB) / (distSq + 1e-5f), 0f, 1f);
                        if (aColor >= a - 0.01f) continue;

                        // 只有在颜色接近前背景混色线时才降低覆盖率，保护与附近头发颜色不同的耳缘等结构。
                        float pR = oR - aColor * dR;
                        float pG = oG - aColor * dG;
                        float pB = oB - aColor * dB;
                        float perpDist = MathF.Sqrt(pR * pR + pG * pG + pB * pB);
                        if (perpDist >= 0.11f) continue;

                        float contrast = MathF.Sqrt(distSq);
                        float conf = SmoothStep(0.22f, 0.50f, contrast)
                                     * (1f - SmoothStep(0.035f, 0.11f, perpDist))
                                     * rimBand * (1f - SmoothStep(0.90f, MatteEdgeOpacityLimit, a))
                                     * decontamCurve;
                        // 对外缘几乎纯背景的像素（aColor 很低）施加更强收敛，彻底扫除 2~3px 泛白/红紫光晕环
                        float pullStrength = aColor < 0.35f ? 0.88f : 0.68f;
                        alphaOut[i] = Math.Clamp(a + (aColor - a) * (pullStrength * conf), 0f, 1f);
                    }
                }
            });
        }

        // 5. 内缩安全羽化：只向人物内侧柔化边缘，绝不向外扩张产生白光晕
        if (featherAmount > 0.15 && w >= 16 && h >= 16)
        {
            float scaleRef = Math.Min(w, h) / 1000f;
            int radius = Math.Clamp((int)MathF.Round((float)featherAmount * Math.Max(0.55f, scaleRef)), 1, 8);
            float[] blurred = FloatPool.Rent(pixelCount);
            try
            {
                BoxBlurFloatInto(alphaOut, blurred, pixelCount, w, h, radius);
                int chunk = Math.Max(4096, pixelCount / (PixelParallelism.MaxDegreeOfParallelism * 4));
                Parallel.ForEach(Partitioner.Create(0, pixelCount, chunk), PixelParallelism, range =>
                {
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        float a = alphaOut[i];
                        float ba = blurred[i];
                        float inward = MathF.Min(a, ba);
                        float tOut = SmoothStep(0.85f, 0.25f, a);
                        alphaOut[i] = ba + (inward - ba) * tOut;
                    }
                });
            }
            finally
            {
                FloatPool.Return(blurred);
            }
        }
    }

    /// <summary>
    /// 亚像素连续形态学边缘微缩/微扩：利用可分离 1D 最小/最大滤波并在原 Alpha 与形态学结果间按小数比例插值，
    /// 实现平滑无阶梯的 -2.0px ~ +1.0px 精细收边或细发补偿。
    /// </summary>
    private static void ApplySubpixelEdgeShift(float[] alpha, int w, int h, float shiftPx)
    {
        int pixelCount = w * h;
        float scaleRef = Math.Max(0.6f, Math.Min(w, h) / 1000f);
        float scaledShift = MathF.Abs(shiftPx) * scaleRef;
        int radius = Math.Clamp((int)MathF.Ceiling(scaledShift), 1, 4);
        float frac = Math.Clamp(scaledShift / radius, 0f, 1f);
        bool shrink = shiftPx < 0f;

        float[] temp = FloatPool.Rent(pixelCount);
        float[] morph = FloatPool.Rent(pixelCount);
        try
        {
            int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));
            Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        float best = alpha[row + x];
                        int x0 = Math.Max(0, x - radius);
                        int x1 = Math.Min(w - 1, x + radius);
                        if (shrink)
                        {
                            for (int kx = x0; kx <= x1; kx++)
                                best = MathF.Min(best, alpha[row + kx]);
                        }
                        else
                        {
                            for (int kx = x0; kx <= x1; kx++)
                                best = MathF.Max(best, alpha[row + kx]);
                        }
                        temp[row + x] = best;
                    }
                }
            });

            Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    int y0 = Math.Max(0, y - radius);
                    int y1 = Math.Min(h - 1, y + radius);
                    int dstRow = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        float best = temp[dstRow + x];
                        if (shrink)
                        {
                            for (int ky = y0; ky <= y1; ky++)
                                best = MathF.Min(best, temp[ky * w + x]);
                        }
                        else
                        {
                            for (int ky = y0; ky <= y1; ky++)
                                best = MathF.Max(best, temp[ky * w + x]);
                        }
                        morph[dstRow + x] = best;
                    }
                }
            });

            int chunk = Math.Max(4096, pixelCount / (PixelParallelism.MaxDegreeOfParallelism * 4));
            Parallel.ForEach(Partitioner.Create(0, pixelCount, chunk), PixelParallelism, range =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    float a = alpha[i];
                    alpha[i] = Math.Clamp(a + (morph[i] - a) * frac, 0f, 1f);
                }
            });
        }
        finally
        {
            FloatPool.Return(morph);
            FloatPool.Return(temp);
        }
    }

    /// <summary>
    /// He &amp; Sun Fast Guided Filter（O(N) 线性复杂度）：
    /// 在下采样引导尺度计算局部线性模型 q = meanA * I + meanB，再双线性上采样系数回到全分辨率原图，
    /// 使边缘 Alpha 精准锁紧高分辨率头发丝的亮度边界。
    /// </summary>
    private static void RefineAlphaWithFastGuidedFilter(
        float[] rBuf, float[] gBuf, float[] bBuf,
        float[] alpha, int w, int h)
    {
        int minDim = Math.Min(w, h);
        int ds = minDim > 720 ? Math.Clamp(minDim / 540, 2, 6) : 1;
        int gw = Math.Max(8, (w + ds - 1) / ds);
        int gh = Math.Max(8, (h + ds - 1) / ds);
        int gCount = gw * gh;
        int radius = Math.Clamp((int)MathF.Round(Math.Min(gw, gh) * 0.0055f), 2, 5);
        const float eps = 0.0018f;

        float[] subI = FloatPool.Rent(gCount);
        float[] subP = FloatPool.Rent(gCount);
        float[] meanI = FloatPool.Rent(gCount);
        float[] meanP = FloatPool.Rent(gCount);
        float[] bufII = FloatPool.Rent(gCount);
        float[] bufIP = FloatPool.Rent(gCount);
        float[] meanII = FloatPool.Rent(gCount);
        float[] meanIP = FloatPool.Rent(gCount);
        try
        {
            int gChunk = Math.Max(4, gh / (PixelParallelism.MaxDegreeOfParallelism * 4));
            Parallel.ForEach(Partitioner.Create(0, gh, gChunk), PixelParallelism, range =>
            {
                for (int gy = range.Item1; gy < range.Item2; gy++)
                {
                    int sy0 = gy * ds;
                    int sy1 = Math.Min(h, sy0 + ds);
                    int gRow = gy * gw;
                    for (int gx = 0; gx < gw; gx++)
                    {
                        int sx0 = gx * ds;
                        int sx1 = Math.Min(w, sx0 + ds);
                        float sI = 0f, sP = 0f;
                        int cnt = 0;
                        for (int sy = sy0; sy < sy1; sy++)
                        {
                            int row = sy * w;
                            for (int sx = sx0; sx < sx1; sx++)
                            {
                                int idx = row + sx;
                                sI += 0.299f * rBuf[idx] + 0.587f * gBuf[idx] + 0.114f * bBuf[idx];
                                sP += alpha[idx];
                                cnt++;
                            }
                        }
                        float inv = 1f / Math.Max(1, cnt);
                        float iVal = sI * inv;
                        float pVal = sP * inv;
                        int gi = gRow + gx;
                        subI[gi] = iVal;
                        subP[gi] = pVal;
                        bufII[gi] = iVal * iVal;
                        bufIP[gi] = iVal * pVal;
                    }
                }
            });

            BoxBlurFloatInto(subI, meanI, gCount, gw, gh, radius);
            BoxBlurFloatInto(subP, meanP, gCount, gw, gh, radius);
            BoxBlurFloatInto(bufII, meanII, gCount, gw, gh, radius);
            BoxBlurFloatInto(bufIP, meanIP, gCount, gw, gh, radius);

            int chunk = Math.Max(2048, gCount / (PixelParallelism.MaxDegreeOfParallelism * 4));
            Parallel.ForEach(Partitioner.Create(0, gCount, chunk), PixelParallelism, range =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    float mi = meanI[i];
                    float mp = meanP[i];
                    float varI = MathF.Max(0f, meanII[i] - mi * mi);
                    float covIP = meanIP[i] - mi * mp;
                    float aCoeff = covIP / (varI + eps);
                    bufII[i] = aCoeff;
                    bufIP[i] = mp - aCoeff * mi;
                }
            });

            BoxBlurFloatInto(bufII, subI, gCount, gw, gh, radius); // subI = meanA
            BoxBlurFloatInto(bufIP, subP, gCount, gw, gh, radius); // subP = meanB

            float scaleX = gw / (float)w;
            float scaleY = gh / (float)h;
            int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));
            Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    float sy = (y + 0.5f) * scaleY - 0.5f;
                    int y0 = Math.Clamp((int)MathF.Floor(sy), 0, gh - 1);
                    int y1 = y0 + 1 < gh ? y0 + 1 : y0;
                    float fy = Math.Clamp(sy - y0, 0f, 1f);
                    int r0 = y0 * gw, r1 = y1 * gw;
                    int rowOffset = y * w;

                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOffset + x;
                        float origA = alpha[i];
                        if (origA <= 0.002f || origA >= 0.996f) continue;

                        float sx = (x + 0.5f) * scaleX - 0.5f;
                        int x0 = Math.Clamp((int)MathF.Floor(sx), 0, gw - 1);
                        int x1 = x0 + 1 < gw ? x0 + 1 : x0;
                        float fx = Math.Clamp(sx - x0, 0f, 1f);

                        float mA = Bilerp(subI, r0, r1, x0, x1, fx, fy);
                        float mB = Bilerp(subP, r0, r1, x0, x1, fx, fy);
                        float guide = 0.299f * rBuf[i] + 0.587f * gBuf[i] + 0.114f * bBuf[i];
                        float q = Math.Clamp(mA * guide + mB, 0f, 1f);

                        float band = SmoothStep(0.002f, 0.04f, origA) * SmoothStep(MatteEdgeOpacityLimit, 0.82f, origA);
                        alpha[i] = origA + (q - origA) * (0.85f * band);
                    }
                }
            });
        }
        finally
        {
            FloatPool.Return(meanIP);
            FloatPool.Return(meanII);
            FloatPool.Return(bufIP);
            FloatPool.Return(bufII);
            FloatPool.Return(meanP);
            FloatPool.Return(meanI);
            FloatPool.Return(subP);
            FloatPool.Return(subI);
        }
    }

    /// <summary>
    /// 基于“空间腐蚀深内区”的两尺度归一化加权颜色外推：
    /// 先对下采样 Alpha 做向内腐蚀（erodedInterior），确保 F_deep 100% 仅从深入头发/衣领内部 ≥4~6px 的零污染像素采样，
    /// 同时输出 erodedInterior 供轮廓带去污染使用，彻底隔绝模型外缘 2~4px 泛白/红紫边混入前景色估计。
    /// </summary>
    private static LocalColorField BuildLocalColorField(
        float[] rBuf, float[] gBuf, float[] bBuf, float[] alpha, int w, int h)
    {
        int minDim = Math.Min(w, h);
        int ds = minDim > 460 ? Math.Clamp(minDim / 380, 2, 8) : 1;
        int sw = Math.Max(8, (w + ds - 1) / ds);
        int sh = Math.Max(8, (h + ds - 1) / ds);
        int count = sw * sh;

        float[] fgR = FloatPool.Rent(count);
        float[] fgG = FloatPool.Rent(count);
        float[] fgB = FloatPool.Rent(count);
        float[] bgR = FloatPool.Rent(count);
        float[] bgG = FloatPool.Rent(count);
        float[] bgB = FloatPool.Rent(count);
        float[] interiorAlpha = FloatPool.Rent(count);

        float[] subR = FloatPool.Rent(count);
        float[] subG = FloatPool.Rent(count);
        float[] subB = FloatPool.Rent(count);
        float[] subA = FloatPool.Rent(count);
        float[] bgOuterA = FloatPool.Rent(count);
        float[] fgW = FloatPool.Rent(count);
        float[] bgW = FloatPool.Rent(count);
        float[] t1R = FloatPool.Rent(count);
        float[] t1G = FloatPool.Rent(count);
        float[] t1B = FloatPool.Rent(count);
        float[] t1W = FloatPool.Rent(count);
        float[] t2R = FloatPool.Rent(count);
        float[] t2G = FloatPool.Rent(count);
        float[] t2B = FloatPool.Rent(count);
        float[] t2W = FloatPool.Rent(count);

        try
        {
            // 1. 下采样原图 RGB 与 Alpha
            int rowChunk = Math.Max(4, sh / (PixelParallelism.MaxDegreeOfParallelism * 4));
            Parallel.ForEach(Partitioner.Create(0, sh, rowChunk), PixelParallelism, range =>
            {
                for (int dy = range.Item1; dy < range.Item2; dy++)
                {
                    int sy0 = dy * ds;
                    int sy1 = Math.Min(h, sy0 + ds);
                    int dRow = dy * sw;
                    for (int dx = 0; dx < sw; dx++)
                    {
                        int sx0 = dx * ds;
                        int sx1 = Math.Min(w, sx0 + ds);
                        float sr = 0f, sg = 0f, sb = 0f, sa = 0f;
                        int cnt = 0;
                        for (int sy = sy0; sy < sy1; sy++)
                        {
                            int sOff = sy * w;
                            for (int sx = sx0; sx < sx1; sx++)
                            {
                                int idx = sOff + sx;
                                sr += rBuf[idx];
                                sg += gBuf[idx];
                                sb += bBuf[idx];
                                sa += alpha[idx];
                                cnt++;
                            }
                        }
                        float inv = 1f / Math.Max(1, cnt);
                        int di = dRow + dx;
                        subR[di] = sr * inv;
                        subG[di] = sg * inv;
                        subB[di] = sb * inv;
                        subA[di] = sa * inv;
                    }
                }
            });

            // 2. 空间腐蚀构建纯净深内区（innerRad ≈ 原图 4~6px）与纯净外背景区
            int innerRad = Math.Clamp((int)MathF.Round(Math.Min(sw, sh) * 0.0085f), 2, 5);
            BoxBlurFloatInto(subA, interiorAlpha, count, sw, sh, innerRad);
            // 再次向内收紧：只有当周围 innerRad 窗口均值 >= 0.96 且自身 >= 0.96 时，才算零污染深内区！
            int chunk = Math.Max(2048, count / (PixelParallelism.MaxDegreeOfParallelism * 4));
            Parallel.ForEach(Partitioner.Create(0, count, chunk), PixelParallelism, range =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    float blurA = interiorAlpha[i];
                    float a = subA[i];
                    // 深内区权重：腐蚀掉最外圈 3~5px 易受背景溢色的边缘像素
                    float pureInner = SmoothStep(0.91f, 0.992f, MathF.Min(a, blurA));
                    interiorAlpha[i] = pureInner;

                    float wf = pureInner * pureInner;
                    fgR[i] = subR[i] * wf;
                    fgG[i] = subG[i] * wf;
                    fgB[i] = subB[i] * wf;
                    fgW[i] = wf;

                    // 纯净外背景区：同样远离轮廓 3~5px 采样，避免把发丝混入 B_orig
                    float pureOuter = SmoothStep(0.09f, 0.008f, MathF.Max(a, blurA));
                    bgOuterA[i] = pureOuter;
                    float wb = pureOuter * pureOuter;
                    bgR[i] = subR[i] * wb;
                    bgG[i] = subG[i] * wb;
                    bgB[i] = subB[i] * wb;
                    bgW[i] = wb;
                }
            });

            int rTight = Math.Clamp(Math.Min(sw, sh) / 56, 4, 8);
            int rWide = Math.Clamp(Math.Min(sw, sh) / 16, 12, 26);

            // 前景两尺度归一化外推（从深内区无缝外推至发丝与外缘）
            ExtrapolateTwoScale(fgR, fgG, fgB, fgW, t1R, t1G, t1B, t1W, t2R, t2G, t2B, t2W, count, sw, sh, rTight, rWide);
            // 背景两尺度归一化外推（从纯外墙无缝内推至发丝边界）
            ExtrapolateTwoScale(bgR, bgG, bgB, bgW, t1R, t1G, t1B, t1W, t2R, t2G, t2B, t2W, count, sw, sh, rTight, rWide);

            return new LocalColorField(fgR, fgG, fgB, bgR, bgG, bgB, interiorAlpha, sw, sh, sw / (float)w, sh / (float)h);
        }
        catch
        {
            FloatPool.Return(fgR); FloatPool.Return(fgG); FloatPool.Return(fgB);
            FloatPool.Return(bgR); FloatPool.Return(bgG); FloatPool.Return(bgB);
            FloatPool.Return(interiorAlpha);
            throw;
        }
        finally
        {
            FloatPool.Return(t2W); FloatPool.Return(t2B); FloatPool.Return(t2G); FloatPool.Return(t2R);
            FloatPool.Return(t1W); FloatPool.Return(t1B); FloatPool.Return(t1G); FloatPool.Return(t1R);
            FloatPool.Return(bgW); FloatPool.Return(fgW);
            FloatPool.Return(bgOuterA);
            FloatPool.Return(subA); FloatPool.Return(subB); FloatPool.Return(subG); FloatPool.Return(subR);
        }
    }

    private static void ExtrapolateTwoScale(
        float[] cR, float[] cG, float[] cB, float[] cW,
        float[] t1R, float[] t1G, float[] t1B, float[] t1W,
        float[] t2R, float[] t2G, float[] t2B, float[] t2W,
        int count, int sw, int sh, int rTight, int rWide)
    {
        BoxBlurFloatInto(cR, t2R, count, sw, sh, rTight);
        BoxBlurFloatInto(t2R, t1R, count, sw, sh, rTight);
        BoxBlurFloatInto(cG, t2G, count, sw, sh, rTight);
        BoxBlurFloatInto(t2G, t1G, count, sw, sh, rTight);
        BoxBlurFloatInto(cB, t2B, count, sw, sh, rTight);
        BoxBlurFloatInto(t2B, t1B, count, sw, sh, rTight);
        BoxBlurFloatInto(cW, t2W, count, sw, sh, rTight);
        BoxBlurFloatInto(t2W, t1W, count, sw, sh, rTight);

        BoxBlurFloatInto(t1R, t2R, count, sw, sh, rWide);
        BoxBlurFloatInto(t1G, t2G, count, sw, sh, rWide);
        BoxBlurFloatInto(t1B, t2B, count, sw, sh, rWide);
        BoxBlurFloatInto(t1W, t2W, count, sw, sh, rWide);

        double gSumR = 0, gSumG = 0, gSumB = 0, gSumW = 0;
        for (int i = 0; i < count; i++)
        {
            gSumR += cR[i]; gSumG += cG[i]; gSumB += cB[i]; gSumW += cW[i];
        }
        float fallbackR = gSumW > 1e-4 ? (float)(gSumR / gSumW) : 0.5f;
        float fallbackG = gSumW > 1e-4 ? (float)(gSumG / gSumW) : 0.5f;
        float fallbackB = gSumW > 1e-4 ? (float)(gSumB / gSumW) : 0.5f;

        int chunk = Math.Max(2048, count / (PixelParallelism.MaxDegreeOfParallelism * 4));
        Parallel.ForEach(Partitioner.Create(0, count, chunk), PixelParallelism, range =>
        {
            for (int i = range.Item1; i < range.Item2; i++)
            {
                float w1 = t1W[i];
                float w2 = t2W[i];
                float wr, wg, wb;
                if (w2 > 1e-5f)
                {
                    float inv2 = 1f / w2;
                    wr = Math.Clamp(t2R[i] * inv2, 0f, 1f);
                    wg = Math.Clamp(t2G[i] * inv2, 0f, 1f);
                    wb = Math.Clamp(t2B[i] * inv2, 0f, 1f);
                }
                else
                {
                    wr = fallbackR; wg = fallbackG; wb = fallbackB;
                }

                if (w1 > 1e-4f)
                {
                    float inv1 = 1f / w1;
                    float tr = Math.Clamp(t1R[i] * inv1, 0f, 1f);
                    float tg = Math.Clamp(t1G[i] * inv1, 0f, 1f);
                    float tb = Math.Clamp(t1B[i] * inv1, 0f, 1f);
                    float trustTight = Math.Clamp(w1 * 6f, 0f, 1f);
                    cR[i] = wr + (tr - wr) * trustTight;
                    cG[i] = wg + (tg - wg) * trustTight;
                    cB[i] = wb + (tb - wb) * trustTight;
                }
                else
                {
                    cR[i] = wr;
                    cG[i] = wg;
                    cB[i] = wb;
                }
            }
        });
    }

    /// <summary>
    /// 对半透明边缘去除原底溢色，并向完全透明区域外推前景色，供后续空间滤镜采样。
    /// </summary>
    private static void DecontaminateAndBleedForeground(
        float[] rBuf, float[] gBuf, float[] bBuf,
        float[] alpha, in LocalColorField colorField,
        int w, int h, float spillStrength)
    {
        float effectiveDecontam = spillStrength > 0.001f ? MathF.Pow(Math.Clamp(spillStrength, 0f, 1f), 0.48f) : 0f;
        int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));
        var field = colorField;

        Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
        {
            for (int y = range.Item1; y < range.Item2; y++)
            {
                int rowOffset = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = rowOffset + x;
                    float a = alpha[i];
                    if (a >= MatteEdgeOpacityLimit) continue;

                    field.Sample(x, y,
                        out float fR, out float fG, out float fB,
                        out float bR, out float bG, out float bB,
                        out float innerA);

                    if (a <= 0.008f)
                    {
                        // 纯外区填充为无缝深内区前景色，消除后续所有空间滤镜的边界振铃
                        rBuf[i] = fR;
                        gBuf[i] = fG;
                        bBuf[i] = fB;
                        continue;
                    }

                    if (effectiveDecontam <= 0.001f) continue;

                    float r = rBuf[i], g = gBuf[i], b = bBuf[i];
                    float dR = bR - fR, dG = bG - fG, dB = bB - fB;
                    float distSq = dR * dR + dG * dG + dB * dB;

                    // 实测该像素向原背景色偏移的污染比例 tBg (0=纯前景, 1=纯原背景)
                    float tBg = 0f;
                    float lineConf = 0f;
                    if (distSq > 0.025f)
                    {
                        float vR = r - fR, vG = g - fG, vB = b - fB;
                        tBg = Math.Clamp((vR * dR + vG * dG + vB * dB) / (distSq + 1e-5f), 0f, 1f);
                        float pR = vR - tBg * dR;
                        float pG = vG - tBg * dG;
                        float pB = vB - tBg * dB;
                        float perp = MathF.Sqrt(pR * pR + pG * pG + pB * pB);
                        lineConf = SmoothStep(0.16f, 0.36f, MathF.Sqrt(distSq)) * (1f - SmoothStep(0.035f, 0.11f, perp));
                    }

                    // 半透明发丝按覆盖率去溢色，接近实心前景时平滑停止。
                    float alphaEdgeWeight = SmoothStep(0.985f, 0.25f, a);
                    float rimMask = 1f - SmoothStep(0.35f, 0.992f, innerA);
                    float spillDetectWeight = rimMask * SmoothStep(0.05f, 0.32f, tBg) * lineConf;

                    float mix = MathF.Max(alphaEdgeWeight, spillDetectWeight)
                        * (1f - SmoothStep(0.90f, MatteEdgeOpacityLimit, a)) * effectiveDecontam;
                    if (mix <= 0.002f) continue;

                    // 混色证据充分时多依赖内侧外推色，独立颜色结构多依赖原像素。
                    float invAlphaSafe = 1f / MathF.Max(a, 0.25f);
                    float invA = 1f - a;
                    float uR = Math.Clamp((r - invA * bR) * invAlphaSafe, 0f, 1f);
                    float uG = Math.Clamp((g - invA * bG) * invAlphaSafe, 0f, 1f);
                    float uB = Math.Clamp((b - invA * bB) * invAlphaSafe, 0f, 1f);

                    float unmixTrust = 0.22f * SmoothStep(0.50f, 0.92f, a) * lineConf
                        + SmoothStep(0.22f, 0.75f, a) * (1f - lineConf);
                    float cleanR = fR + (uR - fR) * unmixTrust;
                    float cleanG = fG + (uG - fG) * unmixTrust;
                    float cleanB = fB + (uB - fB) * unmixTrust;

                    float fgLuma = 0.2126f * fR + 0.7152f * fG + 0.0722f * fB;
                    float bgLuma = 0.2126f * bR + 0.7152f * bG + 0.0722f * bB;
                    if (lineConf > 0.001f && bgLuma > fgLuma + 0.12f)
                    {
                        float cleanLuma = 0.2126f * cleanR + 0.7152f * cleanG + 0.0722f * cleanB;
                        float maxAllowed = fgLuma + 0.03f;
                        if (cleanLuma > maxAllowed)
                        {
                            float scale = 1f + (maxAllowed / cleanLuma - 1f) * lineConf;
                            cleanR *= scale;
                            cleanG *= scale;
                            cleanB *= scale;
                        }
                    }

                    rBuf[i] = r + (cleanR - r) * mix;
                    gBuf[i] = g + (cleanG - g) * mix;
                    bBuf[i] = b + (cleanB - b) * mix;
                }
            }
        });
    }

    /// <summary>
    /// 前景处理完成后，在与遮罩去溢色一致的 sRGB 空间合成底色。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CompositeBackgroundPixel(
        ref float r, ref float g, ref float b, float rawAlpha, ref byte sourceAlpha,
        int x, float invW, float ny, in AiBgStyle st)
    {
        float a = Math.Clamp(rawAlpha, 0f, 1f) * (sourceAlpha * Inv255);
        sourceAlpha = byte.MaxValue;
        if (a >= 0.999f) return;

        float bgR = st.OutR, bgG = st.OutG, bgB = st.OutB;
        if (st.UseGradient)
        {
            float nx = (x * invW - 0.5f) * 1.35f;
            float radial = Math.Clamp(1f - MathF.Sqrt(nx * nx + ny * ny) * 0.85f, 0f, 1f);
            float t = radial * radial * (3f - 2f * radial);
            bgR += (st.InR - bgR) * t;
            bgG += (st.InG - bgG) * t;
            bgB += (st.InB - bgB) * t;
        }

        if (a <= 0.001f)
        {
            r = bgR;
            g = bgG;
            b = bgB;
            return;
        }

        float invA = 1f - a;
        r = Math.Clamp(r, 0f, 1f) * a + bgR * invA;
        g = Math.Clamp(g, 0f, 1f) * a + bgG * invA;
        b = Math.Clamp(b, 0f, 1f) * a + bgB * invA;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyWhiteWallPixel(ref float r, ref float g, ref float b, float threshold, float strength)
    {
        float maxC = MathF.Max(r, MathF.Max(g, b));
        float minC = MathF.Min(r, MathF.Min(g, b));
        float chroma = maxC - minC;
        float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;

        float lumaWeight = SmoothStep(threshold, 0.92f, luma);
        float chromaWeight = 1f - SmoothStep(0.06f, 0.16f, chroma);
        float w = lumaWeight * chromaWeight * strength;
        if (w > 0.001f)
        {
            r += (1f - r) * w;
            g += (1f - g) * w;
            b += (1f - b) * w;
        }
    }

    private sealed class CompactMatteSampler(
        float[] data, int sw, int sh,
        float cropX, float cropY, float scaleX, float scaleY,
        float[]? rentedBuffer) : IDisposable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float SampleBilinear(int x, int y)
        {
            float sx = cropX + (x + 0.5f) * scaleX - 0.5f;
            float sy = cropY + (y + 0.5f) * scaleY - 0.5f;
            int x0 = Math.Clamp((int)MathF.Floor(sx), 0, sw - 1);
            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, sh - 1);
            int x1 = x0 + 1 < sw ? x0 + 1 : x0;
            int y1 = y0 + 1 < sh ? y0 + 1 : y0;
            float fx = Math.Clamp(sx - x0, 0f, 1f);
            float fy = Math.Clamp(sy - y0, 0f, 1f);

            int r0 = y0 * sw;
            int r1 = y1 * sw;
            float v00 = data[r0 + x0];
            float v10 = data[r0 + x1];
            float v01 = data[r1 + x0];
            float v11 = data[r1 + x1];
            float top = v00 + (v10 - v00) * fx;
            float bot = v01 + (v11 - v01) * fx;
            return top + (bot - top) * fy;
        }

        public void Dispose()
        {
            if (rentedBuffer != null) FloatPool.Return(rentedBuffer);
        }
    }

    private readonly struct AiBgStyle
    {
        public readonly float OutR, OutG, OutB, InR, InG, InB, Spill;
        public readonly bool UseGradient;

        private AiBgStyle(float outR, float outG, float outB, float inR, float inG, float inB, float spill, bool useGradient)
        {
            OutR = outR; OutG = outG; OutB = outB;
            InR = inR; InG = inG; InB = inB;
            Spill = spill;
            UseGradient = useGradient;
        }

        public static AiBgStyle FromEditState(PhotoEditState edit)
        {
            var outer = SKColor.TryParse(edit.ReplacementBackgroundHex, out var c) ? c : SKColors.White;
            bool grad = edit.UseGradientBackground;
            var inner = grad && SKColor.TryParse(edit.ReplacementBackgroundSecondaryHex, out var c2) ? c2 : outer;
            return new AiBgStyle(
                outer.Red / 255f, outer.Green / 255f, outer.Blue / 255f,
                inner.Red / 255f, inner.Green / 255f, inner.Blue / 255f,
                (float)(edit.SpillSuppression / 100.0), grad);
        }
    }
}
