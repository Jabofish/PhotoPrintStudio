using System.Windows;
using System.Windows.Threading;

namespace PhotoPrintStudio.App.Services;

/// <summary>
/// UI 线程调度抽象：生产环境委托给 WPF Dispatcher，单元测试可使用 InlineUiDispatcher 同步执行。
/// </summary>
public interface IUiDispatcher
{
    bool CheckAccess();
    void Invoke(Action action);
    void BeginInvoke(Action action, bool dataBindPriority = false);
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    public bool CheckAccess()
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher == null || dispatcher.CheckAccess();
    }

    public void Invoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    public void BeginInvoke(Action action, bool dataBindPriority = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(
            dataBindPriority ? DispatcherPriority.DataBind : DispatcherPriority.Normal,
            action);
    }
}

public sealed class InlineUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public void Invoke(Action action) => action();
    public void BeginInvoke(Action action, bool dataBindPriority = false) => action();
}
