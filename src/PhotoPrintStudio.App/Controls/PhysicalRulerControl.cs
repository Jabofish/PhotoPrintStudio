using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PhotoPrintStudio.App.Controls;

public sealed class PhysicalRulerControl : FrameworkElement
{
    public static readonly DependencyProperty IsVerticalProperty =
        DependencyProperty.Register(nameof(IsVertical), typeof(bool), typeof(PhysicalRulerControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LengthMmProperty =
        DependencyProperty.Register(nameof(LengthMm), typeof(double), typeof(PhysicalRulerControl),
            new FrameworkPropertyMetadata(152.4, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PixelsPerMmProperty =
        DependencyProperty.Register(nameof(PixelsPerMm), typeof(double), typeof(PhysicalRulerControl),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OriginPixelOffsetProperty =
        DependencyProperty.Register(nameof(OriginPixelOffset), typeof(double), typeof(PhysicalRulerControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsVertical
    {
        get => (bool)GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

    public double LengthMm
    {
        get => (double)GetValue(LengthMmProperty);
        set => SetValue(LengthMmProperty, value);
    }

    public double PixelsPerMm
    {
        get => (double)GetValue(PixelsPerMmProperty);
        set => SetValue(PixelsPerMmProperty, value);
    }

    public double OriginPixelOffset
    {
        get => (double)GetValue(OriginPixelOffsetProperty);
        set => SetValue(OriginPixelOffsetProperty, value);
    }

    private static readonly Typeface RulerTypeface = new("Segoe UI");
    private static readonly Brush FallbackBgBrush = Freeze(new SolidColorBrush(Color.FromRgb(24, 26, 32)));
    private static readonly Brush FallbackTickBrush = Freeze(new SolidColorBrush(Color.FromRgb(110, 120, 138)));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 1 || h <= 1 || PixelsPerMm <= 0.05) return;

        var bg = TryFindResource("BrushRulerBg") as Brush ?? FallbackBgBrush;
        var tickBrush = TryFindResource("BrushRulerTick") as Brush ?? FallbackTickBrush;
        var textBrush = TryFindResource("BrushTextSecondary") as Brush ?? Brushes.Gray;
        var borderBrush = TryFindResource("BrushBorderSubtle") as Brush ?? Brushes.DarkGray;

        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));

        var pen = new Pen(tickBrush, 1);
        if (pen.CanFreeze) pen.Freeze();
        var borderPen = new Pen(borderBrush, 1);
        if (borderPen.CanFreeze) borderPen.Freeze();
        double dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = RulerTypeface;

        double stepMm = PixelsPerMm >= 7 ? 1 : (PixelsPerMm >= 3 ? 2 : 5);
        double majorStepMm = stepMm <= 1 ? 10 : (stepMm <= 2 ? 20 : 25);

        int maxMm = (int)Math.Ceiling(LengthMm);
        for (double mm = 0; mm <= maxMm; mm += stepMm)
        {
            double pos = OriginPixelOffset + mm * PixelsPerMm;
            bool isMajor = Math.Abs(mm % majorStepMm) < 0.01;
            bool isMid = !isMajor && Math.Abs(mm % (majorStepMm / 2.0)) < 0.01;
            double tickLen = isMajor ? 11 : (isMid ? 7 : 4);

            if (!IsVertical)
            {
                if (pos < 0 || pos > w) continue;
                dc.DrawLine(pen, new Point(pos, h - tickLen), new Point(pos, h));
                if (isMajor)
                {
                    var ft = new FormattedText($"{mm:0}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, 9.5, textBrush, dpiScale);
                    dc.DrawText(ft, new Point(pos + 2, 1));
                }
            }
            else
            {
                if (pos < 0 || pos > h) continue;
                dc.DrawLine(pen, new Point(w - tickLen, pos), new Point(w, pos));
                if (isMajor)
                {
                    var ft = new FormattedText($"{mm:0}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, 9.5, textBrush, dpiScale);
                    dc.DrawText(ft, new Point(2, pos + 1));
                }
            }
        }

        if (!IsVertical)
            dc.DrawLine(borderPen, new Point(0, h - 0.5), new Point(w, h - 0.5));
        else
            dc.DrawLine(borderPen, new Point(w - 0.5, 0), new Point(w - 0.5, h));
    }
}
