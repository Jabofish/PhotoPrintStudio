using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Portable;

/// <summary>
/// 联机拍摄热文件夹监视服务：
/// 监听指定目录内相机/传图工具新增的图片文件，自动过滤临时文件、等待文件写入完整稳定、去重后触发 <see cref="PhotoReady"/>。
/// </summary>
public sealed class HotFolderWatcherService : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"
    };

    private readonly AppLogger _logger;
    private readonly object _sync = new();
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, long LastWriteTicks)> _seenFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _initialFiles = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _sessionCts;
    private string _watchDirectory = "";
    private bool _includeSubdirectories;
    private bool _disposed;

    public bool IsWatching
    {
        get { lock (_sync) return _watcher != null && _watcher.EnableRaisingEvents; }
    }

    // PhotoReady 的同步订阅方可沿用当前监视会话的取消信号。
    public CancellationToken SessionToken
    {
        get { lock (_sync) return _sessionCts?.Token ?? new CancellationToken(canceled: true); }
    }

    public string ActiveWatchDirectory
    {
        get { lock (_sync) return _watchDirectory; }
    }

    /// <summary>
    /// 当有新图片完成写入并校验可读后触发（在后台线程触发，订阅方需按需调度至 UI 线程）。
    /// </summary>
    public event Action<string>? PhotoReady;

    /// <summary>
    /// 监控异常状态通知（如目录被移除或盘符断开）。
    /// </summary>
    public event Action<string>? WatchError;

    public HotFolderWatcherService(AppLogger logger)
    {
        _logger = logger;
    }

    public static bool IsSupportedImageFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) return false;
        // 忽略常见相机联机软件/系统生成的临时或隐藏占位文件
        if (name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal))
            return false;
        string ext = Path.GetExtension(name);
        return SupportedExtensions.Contains(ext);
    }

    /// <summary>
    /// 启动对目标文件夹的监视。若 <paramref name="ignoreExistingOnStart"/> 为 true，则启动时记录已有文件快照避免重复吸入历史旧图。
    /// </summary>
    public bool Start(string directory, bool includeSubdirectories, bool ignoreExistingOnStart, out string errorMessage)
    {
        errorMessage = "";
        Stop();

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            errorMessage = "所选监视文件夹不存在或无法访问";
            return false;
        }

        string fullDir = Path.GetFullPath(directory);
        CancellationToken token;
        lock (_sync)
        {
            if (_disposed)
            {
                errorMessage = "监视服务已释放";
                return false;
            }

            _seenFiles.Clear();
            _initialFiles.Clear();
            _watchDirectory = fullDir;
            _includeSubdirectories = includeSubdirectories;
            _sessionCts = new CancellationTokenSource();
            token = _sessionCts.Token;

            try
            {
                _watcher = CreateWatcher(fullDir, includeSubdirectories);
                _watcher.EnableRaisingEvents = true;
                if (ignoreExistingOnStart)
                {
                    _initialFiles.UnionWith(EnumerateImages(fullDir, includeSubdirectories));
                }
                _logger.Info($"已启动联机热文件夹监视: {fullDir} (子目录={includeSubdirectories}, 忽略旧图={ignoreExistingOnStart})");
            }
            catch (Exception ex)
            {
                Stop();
                errorMessage = $"无法监视该目录：{ex.Message}";
                _logger.Error(errorMessage, ex);
                return false;
            }
        }

        if (!ignoreExistingOnStart)
            _ = Task.Run(() => ScanFiles(fullDir, includeSubdirectories, token, includeInitiallyIgnored: false), token);

        return true;
    }

    /// <summary>手动补扫会包括启动时跳过、且尚未导入的照片。</summary>
    public int ScanExistingUnseenNow(CancellationToken? sessionToken = null)
    {
        string dir;
        bool includeSub;
        CancellationToken token;
        lock (_sync)
        {
            if (_sessionCts == null || (sessionToken.HasValue && _sessionCts.Token != sessionToken.Value)) return 0;
            dir = _watchDirectory;
            includeSub = _includeSubdirectories;
            token = _sessionCts.Token;
        }

        return ScanFiles(dir, includeSub, token, includeInitiallyIgnored: true);
    }

    private int ScanFiles(string dir, bool includeSub, CancellationToken token, bool includeInitiallyIgnored)
    {
        int queued = 0;
        try
        {
            foreach (string file in EnumerateImages(dir, includeSub).OrderBy(File.GetLastWriteTimeUtc))
            {
                if (token.IsCancellationRequested) break;
                if (ScheduleStabilizeAndEmit(file, token, includeInitiallyIgnored)) queued++;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"扫描热文件夹失败: {dir}", ex);
        }
        return queued;
    }

    private static IEnumerable<string> EnumerateImages(string dir, bool includeSub) =>
        Directory.EnumerateFiles(dir, "*.*", includeSub ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(IsSupportedImageFile);

    public void Stop()
    {
        FileSystemWatcher? oldWatcher;
        CancellationTokenSource? oldCts;
        lock (_sync)
        {
            oldWatcher = _watcher;
            _watcher = null;
            oldCts = _sessionCts;
            _sessionCts = null;
            _watchDirectory = "";
            _pendingPaths.Clear();
        }

        if (oldWatcher != null)
        {
            try
            {
                oldWatcher.EnableRaisingEvents = false;
                oldWatcher.Created -= OnWatcherFileEvent;
                oldWatcher.Changed -= OnWatcherChanged;
                oldWatcher.Renamed -= OnWatcherRenamed;
                oldWatcher.Error -= OnWatcherError;
                oldWatcher.Dispose();
            }
            catch { }
        }

        if (oldCts != null)
        {
            try { oldCts.Cancel(); } catch { }
            oldCts.Dispose();
        }
    }

    private FileSystemWatcher CreateWatcher(string directory, bool includeSubdirectories)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024 // 64KB 缓冲防止相机高速连拍溢出丢失事件
        };
        watcher.Created += OnWatcherFileEvent;
        watcher.Changed += OnWatcherChanged;
        watcher.Renamed += OnWatcherRenamed;
        watcher.Error += OnWatcherError;
        return watcher;
    }

    private void OnWatcherFileEvent(object sender, FileSystemEventArgs e) =>
        HandleIncomingPath(sender, e.FullPath, includeInitiallyIgnored: true);

    private void OnWatcherRenamed(object sender, RenamedEventArgs e) =>
        HandleIncomingPath(sender, e.FullPath, includeInitiallyIgnored: true);

    private void OnWatcherChanged(object sender, FileSystemEventArgs e) =>
        HandleIncomingPath(sender, e.FullPath, includeInitiallyIgnored: false);

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        CancellationToken token;
        lock (_sync)
        {
            if (_sessionCts == null || !ReferenceEquals(sender, _watcher)) return;
            token = _sessionCts.Token;
        }

        var ex = e.GetException();
        _logger.Error("热文件夹 FileSystemWatcher 报告异常，尝试自动恢复", ex);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, token);
                string dir;
                bool includeSub;
                lock (_sync)
                {
                    if (_sessionCts?.Token != token || !ReferenceEquals(sender, _watcher)) return;
                    dir = _watchDirectory;
                    includeSub = _includeSubdirectories;
                    var replacement = CreateWatcher(dir, includeSub);
                    try
                    {
                        replacement.EnableRaisingEvents = true;
                    }
                    catch
                    {
                        replacement.Dispose();
                        throw;
                    }
                    _watcher = replacement;
                }

                ((FileSystemWatcher)sender).Dispose();
                ScanFiles(dir, includeSub, token, includeInitiallyIgnored: false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception recEx)
            {
                _logger.Error("热文件夹监视自动恢复失败", recEx);
                lock (_sync)
                {
                    if (_sessionCts?.Token != token || !ReferenceEquals(sender, _watcher)) return;
                    Stop();
                    WatchError?.Invoke($"监视已停止，无法连接文件夹：{recEx.Message}");
                }
            }
        }, token);
    }

    private void HandleIncomingPath(object sender, string fullPath, bool includeInitiallyIgnored)
    {
        if (!IsSupportedImageFile(fullPath)) return;
        CancellationToken token;
        lock (_sync)
        {
            if (_sessionCts == null || !ReferenceEquals(sender, _watcher)) return;
            token = _sessionCts.Token;
            if (includeInitiallyIgnored) _initialFiles.Remove(fullPath);
        }

        ScheduleStabilizeAndEmit(fullPath, token, includeInitiallyIgnored);
    }

    private bool ScheduleStabilizeAndEmit(string fullPath, CancellationToken token, bool includeInitiallyIgnored)
    {
        lock (_sync)
        {
            if (_sessionCts?.Token != token || IsAlreadySeenUnchanged(fullPath, includeInitiallyIgnored) ||
                !_pendingPaths.Add(fullPath))
                return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!await WaitForFileWriteStableAsync(fullPath, token)) return;
                lock (_sync)
                {
                    if (_sessionCts?.Token != token || IsAlreadySeenUnchanged(fullPath, includeInitiallyIgnored)) return;
                    var fi = new FileInfo(fullPath);
                    if (!fi.Exists) return;
                    _seenFiles[fullPath] = (fi.Length, fi.LastWriteTimeUtc.Ticks);
                    PhotoReady?.Invoke(fullPath);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.Error($"处理热文件夹新增图片异常: {fullPath}", ex);
            }
            finally
            {
                lock (_sync)
                {
                    if (_sessionCts?.Token == token)
                        _pendingPaths.Remove(fullPath);
                }
            }
        });
        return true;
    }

    private static async Task<bool> WaitForFileWriteStableAsync(string path, CancellationToken token)
    {
        (long Length, long LastWriteTicks) previous = default;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;
                var stamp = (fi.Length, fi.LastWriteTimeUtc.Ticks);
                if (stamp == previous && previous.LastWriteTicks != 0)
                    return CanOpenAndInspectImageHeader(path);

                previous = stamp;
            }
            catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
            {
                // 写入端仍持有文件句柄，等待关闭或用户暂停监视。
            }

            await Task.Delay(120, token);
        }
    }

    private static bool CanOpenAndInspectImageHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (fs.Length < 16) return false;
        string ext = Path.GetExtension(path);
        if (ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
        {
            // TIFF 头: II*\0 或 MM\0*
            Span<byte> hdr = stackalloc byte[4];
            int n = fs.Read(hdr);
            return n == 4 && ((hdr[0] == 'I' && hdr[1] == 'I' && hdr[2] == 42 && hdr[3] == 0) ||
                              (hdr[0] == 'M' && hdr[1] == 'M' && hdr[2] == 0 && hdr[3] == 42));
        }
        using var codec = SKCodec.Create(fs);
        return codec != null && codec.Info.Width > 0 && codec.Info.Height > 0;
    }

    private bool IsAlreadySeenUnchanged(string fullPath, bool includeInitiallyIgnored)
    {
        var fi = new FileInfo(fullPath);
        if (!fi.Exists) return false;

        var stamp = (fi.Length, fi.LastWriteTimeUtc.Ticks);
        return (_seenFiles.TryGetValue(fullPath, out var seen) && seen == stamp) ||
               (!includeInitiallyIgnored && _initialFiles.Contains(fullPath));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }
}
