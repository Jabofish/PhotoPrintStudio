using System.Windows;
using System.Windows.Input;
using PhotoPrintStudio.App.Themes;

namespace PhotoPrintStudio.App.Views;

public partial class WorkspaceRestoreDialog : Window
{
    internal bool StartFresh { get; private set; }

    public WorkspaceRestoreDialog(string message)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, ThemeManager.IsDarkTheme);
        WorkspaceSummary.Text = message;
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void StartFresh_Click(object sender, RoutedEventArgs e)
    {
        StartFresh = true;
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
