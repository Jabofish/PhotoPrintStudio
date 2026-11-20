namespace PhotoPrintStudio.Core.Portable;

public sealed class AppLogger
{
    private const int RetentionDays = 14;
    private readonly PortablePaths _paths;
    private readonly object _sync = new();
    private volatile bool _logsDirEnsured;
    private bool _prunedOldLogs;

    public AppLogger(PortablePaths paths)
    {
        _paths = paths;
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private void Write(string level, string message)
    {
        try
        {
            if (!_logsDirEnsured)
            {
                Directory.CreateDirectory(_paths.LogsDirectory);
                _logsDirEnsured = true;
            }

            var logFile = Path.Combine(_paths.LogsDirectory, $"app_{DateTime.Now:yyyyMMdd}.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
            lock (_sync)
            {
                if (!_prunedOldLogs)
                {
                    _prunedOldLogs = true;
                    PruneOldLogsUnsafe();
                }

                try
                {
                    File.AppendAllText(logFile, line);
                }
                catch (DirectoryNotFoundException)
                {
                    Directory.CreateDirectory(_paths.LogsDirectory);
                    File.AppendAllText(logFile, line);
                }
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private void PruneOldLogsUnsafe()
    {
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(_paths.LogsDirectory, "app_*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }
}
