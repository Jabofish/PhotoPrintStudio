using System.Windows;
using PhotoPrintStudio.App.Themes;

namespace PhotoPrintStudio.App.Views;

public partial class PromptInputDialog : Window
{
    public string InputText => InputBox.Text.Trim();

    public PromptInputDialog(string title, string label, string defaultText, bool isDarkTheme)
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = label;
        InputBox.Text = defaultText;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, isDarkTheme);
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            MessageBox.Show(this, "名称不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            InputBox.Focus();
            return;
        }
        DialogResult = true;
        Close();
    }
}
