using System.Globalization;
using System.Windows;
using PhotoPrintStudio.App.Themes;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Rendering;

namespace PhotoPrintStudio.App.Views;

public partial class CalibrationDialog : Window
{
    private readonly MainViewModel _vm;
    private readonly PrinterCalibrationProfile _draft;

    public CalibrationDialog(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, _vm.IsDarkTheme);

        _draft = _vm.CurrentCalibration.Clone();
        PrinterNameLabel.Text = $"当前绑定设备：{_vm.SelectedPrinterName}";
        if (_vm.ActiveSheet is { } sheet)
        {
            var (horizontal, vertical) = CalibrationCardRenderer.GetRuleLengths(sheet.PaperWidthMm, sheet.PaperHeightMm);
            MeasHBox.Text = ExpHBox.Text = horizontal.ToString(CultureInfo.InvariantCulture);
            MeasVBox.Text = ExpVBox.Text = vertical.ToString(CultureInfo.InvariantCulture);
        }

        LoadFieldsFromProfile(_draft);
    }

    private void LoadFieldsFromProfile(PrinterCalibrationProfile cal)
    {
        OffsetXBox.Text = cal.OffsetXMm.ToString("0.##", CultureInfo.InvariantCulture);
        OffsetYBox.Text = cal.OffsetYMm.ToString("0.##", CultureInfo.InvariantCulture);
        ScaleXBox.Text = cal.ScaleXPercent.ToString("0.##", CultureInfo.InvariantCulture);
        ScaleYBox.Text = cal.ScaleYPercent.ToString("0.##", CultureInfo.InvariantCulture);
        OverscanBox.Text = cal.OverscanCompensationPercent.ToString("0.##", CultureInfo.InvariantCulture);
        DuplexXBox.Text = cal.DuplexBackOffsetXMm.ToString("0.##", CultureInfo.InvariantCulture);
        DuplexYBox.Text = cal.DuplexBackOffsetYMm.ToString("0.##", CultureInfo.InvariantCulture);
        CompBrightBox.Text = cal.BrightnessCompensation.ToString("0.##", CultureInfo.InvariantCulture);
        CompGammaBox.Text = cal.GammaCompensation.ToString("0.##", CultureInfo.InvariantCulture);
        CompTempBox.Text = cal.TemperatureCompensation.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void AutoComputeScale_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ExpHBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double eh) ||
            !double.TryParse(MeasHBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double mh) ||
            !double.TryParse(ExpVBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ev) ||
            !double.TryParse(MeasVBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double mv) ||
            !PositiveLength(eh) || !PositiveLength(mh) || !PositiveLength(ev) || !PositiveLength(mv))
        {
            MessageBox.Show("请输入有效的数字长度。", "输入提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyFieldsToProfile();
        var updated = CalibrationCardRenderer.ComputeCalibrationFromMeasured(_draft, eh, mh, ev, mv);
        ScaleXBox.Text = updated.ScaleXPercent.ToString("0.##", CultureInfo.InvariantCulture);
        ScaleYBox.Text = updated.ScaleYPercent.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void PresetSublimationOverscan_Click(object sender, RoutedEventArgs e)
    {
        OverscanBox.Text = "97.5";
    }

    private async void PrintTestCard_Click(object sender, RoutedEventArgs e)
    {
        ApplyFieldsToProfile();
        await _vm.PrintCalibrationCardWithProfileAsync(_draft);
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        LoadFieldsFromProfile(new PrinterCalibrationProfile { PrinterName = _vm.SelectedPrinterName });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyFieldsToProfile();
        _vm.CurrentCalibration = _draft;
        _vm.SaveCurrentCalibrationCommand.Execute(null);
        DialogResult = true;
        Close();
    }

    private void ApplyFieldsToProfile()
    {
        var cal = _draft;
        cal.OffsetXMm = ParseDouble(OffsetXBox.Text, cal.OffsetXMm);
        cal.OffsetYMm = ParseDouble(OffsetYBox.Text, cal.OffsetYMm);
        cal.ScaleXPercent = Math.Clamp(ParseDouble(ScaleXBox.Text, cal.ScaleXPercent), 85, 115);
        cal.ScaleYPercent = Math.Clamp(ParseDouble(ScaleYBox.Text, cal.ScaleYPercent), 85, 115);
        cal.OverscanCompensationPercent = Math.Clamp(ParseDouble(OverscanBox.Text, cal.OverscanCompensationPercent), 85, 115);
        cal.DuplexBackOffsetXMm = ParseDouble(DuplexXBox.Text, cal.DuplexBackOffsetXMm);
        cal.DuplexBackOffsetYMm = ParseDouble(DuplexYBox.Text, cal.DuplexBackOffsetYMm);
        cal.BrightnessCompensation = Math.Clamp(ParseDouble(CompBrightBox.Text, cal.BrightnessCompensation), -50, 50);
        cal.GammaCompensation = Math.Clamp(ParseDouble(CompGammaBox.Text, cal.GammaCompensation), 0.5, 2.0);
        cal.TemperatureCompensation = Math.Clamp(ParseDouble(CompTempBox.Text, cal.TemperatureCompensation), -50, 50);
    }

    private static bool PositiveLength(double value) => double.IsFinite(value) && value > 1;

    private static double ParseDouble(string s, double fallback) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : fallback;
}
