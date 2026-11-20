using System.Windows.Media.Imaging;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.App.Services;

/// <summary>
/// 预览渲染缓存与并发渲染上下文协调器：
/// 统一托管调色预览 LRU 缓存、分屏原图缓存、页面缩略图 LRU 与并发渲染上下文版本号/取消令牌，
/// 将复杂非托管位图生命周期从 MainViewModel 中完全解耦并支持独立单元测试。
/// </summary>
public sealed class PreviewCacheCoordinator : IDisposable
{
    private sealed record ProcessedPreviewEntry(
        string SourceKey,
        PhotoEditState EditSnap,
        bool ShowClip,
        SKBitmap Bitmap,
        HistogramData Histogram,
        long Used);

    private readonly object _contextLock = new();
    private readonly object _cacheLock = new();
    private readonly Dictionary<(string AssetId, bool OriginalColors), ProcessedPreviewEntry> _processedPreviewCache = new();
    private readonly Dictionary<(string SheetId, string State), BitmapSource> _pageThumbnailCache = new();
    private (string Key, SKBitmap Bitmap)? _splitBeforeCache;
    private CancellationTokenSource? _contextCts = new();
    private long _contextVersion;
    private long _useCounter;
    private bool _disposed;

    public long CurrentContextVersion => Volatile.Read(ref _contextVersion);

    public bool TryGetCurrentContext(out long version, out CancellationToken token)
    {
        lock (_contextLock)
        {
            if (_contextCts == null)
            {
                version = _contextVersion;
                token = new CancellationToken(canceled: true);
                return false;
            }
            version = Volatile.Read(ref _contextVersion);
            token = _contextCts.Token;
            return true;
        }
    }

    public long AdvanceContext(bool closing = false)
    {
        CancellationTokenSource? previous;
        long nextVersion;
        lock (_contextLock)
        {
            previous = _contextCts;
            if (previous == null) return _contextVersion;
            nextVersion = Interlocked.Increment(ref _contextVersion);
            _contextCts = closing ? null : new CancellationTokenSource();
        }
        try { previous.Cancel(); } catch (ObjectDisposedException) { }
        previous.Dispose();
        return nextVersion;
    }

    public bool TryGetProcessedPreview(
        string assetId,
        bool originalColors,
        string sourceKey,
        PhotoEditState edit,
        bool showClipping,
        out SKBitmap? sharedBitmap,
        out HistogramData? histogram)
    {
        var key = (assetId, originalColors);
        lock (_cacheLock)
        {
            if (_processedPreviewCache.TryGetValue(key, out var hit) &&
                hit.SourceKey == sourceKey &&
                hit.ShowClip == showClipping &&
                hit.EditSnap.ColorEquals(edit) &&
                hit.Bitmap.Handle != IntPtr.Zero)
            {
                _processedPreviewCache[key] = hit with { Used = ++_useCounter };
                sharedBitmap = BitmapPixels.ShareImmutable(hit.Bitmap);
                histogram = hit.Histogram;
                return true;
            }
        }
        sharedBitmap = null;
        histogram = null;
        return false;
    }

    public void StoreProcessedPreview(
        string assetId,
        bool originalColors,
        string sourceKey,
        PhotoEditState edit,
        bool showClipping,
        SKBitmap bitmap,
        HistogramData histogram)
    {
        var key = (assetId, originalColors);
        lock (_cacheLock)
        {
            if (_disposed) return;
            if (_processedPreviewCache.TryGetValue(key, out var old))
                old.Bitmap.Dispose();

            _processedPreviewCache[key] = new ProcessedPreviewEntry(
                sourceKey,
                edit.Clone(),
                showClipping,
                BitmapPixels.ShareImmutable(bitmap),
                histogram,
                ++_useCounter);

            if (_processedPreviewCache.Count > 16)
            {
                var oldest = _processedPreviewCache.MinBy(entry => entry.Value.Used);
                _processedPreviewCache.Remove(oldest.Key);
                oldest.Value.Bitmap.Dispose();
            }
        }
    }

    public bool TryGetSplitBefore(string beforeKey, out SKBitmap? sharedBitmap)
    {
        lock (_cacheLock)
        {
            if (_splitBeforeCache is { } cached &&
                cached.Key == beforeKey &&
                cached.Bitmap.Handle != IntPtr.Zero)
            {
                sharedBitmap = BitmapPixels.ShareImmutable(cached.Bitmap);
                return true;
            }
        }
        sharedBitmap = null;
        return false;
    }

    public void StoreSplitBefore(string beforeKey, SKBitmap bitmap)
    {
        lock (_cacheLock)
        {
            if (_disposed) return;
            _splitBeforeCache?.Bitmap.Dispose();
            _splitBeforeCache = (beforeKey, BitmapPixels.ShareImmutable(bitmap));
        }
    }

    public bool TryGetPageThumbnail(string sheetId, string stateKey, out BitmapSource? thumbnail)
    {
        lock (_pageThumbnailCache)
        {
            return _pageThumbnailCache.TryGetValue((sheetId, stateKey), out thumbnail);
        }
    }

    public void StorePageThumbnail(string sheetId, string stateKey, BitmapSource thumbnail)
    {
        lock (_pageThumbnailCache)
        {
            _pageThumbnailCache[(sheetId, stateKey)] = thumbnail;
            if (_pageThumbnailCache.Count > 24)
                _pageThumbnailCache.Remove(_pageThumbnailCache.Keys.First());
        }
    }

    public void InvalidateAsset(string assetId)
    {
        lock (_cacheLock)
        {
            if (_splitBeforeCache is { } split && split.Key.StartsWith($"{assetId}|", StringComparison.Ordinal))
            {
                split.Bitmap.Dispose();
                _splitBeforeCache = null;
            }
            foreach (bool originalColors in new[] { false, true })
            {
                if (_processedPreviewCache.Remove((assetId, originalColors), out var old))
                    old.Bitmap.Dispose();
            }
        }
    }

    public void ClearProcessedAndSplitCaches()
    {
        lock (_cacheLock)
        {
            _splitBeforeCache?.Bitmap.Dispose();
            _splitBeforeCache = null;
            foreach (var item in _processedPreviewCache.Values) item.Bitmap.Dispose();
            _processedPreviewCache.Clear();
        }
    }

    public void ClearAllCaches()
    {
        ClearProcessedAndSplitCaches();
        lock (_pageThumbnailCache)
            _pageThumbnailCache.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AdvanceContext(closing: true);
        ClearAllCaches();
    }
}
