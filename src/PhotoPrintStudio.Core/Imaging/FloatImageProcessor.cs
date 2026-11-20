using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

/// <summary>
/// 工业级 32 位浮点线性/sRGB 调色与人像处理管线。
/// 包含：曝光、亮度、对比、高光/阴影、黑白场、Gamma、色阶、4通道曲线、白平衡、自然饱和度、8色相HSL、
/// 清晰度、USM锐化、保边柔肤、白墙去灰提纯、AI抠图换底合成、滤镜风格与 Bayer 4×4 有序抖动量化。
/// </summary>
public static unsafe partial class FloatImageProcessor
{
    private const float Inv255 = 1f / 255f;
    private static readonly float[] ByteToUnitLut = BuildByteToUnitLut();
    private static readonly float[] SrgbToLinearLut = BuildSrgbToLinearLut();
    private static readonly float[] LinearToSrgbLut = BuildLinearToSrgbLut();
    // 标准 4x4 Bayer 有序抖动阈值矩阵（均值为 0，消除 8-bit 渐变色阶断层与周期竖纹）
    private static readonly float[] Bayer4x4 =
    [
        (0  + 0.5f) / 16f - 0.5f, (8  + 0.5f) / 16f - 0.5f, (2  + 0.5f) / 16f - 0.5f, (10 + 0.5f) / 16f - 0.5f,
        (12 + 0.5f) / 16f - 0.5f, (4  + 0.5f) / 16f - 0.5f, (14 + 0.5f) / 16f - 0.5f, (6  + 0.5f) / 16f - 0.5f,
        (3  + 0.5f) / 16f - 0.5f, (11 + 0.5f) / 16f - 0.5f, (1  + 0.5f) / 16f - 0.5f, (9  + 0.5f) / 16f - 0.5f,
        (15 + 0.5f) / 16f - 0.5f, (7  + 0.5f) / 16f - 0.5f, (13 + 0.5f) / 16f - 0.5f, (5  + 0.5f) / 16f - 0.5f
    ];
    private static readonly float[] HslCenters = [0f, 30f, 60f, 120f, 180f, 240f, 275f, 315f];
    private static readonly ParallelOptions PixelParallelism = new()
    {
        MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount)
    };

    private static readonly LargeFloatBufferPool FloatPool = new();

    public static ImageProcessingResult Process(
        SKBitmap source,
        PhotoEditState edit,
        float[]? aiAlphaMatte = null,
        bool showClippingOverlay = false,
        PortraitProcessContext? portraitContext = null) =>
        ProcessInternal(source, edit, aiAlphaMatte, compactAlphaMatte: null, showClippingOverlay, portraitContext);

    public static ImageProcessingResult Process(
        SKBitmap source,
        PhotoEditState edit,
        CompactAlphaMatte? compactAlphaMatte,
        bool showClippingOverlay = false,
        PortraitProcessContext? portraitContext = null) =>
        ProcessInternal(source, edit, aiAlphaMatte: null, compactAlphaMatte, showClippingOverlay, portraitContext);

    private static ImageProcessingResult ProcessInternal(
        SKBitmap source,
        PhotoEditState edit,
        float[]? aiAlphaMatte,
        CompactAlphaMatte? compactAlphaMatte,
        bool showClippingOverlay,
        PortraitProcessContext? portraitContext)
    {
        int w = source.Width;
        int h = source.Height;
        int pixelCount = w * h;

        bool canReadSourceDirectly = source.ColorType == SKColorType.Bgra8888 &&
                                     (source.AlphaType == SKAlphaType.Unpremul || source.AlphaType == SKAlphaType.Opaque);
        using SKBitmap? convertedSource = canReadSourceDirectly ? null : ConvertToBgraUnpremul(source);
        SKBitmap srcBmp = convertedSource ?? source;

        bool hasFullMatte = aiAlphaMatte != null && aiAlphaMatte.Length == pixelCount;
        bool hasCompactMatte = !hasFullMatte && compactAlphaMatte != null;
        bool hasAnyMatte = hasFullMatte || hasCompactMatte;
        bool doAiBgReplace = edit.EnableAiBackgroundReplace && hasAnyMatte;
        bool hasStudioEnhance = edit.IsStudioEnhancementActive();
        bool hasWhiteWall = !doAiBgReplace && edit.WhiteWallCleanup > 0.5;
        bool hasSkin = edit.SkinSoftening > 0.5;
        bool hasClarity = Math.Abs(edit.Clarity) > 0.5;
        bool hasSharpen = edit.Sharpening > 0.5;
        bool needsFullPipeline = doAiBgReplace || hasStudioEnhance || hasSkin || hasClarity || hasSharpen;

        using var pointCfg = PointColorConfig.FromEditState(edit);
        var outBmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));

        try
        {
            byte* srcPtr = (byte*)srcBmp.GetPixels().ToPointer();
            int srcRowBytes = srcBmp.RowBytes;
            byte* dstPtr = (byte*)outBmp.GetPixels().ToPointer();
            int dstRowBytes = outBmp.RowBytes;

            // 快路径：无空间卷积/AI换底/影棚优化时，在单遍分块扫描中直接完成 拆包 -> 白墙 -> 调色 -> 抖动量化 -> 直方图（零大数组分配）
            if (!needsFullPipeline)
            {
                float whiteStrength = hasWhiteWall ? (float)(edit.WhiteWallCleanup / 100.0) : 0f;
                var histFast = RunStreamingSinglePass(
                    srcPtr, srcRowBytes, dstPtr, dstRowBytes,
                    w, h, showClippingOverlay,
                    whiteStrength, in pointCfg);

                GC.KeepAlive(srcBmp);
                GC.KeepAlive(outBmp);
                return new ImageProcessingResult { Bitmap = outBmp, Histogram = histFast };
            }

            // 全管线路径：所有中间平面均从 FloatPool 租借并在 finally 归还，实现零 LOH GC 抖动
            float[] rBuf = FloatPool.Rent(pixelCount);
            float[] gBuf = FloatPool.Rent(pixelCount);
            float[] bBuf = FloatPool.Rent(pixelCount);
            float[]? refinedAlpha = null;
            float[]? blurR = null;
            float[]? blurG = null;
            float[]? blurB = null;
            PortraitMaskSet? portraitMasks = null;
            int bandCount = pixelCount > LargeFloatBufferPool.LargePlaneElements ? PixelParallelism.MaxDegreeOfParallelism * 2 : 1;
            int blurRows = (h - 1) / bandCount + 1;
            int blurCapacity = w * blurRows;

            try
            {
                // 1. 拆包原图（未启用 AI 换底时同时执行白墙提纯）
                UnpackWithWhiteWall(
                    srcPtr, srcRowBytes, rBuf, gBuf, bBuf, w, h,
                    hasWhiteWall ? (float)(edit.WhiteWallCleanup / 100.0) : 0f);

                // 2. 构建高精度引导滤波 Alpha + 局部前景/原底色场 + 前置白墙灰卡采样 + 前景去污染外延
                //    关键顺序：先在原图上采样白墙白平衡与精细 Alpha，再做前景去污染外延消除边界悬崖，
                //    随后修图、调色和锐化作用于前景，最后合成标准底色。
                var wallWb = new WhiteWallWbSample(false, 1f, 1f, 1f);
                var aiStyle = doAiBgReplace ? AiBgStyle.FromEditState(edit) : default;
                bool needAlphaPlane = doAiBgReplace || (hasStudioEnhance && hasAnyMatte);
                if (needAlphaPlane)
                {
                    refinedAlpha = FloatPool.Rent(pixelCount);
                    PrepareRefinedAlpha(
                        rBuf, gBuf, bBuf, refinedAlpha, w, h,
                        hasFullMatte ? aiAlphaMatte : null,
                        hasCompactMatte ? compactAlphaMatte : null);

                    if (hasStudioEnhance)
                        wallWb = SampleOriginalWallWhiteBalance(rBuf, gBuf, bBuf, refinedAlpha, w, h);

                    if (doAiBgReplace)
                    {
                        float spill = (float)(edit.SpillSuppression / 100.0);
                        using var colorField = BuildLocalColorField(rBuf, gBuf, bBuf, refinedAlpha, w, h);
                        RefineMatteEdges(rBuf, gBuf, bBuf, refinedAlpha, w, h, colorField, edit.MattingFeather, spill, edit.MattingEdgeShift);
                        DecontaminateAndBleedForeground(rBuf, gBuf, bBuf, refinedAlpha, in colorField, w, h, spill);
                    }
                }

                // 3. 构建 478 点人脸分区掩码（供一键影棚优化与高精度保边柔肤共享）
                DetectedFace? scaledFace = (hasStudioEnhance || hasSkin)
                    ? portraitContext?.ResolveFaceForSize(w, h)
                    : null;
                if (scaledFace != null)
                    portraitMasks = BuildPortraitMasks(scaledFace, w, h);

                // 4. 一键影棚级人像自动优化（智能白平衡 / 自适应曝光 / 3D去阴阳脸补光 / 去油光 / 高低频肤质精修 / 五官提神）
                if (hasStudioEnhance)
                {
                    ApplyStudioPortraitEnhancement(
                        rBuf, gBuf, bBuf, refinedAlpha, w, h,
                        scaledFace, portraitMasks, in wallWb,
                        portraitContext?.RelightingShading,
                        (float)(edit.StudioEnhanceStrength / 100.0),
                        edit.StudioEnhanceMode);
                }

                // 5. 高低频保边人像柔肤（有 478 点掩码时严格保护双眼/眉毛/嘴唇/头发）
                if (hasSkin)
                {
                    EnsureBlurPlanes(blurCapacity, ref blurR, ref blurG, ref blurB);
                    ApplyEdgePreservingSkinSoften(
                        rBuf, gBuf, bBuf, blurR!, blurG!, blurB!,
                        w, h, blurRows, (float)(edit.SkinSoftening / 100.0), portraitMasks);
                }

                if (portraitMasks != null)
                {
                    portraitMasks.Value.Dispose();
                    portraitMasks = null;
                }

                float[]? finalBgAlpha = doAiBgReplace ? refinedAlpha : null;
                HistogramData hist;
                if (!hasClarity && !hasSharpen)
                {
                    // 6+8 融合：前景点调色 + 最终标准底色合成 + 抖动量化 + 直方图单遍写出
                    hist = ApplyPointColorAndQuantizeToBitmap(
                        rBuf, gBuf, bBuf, srcPtr, srcRowBytes, dstPtr, dstRowBytes,
                        w, h, showClippingOverlay, in pointCfg, finalBgAlpha, in aiStyle);
                }
                else
                {
                    // 6. 前景逐像素调色
                    if (pointCfg.HasAnyWork)
                    {
                        ApplyPointColorInPlace(rBuf, gBuf, bBuf, pixelCount, in pointCfg);
                    }

                    // 7. 前景清晰度 (Clarity)
                    if (hasClarity)
                    {
                        ApplyClarity(
                            rBuf, gBuf, bBuf,
                            w, h, (float)(edit.Clarity / 100.0),
                            fuseQuantize: !hasSharpen, srcPtr, srcRowBytes, dstPtr, dstRowBytes, showClippingOverlay, out var clarityHist,
                            finalBgAlpha, in aiStyle);

                        if (!hasSharpen && clarityHist != null)
                        {
                            GC.KeepAlive(srcBmp);
                            GC.KeepAlive(outBmp);
                            return new ImageProcessingResult { Bitmap = outBmp, Histogram = clarityHist };
                        }
                    }

                        // 7b+8 融合：前景高斯 USM 锐化 + 最终标准底色合成 + 抖动量化
                    if (hasSharpen)
                    {
                        EnsureBlurPlanes(blurCapacity, ref blurR, ref blurG, ref blurB);
                        hist = ApplyUnsharpMaskAndQuantizeToBitmap(
                            rBuf, gBuf, bBuf, blurR!, blurG!, blurB!,
                            srcPtr, srcRowBytes, dstPtr, dstRowBytes,
                            w, h, blurRows, (float)(edit.Sharpening / 100.0), showClippingOverlay,
                            finalBgAlpha, in aiStyle);
                    }
                    else
                    {
                        hist = QuantizeToBitmapAndHistogram(
                            rBuf, gBuf, bBuf, srcPtr, srcRowBytes, dstPtr, dstRowBytes,
                            w, h, showClippingOverlay, finalBgAlpha, in aiStyle);
                    }
                }

                GC.KeepAlive(srcBmp);
                GC.KeepAlive(outBmp);
                return new ImageProcessingResult { Bitmap = outBmp, Histogram = hist };
            }
            finally
            {
                if (portraitMasks != null) portraitMasks.Value.Dispose();
                if (refinedAlpha != null) FloatPool.Return(refinedAlpha);
                if (blurR != null) FloatPool.Return(blurR);
                if (blurG != null) FloatPool.Return(blurG);
                if (blurB != null) FloatPool.Return(blurB);
                FloatPool.Return(rBuf);
                FloatPool.Return(gBuf);
                FloatPool.Return(bBuf);
            }
        }
        catch
        {
            outBmp.Dispose();
            throw;
        }
    }

    private static void EnsureBlurPlanes(int pixelCount, ref float[]? blurR, ref float[]? blurG, ref float[]? blurB)
    {
        blurR ??= FloatPool.Rent(pixelCount);
        blurG ??= FloatPool.Rent(pixelCount);
        blurB ??= FloatPool.Rent(pixelCount);
    }

    private static HistogramData RunStreamingSinglePass(
        byte* srcPtr, int srcRowBytes,
        byte* dstPtr, int dstRowBytes,
        int w, int h, bool showClippingOverlay,
        float whiteStrength,
        in PointColorConfig cfg)
    {
        int pixelCount = w * h;
        var hist = new HistogramData { TotalPixels = pixelCount };
        var histLock = new object();
        int borderX = Math.Max(1, (int)(w * 0.12));
        int borderY = Math.Max(1, (int)(h * 0.12));
        int totalBorderPx = 0, totalBorderWhite = 0, totalInteriorPx = 0, totalInteriorHi = 0;

        bool doWhiteWall = whiteStrength > 0.001f;
        bool doPoint = cfg.HasAnyWork;
        float wwThreshold = 0.72f - whiteStrength * 0.16f;
        float[] byteLut = ByteToUnitLut;
        var pointCfg = cfg;

        int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));
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
                    bool isBorderRow = y < borderY || y >= h - borderY;
                    int ditherY = (y & 3);

                    for (int x = 0; x < w; x++)
                    {
                        int idx4 = x * 4;
                        float b = byteLut[sRow[idx4]];
                        float g = byteLut[sRow[idx4 + 1]];
                        float r = byteLut[sRow[idx4 + 2]];
                        byte alphaByte = sRow[idx4 + 3];

                        if (doWhiteWall)
                        {
                            ApplyWhiteWallPixel(ref r, ref g, ref b, wwThreshold, whiteStrength);
                        }

                        if (doPoint)
                        {
                            ApplyPointPixel(ref r, ref g, ref b, in pointCfg);
                        }

                        QuantizeAndAccumulatePixel(
                            r, g, b, alphaByte, x, ditherY, w, isBorderRow, borderX, showClippingOverlay,
                            dRow + idx4, local);
                    }
                }
                return local;
            },
            local => MergeHistogram(hist, histLock, local, ref totalBorderPx, ref totalBorderWhite, ref totalInteriorPx, ref totalInteriorHi));

        FinalizeHistogramRatios(hist, pixelCount, totalBorderPx, totalBorderWhite, totalInteriorPx, totalInteriorHi);
        return hist;
    }

    private static void ApplyPointColorInPlace(float[] rBuf, float[] gBuf, float[] bBuf, int pixelCount, in PointColorConfig cfg)
    {
        int chunk = Math.Max(4096, pixelCount / (PixelParallelism.MaxDegreeOfParallelism * 4));
        var pointCfg = cfg;
        Parallel.ForEach(Partitioner.Create(0, pixelCount, chunk), PixelParallelism, range =>
        {
            for (int i = range.Item1; i < range.Item2; i++)
            {
                float r = rBuf[i];
                float g = gBuf[i];
                float b = bBuf[i];
                ApplyPointPixel(ref r, ref g, ref b, in pointCfg);
                rBuf[i] = r;
                gBuf[i] = g;
                bBuf[i] = b;
            }
        });
    }

    private static HistogramData ApplyPointColorAndQuantizeToBitmap(
        float[] rBuf, float[] gBuf, float[] bBuf,
        byte* srcPtr, int srcRowBytes,
        byte* dstPtr, int dstRowBytes,
        int w, int h, bool showClippingOverlay, in PointColorConfig cfg,
        float[]? finalBgAlpha = null, in AiBgStyle finalBgStyle = default)
    {
        int pixelCount = w * h;
        var hist = new HistogramData { TotalPixels = pixelCount };
        var histLock = new object();
        int borderX = Math.Max(1, (int)(w * 0.12));
        int borderY = Math.Max(1, (int)(h * 0.12));
        int totalBorderPx = 0, totalBorderWhite = 0, totalInteriorPx = 0, totalInteriorHi = 0;

        bool doPoint = cfg.HasAnyWork;
        bool doFinalBg = finalBgAlpha != null;
        var pointCfg = cfg;
        var bgStyle = finalBgStyle;
        float invW = w > 1 ? 1f / (w - 1) : 1f;
        float invH = h > 1 ? 1f / (h - 1) : 1f;
        int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));

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

                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOffset + x;
                        float r = rBuf[i];
                        float g = gBuf[i];
                        float b = bBuf[i];

                        if (doPoint)
                            ApplyPointPixel(ref r, ref g, ref b, in pointCfg);

                        int idx4 = x * 4;
                        byte alphaByte = sRow[idx4 + 3];
                        if (doFinalBg)
                            CompositeBackgroundPixel(ref r, ref g, ref b, finalBgAlpha![i], ref alphaByte, x, invW, ny, in bgStyle);
                        QuantizeAndAccumulatePixel(
                            r, g, b, alphaByte, x, ditherY, w, isBorderRow, borderX, showClippingOverlay,
                            dRow + idx4, local);
                    }
                }
                return local;
            },
            local => MergeHistogram(hist, histLock, local, ref totalBorderPx, ref totalBorderWhite, ref totalInteriorPx, ref totalInteriorHi));

        FinalizeHistogramRatios(hist, pixelCount, totalBorderPx, totalBorderWhite, totalInteriorPx, totalInteriorHi);
        return hist;
    }

    private static HistogramData QuantizeToBitmapAndHistogram(
        float[] rBuf, float[] gBuf, float[] bBuf,
        byte* srcPtr, int srcRowBytes,
        byte* dstPtr, int dstRowBytes,
        int w, int h, bool showClippingOverlay,
        float[]? finalBgAlpha = null, in AiBgStyle finalBgStyle = default)
    {
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

        int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));
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

                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOffset + x;
                        float r = rBuf[i], g = gBuf[i], b = bBuf[i];
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
            local => MergeHistogram(hist, histLock, local, ref totalBorderPx, ref totalBorderWhite, ref totalInteriorPx, ref totalInteriorHi));

        FinalizeHistogramRatios(hist, pixelCount, totalBorderPx, totalBorderWhite, totalInteriorPx, totalInteriorHi);
        return hist;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyPointPixel(ref float r, ref float g, ref float b, in PointColorConfig cfg)
    {
        // A + B. 线性光曝光与乘性白平衡色度适应（保持纯黑 (0,0,0) 零偏移，不产生暗部偏色起雾）
        if (cfg.HasLinearGain)
        {
            r = LinearToSrgb(SrgbToLinear(r) * cfg.LinearGainR);
            g = LinearToSrgb(SrgbToLinear(g) * cfg.LinearGainG);
            b = LinearToSrgb(SrgbToLinear(b) * cfg.LinearGainB);
        }

        if (cfg.HasBrightnessWb)
        {
            r += cfg.BrightnessAdd;
            g += cfg.BrightnessAdd;
            b += cfg.BrightnessAdd;

            if (cfg.HasWhitesOrBlacks)
            {
                float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                float wWeight = SmoothStep(0.55f, 1.0f, l);
                float bWeight = 1f - SmoothStep(0.0f, 0.45f, l);
                float wbDelta = cfg.Whites * wWeight + cfg.Blacks * bWeight;
                r += wbDelta;
                g += wbDelta;
                b += wbDelta;
            }
        }

        if (cfg.HasHiSh)
        {
            float l = Math.Clamp(0.2126f * r + 0.7152f * g + 0.0722f * b, 0f, 1f);
            // 暗部锚定黑场（l=0 时权重为 0 避免死黑泛灰），并按亮度比例软缩放保护色彩饱和度
            float sMask = (1f - l) * (1f - l) * (l / (l + 0.03f));
            float hMask = l * l;
            float delta = cfg.Shadows * 0.42f * sMask + cfg.Highlights * 0.35f * hMask;
            if (l > 1e-4f)
            {
                float ratioScale = Math.Clamp((l + delta) / l, 0.35f, 2.2f);
                // 混合 60% 比例保持色相饱和度 + 40% 柔和亮度平移避免极端饱和通道裁切
                r = (r * ratioScale) * 0.6f + (r + delta) * 0.4f;
                g = (g * ratioScale) * 0.6f + (g + delta) * 0.4f;
                b = (b * ratioScale) * 0.6f + (b + delta) * 0.4f;
            }
            else
            {
                r += delta;
                g += delta;
                b += delta;
            }
        }

        r = Math.Clamp(r, 0f, 1f);
        g = Math.Clamp(g, 0f, 1f);
        b = Math.Clamp(b, 0f, 1f);

        // E + F + G + H 融合 1D 查表（Gamma + 对比度 + 色阶 + 4 通道曲线一次查表完成）
        if (cfg.HasPostLut)
        {
            r = SampleLut(cfg.PostLutR!, r);
            g = SampleLut(cfg.PostLutG!, g);
            b = SampleLut(cfg.PostLutB!, b);
        }

        if (cfg.HasVibSat)
        {
            float maxC = MathF.Max(r, MathF.Max(g, b));
            float minC = MathF.Min(r, MathF.Min(g, b));
            float curSat = maxC - minC;
            float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;

            float skinProtect = (r > g && g > b) ? 0.55f : 1.0f;
            float vibScale = 1f + cfg.Vibrance * (1f - curSat) * skinProtect;
            float totalScale = MathF.Max(0f, vibScale * (1f + cfg.Saturation));

            r = Math.Clamp(luma + (r - luma) * totalScale, 0f, 1f);
            g = Math.Clamp(luma + (g - luma) * totalScale, 0f, 1f);
            b = Math.Clamp(luma + (b - luma) * totalScale, 0f, 1f);
        }

        if (cfg.HasHsl)
        {
            ApplyHslActiveChannels(ref r, ref g, ref b, cfg.ActiveHslChannels!);
        }

        if (cfg.HasFilter)
        {
            ApplyFilterPreset(ref r, ref g, ref b, cfg.FilterKind, cfg.FilterMix);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QuantizeAndAccumulatePixel(
        float r, float g, float b, byte alphaByte,
        int x, int ditherY, int w, bool isBorderRow, int borderX, bool showClippingOverlay,
        byte* dstPixel, HistAccum local)
    {
        r = Math.Clamp(r, 0f, 1f);
        g = Math.Clamp(g, 0f, 1f);
        b = Math.Clamp(b, 0f, 1f);

        float dither = Bayer4x4[(ditherY << 2) | (x & 3)] * Inv255;
        byte rb = (byte)Math.Clamp((int)MathF.Round((r + dither) * 255f), 0, 255);
        byte gb = (byte)Math.Clamp((int)MathF.Round((g + dither) * 255f), 0, 255);
        byte bb = (byte)Math.Clamp((int)MathF.Round((b + dither) * 255f), 0, 255);

        int luma = Math.Clamp((int)MathF.Round(0.2126f * rb + 0.7152f * gb + 0.0722f * bb), 0, 255);

        local.R[rb]++;
        local.G[gb]++;
        local.B[bb]++;
        local.L[luma]++;

        bool isHiClip = rb >= 254 && gb >= 254 && bb >= 254;
        bool isLoClip = rb <= 1 && gb <= 1 && bb <= 1;
        if (isHiClip) local.Hi++;
        if (isLoClip) local.Lo++;

        if (isBorderRow || x < borderX || x >= w - borderX)
        {
            local.BPx++;
            if (rb >= 236 && gb >= 236 && bb >= 236 && Math.Abs(rb - bb) < 18)
                local.BWh++;
        }
        else
        {
            local.IPx++;
            if (isHiClip) local.IHi++;
        }

        if (showClippingOverlay && isHiClip)
        {
            dstPixel[0] = 40;
            dstPixel[1] = 40;
            dstPixel[2] = 245;
            dstPixel[3] = alphaByte;
        }
        else if (showClippingOverlay && isLoClip)
        {
            dstPixel[0] = 245;
            dstPixel[1] = 110;
            dstPixel[2] = 30;
            dstPixel[3] = alphaByte;
        }
        else
        {
            dstPixel[0] = bb;
            dstPixel[1] = gb;
            dstPixel[2] = rb;
            dstPixel[3] = alphaByte;
        }
    }

    private static void MergeHistogram(
        HistogramData hist, object histLock, HistAccum local,
        ref int totalBorderPx, ref int totalBorderWhite, ref int totalInteriorPx, ref int totalInteriorHi)
    {
        lock (histLock)
        {
            for (int k = 0; k < 256; k++)
            {
                hist.Red[k] += local.R[k];
                hist.Green[k] += local.G[k];
                hist.Blue[k] += local.B[k];
                hist.Luminance[k] += local.L[k];
            }
            hist.HighlightClippedRatio += local.Hi;
            hist.ShadowClippedRatio += local.Lo;
            totalBorderPx += local.BPx;
            totalBorderWhite += local.BWh;
            totalInteriorPx += local.IPx;
            totalInteriorHi += local.IHi;
        }
    }

    private static void FinalizeHistogramRatios(
        HistogramData hist, int pixelCount,
        int totalBorderPx, int totalBorderWhite, int totalInteriorPx, int totalInteriorHi)
    {
        if (pixelCount <= 0) return;
        hist.HighlightClippedRatio /= pixelCount;
        hist.ShadowClippedRatio /= pixelCount;
        hist.BorderWhiteRatio = totalBorderPx > 0 ? totalBorderWhite / (double)totalBorderPx : 0;
        hist.InteriorHighlightClippedRatio = totalInteriorPx > 0 ? totalInteriorHi / (double)totalInteriorPx : 0;
        hist.LooksLikeWhiteBackdrop = hist.BorderWhiteRatio > 0.35;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyHslActiveChannels(ref float r, ref float g, ref float b, HslChannelParam[] active)
    {
        RgbToHsl(r, g, b, out float hDeg, out float s, out float l);
        if (s < 0.01f) return;

        float dHue = 0f, dSat = 0f, dLight = 0f;
        for (int idx = 0; idx < active.Length; idx++)
        {
            ref readonly var ch = ref active[idx];
            float dist = MathF.Abs(hDeg - ch.CenterHue);
            if (dist > 180f) dist = 360f - dist;
            if (dist >= 45f) continue;

            float w = 0.5f * (1f + MathF.Cos(dist * (MathF.PI / 45f)));
            dHue += ch.HueDelta * w;
            dSat += ch.SatDelta * w;
            dLight += ch.LightDelta * w;
        }

        hDeg = (hDeg + dHue + 360f) % 360f;
        s = Math.Clamp(s * (1f + dSat), 0f, 1f);
        l = Math.Clamp(l + dLight * (1f - MathF.Abs(2f * l - 1f) * 0.5f), 0f, 1f);
        HslToRgb(hDeg, s, l, out r, out g, out b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyFilterPreset(ref float r, ref float g, ref float b, FilterKind kind, float mix)
    {
        float nr = r, ng = g, nb = b;
        switch (kind)
        {
            case FilterKind.FujiFresh:
                nr = r * 0.96f + 0.01f;
                ng = g * 1.01f + 0.015f;
                nb = b * 1.04f + 0.025f;
                break;
            case FilterKind.KodakWarm:
                nr = r * 1.05f + 0.02f;
                ng = g * 1.01f + 0.01f;
                nb = b * 0.92f + 0.015f;
                break;
            case FilterKind.BwSilver:
                float lum = 0.25f * r + 0.62f * g + 0.13f * b;
                nr = ng = nb = lum;
                break;
            case FilterKind.BwWarm:
                float lumW = 0.26f * r + 0.60f * g + 0.14f * b;
                nr = Math.Clamp(lumW * 1.05f + 0.02f, 0f, 1f);
                ng = Math.Clamp(lumW * 1.01f + 0.01f, 0f, 1f);
                nb = Math.Clamp(lumW * 0.93f, 0f, 1f);
                break;
            case FilterKind.VintageFade:
                nr = r * 0.92f + 0.055f;
                ng = g * 0.90f + 0.050f;
                nb = b * 0.85f + 0.065f;
                break;
        }

        r = Math.Clamp(r + (nr - r) * mix, 0f, 1f);
        g = Math.Clamp(g + (ng - g) * mix, 0f, 1f);
        b = Math.Clamp(b + (nb - b) * mix, 0f, 1f);
    }

    private readonly struct HslChannelParam(float centerHue, float hueDelta, float satDelta, float lightDelta)
    {
        public readonly float CenterHue = centerHue;
        public readonly float HueDelta = hueDelta;
        public readonly float SatDelta = satDelta;
        public readonly float LightDelta = lightDelta;
    }

    private enum FilterKind : byte
    {
        None = 0,
        FujiFresh = 1,
        KodakWarm = 2,
        BwSilver = 3,
        BwWarm = 4,
        VintageFade = 5
    }

    private readonly struct PointColorConfig : IDisposable
    {
        public readonly bool HasAnyWork;
        public readonly bool HasLinearGain;
        public readonly float LinearGainR, LinearGainG, LinearGainB;
        public readonly bool HasBrightnessWb;
        public readonly bool HasWhitesOrBlacks;
        public readonly float BrightnessAdd, Whites, Blacks;
        public readonly bool HasHiSh;
        public readonly float Highlights, Shadows;
        public readonly bool HasPostLut;
        public readonly float[]? PostLutR, PostLutG, PostLutB;
        private readonly bool _hasDistinctChannelLuts;
        public readonly bool HasVibSat;
        public readonly float Vibrance, Saturation;
        public readonly bool HasHsl;
        public readonly HslChannelParam[]? ActiveHslChannels;
        public readonly bool HasFilter;
        public readonly FilterKind FilterKind;
        public readonly float FilterMix;

        private PointColorConfig(
            bool hasLinearGain, float gainR, float gainG, float gainB,
            bool hasBrightnessWb, bool hasWhitesOrBlacks, float brightnessAdd, float whites, float blacks,
            bool hasHiSh, float highlights, float shadows,
            bool hasPostLut, float[]? lutR, float[]? lutG, float[]? lutB, bool distinctLuts,
            bool hasVibSat, float vib, float sat,
            bool hasHsl, HslChannelParam[]? activeHsl,
            bool hasFilter, FilterKind filterKind, float filterMix)
        {
            HasLinearGain = hasLinearGain; LinearGainR = gainR; LinearGainG = gainG; LinearGainB = gainB;
            HasBrightnessWb = hasBrightnessWb; HasWhitesOrBlacks = hasWhitesOrBlacks;
            BrightnessAdd = brightnessAdd; Whites = whites; Blacks = blacks;
            HasHiSh = hasHiSh; Highlights = highlights; Shadows = shadows;
            HasPostLut = hasPostLut; PostLutR = lutR; PostLutG = lutG; PostLutB = lutB;
            _hasDistinctChannelLuts = distinctLuts;
            HasVibSat = hasVibSat; Vibrance = vib; Saturation = sat;
            HasHsl = hasHsl; ActiveHslChannels = activeHsl;
            HasFilter = hasFilter; FilterKind = filterKind; FilterMix = filterMix;
            HasAnyWork = hasLinearGain || hasBrightnessWb || hasHiSh || hasPostLut || hasVibSat || hasHsl || hasFilter;
        }

        public static PointColorConfig FromEditState(PhotoEditState edit)
        {
            float exposureMul = MathF.Pow(2f, (float)edit.Exposure);
            bool hasExp = MathF.Abs(exposureMul - 1f) > 1e-4f;

            // 线性光乘性白平衡（归一化保持 Rec.709 亮度守恒，且纯黑保持绝对零点）
            float tempNorm = (float)Math.Clamp(edit.Temperature / 100.0, -1.0, 1.0);
            float tintNorm = (float)Math.Clamp(edit.Tint / 100.0, -1.0, 1.0);
            bool hasWb = MathF.Abs(tempNorm) > 1e-4f || MathF.Abs(tintNorm) > 1e-4f;
            float wbMulR = 1f, wbMulG = 1f, wbMulB = 1f;
            if (hasWb)
            {
                wbMulR = MathF.Pow(2f, tempNorm * 0.42f - tintNorm * 0.14f);
                wbMulG = MathF.Pow(2f, tintNorm * 0.28f);
                wbMulB = MathF.Pow(2f, -tempNorm * 0.48f - tintNorm * 0.14f);
                float lumaNorm = 0.2126f * wbMulR + 0.7152f * wbMulG + 0.0722f * wbMulB;
                if (lumaNorm > 1e-4f)
                {
                    float invLuma = 1f / lumaNorm;
                    wbMulR *= invLuma;
                    wbMulG *= invLuma;
                    wbMulB *= invLuma;
                }
            }

            bool hasLinearGain = hasExp || hasWb;
            float gainR = exposureMul * wbMulR;
            float gainG = exposureMul * wbMulG;
            float gainB = exposureMul * wbMulB;

            float brightnessAdd = (float)(edit.Brightness / 250.0);
            float whites = (float)(edit.Whites / 100.0) * 0.25f;
            float blacks = (float)(edit.Blacks / 100.0) * 0.25f;
            bool hasWhitesBlacks = MathF.Abs(whites) > 1e-4f || MathF.Abs(blacks) > 1e-4f;
            bool hasBrightWb = MathF.Abs(brightnessAdd) > 1e-4f || hasWhitesBlacks;

            float highlights = (float)(edit.Highlights / 100.0);
            float shadows = (float)(edit.Shadows / 100.0);
            bool hasHiSh = MathF.Abs(highlights) > 1e-4f || MathF.Abs(shadows) > 1e-4f;

            float invGamma = (float)(1.0 / Math.Clamp(edit.Gamma, 0.35, 2.5));
            bool hasGamma = MathF.Abs(invGamma - 1f) > 1e-4f;

            float contrastFactor = (float)((100.0 + edit.Contrast) / 100.0);
            contrastFactor *= contrastFactor;
            bool hasContrast = MathF.Abs(contrastFactor - 1f) > 1e-4f;

            float inBlack = (float)(edit.InputBlack / 255.0);
            float inRange = MathF.Max(0.02f, (float)((edit.InputWhite - edit.InputBlack) / 255.0));
            float outBlack = (float)(edit.OutputBlack / 255.0);
            float outRange = (float)((edit.OutputWhite - edit.OutputBlack) / 255.0);
            bool hasLevels = Math.Abs(edit.InputBlack) > 0.1 || Math.Abs(edit.InputWhite - 255.0) > 0.1 ||
                             Math.Abs(edit.OutputBlack) > 0.1 || Math.Abs(edit.OutputWhite - 255.0) > 0.1;

            bool hasCurves = !edit.ToneCurves.IsIdentity;
            bool hasPostLut = hasGamma || hasContrast || hasLevels || hasCurves;
            float[]? lutR = null, lutG = null, lutB = null;
            bool distinct = false;

            if (hasPostLut)
            {
                const int lutSize = 4096;
                if (!hasCurves)
                {
                    lutR = FloatPool.Rent(lutSize);
                    BakeScalarPostLut(lutR, lutSize, hasGamma, invGamma, hasContrast, contrastFactor,
                        hasLevels, inBlack, inRange, outBlack, outRange, null, null);
                    lutG = lutR;
                    lutB = lutR;
                }
                else
                {
                    distinct = true;
                    float[] mLut = ToneCurveData.BuildLut(edit.ToneCurves.Master, lutSize);
                    float[] cR = ToneCurveData.BuildLut(edit.ToneCurves.Red, lutSize);
                    float[] cG = ToneCurveData.BuildLut(edit.ToneCurves.Green, lutSize);
                    float[] cB = ToneCurveData.BuildLut(edit.ToneCurves.Blue, lutSize);
                    lutR = FloatPool.Rent(lutSize);
                    lutG = FloatPool.Rent(lutSize);
                    lutB = FloatPool.Rent(lutSize);
                    BakeScalarPostLut(lutR, lutSize, hasGamma, invGamma, hasContrast, contrastFactor,
                        hasLevels, inBlack, inRange, outBlack, outRange, mLut, cR);
                    BakeScalarPostLut(lutG, lutSize, hasGamma, invGamma, hasContrast, contrastFactor,
                        hasLevels, inBlack, inRange, outBlack, outRange, mLut, cG);
                    BakeScalarPostLut(lutB, lutSize, hasGamma, invGamma, hasContrast, contrastFactor,
                        hasLevels, inBlack, inRange, outBlack, outRange, mLut, cB);
                }
            }

            float sat = (float)(edit.Saturation / 100.0);
            float vib = (float)(edit.Vibrance / 100.0);
            bool hasVibSat = MathF.Abs(vib) > 1e-4f || MathF.Abs(sat) > 1e-4f;

            HslChannelParam[]? activeHsl = null;
            if (!edit.Hsl.IsZero)
            {
                var list = new List<HslChannelParam>(8);
                for (int c = 0; c < 8; c++)
                {
                    var adj = edit.Hsl.GetByIndex(c);
                    if (!adj.IsZero)
                    {
                        list.Add(new HslChannelParam(
                            HslCenters[c],
                            (float)(adj.Hue / 100.0 * 30.0),
                            (float)(adj.Saturation / 100.0),
                            (float)(adj.Lightness / 100.0 * 0.35)));
                    }
                }
                if (list.Count > 0) activeHsl = [.. list];
            }

            var filterKind = edit.FilterIntensity > 0.5 ? edit.FilterPresetId switch
            {
                "fuji_fresh" => FilterKind.FujiFresh,
                "kodak_warm" => FilterKind.KodakWarm,
                "bw_silver" => FilterKind.BwSilver,
                "bw_warm" => FilterKind.BwWarm,
                "vintage_fade" => FilterKind.VintageFade,
                _ => FilterKind.None
            } : FilterKind.None;
            float filterMix = (float)Math.Clamp(edit.FilterIntensity / 100.0, 0.0, 1.0);

            return new PointColorConfig(
                hasLinearGain, gainR, gainG, gainB,
                hasBrightWb, hasWhitesBlacks, brightnessAdd, whites, blacks,
                hasHiSh, highlights, shadows,
                hasPostLut, lutR, lutG, lutB, distinct,
                hasVibSat, vib, sat,
                activeHsl != null, activeHsl,
                filterKind != FilterKind.None, filterKind, filterMix);
        }

        private static void BakeScalarPostLut(
            float[] dst, int size,
            bool hasGamma, float invGamma,
            bool hasContrast, float contrastFactor,
            bool hasLevels, float inBlack, float inRange, float outBlack, float outRange,
            float[]? masterCurve, float[]? channelCurve)
        {
            float denom = size - 1;
            for (int i = 0; i < size; i++)
            {
                float v = i / denom;
                if (hasGamma)
                    v = MathF.Pow(v, invGamma);
                if (hasContrast)
                    v = Math.Clamp((v - 0.5f) * contrastFactor + 0.5f, 0f, 1f);
                if (hasLevels)
                    v = Math.Clamp((v - inBlack) / inRange * outRange + outBlack, 0f, 1f);
                if (masterCurve != null && channelCurve != null)
                {
                    float mVal = SampleLutExact(masterCurve, v);
                    v = SampleLutExact(channelCurve, mVal);
                }
                dst[i] = v;
            }
        }

        public void Dispose()
        {
            if (PostLutR != null) FloatPool.Return(PostLutR);
            if (_hasDistinctChannelLuts)
            {
                if (PostLutG != null) FloatPool.Return(PostLutG);
                if (PostLutB != null) FloatPool.Return(PostLutB);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleLut(float[] lut, float x)
    {
        float pos = Math.Clamp(x, 0f, 1f) * 4095f;
        int i0 = (int)pos;
        int i1 = i0 < 4095 ? i0 + 1 : 4095;
        return lut[i0] + (lut[i1] - lut[i0]) * (pos - i0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleLutExact(float[] lut, float x)
    {
        float pos = Math.Clamp(x, 0f, 1f) * (lut.Length - 1);
        int i0 = (int)pos;
        int i1 = i0 + 1 < lut.Length ? i0 + 1 : i0;
        return lut[i0] + (lut[i1] - lut[i0]) * (pos - i0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float diff = edge1 - edge0;
        if (MathF.Abs(diff) < 1e-6f) return x >= edge1 ? 1f : 0f;
        float t = Math.Clamp((x - edge0) / diff, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SrgbToLinear(float c)
    {
        if (c <= 0f) return 0f;
        if (c >= 1f) return 1f;
        if (c <= 0.04045f) return c * (1f / 12.92f);
        float pos = c * 4095f;
        int i0 = (int)pos;
        int i1 = i0 < 4095 ? i0 + 1 : 4095;
        return SrgbToLinearLut[i0] + (SrgbToLinearLut[i1] - SrgbToLinearLut[i0]) * (pos - i0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LinearToSrgb(float c)
    {
        if (c <= 0f) return 0f;
        if (c >= 1f) return 1f;
        // 暗部线性段走精确解析式，避免等间距 LUT 在低亮度区仅占 ~12 格造成阴影跳阶
        if (c <= 0.0031308f) return c * 12.92f;
        float pos = c * 4095f;
        int i0 = (int)pos;
        int i1 = i0 < 4095 ? i0 + 1 : 4095;
        return LinearToSrgbLut[i0] + (LinearToSrgbLut[i1] - LinearToSrgbLut[i0]) * (pos - i0);
    }

    private static float[] BuildByteToUnitLut()
    {
        var lut = new float[256];
        for (int i = 0; i < 256; i++)
            lut[i] = i / 255f;
        return lut;
    }

    private static float[] BuildSrgbToLinearLut()
    {
        var lut = new float[4096];
        for (int i = 0; i < lut.Length; i++)
        {
            double c = i / 4095.0;
            lut[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return lut;
    }

    private static float[] BuildLinearToSrgbLut()
    {
        var lut = new float[4096];
        for (int i = 0; i < lut.Length; i++)
        {
            double c = i / 4095.0;
            lut[i] = (float)(c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055);
        }
        return lut;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RgbToHsl(float r, float g, float b, out float h, out float s, out float l)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        l = (max + min) * 0.5f;
        float d = max - min;
        if (d < 1e-5f)
        {
            h = 0f;
            s = 0f;
            return;
        }

        s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
        if (max == r) h = (g - b) / d + (g < b ? 6f : 0f);
        else if (max == g) h = (b - r) / d + 2f;
        else h = (r - g) / d + 4f;
        h *= 60f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HslToRgb(float h, float s, float l, out float r, out float g, out float b)
    {
        if (s < 1e-5f)
        {
            r = g = b = l;
            return;
        }
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        float hk = h / 360f;
        r = Hue2Rgb(p, q, hk + 1f / 3f);
        g = Hue2Rgb(p, q, hk);
        b = Hue2Rgb(p, q, hk - 1f / 3f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Hue2Rgb(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }

    private static SKBitmap ConvertToBgraUnpremul(SKBitmap src)
    {
        var converted = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(converted);
        canvas.Clear(SKColors.Transparent);
        using var img = SKImage.FromBitmap(src);
        canvas.DrawImage(img, 0, 0, SKSamplingOptions.Default);
        return converted;
    }
}
