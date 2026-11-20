using System.Windows;
using Microsoft.Win32;
using PhotoPrintStudio.App.Themes;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.Views;

public enum SettingsSection
{
    Appearance,
    Preview,
    Output
}

public partial class SettingsDialog : Window
{
    private readonly MainViewModel _vm;

    public SettingsDialog(MainViewModel vm, SettingsSection section = SettingsSection.Appearance)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Sections.SelectedIndex = (int)section;
        JpegQualitySlider.DefaultValue = AppPortableConfig.StandardJpegQuality;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, vm.IsDarkTheme);
        Closed += (_, _) => vm.SaveConfig();
    }

    private void ResetPreviewDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppPortableConfig();
        _vm.ShowRulers = defaults.ShowRulers;
        _vm.ShowSafeMarginOverlay = defaults.ShowSafeMarginOverlay;
        _vm.ShowBleedOverlay = defaults.ShowBleedOverlay;
        _vm.ShowIdGuidelines = defaults.ShowIdGuidelines;
        _vm.ShowFaceLandmarksOverlay = defaults.ShowFaceLandmarksOverlay;
        _vm.ShowClippingOverlay = defaults.ShowClippingOverlay;
    }

    private void ImportIcc_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入相纸色彩配置",
            Filter = "相纸色彩配置 (*.icc;*.icm)|*.icc;*.icm"
        };
        if (dialog.ShowDialog(this) == true) _vm.ImportIccProfile(dialog.FileName);
    }

    private void ResetExportDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppPortableConfig();
        _vm.JpegQuality = AppPortableConfig.StandardJpegQuality;
        _vm.ExportDpi = AppPortableConfig.StandardOutputDpi;
        _vm.PrintDpi = AppPortableConfig.StandardOutputDpi;
        _vm.OutputSharpeningAmount = defaults.OutputSharpeningAmount;
        _vm.EmbedIccProfileOnExport = defaults.EmbedIccProfileOnExport;
        _vm.SoftProofIntent = defaults.SoftProofIntent;
        _vm.SoftProofSimulatePaperWhite = defaults.SoftProofSimulatePaperWhite;
        _vm.SoftProofGamutWarning = defaults.SoftProofGamutWarning;
    }
}
