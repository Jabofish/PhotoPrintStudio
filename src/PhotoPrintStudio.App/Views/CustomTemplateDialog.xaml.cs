using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PhotoPrintStudio.App.Themes;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.Views;

public partial class CustomTemplateDialog : Window
{
    private readonly MainViewModel _vm;

    public CustomTemplateDialog(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, _vm.IsDarkTheme);

        PaperCombo.ItemsSource = _vm.PaperSizes;
        PaperCombo.SelectedItem = _vm.SelectedPaper;
        MarginBox.Text = _vm.LayoutMarginMm.ToString("0.#", CultureInfo.InvariantCulture);
        GapBox.Text = _vm.LayoutGapMm.ToString("0.#", CultureInfo.InvariantCulture);
        BleedBox.Text = _vm.LayoutBleedMm.ToString("0.#", CultureInfo.InvariantCulture);
        RadiusBox.Text = _vm.LayoutCornerRadiusMm.ToString("0.#", CultureInfo.InvariantCulture);
        RowsBox.Text = _vm.CustomGridRows.ToString(CultureInfo.InvariantCulture);
        ColsBox.Text = _vm.CustomGridCols.ToString(CultureInfo.InvariantCulture);
        LandscapeCheck.IsChecked = _vm.IsLandscape;

        foreach (ComboBoxItem item in ScenarioCombo.Items)
        {
            if (item.Tag?.ToString() == _vm.ActiveScenario.ToString())
            {
                ScenarioCombo.SelectedItem = item;
                break;
            }
        }
        FixedPolaroidGrid.IsChecked = _vm.SelectedTemplate?.TemplateKind is "polaroid_fixed_grid" or "polaroid_2in1_fit";
        UpdateCapacity();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var tpl = ReadTemplate();
        _vm.DataStore.SaveCustomTemplate(tpl);
        _vm.ReloadTemplates();
        _vm.ApplyCustomTemplate(tpl);
        DialogResult = true;
        Close();
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (SaveButton != null) UpdateCapacity();
    }

    private void UpdateCapacity()
    {
        var template = ReadTemplate();
        FixedPolaroidGrid.Visibility = template.Scenario == LayoutScenario.Polaroid ? Visibility.Visible : Visibility.Collapsed;
        GapPanel.Visibility = template.Scenario == LayoutScenario.SinglePhoto ? Visibility.Collapsed : Visibility.Visible;
        RowsPanel.Visibility = ColumnsPanel.Visibility = template.TemplateKind is "free_grid" or "polaroid_fixed_grid"
            ? Visibility.Visible : Visibility.Collapsed;
        var sheet = LayoutEngine.BuildSheet(template, (PaperSizeSpec?)PaperCombo.SelectedItem ?? _vm.SelectedPaper,
            idSpecOverride: _vm.SelectedIdSpec, polaroidSpecOverride: _vm.SelectedPolaroidSpec);
        var slot = sheet.Slots.FirstOrDefault();
        SaveButton.IsEnabled = slot != null && sheet.Slots.All(s => s.WidthMm > 0 && s.HeightMm > 0 &&
            s.XMm >= -0.002 && s.YMm >= -0.002 &&
            s.XMm + s.WidthMm <= sheet.PaperWidthMm + 0.002 && s.YMm + s.HeightMm <= sheet.PaperHeightMm + 0.002);
        CapacityText.Text = SaveButton.IsEnabled && slot != null
            ? $"每页 {sheet.Slots.Count} 张，单张 {slot.WidthMm:0.#} × {slot.HeightMm:0.#} mm。"
            : "当前参数放不下卡片，请调整纸张、行列或边距。";
    }

    private LayoutTemplateDefinition ReadTemplate()
    {
        var tag = (ScenarioCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "FreeGrid";
        var scenario = Enum.Parse<LayoutScenario>(tag);
        var paper = (PaperCombo.SelectedItem as PaperSizeSpec) ?? _vm.SelectedPaper;

        string kind = scenario switch
        {
            LayoutScenario.IdPhoto => "auto_grid",
            LayoutScenario.Polaroid => FixedPolaroidGrid.IsChecked == true ? "polaroid_fixed_grid" : "polaroid_grid",
            LayoutScenario.SixInchTwoInOne => "six_2in1_border",
            LayoutScenario.SinglePhoto => "single_photo",
            _ => "free_grid"
        };

        return new LayoutTemplateDefinition
        {
            Id = $"tpl_user_{Guid.NewGuid():N}",
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "自定义排版" : NameBox.Text.Trim(),
            Subtitle = SubBox.Text.Trim(),
            Scenario = scenario,
            DefaultPaperId = paper.Id,
            DefaultLandscape = LandscapeCheck.IsChecked == true,
            DefaultMarginMm = Parse(MarginBox.Text, 5.0),
            DefaultGapMm = Parse(GapBox.Text, 4.0),
            DefaultBleedMm = Parse(BleedBox.Text, 0.0),
            CustomCornerRadiusMm = Parse(RadiusBox.Text, 0.0),
            CustomRows = Math.Clamp((int)Parse(RowsBox.Text, 2), 1, 10),
            CustomCols = Math.Clamp((int)Parse(ColsBox.Text, 2), 1, 10),
            IdPhotoSpecId = _vm.SelectedIdSpec.Id,
            PolaroidSpecId = _vm.SelectedPolaroidSpec.Id,
            DefaultCropMarkStyle = _vm.SelectedCropMarkStyle,
            TemplateKind = kind,
            IsBuiltIn = false
        };

    }

    private static double Parse(string s, double fallback) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && v >= 0 ? v : fallback;
}
