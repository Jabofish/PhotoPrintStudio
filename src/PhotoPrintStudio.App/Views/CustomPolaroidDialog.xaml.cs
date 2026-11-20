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
        UpdateComputedPreview();
    }

    private void Geometry_TextChanged(object sender, TextChangedEventArgs e) => UpdateComputedPreview();

    private void UpdateComputedPreview()
    {
        if (ComputedPhotoWindowText == null) return;
        if (TryReadValues(out double cw, out double ch, out double mt, out double ml, out double mr, out double mb, out _))
        {
            double pw = cw - ml - mr;
            double ph = ch - mt - mb;
            ComputedPhotoWindowText.Text = pw >= 10 && ph >= 10
                ? $"内框有效画面区域：{pw:0.#} × {ph:0.#} mm"
                : "警告：边框过大，内框画面尺寸不足 10 mm";
        }
        else
        {
            ComputedPhotoWindowText.Text = "请输入有效毫米数值";
        }
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
            MessageBox.Show(this, "请检查整卡及边框尺寸（20~500 mm，边框 >= 0 mm）。", "参数无效", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        double pw = Math.Round(cw - ml - mr, 2);
        double ph = Math.Round(ch - mt - mb, 2);
        if (pw < 10 || ph < 10)
        {
            MessageBox.Show(this, "内框画面宽度和高度均需至少 10 mm。", "边框过大", MessageBoxButton.OK, MessageBoxImage.Information);
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
