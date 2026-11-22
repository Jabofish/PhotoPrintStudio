using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PhotoPrintStudio.App.Themes;

namespace PhotoPrintStudio.App.Views;

public partial class ColorPickerDialog : Window
{
    private static readonly (string Name, string Hex)[] StudioSwatches =
    [
        ("纯白", "#FFFFFF"),
        ("证件蓝", "#438EDB"),
        ("深海蓝", "#1E4E8C"),
        ("天青蓝", "#8CC4F2"),
        ("证件红", "#D9001B"),
        ("朱砂红", "#B91C1C"),
        ("浅灰", "#CBD5E1"),
        ("深灰", "#475569"),
        ("暖米白", "#FAF6EE"),
        ("奶茶", "#F3EBDD"),
        ("樱花粉", "#F6E6E8"),
        ("暗夜黑", "#181A20")
    ];

    private bool _syncing;
    public string SelectedHex { get; private set; } = "#FFFFFF";

    public ColorPickerDialog(string initialHex, string title, bool isDarkTheme)
    {
        InitializeComponent();
        Title = title;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, isDarkTheme);

        BuildQuickPalette();

        var c = TryParseColor(initialHex) ?? Colors.White;
        ApplyColorToControls(c);

        var valueProperty = DependencyPropertyDescriptor.FromProperty(Controls.StudioSliderRow.ValueProperty, typeof(Controls.StudioSliderRow));
        valueProperty.AddValueChanged(SliderR, OnRgbSliderChanged);
        valueProperty.AddValueChanged(SliderG, OnRgbSliderChanged);
        valueProperty.AddValueChanged(SliderB, OnRgbSliderChanged);
        Closed += (_, _) =>
        {
            valueProperty.RemoveValueChanged(SliderR, OnRgbSliderChanged);
            valueProperty.RemoveValueChanged(SliderG, OnRgbSliderChanged);
            valueProperty.RemoveValueChanged(SliderB, OnRgbSliderChanged);
        };
    }

    private void BuildQuickPalette()
    {
        foreach (var (name, hex) in StudioSwatches)
        {
            var c = TryParseColor(hex) ?? Colors.White;
            var btn = new Button
            {
                Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(c),
                ToolTip = $"{name} ({hex})"
            };
            btn.SetResourceReference(StyleProperty, "SwatchButton");
            System.Windows.Automation.AutomationProperties.SetName(btn, name);
            btn.Click += (_, _) => ApplyColorToControls(c);
            QuickPalette.Children.Add(btn);
        }
    }

    private void OnRgbSliderChanged(object? sender, EventArgs e)
    {
        if (_syncing) return;
        byte r = (byte)Math.Clamp(Math.Round(SliderR.Value), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round(SliderG.Value), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round(SliderB.Value), 0, 255);
        ApplyColorToControls(Color.FromRgb(r, g, b), updateSliders: false);
    }

    private void HexInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        var parsed = TryParseColor(HexInput.Text);
        if (parsed != null)
            ApplyColorToControls(parsed.Value, updateHex: false);
    }

    private void ApplyColorToControls(Color color, bool updateSliders = true, bool updateHex = true)
    {
        _syncing = true;
        try
        {
            SelectedHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            PreviewSwatch.Background = new SolidColorBrush(color);
            if (updateSliders)
            {
                SliderR.Value = color.R;
                SliderG.Value = color.G;
                SliderB.Value = color.B;
            }
            if (updateHex)
                HexInput.Text = SelectedHex;
        }
        finally
        {
            _syncing = false;
        }
    }

    private static Color? TryParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        if (s.Length == 4)
            s = $"#{s[1]}{s[1]}{s[2]}{s[2]}{s[3]}{s[3]}";
        if (s.Length != 7) return null;
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch { return null; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
