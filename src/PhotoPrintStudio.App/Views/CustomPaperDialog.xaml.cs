using System.Globalization;
using System.Windows;
using PhotoPrintStudio.App.Themes;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.Views;

public partial class CustomPaperDialog : Window
{
    public PaperSizeSpec? ResultPaper { get; private set; }

    public CustomPaperDialog(bool isDarkTheme, double initialW = 203.2, double initialH = 254.0)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, isDarkTheme);
        WidthBox.Text = initialW.ToString("0.#", CultureInfo.InvariantCulture);
        HeightBox.Text = initialH.ToString("0.#", CultureInfo.InvariantCulture);
        NameBox.Text = $"自定义 ({initialW:0.#}×{initialH:0.#} mm)";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(WidthBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ||
            !double.TryParse(HeightBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ||
            !(w is >= 20 and <= 1200 && h is >= 20 and <= 1600))
        {
            MessageBox.Show(this, "请输入有效的纸张物理毫米尺寸（20 ~ 1200 mm）。", "参数无效", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        double shortSide = Math.Round(Math.Min(w, h), 2);
        double longSide = Math.Round(Math.Max(w, h), 2);
        string name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? $"自定义 ({shortSide:0.#}×{longSide:0.#} mm)"
            : NameBox.Text.Trim();

        ResultPaper = new PaperSizeSpec(
            $"paper_user_{Guid.NewGuid():N}"[..18],
            name,
            shortSide,
            longSide,
            "自定义",
            $"{shortSide:0.#}×{longSide:0.#} mm",
            IsBuiltIn: false);

        DialogResult = true;
        Close();
    }
}
