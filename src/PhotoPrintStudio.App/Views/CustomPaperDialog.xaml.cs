using System.Globalization;
using System.Windows;
using PhotoPrintStudio.App.Themes;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.Views;

public partial class CustomPaperDialog : Window
{
    public PaperSizeSpec? ResultPaper { get; private set; }
    private bool _nameManuallyEdited;
    private bool _updatingAutoName;

    public CustomPaperDialog(bool isDarkTheme, double initialW = 203.2, double initialH = 254.0)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, isDarkTheme);
        WidthBox.Text = initialW.ToString("0.#", CultureInfo.InvariantCulture);
        HeightBox.Text = initialH.ToString("0.#", CultureInfo.InvariantCulture);
        UpdateAutoName(initialW, initialH);

        WidthBox.TextChanged += (_, _) => TryUpdateAutoNameFromInputs();
        HeightBox.TextChanged += (_, _) => TryUpdateAutoNameFromInputs();
        NameBox.TextChanged += (_, _) =>
        {
            if (!_updatingAutoName) _nameManuallyEdited = !string.IsNullOrWhiteSpace(NameBox.Text);
        };
        Loaded += (_, _) =>
        {
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
    }

    private void TryUpdateAutoNameFromInputs()
    {
        if (_nameManuallyEdited) return;
        if (double.TryParse(WidthBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
            double.TryParse(HeightBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
            w > 0 && h > 0)
        {
            UpdateAutoName(w, h);
        }
    }

    private void UpdateAutoName(double w, double h)
    {
        _updatingAutoName = true;
        try { NameBox.Text = $"自定义 ({Math.Min(w, h):0.#}×{Math.Max(w, h):0.#} mm)"; }
        finally { _updatingAutoName = false; }
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
            !double.IsFinite(w) || !double.IsFinite(h) ||
            !(w is >= 20 and <= 1200 && h is >= 20 and <= 1600))
        {
            MessageBox.Show(this, "纸张宽度需在 20–1200 mm，高度需在 20–1600 mm 之间。", "自定义纸张尺寸", MessageBoxButton.OK, MessageBoxImage.Information);
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
