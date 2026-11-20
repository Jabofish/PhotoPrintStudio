using System.IO;
using System.Windows;
using PhotoPrintStudio.App.Helpers;
using PhotoPrintStudio.App.ViewModels;

namespace PhotoPrintStudio.App;

public partial class App : Application
{
    private MainViewModel? _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogFatalCrash("AppDomain", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogFatalCrash("UnobservedTask", args.Exception);
            args.SetObserved();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            LogFatalCrash("Dispatcher", args.Exception);
            bool isFatal = args.Exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
            if (!e.Args.Contains("--smoke-test"))
            {
                MessageBox.Show($"程序运行遇到异常：{args.Exception.Message}\n详情已写入 portable_data\\logs\\",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            if (!isFatal) args.Handled = true;
        };

        base.OnStartup(e);

        try
        {
            bool isSmokeTest = e.Args.Contains("--smoke-test");
            var shutdownMode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _vm = new MainViewModel(checkRecovery: !isSmokeTest);
            if (_vm.IsStartupCancelled)
            {
                Shutdown();
                return;
            }
            var window = new MainWindow(_vm, confirmOnClose: !isSmokeTest);
            MainWindow = window;
            window.Show();
            ShutdownMode = shutdownMode;

            if (e.Args.Length >= 2 && e.Args[0] == "--smoke-test")
            {
                _ = SmokeTestRunner.RunAsync(window, _vm, e.Args[1], LogFatalCrash);
            }
        }
        catch (Exception ex)
        {
            LogFatalCrash("Startup", ex);
            if (!e.Args.Contains("--smoke-test"))
            {
                MessageBox.Show($"启动失败：{ex.Message}\n详情请查看 portable_data\\logs\\crash.log",
                    "启动异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _vm?.SaveConfig();
        _vm?.Dispose();
        base.OnExit(e);
    }

    private static void LogFatalCrash(string source, Exception? ex)
    {
        try
        {
            var paths = new Core.Portable.PortablePaths();
            Directory.CreateDirectory(paths.LogsDirectory);
            string file = Path.Combine(paths.LogsDirectory, "crash.log");
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n\n");
        }
        catch
        {
            // ignore
        }
    }
}
