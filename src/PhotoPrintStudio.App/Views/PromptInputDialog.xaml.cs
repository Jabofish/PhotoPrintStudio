using System.Windows;
using PhotoPrintStudio.App.Themes;

namespace PhotoPrintStudio.App.Views;

public partial class PromptInputDialog : Window
{
    private readonly bool _allowEmpty;
    public string InputText => InputBox.Text.Trim();

    public PromptInputDialog(string title, string label, string defaultText, bool isDarkTheme, bool allowEmpty = false)
    {
        _allowEmpty = allowEmpty;
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
        if (!_allowEmpty && string.IsNullOrWhiteSpace(InputBox.Text))
        {
            MessageBox.Show(this, "名称不能为空。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            InputBox.Focus();
            return;
        }
        DialogResult = true;
        Close();
    }
}
