using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PhotoPrintStudio.App.Themes;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.Views;

public partial class CustomPolaroidDialog : Window
{
    public PolaroidSpec? ResultSpec { get; private set; }

    public CustomPolaroidDialog(PolaroidSpec seed, bool isDarkTheme)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, isDarkTheme);

        CardWBox.Text = seed.CardWidthMm.ToString("0.#", CultureInfo.InvariantCulture);
        CardHBox.Text = seed.CardHeightMm.ToString("0.#", CultureInfo.InvariantCulture);
        TopBox.Text = seed.MarginTopMm.ToString("0.#", CultureInfo.InvariantCulture);
        LeftBox.Text = seed.MarginLeftMm.ToString("0.#", CultureInfo.InvariantCulture);
        RightBox.Text = seed.MarginRightMm.ToString("0.#", CultureInfo.InvariantCulture);
        BottomBox.Text = seed.MarginBottomMm.ToString("0.#", CultureInfo.InvariantCulture);
        RadiusBox.Text = seed.CornerRadiusMm.ToString("0.#", CultureInfo.InvariantCulture);
        NameBox.Text = $"{seed.Name.Split('(')[0].Trim()} 自定义框";
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        UpdateComputedPreview();
    }

    private void Geometry_TextChanged(object sender, TextChangedEventArgs e) => UpdateComputedPreview();

    private void UpdateComputedPreview()
    {
        if (ComputedPhotoWindowText == null) return;
        bool valid = false;
        if (TryReadValues(out double cw, out double ch, out double mt, out double ml, out double mr, out double mb, out double cr))
        {
            double pw = cw - ml - mr;
            double ph = ch - mt - mb;
            if (cw < 20 || ch < 20 || cw > 500 || ch > 500 || mt < 0 || ml < 0 || mr < 0 || mb < 0 || cr < 0)
            {
                ComputedPhotoWindowText.Text = "整卡尺寸需在 20–500 mm 之间，边框与圆角不能为负数";
            }
            else if (pw < 10 || ph < 10)
            {
                ComputedPhotoWindowText.Text = "边框过大：画面区域宽和高均需至少 10 mm";
            }
            else
            {
                valid = true;
                ComputedPhotoWindowText.Text = $"画面区域：{pw:0.#} × {ph:0.#} mm";
            }
        }
        else
        {
            ComputedPhotoWindowText.Text = "请输入有效的毫米数值";
        }

        if (SaveButton != null) SaveButton.IsEnabled = valid;
        ComputedPhotoWindowText.SetResourceReference(TextBlock.ForegroundProperty,
            valid ? "BrushTextSecondary" : "BrushStatusError");
    }

    private bool TryReadValues(out double cw, out double ch, out double mt, out double ml, out double mr, out double mb, out double cr)
    {
        cw = ch = mt = ml = mr = mb = cr = 0;
        return TryParse(CardWBox.Text, out cw)
            && TryParse(CardHBox.Text, out ch)
            && TryParse(TopBox.Text, out mt)
            && TryParse(LeftBox.Text, out ml)
            && TryParse(RightBox.Text, out mr)
            && TryParse(BottomBox.Text, out mb)
            && TryParse(RadiusBox.Text, out cr);
    }

    private static bool TryParse(string s, out double val)
        => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out val) && double.IsFinite(val);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadValues(out double cw, out double ch, out double mt, out double ml, out double mr, out double mb, out double cr) ||
            cw < 20 || ch < 20 || cw > 500 || ch > 500 || mt < 0 || ml < 0 || mr < 0 || mb < 0 || cr < 0)
        {
            MessageBox.Show(this, "整卡尺寸需在 20–500 mm 之间，边框不能为负数。", "自定义拍立得尺寸", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        double pw = Math.Round(cw - ml - mr, 2);
        double ph = Math.Round(ch - mt - mb, 2);
        if (pw < 10 || ph < 10)
        {
            MessageBox.Show(this, "画面区域的宽和高都需至少 10 mm。", "自定义拍立得尺寸", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? $"自定义拍立得 ({cw:0.#}×{ch:0.#} mm)"
            : NameBox.Text.Trim();

        ResultSpec = new PolaroidSpec(
            $"pol_user_{Guid.NewGuid():N}"[..18],
            name,
            Math.Round(cw, 2),
            Math.Round(ch, 2),
            pw,
            ph,
            Math.Round(mt, 2),
            Math.Round(ml, 2),
            Math.Round(mr, 2),
            Math.Round(mb, 2),
            Math.Round(cr, 2),
            $"自定义 {cw:0.#}×{ch:0.#}，底边 {mb:0.#}mm",
            IsBuiltIn: false);

        DialogResult = true;
        Close();
    }
}
