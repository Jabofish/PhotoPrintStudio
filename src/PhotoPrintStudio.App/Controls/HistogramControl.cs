using System.Windows;
using System.Windows.Media;
using PhotoPrintStudio.Core.Imaging;

namespace PhotoPrintStudio.App.Controls;

public sealed class HistogramControl : FrameworkElement
{
    public static readonly DependencyProperty HistogramProperty =
        DependencyProperty.Register(nameof(Histogram), typeof(HistogramData), typeof(HistogramControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public HistogramData? Histogram
    {
        get => (HistogramData?)GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }

    private static readonly Brush FallbackBgBrush = Freeze(new SolidColorBrush(Color.FromRgb(24, 26, 32)));
    private static readonly Brush FallbackBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(45, 50, 62)));
    private static readonly Pen GridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(40, 148, 163, 184))), 1));
    private static readonly (Brush Fill, Pen Stroke) RedStyle = MakeChannelStyle(Color.FromArgb(75, 239, 68, 68), Color.FromArgb(165, 239, 68, 68));
    private static readonly (Brush Fill, Pen Stroke) GreenStyle = MakeChannelStyle(Color.FromArgb(75, 34, 197, 94), Color.FromArgb(165, 34, 197, 94));
    private static readonly (Brush Fill, Pen Stroke) BlueStyle = MakeChannelStyle(Color.FromArgb(75, 59, 130, 246), Color.FromArgb(165, 59, 130, 246));
    private static readonly (Brush Fill, Pen Stroke) LumaDarkStyle = MakeChannelStyle(Color.FromArgb(65, 226, 232, 240), Color.FromArgb(200, 226, 232, 240));
    private static readonly (Brush Fill, Pen Stroke) LumaLightStyle = MakeChannelStyle(Color.FromArgb(55, 71, 85, 105), Color.FromArgb(170, 51, 65, 85));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }

    private static (Brush Fill, Pen Stroke) MakeChannelStyle(Color fill, Color stroke) =>
        (Freeze(new SolidColorBrush(fill)), Freeze(new Pen(Freeze(new SolidColorBrush(stroke)), 1.0)));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 4 || h <= 4) return;

        var bg = TryFindResource("BrushBgInput") as Brush ?? FallbackBgBrush;
        var border = TryFindResource("BrushBorderSubtle") as Brush ?? FallbackBorderBrush;
        var borderPen = new Pen(border, 1);
        if (borderPen.CanFreeze) borderPen.Freeze();
        dc.DrawRoundedRectangle(bg, borderPen, new Rect(0, 0, w, h), 6, 6);

        // 4等分参考网格线
        for (int i = 1; i <= 3; i++)
        {
            double x = w * i / 4.0;
            dc.DrawLine(GridPen, new Point(x, 2), new Point(x, h - 2));
        }

        var data = Histogram;
        if (data == null || data.TotalPixels <= 0) return;

        // 忽略极端的纯白/纯黑单峰值以免压缩中间细节显示
        int maxCount = 1;
        for (int i = 3; i < 253; i++)
        {
            maxCount = Math.Max(maxCount, data.Luminance[i]);
            maxCount = Math.Max(maxCount, data.Red[i]);
            maxCount = Math.Max(maxCount, data.Green[i]);
            maxCount = Math.Max(maxCount, data.Blue[i]);
        }
        if (maxCount < 10)
        {
            for (int i = 0; i < 256; i++)
                maxCount = Math.Max(maxCount, Math.Max(data.Luminance[i], Math.Max(data.Red[i], Math.Max(data.Green[i], data.Blue[i]))));
        }

        DrawChannel(dc, data.Red, maxCount, w, h, RedStyle.Fill, RedStyle.Stroke);
        DrawChannel(dc, data.Green, maxCount, w, h, GreenStyle.Fill, GreenStyle.Stroke);
        DrawChannel(dc, data.Blue, maxCount, w, h, BlueStyle.Fill, BlueStyle.Stroke);
        var lumaStyle = Themes.ThemeManager.IsDarkTheme ? LumaDarkStyle : LumaLightStyle;
        DrawChannel(dc, data.Luminance, maxCount, w, h, lumaStyle.Fill, lumaStyle.Stroke);
    }

    private static void DrawChannel(DrawingContext dc, int[] bins, int maxVal, double w, double h, Brush fill, Pen stroke)
    {
        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(new Point(2, h - 2), true, true);
            for (int i = 0; i < 256; i++)
            {
                double x = 2 + (w - 4) * (i / 255.0);
                double norm = Math.Min(1.0, bins[i] / (double)maxVal);
                // 微调平方根映射增强暗细节可见度
                double y = (h - 2) - Math.Pow(norm, 0.72) * (h - 8);
                ctx.LineTo(new Point(x, y), true, false);
            }
            ctx.LineTo(new Point(w - 2, h - 2), true, false);
        }
        geom.Freeze();
        dc.DrawGeometry(fill, stroke, geom);
    }
}
