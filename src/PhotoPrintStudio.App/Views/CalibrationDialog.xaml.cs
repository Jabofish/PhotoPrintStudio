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
    private PrinterCalibrationProfile _measuredWith;
    private PrinterGeometryCalibration? _calculatedGeometry;

    public CalibrationDialog(MainViewModel vm, PrinterCalibrationProfile profile)
    {
        _vm = vm;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, _vm.IsDarkTheme);

        _draft = profile;
        _measuredWith = _draft.Clone();
        PrinterNameLabel.Text = $"打印机：{_vm.SelectedPrinterName}";
        if (_vm.ActiveSheet is { } sheet)
        {
            var (horizontal, vertical) = CalibrationCardRenderer.GetRuleLengths(sheet.PaperWidthMm, sheet.PaperHeightMm);
            ExpHBox.Text = horizontal.ToString(CultureInfo.InvariantCulture);
            ExpVBox.Text = vertical.ToString(CultureInfo.InvariantCulture);
        }
        MeasHBox.Clear();
        MeasVBox.Clear();

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
            MessageBox.Show(this, "请输入大于 0 的长度（mm）。", "打印校准", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyFieldsToProfile();
        var updated = CalibrationCardRenderer.ComputeCalibrationFromMeasured(_draft, eh, mh, ev, mv, _measuredWith);
        var (oldX, oldY, _, _) = _measuredWith.GetGeometryTransform();
        var (newX, newY, _, _) = updated.GetGeometryTransform();
        if (Math.Abs(mh * newX / oldX - eh) > 0.05 || Math.Abs(mv * newY / oldY - ev) > 0.05)
        {
            MessageBox.Show(this, "实测差值超出可校准范围。请检查纸型和驱动扩张设置，确认量的是测试卡标尺的两条端线。", "打印校准",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ScaleXBox.Text = updated.ScaleXPercent.ToString("0.##", CultureInfo.InvariantCulture);
        ScaleYBox.Text = updated.ScaleYPercent.ToString("0.##", CultureInfo.InvariantCulture);
        _calculatedGeometry = updated.CaptureGeometry();
        ScaleXBox.BringIntoView();
        ScaleXBox.Focus();
        ScaleXBox.SelectAll();
    }

    private void PresetSublimationOverscan_Click(object sender, RoutedEventArgs e)
    {
        OverscanBox.Text = "97.5";
    }

    private async void PrintTestCard_Click(object sender, RoutedEventArgs e)
    {
        ApplyFieldsToProfile();
        var printedProfile = _draft.Clone();
        if (await _vm.PrintCalibrationCardWithProfileAsync(printedProfile))
            _measuredWith = printedProfile;
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        LoadFieldsFromProfile(new PrinterCalibrationProfile { PrinterName = _vm.SelectedPrinterName });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyFieldsToProfile();
        if (_draft.GeometryTarget is { } key)
        {
            var profile = _vm.CurrentCalibration.Clone();
            var geometry = _draft.CaptureGeometry();
            var prior = profile.GeometryByOutput.GetValueOrDefault(key);
            profile.GeometryByOutput[key] = geometry with
            {
                HasMeasuredScale = MatchesScale(_calculatedGeometry, geometry) ||
                    (prior is { HasMeasuredScale: true } && MatchesScale(prior, geometry))
            };
            profile.BrightnessCompensation = _draft.BrightnessCompensation;
            profile.GammaCompensation = _draft.GammaCompensation;
            profile.TemperatureCompensation = _draft.TemperatureCompensation;
            _vm.CurrentCalibration = profile;
        }
        else
        {
            _vm.CurrentCalibration = _draft;
        }
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

    private static bool MatchesScale(PrinterGeometryCalibration? baseline, PrinterGeometryCalibration value) =>
        baseline != null && baseline.ScaleX == value.ScaleX && baseline.ScaleY == value.ScaleY && baseline.Overscan == value.Overscan;

    private static bool PositiveLength(double value) => double.IsFinite(value) && value > 1;

    private static double ParseDouble(string s, double fallback) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : fallback;
}
