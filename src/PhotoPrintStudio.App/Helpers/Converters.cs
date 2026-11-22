using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.Helpers;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool bv && bv;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && (Invert ? v != Visibility.Visible : v == Visibility.Visible);
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                return new SolidColorBrush(c);
            }
            catch { }
        }
        return Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : "#FFFFFF";
}

public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter != null && targetType.IsEnum)
        {
            return Enum.Parse(targetType, parameter.ToString()!, true);
        }
        return Binding.DoNothing;
    }
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return Visibility.Collapsed;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class IntZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int iv && parameter != null && int.TryParse(parameter.ToString(), out int target))
            return iv == target;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter != null && int.TryParse(parameter.ToString(), out int target))
            return target;
        return Binding.DoNothing;
    }
}

public sealed class IntEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int iv && parameter != null && int.TryParse(parameter.ToString(), out int target))
            return iv == target ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class SliderRailGeometryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        // PART_Track uses a 14 DIP thumb hit target. Its centre can travel from 7 to width - 7.
        const double inset = 7;
        const double centerY = 10;
        if (values[0] is not double width || width <= 2 * inset ||
            values[1] is not double min || values[2] is not double max || max <= min ||
            values[3] is not double value)
            return Geometry.Empty;

        bool bipolar = min < 0 && max > 0;
        double scale = (width - 2 * inset) / (max - min);
        double origin = bipolar ? inset - min * scale : inset;
        double position = inset + (value - min) * scale;
        return parameter switch
        {
            "Zero" when bipolar => new LineGeometry(new Point(origin, centerY - 4), new Point(origin, centerY + 4)),
            "Value" when position != origin => new LineGeometry(new Point(origin, centerY), new Point(position, centerY)),
            _ => Geometry.Empty
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class TemplateListTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not LayoutTemplateDefinition template) return "";
        if (Equals(parameter, "Size"))
        {
            if (template.IsCustomFreeform)
            {
                if (template.CustomSlots is { Count: > 0 } slots)
                {
                    if (slots.Count == 1)
                        return Size(slots[0].WidthMm, slots[0].HeightMm, culture);
                    bool uniform = slots.All(s =>
                        Math.Abs(s.WidthMm - slots[0].WidthMm) < 0.2 &&
                        Math.Abs(s.HeightMm - slots[0].HeightMm) < 0.2);
                    return uniform
                        ? $"{slots.Count} 格 · {Size(slots[0].WidthMm, slots[0].HeightMm, culture)}"
                        : $"{slots.Count} 格自由版式";
                }
                return "自由版式";
            }
            if (template.TemplateKind == "id_mixed_1_2") return "25 × 35 / 35 × 49 mm";
            if (template.TemplateKind == "polaroid_2in1_fit") return "";
            if (IdPhotoSpec.BuiltInSpecs.FirstOrDefault(spec => spec.Id == template.IdPhotoSpecId) is { } id)
                return Size(id.WidthMm, id.HeightMm, culture);
            if (PolaroidSpec.BuiltInSpecs.FirstOrDefault(spec => spec.Id == template.PolaroidSpecId) is { } card)
                return Size(card.CardWidthMm, card.CardHeightMm, culture);
            if (template.Scenario == LayoutScenario.SinglePhoto &&
                PaperSizeSpec.BuiltInPapers.FirstOrDefault(paper => paper.Id == template.DefaultPaperId) is { } paper)
                return Size(paper.WidthMm, paper.HeightMm, culture);
            return "";
        }

        if (!template.IsBuiltIn) return template.Name;
        return template.Id switch
        {
            "tpl_id_6in_1in_8" => "6寸 8张一寸（正排）",
            "tpl_id_6in_1in_9" => "6寸 9张一寸（紧凑）",
            "tpl_id_6in_2in_4" => "6寸 4张二寸",
            "tpl_id_6in_small2_6" => "6寸 6张小二寸 / 签证",
            "tpl_id_6in_big1_4" => "6寸 4张大一寸 / 护照",
            "tpl_id_6in_us_visa_2" => "6寸 美签 / 印签（2 张）",
            "tpl_id_a4_1in_max" => "A4 一寸满版（最多 30 张）",
            "tpl_6in_2in1_border" => "六寸并排（留白）",
            "tpl_6in_2in1_full" => "六寸并排（满幅）",
            "tpl_6in_2in1_vert" => "六寸上下拼印",
            "tpl_6in_2in1_card_duplex" => "六寸卡证正反面",
            "tpl_pol_6in_square_2_safe" => "6寸 2张 Instax Square（留边）",
            "tpl_pol_6in_600_2_fit" => "6寸 2张宝丽来 600（等比）",
            _ when template.Scenario == LayoutScenario.Polaroid => template.Name.Split(" (", 2)[0],
            _ => template.Name
        };
    }

    private static string Size(double width, double height, CultureInfo culture) =>
        string.Create(culture, $"{width:0.#} × {height:0.#} mm");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class CanvasToolbarLabelVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && width >= 620 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
