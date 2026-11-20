using System.Windows;
using System.Windows.Media;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.Controls;

/// <summary>
/// 模板卡片左侧的小型排版原理示意图（按真实纸张与格位比例绘制）。
/// </summary>
public sealed class TemplateGlyphControl : FrameworkElement
{
    public static readonly DependencyProperty TemplateProperty =
        DependencyProperty.Register(nameof(Template), typeof(LayoutTemplateDefinition), typeof(TemplateGlyphControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public LayoutTemplateDefinition? Template
    {
        get => (LayoutTemplateDefinition?)GetValue(TemplateProperty);
        set => SetValue(TemplateProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Template == null) return;

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 4 || h <= 4) return;

        var paper = PaperSizeSpec.BuiltInPapers.FirstOrDefault(p => p.Id == Template.DefaultPaperId)
                    ?? PaperSizeSpec.BuiltInPapers[0];
        var sheet = LayoutEngine.BuildSheet(Template, paper);

        double scale = Math.Min((w - 6) / sheet.PaperWidthMm, (h - 6) / sheet.PaperHeightMm);
        double pw = sheet.PaperWidthMm * scale;
        double ph = sheet.PaperHeightMm * scale;
        double ox = (w - pw) * 0.5;
        double oy = (h - ph) * 0.5;

        var paperBg = Brushes.White;
        var paperBorder = new Pen(TryFindResource("BrushBorderStrong") as Brush ?? Brushes.Gray, 1);
        dc.DrawRoundedRectangle(paperBg, paperBorder, new Rect(ox, oy, pw, ph), 2, 2);

        var accentBrush = TryFindResource("BrushAccent") as SolidColorBrush ?? new SolidColorBrush(Color.FromRgb(59, 130, 246));
        var slotFill = new SolidColorBrush(Color.FromArgb(65, accentBrush.Color.R, accentBrush.Color.G, accentBrush.Color.B));
        var slotPen = new Pen(accentBrush, 0.9);

        foreach (var s in sheet.Slots)
        {
            var r = new Rect(ox + s.XMm * scale, oy + s.YMm * scale, Math.Max(2, s.WidthMm * scale), Math.Max(2, s.HeightMm * scale));
            if (s.PolaroidStyle != null)
            {
                dc.DrawRectangle(Brushes.White, slotPen, r);
                double chin = r.Height * 0.22;
                var inner = new Rect(r.X + 1.5, r.Y + 1.5, Math.Max(1, r.Width - 3), Math.Max(1, r.Height - chin - 2));
                dc.DrawRectangle(slotFill, null, inner);
            }
            else
            {
                dc.DrawRoundedRectangle(slotFill, slotPen, r, s.CornerRadiusMm > 0.5 ? 2 : 0.5, s.CornerRadiusMm > 0.5 ? 2 : 0.5);
            }
        }
    }
}
