using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PhotoPrintStudio.App.Views;

namespace PhotoPrintStudio.App.Services;

public enum DialogChoice
{
    Yes,
    No,
    Cancel
}

/// <summary>
/// 弹窗与系统文件对话框抽象，将 ViewModel 与 WPF MessageBox / Win32 对话框解耦，支持无头自动化测试。
/// </summary>
public interface IDialogService
{
    void ShowInfo(string message, string title);
    void ShowError(string message, string title);
    bool Confirm(string message, string title, bool isWarning = false);
    DialogChoice ConfirmYesNoCancel(string message, string title);
    DialogChoice ChooseWorkspaceRestore(string message) =>
        Confirm(message, "恢复上次工作") ? DialogChoice.Yes : DialogChoice.No;
    string? ShowSaveFileDialog(string title, string filter, string defaultExt, string fileName, string? initialDir = null);
    string? ShowOpenFileDialog(string title, string filter, string? initialDir = null);
    string? ShowOpenFolderDialog(string title, string? initialDir = null);
    void OpenPathInShell(string path);
    void RevealFileInShell(string filePath) => OpenPathInShell(Path.GetDirectoryName(filePath) ?? "");
}

public sealed class WpfDialogService : IDialogService
{
    public void ShowInfo(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title, bool isWarning = false) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            isWarning ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;

    public DialogChoice ConfirmYesNoCancel(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => DialogChoice.Yes,
            MessageBoxResult.No => DialogChoice.No,
            _ => DialogChoice.Cancel
        };

    public DialogChoice ChooseWorkspaceRestore(string message)
    {
        var dialog = new WorkspaceRestoreDialog(message);
        return dialog.ShowDialog() == true ? DialogChoice.Yes :
            dialog.StartFresh ? DialogChoice.No : DialogChoice.Cancel;
    }

    public string? ShowSaveFileDialog(string title, string filter, string defaultExt, string fileName, string? initialDir = null)
    {
        var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            DefaultExt = defaultExt,
            FileName = fileName
        };
        if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
            dlg.InitialDirectory = initialDir;
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? ShowOpenFileDialog(string title, string filter, string? initialDir = null)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = filter
        };
        if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
            dlg.InitialDirectory = initialDir;
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? ShowOpenFolderDialog(string title, string? initialDir = null)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
            dlg.InitialDirectory = initialDir;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public void OpenPathInShell(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void RevealFileInShell(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        string fullPath = Path.GetFullPath(filePath);
        if (File.Exists(fullPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
        }
        else if (Path.GetDirectoryName(fullPath) is { } dir && Directory.Exists(dir))
        {
            OpenPathInShell(dir);
        }
    }
}
