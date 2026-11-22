using System.Windows;
using System.Windows.Media;

namespace PhotoPrintStudio.App.Controls;

/// <summary>
/// 右侧“图片框列表”中每项左侧的微型纸张位置缩略图（直观显示该图片框在整张纸上的位置与大小）。
/// </summary>
public sealed class FrameMiniThumbControl : FrameworkElement
{
    public static readonly DependencyProperty PaperWidthMmProperty =
        DependencyProperty.Register(nameof(PaperWidthMm), typeof(double), typeof(FrameMiniThumbControl),
            new FrameworkPropertyMetadata(152.4, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaperHeightMmProperty =
        DependencyProperty.Register(nameof(PaperHeightMm), typeof(double), typeof(FrameMiniThumbControl),
            new FrameworkPropertyMetadata(101.6, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrameXMmProperty =
        DependencyProperty.Register(nameof(FrameXMm), typeof(double), typeof(FrameMiniThumbControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrameYMmProperty =
        DependencyProperty.Register(nameof(FrameYMm), typeof(double), typeof(FrameMiniThumbControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrameWidthMmProperty =
        DependencyProperty.Register(nameof(FrameWidthMm), typeof(double), typeof(FrameMiniThumbControl),
            new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrameHeightMmProperty =
        DependencyProperty.Register(nameof(FrameHeightMm), typeof(double), typeof(FrameMiniThumbControl),
            new FrameworkPropertyMetadata(90.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsOutOfBoundsProperty =
        DependencyProperty.Register(nameof(IsOutOfBounds), typeof(bool), typeof(FrameMiniThumbControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PaperWidthMm { get => (double)GetValue(PaperWidthMmProperty); set => SetValue(PaperWidthMmProperty, value); }
    public double PaperHeightMm { get => (double)GetValue(PaperHeightMmProperty); set => SetValue(PaperHeightMmProperty, value); }
    public double FrameXMm { get => (double)GetValue(FrameXMmProperty); set => SetValue(FrameXMmProperty, value); }
    public double FrameYMm { get => (double)GetValue(FrameYMmProperty); set => SetValue(FrameYMmProperty, value); }
    public double FrameWidthMm { get => (double)GetValue(FrameWidthMmProperty); set => SetValue(FrameWidthMmProperty, value); }
    public double FrameHeightMm { get => (double)GetValue(FrameHeightMmProperty); set => SetValue(FrameHeightMmProperty, value); }
    public bool IsOutOfBounds { get => (bool)GetValue(IsOutOfBoundsProperty); set => SetValue(IsOutOfBoundsProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 4 || h <= 4 || PaperWidthMm <= 0.1 || PaperHeightMm <= 0.1) return;

        double scale = Math.Min((w - 4) / PaperWidthMm, (h - 4) / PaperHeightMm);
        double pw = PaperWidthMm * scale;
        double ph = PaperHeightMm * scale;
        double ox = (w - pw) * 0.5;
        double oy = (h - ph) * 0.5;

        var borderPen = new Pen(TryFindResource("BrushBorderStrong") as Brush ?? Brushes.Gray, 1);
        borderPen.Freeze();
        dc.DrawRoundedRectangle(Brushes.White, borderPen, new Rect(ox, oy, pw, ph), 1.5, 1.5);

        var accentFill = IsOutOfBounds
            ? new SolidColorBrush(Color.FromArgb(160, 220, 38, 38))
            : new SolidColorBrush(Color.FromArgb(165, 20, 115, 230));
        var accentPen = new Pen(IsOutOfBounds ? Brushes.Crimson : new SolidColorBrush(Color.FromRgb(20, 115, 230)), 0.8);
        accentPen.Freeze();

        double fx = ox + FrameXMm * scale;
        double fy = oy + FrameYMm * scale;
        double fw = Math.Max(2, FrameWidthMm * scale);
        double fh = Math.Max(2, FrameHeightMm * scale);
        dc.PushClip(new RectangleGeometry(new Rect(ox, oy, pw, ph)));
        dc.DrawRectangle(accentFill, accentPen, new Rect(fx, fy, fw, fh));
        dc.Pop();
    }
}
