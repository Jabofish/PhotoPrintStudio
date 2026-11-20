using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Icc;
using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

public static unsafe class ExifAndColorHelper
{
    private const int ProofLutDim = 33;
    private static readonly ParallelOptions TileParallelism = new()
    {
        MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount)
    };

    private sealed class CachedIccProfile(byte[] rawBytes, bool isSupported, float[]? lut33, byte[]? gamutMask33)
    {
        public byte[] RawBytes { get; } = rawBytes;
        public bool IsSupported { get; } = isSupported;
        public float[]? Lut33 { get; } = lut33;
        public byte[]? GamutMask33 { get; } = gamutMask33;
    }

    private static readonly ConcurrentDictionary<(ulong Hash, SoftProofOptions Options), CachedIccProfile> IccCache = new();
    public static Func<string, (SKBitmap Bitmap, SKEncodedOrigin Origin)>? TiffDecoder { private get; set; }

    public static PhotoAsset InspectAsset(string filePath)
    {
        var asset = new PhotoAsset
        {
            FilePath = Path.GetFullPath(filePath),
            DisplayName = Path.GetFileNameWithoutExtension(filePath)
        };

        try
        {
            // 单次打开文件流依次读取尺寸/色域与 EXIF 元数据，避免多次重复文件句柄探测
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.SequentialScan);
            try
            {
                using var stream = new SKManagedStream(fs, disposeManagedStream: false);
                using var codec = SKCodec.Create(stream);
                if (codec != null)
                {
                    bool swap = IsSwappedOrigin(codec.EncodedOrigin);
                    asset.PixelWidth = swap ? codec.Info.Height : codec.Info.Width;
                    asset.PixelHeight = swap ? codec.Info.Width : codec.Info.Height;
                    asset.ExifOrientation = OriginToInt(codec.EncodedOrigin);
                    asset.ColorProfileName = codec.Info.ColorSpace?.IsSrgb == true
                        ? "sRGB"
                        : (codec.Info.ColorSpace != null ? "内嵌广色域 ICC (自动转 sRGB)" : "未标记 (按 sRGB)");
                }
            }
            catch
            {
                // Fallback below
            }

            try
            {
                if (fs.CanSeek) fs.Position = 0;
                var directories = ImageMetadataReader.ReadMetadata(fs);
                ApplyExifMetadata(directories, asset, filePath);
            }
            catch
            {
                asset.DateTaken ??= File.GetLastWriteTime(filePath);
            }
        }
        catch
        {
            asset.DateTaken ??= File.GetLastWriteTime(filePath);
        }

        return asset;
    }

    private static void ApplyExifMetadata(IReadOnlyList<MetadataExtractor.Directory> directories, PhotoAsset asset, string filePath)
    {
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        if (ifd0 != null)
        {
            if (asset.PixelWidth == 0 &&
                ifd0.TryGetInt32(ExifDirectoryBase.TagImageWidth, out int width) &&
                ifd0.TryGetInt32(ExifDirectoryBase.TagImageHeight, out int height))
            {
                if (ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out int orientation))
                    asset.ExifOrientation = orientation;
                bool swap = IsSwappedOrigin((SKEncodedOrigin)asset.ExifOrientation);
                asset.PixelWidth = swap ? height : width;
                asset.PixelHeight = swap ? width : height;
            }

            var make = ifd0.GetDescription(ExifDirectoryBase.TagMake)?.Trim();
            var model = ifd0.GetDescription(ExifDirectoryBase.TagModel)?.Trim();
            if (!string.IsNullOrWhiteSpace(model))
            {
                asset.CameraModel = !string.IsNullOrWhiteSpace(make) && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase)
                    ? $"{make} {model}"
                    : model;
            }
        }

        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (subIfd != null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
        {
            asset.DateTaken = dt;
        }
        else
        {
            asset.DateTaken = File.GetLastWriteTime(filePath);
        }

        var iccDir = directories.OfType<IccDirectory>().FirstOrDefault();
        if (iccDir != null)
        {
            var tagDesc = iccDir.Tags.FirstOrDefault(t =>
                t.Name.Contains("Description", StringComparison.OrdinalIgnoreCase) ||
                t.Name.Contains("Profile", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(tagDesc?.Description))
            {
                asset.ColorProfileName = tagDesc.Description;
            }
        }
    }

    /// <summary>
    /// 解码图像并自动完成 EXIF 方向矫正与内嵌 ICC → sRGB 色域转换。
    /// maxLongEdge &gt; 0 时利用 SKCodec 进行降采样解码以节省内存。
    /// </summary>
    public static SKBitmap DecodeNormalizedSrgb(string filePath, int maxLongEdge = 0)
    {
        using var codec = SKCodec.Create(filePath);
        if (codec == null)
        {
            if (TiffDecoder is { } decode &&
                (filePath.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                 filePath.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)))
            {
                var (bitmap, origin) = decode(filePath);
                return ResizeAndOrient(bitmap, maxLongEdge, origin);
            }
            return SKBitmap.Decode(filePath)
                ?? throw new InvalidOperationException($"无法解码图像文件: {filePath}");
        }

        int srcW = codec.Info.Width;
        int srcH = codec.Info.Height;
        int decodeW = srcW;
        int decodeH = srcH;

        if (maxLongEdge > 0 && Math.Max(srcW, srcH) > maxLongEdge)
        {
            float scale = maxLongEdge / (float)Math.Max(srcW, srcH);
            var scaled = codec.GetScaledDimensions(scale);
            decodeW = Math.Max(1, scaled.Width);
            decodeH = Math.Max(1, scaled.Height);
        }

        var targetInfo = new SKImageInfo(
            decodeW,
            decodeH,
            SKColorType.Bgra8888,
            SKAlphaType.Unpremul,
            SKColorSpace.CreateSrgb());

        var rawBitmap = new SKBitmap(targetInfo);
        var result = codec.GetPixels(targetInfo, rawBitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            rawBitmap.Dispose();
            var fallbackInfo = new SKImageInfo(decodeW, decodeH, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            rawBitmap = new SKBitmap(fallbackInfo);
            result = codec.GetPixels(fallbackInfo, rawBitmap.GetPixels());
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            {
                rawBitmap.Dispose();
                rawBitmap = SKBitmap.Decode(filePath)
                    ?? throw new InvalidOperationException($"解码图像失败 ({result}): {filePath}");
            }
        }

        return ResizeAndOrient(rawBitmap, maxLongEdge, codec.EncodedOrigin);
    }

    private static SKBitmap ResizeAndOrient(SKBitmap rawBitmap, int maxLongEdge, SKEncodedOrigin origin)
    {
        // 缩略图 (<= 320px) 用快速双线性降采样；编辑预览代理与大图保留 Mitchell 锐度
        if (maxLongEdge > 0 && Math.Max(rawBitmap.Width, rawBitmap.Height) > maxLongEdge)
        {
            float s = maxLongEdge / (float)Math.Max(rawBitmap.Width, rawBitmap.Height);
            int w = Math.Max(1, (int)Math.Round(rawBitmap.Width * s));
            int h = Math.Max(1, (int)Math.Round(rawBitmap.Height * s));
            var sampling = maxLongEdge <= 320
                ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)
                : new SKSamplingOptions(SKCubicResampler.Mitchell);
            var resized = rawBitmap.Resize(
                new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()),
                sampling);
            if (resized != null)
            {
                rawBitmap.Dispose();
                rawBitmap = resized;
            }
        }

        var oriented = ApplyEncodedOrigin(rawBitmap, origin);
        if (!ReferenceEquals(oriented, rawBitmap))
        {
            rawBitmap.Dispose();
        }
        return oriented;
    }

    public static bool SupportsSoftProofIcc(byte[] iccBytes) =>
        GetOrBuildIccProfile(iccBytes, SoftProofOptions.Default, needLut: false).IsSupported;

    /// <summary>
    /// 使用输出相纸/打印机 ICC 文件进行软打样色彩空间模拟（Windows 上优先走原生 WCS/ICM，完整支持 RGB/CMYK CLUT ICC、
    /// 渲染意图、纸白模拟与超色域警告；其余平台自动回退至 Skia 矩阵 ICC）。
    /// </summary>
    public static SKBitmap ApplySoftProofIcc(
        SKBitmap sourceSrgb,
        byte[] iccBytes,
        bool fastPreviewLut = false,
        SoftProofOptions? options = null)
    {
        var opts = options ?? SoftProofOptions.Default;
        var cached = GetOrBuildIccProfile(iccBytes, opts, needLut: true);
        if (!cached.IsSupported)
            return sourceSrgb.Copy();

        if (cached.Lut33 != null &&
            sourceSrgb.ColorType == SKColorType.Bgra8888 &&
            sourceSrgb.RowBytes % 4 == 0)
        {
            return ApplySoftProofLut33(sourceSrgb, cached.Lut33, opts.ShowGamutWarning ? cached.GamutMask33 : null);
        }

        using var skSpace = SKColorSpace.CreateIcc(iccBytes);
        if (skSpace == null)
            return sourceSrgb.Copy();

        var iccInfo = new SKImageInfo(sourceSrgb.Width, sourceSrgb.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul, skSpace);
        using var inPrinterGamut = new SKBitmap(iccInfo);
        using (var canvas = new SKCanvas(inPrinterGamut))
        using (var img = SKImage.FromBitmap(sourceSrgb))
        {
            canvas.DrawImage(img, 0, 0, SKSamplingOptions.Default);
        }

        var backSrgbInfo = new SKImageInfo(sourceSrgb.Width, sourceSrgb.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
        var proofed = new SKBitmap(backSrgbInfo);
        using (var canvas = new SKCanvas(proofed))
        using (var img = SKImage.FromBitmap(inPrinterGamut))
        {
            canvas.DrawImage(img, 0, 0, SKSamplingOptions.Default);
        }
        return proofed;
    }

    private static CachedIccProfile GetOrBuildIccProfile(byte[] iccBytes, SoftProofOptions options, bool needLut)
    {
        ulong hash = ComputeFnv1a64(iccBytes) ^ ((ulong)iccBytes.Length << 32);
        var cacheKey = (hash, options);
        if (IccCache.TryGetValue(cacheKey, out var hit) && hit.RawBytes.AsSpan().SequenceEqual(iccBytes))
        {
            if (!needLut || hit.Lut33 != null || !hit.IsSupported)
                return hit;
        }

        if (IccCache.Count >= 12) IccCache.Clear();

        if (!needLut)
        {
            bool supported = WindowsIccEngine.TryValidateSoftProofProfile(iccBytes);
            if (!supported)
            {
                using var skCheck = SKColorSpace.CreateIcc(iccBytes);
                supported = skCheck != null;
            }
            var entry = new CachedIccProfile((byte[])iccBytes.Clone(), supported, null, null);
            IccCache[cacheKey] = entry;
            return entry;
        }

        if (WindowsIccEngine.TryBakeSoftProofLut33(iccBytes, ProofLutDim, options, out float[] wcsLut, out byte[]? wcsGamut))
        {
            var entry = new CachedIccProfile((byte[])iccBytes.Clone(), true, wcsLut, wcsGamut);
            IccCache[cacheKey] = entry;
            return entry;
        }

        using var iccSpace = SKColorSpace.CreateIcc(iccBytes);
        if (iccSpace != null)
        {
            float[] skLut = BuildSoftProofLut33(iccSpace);
            var entry = new CachedIccProfile((byte[])iccBytes.Clone(), true, skLut, null);
            IccCache[cacheKey] = entry;
            return entry;
        }

        var invalidEntry = new CachedIccProfile((byte[])iccBytes.Clone(), false, null, null);
        IccCache[cacheKey] = invalidEntry;
        return invalidEntry;
    }

    private static ulong ComputeFnv1a64(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static float[] BuildSoftProofLut33(SKColorSpace iccSpace)
    {
        const int n = ProofLutDim;
        using var gridSrgb = new SKBitmap(new SKImageInfo(n * n, n, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        byte* gPtr = (byte*)gridSrgb.GetPixels().ToPointer();
        int gStride = gridSrgb.RowBytes;
        for (int b = 0; b < n; b++)
        {
            byte bv = (byte)Math.Round(b * 255.0 / (n - 1));
            byte* row = gPtr + b * gStride;
            for (int g = 0; g < n; g++)
            {
                byte gv = (byte)Math.Round(g * 255.0 / (n - 1));
                for (int r = 0; r < n; r++)
                {
                    byte rv = (byte)Math.Round(r * 255.0 / (n - 1));
                    int px = (g * n + r) * 4;
                    row[px] = bv;
                    row[px + 1] = gv;
                    row[px + 2] = rv;
                    row[px + 3] = 255;
                }
            }
        }

        using var inGamut = new SKBitmap(new SKImageInfo(n * n, n, SKColorType.Bgra8888, SKAlphaType.Unpremul, iccSpace));
        using (var c1 = new SKCanvas(inGamut))
        using (var i1 = SKImage.FromBitmap(gridSrgb))
            c1.DrawImage(i1, 0, 0, SKSamplingOptions.Default);

        using var backSrgb = new SKBitmap(new SKImageInfo(n * n, n, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        using (var c2 = new SKCanvas(backSrgb))
        using (var i2 = SKImage.FromBitmap(inGamut))
            c2.DrawImage(i2, 0, 0, SKSamplingOptions.Default);

        float[] lut = new float[n * n * n * 3];
        byte* outPtr = (byte*)backSrgb.GetPixels().ToPointer();
        int outStride = backSrgb.RowBytes;
        for (int b = 0; b < n; b++)
        {
            byte* row = outPtr + b * outStride;
            for (int g = 0; g < n; g++)
            {
                for (int r = 0; r < n; r++)
                {
                    int px = (g * n + r) * 4;
                    int dstIdx = ((r * n + g) * n + b) * 3;
                    lut[dstIdx] = row[px + 2];
                    lut[dstIdx + 1] = row[px + 1];
                    lut[dstIdx + 2] = row[px];
                }
            }
        }

        GC.KeepAlive(gridSrgb);
        GC.KeepAlive(backSrgb);
        return lut;
    }

    private static SKBitmap ApplySoftProofLut33(SKBitmap src, float[] lut, byte[]? gamutMask33)
    {
        int w = src.Width;
        int h = src.Height;
        var dst = new SKBitmap(new SKImageInfo(w, h, src.ColorType, src.AlphaType, SKColorSpace.CreateSrgb()));
        byte* srcBase = (byte*)src.GetPixels().ToPointer();
        byte* dstBase = (byte*)dst.GetPixels().ToPointer();
        int srcStride = src.RowBytes;
        int dstStride = dst.RowBytes;

        const int n = ProofLutDim;
        const float scale = (n - 1) / 255f;
        const int strideR = n * n * 3;
        const int strideG = n * 3;
        const int gStrideR = n * n;
        const int gStrideG = n;

        Parallel.For(0, h, TileParallelism, y =>
        {
            byte* sRow = srcBase + y * srcStride;
            byte* dRow = dstBase + y * dstStride;
            for (int x = 0; x < w; x++)
            {
                int idx = x * 4;
                float rf = sRow[idx + 2] * scale;
                float gf = sRow[idx + 1] * scale;
                float bf = sRow[idx] * scale;

                int r0 = Math.Min((int)rf, n - 2);
                int g0 = Math.Min((int)gf, n - 2);
                int b0 = Math.Min((int)bf, n - 2);
                float fr = rf - r0;
                float fg = gf - g0;
                float fb = bf - b0;

                int i000 = r0 * strideR + g0 * strideG + b0 * 3;
                int i001 = i000 + 3;
                int i010 = i000 + strideG;
                int i011 = i010 + 3;
                int i100 = i000 + strideR;
                int i101 = i100 + 3;
                int i110 = i100 + strideG;
                int i111 = i110 + 3;

                float w00 = (1f - fr) * (1f - fg);
                float w01 = (1f - fr) * fg;
                float w10 = fr * (1f - fg);
                float w11 = fr * fg;
                float ib = 1f - fb;

                float outR = (w00 * (lut[i000] * ib + lut[i001] * fb) +
                              w01 * (lut[i010] * ib + lut[i011] * fb) +
                              w10 * (lut[i100] * ib + lut[i101] * fb) +
                              w11 * (lut[i110] * ib + lut[i111] * fb));
                float outG = (w00 * (lut[i000 + 1] * ib + lut[i001 + 1] * fb) +
                              w01 * (lut[i010 + 1] * ib + lut[i011 + 1] * fb) +
                              w10 * (lut[i100 + 1] * ib + lut[i101 + 1] * fb) +
                              w11 * (lut[i110 + 1] * ib + lut[i111 + 1] * fb));
                float outB = (w00 * (lut[i000 + 2] * ib + lut[i001 + 2] * fb) +
                              w01 * (lut[i010 + 2] * ib + lut[i011 + 2] * fb) +
                              w10 * (lut[i100 + 2] * ib + lut[i101 + 2] * fb) +
                              w11 * (lut[i110 + 2] * ib + lut[i111 + 2] * fb));

                if (gamutMask33 != null)
                {
                    int gIdx = Math.Clamp((int)MathF.Round(rf), 0, n - 1) * gStrideR
                             + Math.Clamp((int)MathF.Round(gf), 0, n - 1) * gStrideG
                             + Math.Clamp((int)MathF.Round(bf), 0, n - 1);
                    if (gamutMask33[gIdx] >= 12)
                    {
                        // 超出目标相纸色域的区域叠加洋红高亮提示
                        outR = outR * 0.35f + 235f * 0.65f;
                        outG = outG * 0.35f + 35f * 0.65f;
                        outB = outB * 0.35f + 195f * 0.65f;
                    }
                }

                dRow[idx] = (byte)Math.Clamp((int)MathF.Round(outB), 0, 255);
                dRow[idx + 1] = (byte)Math.Clamp((int)MathF.Round(outG), 0, 255);
                dRow[idx + 2] = (byte)Math.Clamp((int)MathF.Round(outR), 0, 255);
                dRow[idx + 3] = sRow[idx + 3];
            }
        });

        GC.KeepAlive(src);
        GC.KeepAlive(dst);
        return dst;
    }

    /// <summary>
    /// 按 90 度倍数快速旋转图像（用于人脸检视等正交转正场景），使用缓存分块并行像素转置。
    /// </summary>
    public static SKBitmap RotateQuarterTurnsFast(SKBitmap src, int quarterTurns)
    {
        int qt = ((quarterTurns % 4) + 4) % 4;
        var origin = qt switch
        {
            1 => SKEncodedOrigin.RightTop,
            2 => SKEncodedOrigin.BottomRight,
            3 => SKEncodedOrigin.LeftBottom,
            _ => SKEncodedOrigin.TopLeft
        };
        if (origin == SKEncodedOrigin.TopLeft)
            return src.Copy();
        return ApplyEncodedOrigin(src, origin);
    }

    private static SKBitmap ApplyEncodedOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default)
            return src;

        bool swap = IsSwappedOrigin(origin);
        int srcW = src.Width;
        int srcH = src.Height;
        int dstW = swap ? srcH : srcW;
        int dstH = swap ? srcW : srcH;

        var dst = new SKBitmap(new SKImageInfo(dstW, dstH, src.ColorType, src.AlphaType, src.ColorSpace));

        // 对标准 32-bit BGRA 位图使用多线程 32x32 分块整数像素直拷，避免 Skia 单线程矩阵光栅化带来的 800+ ms 开销
        if (src.BytesPerPixel == 4 && src.RowBytes % 4 == 0 && dst.RowBytes % 4 == 0)
        {
            uint* pSrc = (uint*)src.GetPixels().ToPointer();
            uint* pDst = (uint*)dst.GetPixels().ToPointer();
            int sStride = src.RowBytes >> 2;
            int dStride = dst.RowBytes >> 2;

            switch (origin)
            {
                case SKEncodedOrigin.BottomLeft: // 垂直翻转：整行拷贝即可
                    Parallel.For(0, srcH, TileParallelism, y =>
                    {
                        Buffer.MemoryCopy(
                            pSrc + y * sStride,
                            pDst + (srcH - 1 - y) * dStride,
                            (long)dStride * sizeof(uint),
                            (long)srcW * sizeof(uint));
                    });
                    break;

                case SKEncodedOrigin.TopRight: // 水平翻转
                    Parallel.For(0, srcH, TileParallelism, y =>
                    {
                        uint* sr = pSrc + y * sStride;
                        uint* dr = pDst + y * dStride + (srcW - 1);
                        for (int x = 0; x < srcW; x++)
                            dr[-x] = sr[x];
                    });
                    break;

                case SKEncodedOrigin.BottomRight: // 180° 旋转
                    Parallel.For(0, srcH, TileParallelism, y =>
                    {
                        uint* sr = pSrc + y * sStride;
                        uint* dr = pDst + (srcH - 1 - y) * dStride + (srcW - 1);
                        for (int x = 0; x < srcW; x++)
                            dr[-x] = sr[x];
                    });
                    break;

                case SKEncodedOrigin.LeftTop: // 主对角转置: dx = sy, dy = sx
                case SKEncodedOrigin.RightTop: // 顺时针 90°: dx = srcH - 1 - sy, dy = sx
                case SKEncodedOrigin.RightBottom: // 反对角转置: dx = srcH - 1 - sy, dy = srcW - 1 - sx
                case SKEncodedOrigin.LeftBottom: // 逆时针 90°: dx = sy, dy = srcW - 1 - sx
                {
                    const int tile = 32;
                    int tileRows = (srcH + tile - 1) / tile;
                    Parallel.For(0, tileRows, TileParallelism, tyIdx =>
                    {
                        int y0 = tyIdx * tile;
                        int y1 = Math.Min(y0 + tile, srcH);
                        for (int x0 = 0; x0 < srcW; x0 += tile)
                        {
                            int x1 = Math.Min(x0 + tile, srcW);
                            for (int sy = y0; sy < y1; sy++)
                            {
                                uint* sr = pSrc + sy * sStride;
                                switch (origin)
                                {
                                    case SKEncodedOrigin.LeftTop:
                                        for (int sx = x0; sx < x1; sx++)
                                            pDst[sx * dStride + sy] = sr[sx];
                                        break;
                                    case SKEncodedOrigin.RightTop:
                                    {
                                        int dx = srcH - 1 - sy;
                                        for (int sx = x0; sx < x1; sx++)
                                            pDst[sx * dStride + dx] = sr[sx];
                                        break;
                                    }
                                    case SKEncodedOrigin.RightBottom:
                                    {
                                        int dx = srcH - 1 - sy;
                                        for (int sx = x0; sx < x1; sx++)
                                            pDst[(srcW - 1 - sx) * dStride + dx] = sr[sx];
                                        break;
                                    }
                                    case SKEncodedOrigin.LeftBottom:
                                        for (int sx = x0; sx < x1; sx++)
                                            pDst[(srcW - 1 - sx) * dStride + sy] = sr[sx];
                                        break;
                                }
                            }
                        }
                    });
                    break;
                }
            }

            GC.KeepAlive(src);
            GC.KeepAlive(dst);
            return dst;
        }

        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Scale(-1, 1, dstW / 2f, dstH / 2f);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.RotateDegrees(180, dstW / 2f, dstH / 2f);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Scale(1, -1, dstW / 2f, dstH / 2f);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.Translate(0, 0);
                canvas.Scale(-1, 1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(dstW, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(dstW, dstH);
                canvas.Scale(-1, 1);
                canvas.RotateDegrees(-90);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, dstH);
                canvas.RotateDegrees(-90);
                break;
        }

        using var srcImg = SKImage.FromBitmap(src);
        canvas.DrawImage(srcImg, 0, 0, SKSamplingOptions.Default);
        return dst;
    }

    private static bool IsSwappedOrigin(SKEncodedOrigin origin) => origin is
        SKEncodedOrigin.LeftTop or
        SKEncodedOrigin.RightTop or
        SKEncodedOrigin.RightBottom or
        SKEncodedOrigin.LeftBottom;

    private static int OriginToInt(SKEncodedOrigin origin) => (int)origin;
}
