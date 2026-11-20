using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Rendering;

public sealed record DigitalIdExportResult(
    string OutputPath,
    int PixelWidth,
    int PixelHeight,
    long FileSizeBytes,
    int JpegQualityUsed,
    bool MetTargetSizeLimit);

/// <summary>
/// 工业级高清导出服务：支持 JPG / PNG（精确写入 300/600 DPI 物理密度元数据与标准 sRGB ICC 色彩配置）、
/// 单张电子证件照（按所选毫米规格与 DPI 换算像素，支持文件体积上限）以及多页精确物理毫米 PDF。
/// </summary>
public static class ExportService
{
    private const uint PngIhdrTag = 0x49484452; // "IHDR"
    private const uint PngPhysTag = 0x70485973; // "pHYs"
    private const uint PngSrgbTag = 0x73524742; // "sRGB"
    private const uint PngIccpTag = 0x69434350; // "iCCP"
    private static readonly uint[] Crc32Table = BuildCrc32Table();
    private static readonly JsonSerializerOptions CompactKeyJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly SKPngEncoderOptions PrintPngOptions = new(
        SKPngEncoderFilterFlags.None | SKPngEncoderFilterFlags.Sub | SKPngEncoderFilterFlags.Up, 6);

    public static void ExportSingleSheetImage(
        string outputPath,
        PrintSheet sheet,
        IReadOnlyDictionary<string, PhotoAsset> assetsById,
        Func<PhotoAsset, PhotoEditState, SKBitmap> resolveFullResBitmap,
        ExportImageFormat format,
        int dpi = 300,
        int jpegQuality = AppPortableConfig.StandardJpegQuality,
        double outputSharpeningAmount = 0.0,
        bool embedIccProfile = true)
    {
        using var bitmap = RenderImage(sheet, assetsById, resolveFullResBitmap, dpi, outputSharpeningAmount);
        WriteImage(outputPath, bitmap, format, dpi, jpegQuality, embedIccProfile);
    }

    /// <summary>
    /// 按所选规格和 DPI 导出单张 JPEG 电子照，可选文件体积上限。
    /// </summary>
    public static DigitalIdExportResult ExportDigitalIdPhoto(
        string outputPath,
        PhotoAsset asset,
        IdPhotoSpec spec,
        Func<PhotoAsset, PhotoEditState, SKBitmap> resolveFullResBitmap,
        int dpi = 300,
        int maxFileSizeKb = 0,
        double outputSharpeningAmount = 0.0,
        bool embedIccProfile = true,
        LayoutSlot? sourceSlot = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        var singleSheet = new PrintSheet
        {
            PaperWidthMm = spec.WidthMm,
            PaperHeightMm = spec.HeightMm,
            CanvasBackgroundHex = asset.EditState.EnableAiBackgroundReplace
                ? asset.EditState.ReplacementBackgroundHex
                : "#FFFFFF",
            CropMarkStyle = CropMarkStyle.None,
            Scenario = LayoutScenario.IdPhoto,
            Slots =
            [
                new LayoutSlot
                {
                    XMm = 0,
                    YMm = 0,
                    WidthMm = spec.WidthMm,
                    HeightMm = spec.HeightMm,
                    PhotoAssetId = asset.Id,
                    IdPhotoSpecId = spec.Id,
                    FitModeOverride = sourceSlot == null ? FitMode.Cover : sourceSlot.FitModeOverride,
                    SlotZoom = sourceSlot?.SlotZoom ?? 1.0,
                    SlotPanX = sourceSlot?.SlotPanX ?? 0.0,
                    SlotPanY = sourceSlot?.SlotPanY ?? 0.0,
                    BleedMm = 0.0
                }
            ]
        };

        var assetsById = new Dictionary<string, PhotoAsset> { [asset.Id] = asset };
        using var rendered = RenderImage(singleSheet, assetsById, resolveFullResBitmap, dpi, outputSharpeningAmount);
        byte[]? iccBytes = embedIccProfile ? WindowsIccEngine.GetStandardSrgbIccBytes() : null;
        int iccOverhead = iccBytes == null ? 0 : BuildJpegApp2IccSegments(iccBytes).Length;
        using var untagged = new SKPixmap(rendered.Info with { ColorSpace = null }, rendered.GetPixels(), rendered.RowBytes);
        long maxBytes = maxFileSizeKb > 0 ? maxFileSizeKb * 1024L : long.MaxValue;
        int low = maxFileSizeKb > 0 ? 1 : AppPortableConfig.StandardJpegQuality;
        int high = AppPortableConfig.StandardJpegQuality;
        int bestQuality = 0;
        long bestSize = 0;
        SKData? best = null;
        try
        {
            while (low <= high)
            {
                token.ThrowIfCancellationRequested();
                int quality = low + (high - low) / 2;
                var candidate = untagged.Encode(new SKJpegEncoderOptions(quality))
                    ?? throw new InvalidOperationException("电子证件照 JPG 编码失败。");
                int jfifOverhead = HasApp0JfifHeader(candidate.AsSpan()) ? 0 : 18;
                long bytes = candidate.Size + iccOverhead + jfifOverhead;
                if (bytes <= maxBytes)
                {
                    best?.Dispose();
                    best = candidate;
                    bestQuality = quality;
                    bestSize = bytes;
                    low = quality + 1;
                }
                else
                {
                    candidate.Dispose();
                    high = quality - 1;
                }
            }

            if (best == null)
                throw new InvalidOperationException($"当前像素尺寸的 JPG 无法压到 {maxFileSizeKb} KB，请提高体积上限或降低导出 DPI。");

            token.ThrowIfCancellationRequested();
            WriteJpegWithDpiAndIccAtomic(outputPath, best, dpi, iccBytes);
            return new DigitalIdExportResult(outputPath, rendered.Width, rendered.Height, bestSize, bestQuality, true);
        }
        finally
        {
            best?.Dispose();
        }
    }

    private static SKBitmap RenderImage(PrintSheet sheet, IReadOnlyDictionary<string, PhotoAsset> assetsById,
        Func<PhotoAsset, PhotoEditState, SKBitmap> resolveFullResBitmap, int dpi, double outputSharpeningAmount)
    {
        var opts = new SheetRenderOptions
        {
            Dpi = dpi,
            IsScreenPreview = false,
            ShowSafeMarginOverlay = false,
            ShowBleedOverlay = false,
            ShowIdGuidelinesOnSelected = false,
            OutputSharpeningAmount = outputSharpeningAmount
        };

        return SheetRenderer.RenderSheetBitmap(sheet, assetsById, resolveFullResBitmap, opts);
    }

    private static void WriteImage(string outputPath, SKBitmap bmp, ExportImageFormat format, int dpi, int jpegQuality, bool embedIccProfile)
    {
        byte[]? iccBytes = embedIccProfile ? WindowsIccEngine.GetStandardSrgbIccBytes() : null;
        if (format == ExportImageFormat.Png)
        {
            using var pixels = bmp.PeekPixels();
            using var data = pixels.Encode(PrintPngOptions) ?? throw new InvalidOperationException("PNG 编码失败。");
            WritePngWithDpiAndIccAtomic(outputPath, data, dpi, iccBytes);
        }
        else
        {
            using var untagged = new SKPixmap(bmp.Info with { ColorSpace = null }, bmp.GetPixels(), bmp.RowBytes);
            using var data = untagged.Encode(new SKJpegEncoderOptions(Math.Clamp(jpegQuality, 60, 100)))
                ?? throw new InvalidOperationException("JPG 编码失败。");
            WriteJpegWithDpiAndIccAtomic(outputPath, data, dpi, iccBytes);
        }
    }

    public static void ExportSheetsToPdf(
        string outputPdfPath,
        IReadOnlyList<PrintSheet> sheets,
        IReadOnlyDictionary<string, PhotoAsset> assetsById,
        Func<PhotoAsset, PhotoEditState, SKBitmap> resolveFullResBitmap,
        int dpi = 300,
        CancellationToken cancellationToken = default,
        double outputSharpeningAmount = 0.0)
    {
        if (sheets.Count == 0) throw new ArgumentException("没有可导出的排版页。", nameof(sheets));

        var metadata = new SKDocumentPdfMetadata
        {
            Title = "PhotoPrintStudio 排版冲印输出",
            Creator = "PhotoPrintStudio Portable",
            Creation = DateTime.Now,
            Modified = DateTime.Now,
            RasterDpi = dpi,
            EncodingQuality = SKDocumentPdfMetadata.DefaultEncodingQuality
        };

        using var cache = new BoundedExportBitmapCache(sheets, maxBytes: 384L * 1024 * 1024, maxEntries: 6);
        SKBitmap CachedResolve(PhotoAsset asset, PhotoEditState edit) => cache.Resolve(asset, edit, resolveFullResBitmap);
        string directory = Path.GetDirectoryName(Path.GetFullPath(outputPdfPath))!;
        Directory.CreateDirectory(directory);
        string pending = Path.Combine(directory, $".{Guid.NewGuid():N}.pdf.tmp");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew))
            using (var doc = SKDocument.CreatePdf(stream, metadata))
            {
                foreach (var sheet in sheets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    float ptW = UnitConverter.MmToPoints(sheet.PaperWidthMm);
                    float ptH = UnitConverter.MmToPoints(sheet.PaperHeightMm);

                    using var pageCanvas = doc.BeginPage(ptW, ptH);
                    if (outputSharpeningAmount > 0.5)
                    {
                        using var sharpenedPage = RenderImage(sheet, assetsById, CachedResolve, dpi, outputSharpeningAmount);
                        using var pageImg = SKImage.FromBitmap(sharpenedPage);
                        pageCanvas.DrawImage(pageImg, new SKRect(0, 0, ptW, ptH), new SKSamplingOptions(SKFilterMode.Linear));
                    }
                    else
                    {
                        var opts = new SheetRenderOptions
                        {
                            Dpi = dpi,
                            IsScreenPreview = false,
                            ShowSafeMarginOverlay = false,
                            ShowBleedOverlay = false,
                            ShowIdGuidelinesOnSelected = false
                        };

                        pageCanvas.Save();
                        float scaleToPt = (float)(UnitConverter.PointsPerInch / dpi);
                        pageCanvas.Scale(scaleToPt);
                        SheetRenderer.RenderToCanvas(pageCanvas, sheet, assetsById, CachedResolve, opts);
                        pageCanvas.Restore();
                    }

                    doc.EndPage();
                }

                doc.Close();
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, outputPdfPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(pending))
            {
                try { File.Delete(pending); } catch { }
            }
        }
    }

    public static IReadOnlyList<string> ExportSheetsBatchImages(
        string outputDirectory,
        string baseFileName,
        IReadOnlyList<PrintSheet> sheets,
        IReadOnlyDictionary<string, PhotoAsset> assetsById,
        Func<PhotoAsset, PhotoEditState, SKBitmap> resolveFullResBitmap,
        ExportImageFormat format,
        int dpi = 300,
        int jpegQuality = AppPortableConfig.StandardJpegQuality,
        IProgress<(int current, int total, string sheetName)>? progress = null,
        CancellationToken cancellationToken = default,
        double outputSharpeningAmount = 0.0,
        bool embedIccProfile = true)
    {
        if (sheets.Count == 0) throw new ArgumentException("没有可导出的排版页。", nameof(sheets));
        Directory.CreateDirectory(outputDirectory);

        string safeBase = string.Concat((string.IsNullOrWhiteSpace(baseFileName) ? "排版输出" : baseFileName.Trim())
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string ext = format == ExportImageFormat.Png ? "png" : "jpg";
        int digits = sheets.Count >= 100 ? 3 : 2;
        var outputs = new List<string>(sheets.Count);

        using var cache = new BoundedExportBitmapCache(sheets, maxBytes: 384L * 1024 * 1024, maxEntries: 6);
        SKBitmap CachedResolve(PhotoAsset asset, PhotoEditState edit) => cache.Resolve(asset, edit, resolveFullResBitmap);
        var encodedPages = format == ExportImageFormat.Png && cache.HasReusedAssets
            ? new Dictionary<(int Width, int Height, string Pixels), string>() : null;

        for (int i = 0; i < sheets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = sheets[i];

            string pageTag = $"P{(i + 1).ToString($"D{digits}")}";
            string fileName = $"{safeBase}_{pageTag}_{sheet.PaperWidthMm:0}x{sheet.PaperHeightMm:0}mm_{dpi}DPI.{ext}";
            string fullPath = Path.Combine(outputDirectory, fileName);

            using var bitmap = RenderImage(sheet, assetsById, CachedResolve, dpi, outputSharpeningAmount);
            if (encodedPages == null)
            {
                WriteImage(fullPath, bitmap, format, dpi, jpegQuality, embedIccProfile);
            }
            else
            {
                var key = (bitmap.Width, bitmap.Height, Convert.ToHexString(SHA256.HashData(bitmap.GetPixelSpan())));
                if (encodedPages.TryGetValue(key, out string? encoded) && File.Exists(encoded))
                {
                    using var source = File.OpenRead(encoded);
                    WriteAtomic(fullPath, source.CopyTo);
                }
                else
                {
                    WriteImage(fullPath, bitmap, format, dpi, jpegQuality, embedIccProfile);
                    encodedPages[key] = fullPath;
                }
            }
            outputs.Add(fullPath);
            progress?.Report((i + 1, sheets.Count, sheet.SheetName));
        }
        return outputs;
    }

    /// <summary>
    /// 按字节预算与条数上限约束的多图/跨页 LRU 导出与打印共享缓存：
    /// 既能在多图混排和跨页输出/打印时复用已调色大图，又不会在数十页任务中无限增长导致内存溢出。
    /// </summary>
    public sealed class BoundedExportBitmapCache(IReadOnlyList<PrintSheet> sheets, long maxBytes = 384L * 1024 * 1024, int maxEntries = 6) : IDisposable
    {
        private readonly HashSet<string> _reusedAssets = sheets
            .SelectMany(sheet => sheet.Slots.Select(slot => slot.PhotoAssetId).OfType<string>().Distinct())
            .GroupBy(id => id).Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet();
        private readonly Dictionary<(string assetId, string editJson), LinkedListNode<CacheItem>> _map = new();
        private readonly LinkedList<CacheItem> _lru = new();
        private long _totalBytes;
        public bool HasReusedAssets => _reusedAssets.Count > 0;

        private sealed record CacheItem((string assetId, string editJson) Key, SKBitmap Bitmap, long ByteSize);

        public SKBitmap Resolve(PhotoAsset asset, PhotoEditState edit, Func<PhotoAsset, PhotoEditState, SKBitmap> resolver)
        {
            if (!_reusedAssets.Contains(asset.Id)) return resolver(asset, edit);

            string editJson = JsonSerializer.Serialize(edit, CompactKeyJsonOptions);
            var key = (asset.Id, editJson);

            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return BitmapPixels.ShareImmutable(node.Value.Bitmap);
            }

            SKBitmap resolved = resolver(asset, edit);
            long size = (long)resolved.ByteCount;
            if (size <= maxBytes)
            {
                var item = new CacheItem(key, resolved, size);
                var newNode = new LinkedListNode<CacheItem>(item);
                _lru.AddFirst(newNode);
                _map[key] = newNode;
                _totalBytes += size;

                while (_lru.Count > 1 && (_lru.Count > maxEntries || _totalBytes > maxBytes))
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                    _totalBytes -= last.Value.ByteSize;
                    last.Value.Bitmap.Dispose();
                }

                return BitmapPixels.ShareImmutable(resolved);
            }

            return resolved;
        }

        public void Dispose()
        {
            foreach (var item in _lru)
                item.Bitmap.Dispose();
            _lru.Clear();
            _map.Clear();
            _totalBytes = 0;
        }
    }

    private static void WriteAtomic(string outputPath, Action<FileStream> writeAction)
    {
        string fullPath = Path.GetFullPath(outputPath);
        string dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        string pending = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                writeAction(fs);
                fs.Flush(flushToDisk: true);
            }
            File.Move(pending, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(pending))
            {
                try { File.Delete(pending); } catch { }
            }
        }
    }

    private static void WriteJpegWithDpiAndIccAtomic(string outputPath, SKData jpeg, int dpi, byte[]? iccBytes)
    {
        byte[]? iccSegments = iccBytes is { Length: > 0 } ? BuildJpegApp2IccSegments(iccBytes) : null;
        WriteAtomic(outputPath, fs =>
        {
            ReadOnlySpan<byte> src = jpeg.AsSpan();
            if (src.Length < 20 || src[0] != 0xFF || src[1] != 0xD8)
            {
                fs.Write(src);
                return;
            }

            ushort d = (ushort)Math.Clamp(dpi, 72, 2400);
            if (HasApp0JfifHeader(src))
            {
                Span<byte> head = stackalloc byte[20];
                src[..20].CopyTo(head);
                head[13] = 1;
                BinaryPrimitives.WriteUInt16BigEndian(head.Slice(14, 2), d);
                BinaryPrimitives.WriteUInt16BigEndian(head.Slice(16, 2), d);
                fs.Write(head);
                if (iccSegments != null) fs.Write(iccSegments);
                fs.Write(src[20..]);
                return;
            }

            Span<byte> soiAndApp0 = stackalloc byte[20];
            BuildSoiPlusApp0(soiAndApp0, d);
            fs.Write(soiAndApp0);
            if (iccSegments != null) fs.Write(iccSegments);
            fs.Write(src[2..]);
        });
    }

    private static void WritePngWithDpiAndIccAtomic(string outputPath, SKData png, int dpi, byte[]? iccBytes)
    {
        byte[]? iccpChunk = iccBytes is { Length: > 0 } ? BuildPngIccpChunk("sRGB IEC61966-2.1", iccBytes) : null;
        WriteAtomic(outputPath, fs =>
        {
            ReadOnlySpan<byte> src = png.AsSpan();
            if (src.Length < 33 ||
                src[0] != 0x89 || src[1] != 0x50 || src[2] != 0x4E || src[3] != 0x47)
            {
                fs.Write(src);
                return;
            }

            Span<byte> physChunk = stackalloc byte[21];
            BuildPhysChunk(physChunk, dpi);

            fs.Write(src[..8]);
            int pos = 8;
            bool inserted = false;
            while (pos + 12 <= src.Length)
            {
                uint len = BinaryPrimitives.ReadUInt32BigEndian(src.Slice(pos, 4));
                int chunkTotal = (int)(12 + len);
                if (pos + chunkTotal > src.Length) break;

                uint tag = BinaryPrimitives.ReadUInt32BigEndian(src.Slice(pos + 4, 4));
                bool skipExisting = tag == PngPhysTag || tag == PngIccpTag || (iccpChunk != null && tag == PngSrgbTag);
                if (!skipExisting)
                {
                    fs.Write(src.Slice(pos, chunkTotal));
                    if (!inserted && tag == PngIhdrTag)
                    {
                        if (iccpChunk != null) fs.Write(iccpChunk);
                        fs.Write(physChunk);
                        inserted = true;
                    }
                }
                pos += chunkTotal;
            }

            if (pos < src.Length)
                fs.Write(src[pos..]);
        });
    }

    private static byte[] BuildJpegApp2IccSegments(byte[] iccBytes)
    {
        // ICC.1 JPEG APP2 规范：每段头部 "ICC_PROFILE\0" (12B) + seq (1B) + total (1B)，单段负载最大 65519 字节
        const int maxPayloadPerChunk = 65519;
        int chunkCount = Math.Max(1, (iccBytes.Length + maxPayloadPerChunk - 1) / maxPayloadPerChunk);
        if (chunkCount > 255) return [];

        using var ms = new MemoryStream(iccBytes.Length + chunkCount * 18);
        ReadOnlySpan<byte> sig = "ICC_PROFILE\0"u8;
        Span<byte> header = stackalloc byte[18];
        for (int i = 0; i < chunkCount; i++)
        {
            int offset = i * maxPayloadPerChunk;
            int payloadLen = Math.Min(maxPayloadPerChunk, iccBytes.Length - offset);
            ushort segLen = (ushort)(2 + 14 + payloadLen);

            header[0] = 0xFF;
            header[1] = 0xE2; // APP2
            BinaryPrimitives.WriteUInt16BigEndian(header.Slice(2, 2), segLen);
            sig.CopyTo(header.Slice(4, 12));
            header[16] = (byte)(i + 1);
            header[17] = (byte)chunkCount;
            ms.Write(header);
            ms.Write(iccBytes, offset, payloadLen);
        }
        return ms.ToArray();
    }

    private static byte[] BuildPngIccpChunk(string profileName, byte[] iccBytes)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(profileName);
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(iccBytes, 0, iccBytes.Length);
            compressed = ms.ToArray();
        }

        int dataLen = nameBytes.Length + 2 + compressed.Length; // name + '\0' + compressionMethod(0) + zlibData
        byte[] chunk = new byte[12 + dataLen];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)dataLen);
        chunk[4] = (byte)'i'; chunk[5] = (byte)'C'; chunk[6] = (byte)'C'; chunk[7] = (byte)'P';
        nameBytes.CopyTo(chunk, 8);
        chunk[8 + nameBytes.Length] = 0; // null separator
        chunk[9 + nameBytes.Length] = 0; // compression method = deflate/zlib
        compressed.CopyTo(chunk, 10 + nameBytes.Length);
        uint crc = Crc32(chunk.AsSpan(4, 4 + dataLen));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + dataLen, 4), crc);
        return chunk;
    }

    /// <summary>
    /// 修补/注入 JPEG JFIF APP0 DPI 物理密度（units=1 inch, Xdensity=dpi, Ydensity=dpi），
    /// 防止冲印店或看图软件按默认 72/96 DPI 错误解释物理毫米尺寸。
    /// </summary>
    public static byte[] PatchJpegJfidDpi(byte[] jpegBytes, int dpi)
    {
        if (jpegBytes.Length < 20 || jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8)
            return jpegBytes;

        ushort d = (ushort)Math.Clamp(dpi, 72, 2400);

        if (HasApp0JfifHeader(jpegBytes))
        {
            var copy = (byte[])jpegBytes.Clone();
            copy[13] = 1; // density units = dots per inch
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(14, 2), d);
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(16, 2), d);
            return copy;
        }

        byte[] result = new byte[jpegBytes.Length + 18];
        BuildSoiPlusApp0(result.AsSpan(0, 20), d);
        Buffer.BlockCopy(jpegBytes, 2, result, 20, jpegBytes.Length - 2);
        return result;
    }

    /// <summary>
    /// 写入或更新 PNG pHYs Chunk（像素/米 + unit=1），确保 PNG 携带准确的 300/600 DPI 物理密度。
    /// </summary>
    public static byte[] PatchPngDpiChunk(byte[] pngBytes, int dpi)
    {
        if (pngBytes.Length < 33 ||
            pngBytes[0] != 0x89 || pngBytes[1] != 0x50 || pngBytes[2] != 0x4E || pngBytes[3] != 0x47)
            return pngBytes;

        Span<byte> physChunk = stackalloc byte[21];
        BuildPhysChunk(physChunk, dpi);

        // 先精确计算输出长度并单次分配精确目标数组，消除 MemoryStream 扩容与二次 ToArray() 拷贝
        int existingPhysBytes = 0;
        int scan = 8;
        while (scan + 12 <= pngBytes.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(scan, 4));
            int chunkTotal = (int)(12 + len);
            if (scan + chunkTotal > pngBytes.Length) break;
            if (BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(scan + 4, 4)) == PngPhysTag)
                existingPhysBytes += chunkTotal;
            scan += chunkTotal;
        }

        byte[] output = new byte[pngBytes.Length - existingPhysBytes + 21];
        Buffer.BlockCopy(pngBytes, 0, output, 0, 8);
        int outPos = 8;
        int pos = 8;
        bool inserted = false;

        while (pos + 12 <= pngBytes.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(pos, 4));
            int chunkTotal = (int)(12 + len);
            if (pos + chunkTotal > pngBytes.Length) break;

            uint tag = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(pos + 4, 4));
            if (tag != PngPhysTag)
            {
                Buffer.BlockCopy(pngBytes, pos, output, outPos, chunkTotal);
                outPos += chunkTotal;
                if (!inserted && tag == PngIhdrTag)
                {
                    physChunk.CopyTo(output.AsSpan(outPos, 21));
                    outPos += 21;
                    inserted = true;
                }
            }
            pos += chunkTotal;
        }

        if (pos < pngBytes.Length)
        {
            int tail = pngBytes.Length - pos;
            Buffer.BlockCopy(pngBytes, pos, output, outPos, tail);
        }

        return output;
    }

    private static bool HasApp0JfifHeader(ReadOnlySpan<byte> jpeg) =>
        jpeg[2] == 0xFF && jpeg[3] == 0xE0 &&
        jpeg.Length >= 20 &&
        jpeg[6] == (byte)'J' && jpeg[7] == (byte)'F' &&
        jpeg[8] == (byte)'I' && jpeg[9] == (byte)'F' && jpeg[10] == 0x00;

    private static void BuildSoiPlusApp0(Span<byte> dst, ushort d)
    {
        dst[0] = 0xFF; dst[1] = 0xD8;
        dst[2] = 0xFF; dst[3] = 0xE0;
        dst[4] = 0x00; dst[5] = 16;
        dst[6] = (byte)'J'; dst[7] = (byte)'F'; dst[8] = (byte)'I'; dst[9] = (byte)'F'; dst[10] = 0x00;
        dst[11] = 1; dst[12] = 1;
        dst[13] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(14, 2), d);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(16, 2), d);
        dst[18] = 0; dst[19] = 0;
    }

    private static void BuildPhysChunk(Span<byte> physChunk, int dpi)
    {
        uint pixelsPerMeter = (uint)Math.Round(dpi / 0.0254);
        BinaryPrimitives.WriteUInt32BigEndian(physChunk[..4], 9);
        physChunk[4] = (byte)'p'; physChunk[5] = (byte)'H'; physChunk[6] = (byte)'Y'; physChunk[7] = (byte)'s';
        BinaryPrimitives.WriteUInt32BigEndian(physChunk.Slice(8, 4), pixelsPerMeter);
        BinaryPrimitives.WriteUInt32BigEndian(physChunk.Slice(12, 4), pixelsPerMeter);
        physChunk[16] = 1;
        uint crc = Crc32(physChunk.Slice(4, 13));
        BinaryPrimitives.WriteUInt32BigEndian(physChunk.Slice(17, 4), crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
            table[i] = c;
        }
        return table;
    }
}
