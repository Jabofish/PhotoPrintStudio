using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PhotoPrintStudio.App.Controls;

/// <summary>
/// 仿专业打印排版工具的右下角可视化尺寸与四边距示意图：
/// 实时展示当前图片框的 X/Y 坐标、宽×高（mm）以及距纸张上/下/左/右四边距离。
/// </summary>
public sealed class MarginDiagramControl : FrameworkElement
{
    public static readonly DependencyProperty HasSelectionProperty =
        DependencyProperty.Register(nameof(HasSelection), typeof(bool), typeof(MarginDiagramControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaperWidthMmProperty =
        DependencyProperty.Register(nameof(PaperWidthMm), typeof(double), typeof(MarginDiagramControl),
            new FrameworkPropertyMetadata(152.4, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaperHeightMmProperty =
        DependencyProperty.Register(nameof(PaperHeightMm), typeof(double), typeof(MarginDiagramControl),
            new FrameworkPropertyMetadata(101.6, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrameXMmProperty =
        DependencyProperty.Register(nameof(FrameXMm), typeof(double), typeof(MarginDiagramControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrameYMmProperty =
        DependencyProperty.Register(nameof(FrameYMm), typeof(double), typeof(MarginDiagramControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrameWidthMmProperty =
        DependencyProperty.Register(nameof(FrameWidthMm), typeof(double), typeof(MarginDiagramControl),
            new FrameworkPropertyMetadata(63.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrameHeightMmProperty =
        DependencyProperty.Register(nameof(FrameHeightMm), typeof(double), typeof(MarginDiagramControl),
            new FrameworkPropertyMetadata(90.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool HasSelection { get => (bool)GetValue(HasSelectionProperty); set => SetValue(HasSelectionProperty, value); }
    public double PaperWidthMm { get => (double)GetValue(PaperWidthMmProperty); set => SetValue(PaperWidthMmProperty, value); }
    public double PaperHeightMm { get => (double)GetValue(PaperHeightMmProperty); set => SetValue(PaperHeightMmProperty, value); }
    public double FrameXMm { get => (double)GetValue(FrameXMmProperty); set => SetValue(FrameXMmProperty, value); }
    public double FrameYMm { get => (double)GetValue(FrameYMmProperty); set => SetValue(FrameYMmProperty, value); }
    public double FrameWidthMm { get => (double)GetValue(FrameWidthMmProperty); set => SetValue(FrameWidthMmProperty, value); }
    public double FrameHeightMm { get => (double)GetValue(FrameHeightMmProperty); set => SetValue(FrameHeightMmProperty, value); }

    private static readonly Typeface UiTypeface = new("Segoe UI, Microsoft YaHei UI");
    private static readonly Typeface BoldTypeface = new(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 80 || h < 80) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var cardBg = TryFindResource("BrushBgCard") as Brush ?? Brushes.White;
        var borderStrong = TryFindResource("BrushBorderStrong") as Brush ?? Brushes.Gray;
        var textPrimary = TryFindResource("BrushTextPrimary") as Brush ?? Brushes.Black;
        var textSecondary = TryFindResource("BrushTextSecondary") as Brush ?? Brushes.DimGray;
        var accent = TryFindResource("BrushAccent") as Brush ?? new SolidColorBrush(Color.FromRgb(20, 115, 230));
        var accentText = TryFindResource("BrushAccentText") as Brush ?? Brushes.White;

        // 顶部坐标行：X / Y
        const double headerH = 20;
        if (HasSelection)
        {
            var xText = MakeText($"X: {FrameXMm:0.#}", 11, textPrimary, dpi, bold: false);
            var yText = MakeText($"Y: {FrameYMm:0.#}", 11, textPrimary, dpi, bold: false);
            var unitText = MakeText("mm", 10, textSecondary, dpi, bold: false);
            dc.DrawText(xText, new Point(4, 2));
            dc.DrawText(yText, new Point(w * 0.5 - yText.Width * 0.35, 2));
            dc.DrawText(unitText, new Point(w - unitText.Width - 4, 3));
        }
        else
        {
            var emptyHeader = MakeText("未选中图片框", 11, textSecondary, dpi, bold: false);
            dc.DrawText(emptyHeader, new Point(4, 2));
        }

        // 外框（示意纸张容器）
        var outerRect = new Rect(4, headerH + 2, w - 8, h - headerH - 6);
        var outerPen = new Pen(borderStrong, 1);
        outerPen.Freeze();
        dc.DrawRoundedRectangle(cardBg, outerPen, outerRect, 3, 3);

        if (!HasSelection || PaperWidthMm <= 0.1 || PaperHeightMm <= 0.1)
        {
            var hint = MakeText("选中画布中的图片框\n即可查看四边距与尺寸", 10.5, textSecondary, dpi, bold: false);
            hint.TextAlignment = TextAlignment.Center;
            dc.DrawText(hint, new Point(outerRect.X + (outerRect.Width - hint.Width) * 0.5,
                outerRect.Y + (outerRect.Height - hint.Height) * 0.5));
            return;
        }

        // 中心蓝框（固定居中示意盒，四边留出足够空间清晰标注上下左右边距箭头）
        double boxW = Math.Clamp(outerRect.Width * 0.42, 62, 104);
        double boxH = Math.Clamp(outerRect.Height * 0.46, 46, 72);
        var innerRect = new Rect(
            outerRect.X + (outerRect.Width - boxW) * 0.5,
            outerRect.Y + (outerRect.Height - boxH) * 0.5,
            boxW, boxH);

        var frameBorderPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 15, 23, 42)), 1);
        frameBorderPen.Freeze();
        dc.DrawRoundedRectangle(accent, frameBorderPen, innerRect, 3, 3);

        // 框内尺寸标注（宽 × 高）
        var dimText = MakeText($"{FrameWidthMm:0.#}\n×\n{FrameHeightMm:0.#}", 10.5, accentText, dpi, bold: true);
        dimText.TextAlignment = TextAlignment.Center;
        dimText.LineHeight = 12.5;
        dc.DrawText(dimText, new Point(
            innerRect.X + (innerRect.Width - dimText.Width) * 0.5,
            innerRect.Y + (innerRect.Height - dimText.Height) * 0.5));

        double leftMargin = Math.Round(FrameXMm, 1);
        double topMargin = Math.Round(FrameYMm, 1);
        double rightMargin = Math.Round(PaperWidthMm - FrameXMm - FrameWidthMm, 1);
        double bottomMargin = Math.Round(PaperHeightMm - FrameYMm - FrameHeightMm, 1);

        var arrowBrush = accent;
        var arrowPen = new Pen(arrowBrush, 1.0);
        arrowPen.Freeze();

        double midX = innerRect.X + innerRect.Width * 0.5;
        double midY = innerRect.Y + innerRect.Height * 0.5;

        // 上边距箭头 + 数值
        DrawDoubleArrow(dc, arrowPen, new Point(midX, outerRect.Top + 2), new Point(midX, innerRect.Top - 1), vertical: true);
        DrawMarginLabel(dc, topMargin, new Point(midX + 5, (outerRect.Top + innerRect.Top) * 0.5), textPrimary, dpi, leftAlign: true);

        // 下边距箭头 + 数值
        DrawDoubleArrow(dc, arrowPen, new Point(midX, innerRect.Bottom + 1), new Point(midX, outerRect.Bottom - 2), vertical: true);
        DrawMarginLabel(dc, bottomMargin, new Point(midX + 5, (innerRect.Bottom + outerRect.Bottom) * 0.5), textPrimary, dpi, leftAlign: true);

        // 左边距箭头 + 数值
        DrawDoubleArrow(dc, arrowPen, new Point(outerRect.Left + 2, midY), new Point(innerRect.Left - 1, midY), vertical: false);
        DrawMarginLabel(dc, leftMargin, new Point((outerRect.Left + innerRect.Left) * 0.5, midY - 10), textPrimary, dpi, leftAlign: false);

        // 右边距箭头 + 数值
        DrawDoubleArrow(dc, arrowPen, new Point(innerRect.Right + 1, midY), new Point(outerRect.Right - 2, midY), vertical: false);
        DrawMarginLabel(dc, rightMargin, new Point((innerRect.Right + outerRect.Right) * 0.5, midY - 10), textPrimary, dpi, leftAlign: false);
    }

    private static void DrawDoubleArrow(DrawingContext dc, Pen pen, Point p1, Point p2, bool vertical)
    {
        dc.DrawLine(pen, p1, p2);
        const double s = 3.5;
        if (vertical)
        {
            dc.DrawLine(pen, p1, new Point(p1.X - s * 0.7, p1.Y + s));
            dc.DrawLine(pen, p1, new Point(p1.X + s * 0.7, p1.Y + s));
            dc.DrawLine(pen, p2, new Point(p2.X - s * 0.7, p2.Y - s));
            dc.DrawLine(pen, p2, new Point(p2.X + s * 0.7, p2.Y - s));
        }
        else
        {
            dc.DrawLine(pen, p1, new Point(p1.X + s, p1.Y - s * 0.7));
            dc.DrawLine(pen, p1, new Point(p1.X + s, p1.Y + s * 0.7));
            dc.DrawLine(pen, p2, new Point(p2.X - s, p2.Y - s * 0.7));
            dc.DrawLine(pen, p2, new Point(p2.X - s, p2.Y + s * 0.7));
        }
    }

    private void DrawMarginLabel(DrawingContext dc, double valueMm, Point anchor, Brush normalBrush, double dpi, bool leftAlign)
    {
        var brush = valueMm < -0.05
            ? (TryFindResource("BrushStatusError") as Brush ?? Brushes.Crimson)
            : normalBrush;
        var ft = MakeText($"{valueMm:0.#}", 10, brush, dpi, bold: false);
        double x = leftAlign ? anchor.X : anchor.X - ft.Width * 0.5;
        double y = anchor.Y - ft.Height * 0.5;
        dc.DrawText(ft, new Point(x, y));
    }

    private static FormattedText MakeText(string text, double size, Brush brush, double dpi, bool bold) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            bold ? BoldTypeface : UiTypeface, size, brush, dpi);
}
