using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

/// <summary>
/// 三级图像金字塔缓存：L0 缩略图 (256px, LRU 上限 256) / L1 实时编辑代理 (1400px, LRU 上限 16) / L2 按需全分辨率流式解码。
/// 所有缓存访问均通过共享不可变像素返回独立所有权句柄，避免驱逐或销毁时的原生悬空指针风险。
/// </summary>
public sealed class ImageCacheManager : IDisposable
{
    private readonly Dictionary<string, (string key, SKBitmap bmp, LinkedListNode<string> node)> _thumbCache = new();
    private readonly LinkedList<string> _thumbLru = new();
    private readonly object _thumbLock = new();

    private readonly Dictionary<string, (string key, SKBitmap bmp, LinkedListNode<string> node)> _previewCache = new();
    private readonly LinkedList<string> _previewLru = new();
    private readonly object _lruLock = new();

    private const int MaxThumbCachedCount = 256;
    private const int MaxPreviewCachedCount = 16;
    private volatile bool _disposed;

    public SKBitmap GetThumbnail(PhotoAsset asset) => GetReadOnlyThumbnail(asset);

    public SKBitmap GetReadOnlyThumbnail(PhotoAsset asset)
    {
        string key = $"{asset.FilePath}|{File.GetLastWriteTimeUtc(asset.FilePath).Ticks}";
        lock (_thumbLock)
        {
            if (_thumbCache.TryGetValue(asset.Id, out var cached) && cached.key == key)
            {
                MoveToFront(_thumbLru, cached.node);
                return BitmapPixels.ShareImmutable(cached.bmp);
            }
        }

        var bmp = ExifAndColorHelper.DecodeNormalizedSrgb(asset.FilePath, 256);
        bmp.SetImmutable();
        lock (_thumbLock)
        {
            if (_disposed) return bmp;
            if (_thumbCache.TryGetValue(asset.Id, out var old))
            {
                if (old.key == key)
                {
                    bmp.Dispose();
                    MoveToFront(_thumbLru, old.node);
                    return BitmapPixels.ShareImmutable(old.bmp);
                }
                _thumbLru.Remove(old.node);
                old.bmp.Dispose();
            }

            var node = _thumbLru.AddFirst(asset.Id);
            _thumbCache[asset.Id] = (key, bmp, node);
            while (_thumbLru.Count > MaxThumbCachedCount)
            {
                var oldest = _thumbLru.Last!;
                _thumbLru.RemoveLast();
                if (_thumbCache.Remove(oldest.Value, out var evicted))
                    evicted.bmp.Dispose();
            }
            return BitmapPixels.ShareImmutable(bmp);
        }
    }

    public SKBitmap GetPreviewProxy(PhotoAsset asset, int maxLongEdge = 1400)
    {
        using var proxy = GetReadOnlyPreviewProxy(asset, maxLongEdge);
        return proxy.Copy();
    }

    public SKBitmap GetReadOnlyPreviewProxy(PhotoAsset asset, int maxLongEdge = 1400)
    {
        string key = $"{asset.FilePath}|{File.GetLastWriteTimeUtc(asset.FilePath).Ticks}|{maxLongEdge}";
        lock (_lruLock)
        {
            if (_previewCache.TryGetValue(asset.Id, out var cached) && cached.key == key)
            {
                MoveToFront(_previewLru, cached.node);
                return BitmapPixels.ShareImmutable(cached.bmp);
            }
        }

        var bmp = ExifAndColorHelper.DecodeNormalizedSrgb(asset.FilePath, maxLongEdge);
        bmp.SetImmutable();
        lock (_lruLock)
        {
            if (_disposed) return bmp;
            if (_previewCache.TryGetValue(asset.Id, out var cached) && cached.key == key)
            {
                bmp.Dispose();
                MoveToFront(_previewLru, cached.node);
                return BitmapPixels.ShareImmutable(cached.bmp);
            }

            if (_previewCache.TryGetValue(asset.Id, out var old))
            {
                _previewLru.Remove(old.node);
                old.bmp.Dispose();
            }

            var node = _previewLru.AddFirst(asset.Id);
            _previewCache[asset.Id] = (key, bmp, node);
            while (_previewLru.Count > MaxPreviewCachedCount)
            {
                var oldest = _previewLru.Last!;
                _previewLru.RemoveLast();
                if (_previewCache.Remove(oldest.Value, out var evicted))
                    evicted.bmp.Dispose();
            }
            return BitmapPixels.ShareImmutable(bmp);
        }
    }

    public SKBitmap DecodeFullResolution(PhotoAsset asset)
    {
        return ExifAndColorHelper.DecodeNormalizedSrgb(asset.FilePath, 0);
    }

    public void RemoveAsset(string assetId)
    {
        lock (_thumbLock)
        {
            if (_thumbCache.Remove(assetId, out var t))
            {
                _thumbLru.Remove(t.node);
                t.bmp.Dispose();
            }
        }
        lock (_lruLock)
        {
            if (_previewCache.Remove(assetId, out var p))
            {
                _previewLru.Remove(p.node);
                p.bmp.Dispose();
            }
        }
    }

    public void ClearPreviews()
    {
        lock (_lruLock)
        {
            _previewLru.Clear();
            foreach (var kv in _previewCache) kv.Value.bmp.Dispose();
            _previewCache.Clear();
        }
    }

    private static void MoveToFront(LinkedList<string> lru, LinkedListNode<string> node)
    {
        if (lru.First == node) return;
        lru.Remove(node);
        lru.AddFirst(node);
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_thumbLock)
        {
            _thumbLru.Clear();
            foreach (var kv in _thumbCache) kv.Value.bmp.Dispose();
            _thumbCache.Clear();
        }
        ClearPreviews();
    }
}
