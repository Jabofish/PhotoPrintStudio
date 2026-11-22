using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.Controls;

/// <summary>
/// 专业打印机排版工具风格的可视化自由版式设计画布。
/// 坐标系与纸张物理尺寸 1:1 映射：(0,0) 为纸张左上角，(ActualWidth, ActualHeight) 为纸张右下角。
/// </summary>
public sealed class LayoutDesignerCanvas : FrameworkElement
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(CustomLayoutEditorViewModel), typeof(LayoutDesignerCanvas),
            new FrameworkPropertyMetadata(null, OnViewModelChanged));

    public CustomLayoutEditorViewModel? ViewModel
    {
        get => (CustomLayoutEditorViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public double PixelsPerMm { get; set; } = 4.0;

    private enum DragMode
    {
        None,
        PendingMove,
        Moving,
        Resizing,
        Marquee
    }

    private DragMode _dragMode = DragMode.None;
    private Point _dragStartPx;
    private ResizeHandle _activeHandle = ResizeHandle.None;
    private DesignerFrameItem? _resizeTarget;
    private LayoutRectMm _resizeOriginalRect;
    private List<(DesignerFrameItem Item, double OrigX, double OrigY)> _moveOriginals = [];
    private LayoutRectMm _moveOriginalBounds;
    private Point _marqueeCurrentPx;
    private IReadOnlyList<SnapGuideLine> _activeSnapGuides = [];
    private string? _hoveredFrameId;
    private bool _isLiveTransforming;

    private static readonly Typeface UiTypeface = new("Segoe UI, Microsoft YaHei UI");
    private static readonly Typeface BoldTypeface = new(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush CheckerBrush = CreateCheckerBrush();

    public LayoutDesignerCanvas()
    {
        Focusable = true;
        ClipToBounds = false;
    }

    private static Brush CreateCheckerBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(245, 247, 250)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var lightGray = new SolidColorBrush(Color.FromRgb(229, 233, 240));
        group.Children.Add(new GeometryDrawing(lightGray, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(lightGray, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (LayoutDesignerCanvas)d;
        if (e.OldValue is CustomLayoutEditorViewModel oldVm)
            oldVm.CanvasVisualChanged -= canvas.OnVmVisualChanged;
        if (e.NewValue is CustomLayoutEditorViewModel newVm)
            newVm.CanvasVisualChanged += canvas.OnVmVisualChanged;
        canvas.InvalidateVisual();
    }

    private void OnVmVisualChanged()
    {
        if (Dispatcher.CheckAccess())
            InvalidateVisual();
        else
            Dispatcher.BeginInvoke(InvalidateVisual);
    }

    // 扩展命中测试区域 8px，确保贴近纸张边缘的 8 个缩放控制柄也能顺畅抓到
    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var pt = hitTestParameters.HitPoint;
        if (pt.X >= -8 && pt.Y >= -8 && pt.X <= ActualWidth + 8 && pt.Y <= ActualHeight + 8)
            return new PointHitTestResult(this, pt);
        return null!;
    }

    private double ScaleX => ViewModel is { PaperWidthMm: > 0.1 } vm ? ActualWidth / vm.PaperWidthMm : PixelsPerMm;
    private double ScaleY => ViewModel is { PaperHeightMm: > 0.1 } vm ? ActualHeight / vm.PaperHeightMm : PixelsPerMm;

    private Rect FrameToPx(DesignerFrameItem f) =>
        new(f.XMm * ScaleX, f.YMm * ScaleY, Math.Max(2, f.WidthMm * ScaleX), Math.Max(2, f.HeightMm * ScaleY));

    private Point PxToMm(Point p) =>
        new(p.X / Math.Max(0.01, ScaleX), p.Y / Math.Max(0.01, ScaleY));

    // === 渲染 ===
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ViewModel is not { } vm || ActualWidth < 20 || ActualHeight < 20) return;

        double w = ActualWidth;
        double h = ActualHeight;
        double sx = ScaleX;
        double sy = ScaleY;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 1. 纸张底色与外框
        Brush paperBg = Brushes.White;
        if (!string.IsNullOrWhiteSpace(vm.CanvasBackgroundHex))
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(vm.CanvasBackgroundHex);
                var b = new SolidColorBrush(c);
                b.Freeze();
                paperBg = b;
            }
            catch { }
        }
        var paperBorderPen = new Pen(new SolidColorBrush(Color.FromRgb(148, 163, 184)), 1);
        paperBorderPen.Freeze();
        dc.DrawRectangle(paperBg, paperBorderPen, new Rect(0, 0, w, h));

        // 2. 可选毫米网格
        if (vm.ShowGrid && vm.GridStepMm >= 0.5)
        {
            DrawPaperGrid(dc, vm, w, h, sx, sy);
        }

        // 3. 硬件安全边距虚线框
        if (vm.ShowSafeMargin && vm.SafeMarginMm > 0.2)
        {
            double mx = vm.SafeMarginMm * sx;
            double my = vm.SafeMarginMm * sy;
            if (w - mx * 2 > 4 && h - my * 2 > 4)
            {
                var safePen = new Pen(new SolidColorBrush(Color.FromArgb(140, 239, 68, 68)), 1.0)
                {
                    DashStyle = new DashStyle([4, 3], 0)
                };
                safePen.Freeze();
                dc.DrawRectangle(null, safePen, new Rect(mx, my, w - mx * 2, h - my * 2));
            }
        }

        // 4. 逐个绘制图片框（按 Z 序从底到顶）
        foreach (var frame in vm.Frames)
        {
            DrawFrame(dc, vm, frame, sx, sy, dpi);
        }

        // 5. 单选时的 8 个缩放控制柄
        if (vm.IsSingleSelection && vm.PrimarySelectedFrame is { } selected)
        {
            DrawResizeHandles(dc, FrameToPx(selected));
        }

        // 6. 拖拽/缩放时的磁吸对齐辅助线
        if (_activeSnapGuides.Count > 0)
        {
            DrawSnapGuides(dc, _activeSnapGuides, sx, sy);
        }

        // 7. 空白处框选矩形
        if (_dragMode == DragMode.Marquee)
        {
            var r = MakeNormalizedRect(_dragStartPx, _marqueeCurrentPx);
            var fill = new SolidColorBrush(Color.FromArgb(36, 20, 115, 230));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(20, 115, 230)), 1.2)
            {
                DashStyle = new DashStyle([4, 2], 0)
            };
            fill.Freeze();
            pen.Freeze();
            dc.DrawRectangle(fill, pen, r);
        }

        // 8. 拖拽/缩放实时尺寸坐标悬浮提示
        if (_isLiveTransforming && vm.PrimarySelectedFrame is { } active)
        {
            DrawLiveTransformHud(dc, FrameToPx(active), active, dpi);
        }
    }

    private static void DrawPaperGrid(DrawingContext dc, CustomLayoutEditorViewModel vm, double w, double h, double sx, double sy)
    {
        double step = vm.GridStepMm;
        // 避免过密：屏幕间隔小于 6px 时自动倍增显示步长
        while (step * sx < 6.0 && step < 50.0) step *= 2.0;

        var minorPen = new Pen(new SolidColorBrush(Color.FromArgb(38, 100, 116, 139)), 0.7);
        var majorPen = new Pen(new SolidColorBrush(Color.FromArgb(68, 100, 116, 139)), 0.9);
        minorPen.Freeze();
        majorPen.Freeze();

        double majorStep = step * 5.0;
        for (double xMm = step; xMm < vm.PaperWidthMm - 0.05; xMm += step)
        {
            double x = xMm * sx;
            bool isMajor = Math.Abs(xMm % majorStep) < 0.05 || Math.Abs(majorStep - (xMm % majorStep)) < 0.05;
            dc.DrawLine(isMajor ? majorPen : minorPen, new Point(x, 0), new Point(x, h));
        }
        for (double yMm = step; yMm < vm.PaperHeightMm - 0.05; yMm += step)
        {
            double y = yMm * sy;
            bool isMajor = Math.Abs(yMm % majorStep) < 0.05 || Math.Abs(majorStep - (yMm % majorStep)) < 0.05;
            dc.DrawLine(isMajor ? majorPen : minorPen, new Point(0, y), new Point(w, y));
        }
    }

    private void DrawFrame(DrawingContext dc, CustomLayoutEditorViewModel vm, DesignerFrameItem frame, double sx, double sy, double dpi)
    {
        var rect = FrameToPx(frame);
        double cornerR = Math.Min(frame.CornerRadiusMm * sx, Math.Min(rect.Width, rect.Height) * 0.48);

        // 出血预览虚线
        if (frame.BleedMm > 0.05)
        {
            double bx = frame.BleedMm * sx;
            double by = frame.BleedMm * sy;
            var bleedRect = new Rect(rect.X - bx, rect.Y - by, rect.Width + bx * 2, rect.Height + by * 2);
            var bleedPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 59, 130, 246)), 1.0)
            {
                DashStyle = new DashStyle([3, 3], 0)
            };
            bleedPen.Freeze();
            dc.DrawRectangle(null, bleedPen, bleedRect);
        }

        // 框内部填充（棋盘格 + 可选半透明底图预览 + 拍立得下巴示意）
        var clipGeom = cornerR > 0.5
            ? new RectangleGeometry(rect, cornerR, cornerR)
            : new RectangleGeometry(rect);
        dc.PushClip(clipGeom);

        dc.DrawRectangle(CheckerBrush, null, rect);

        if (frame.PolaroidStyle != null)
        {
            DrawPolaroidInnerPreview(dc, vm, frame, rect, sx, sy);
        }
        else if (vm.ShowSampleOverlay && vm.SamplePreviewImage != null)
        {
            DrawSamplePhotoInRect(dc, vm.SamplePreviewImage, rect, frame.SlotRotationDegrees, frame.FitModeOverride);
        }
        else
        {
            var tint = frame.IsSelected
                ? new SolidColorBrush(Color.FromArgb(46, 59, 130, 246))
                : new SolidColorBrush(Color.FromArgb(26, 100, 116, 139));
            tint.Freeze();
            dc.DrawRectangle(tint, null, rect);
        }

        // 选中时绘制柔和的十字中心参考线（对应参考图红中线）
        if (frame.IsSelected)
        {
            var centerLinePen = new Pen(new SolidColorBrush(Color.FromArgb(150, 239, 68, 68)), 1.0);
            centerLinePen.Freeze();
            dc.DrawLine(centerLinePen, new Point(rect.X + rect.Width * 0.5, rect.Y), new Point(rect.X + rect.Width * 0.5, rect.Bottom));
            dc.DrawLine(centerLinePen, new Point(rect.X, rect.Y + rect.Height * 0.5), new Point(rect.Right, rect.Y + rect.Height * 0.5));
        }

        // 方向箭头 + 居中编号徽章（复刻打印排版工具直观朝向指示）
        DrawOrientationArrowAndBadge(dc, rect, frame, dpi);

        // 角落尺寸标签（框足够大时显示）
        if (rect.Width >= 58 && rect.Height >= 36)
        {
            var dimFt = new FormattedText($"{frame.WidthMm:0.#}×{frame.HeightMm:0.#}",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, 9.5, Brushes.White, dpi);
            var tagRect = new Rect(rect.X + 4, rect.Y + 4, dimFt.Width + 8, dimFt.Height + 3);
            var tagBg = new SolidColorBrush(Color.FromArgb(165, 15, 23, 42));
            tagBg.Freeze();
            dc.DrawRoundedRectangle(tagBg, null, tagRect, 2.5, 2.5);
            dc.DrawText(dimFt, new Point(tagRect.X + 4, tagRect.Y + 1.5));
        }

        dc.Pop();

        // 外边框
        Color borderColor;
        double borderThickness;
        if (frame.IsOutOfBounds)
        {
            borderColor = Color.FromRgb(220, 38, 38);
            borderThickness = 2.2;
        }
        else if (frame.IsSelected)
        {
            borderColor = Color.FromRgb(20, 115, 230);
            borderThickness = 2.0;
        }
        else if (frame.Id == _hoveredFrameId)
        {
            borderColor = Color.FromRgb(59, 130, 246);
            borderThickness = 1.6;
        }
        else
        {
            borderColor = Color.FromRgb(100, 116, 139);
            borderThickness = 1.2;
        }

        var borderPen = new Pen(new SolidColorBrush(borderColor), borderThickness);
        borderPen.Freeze();
        if (cornerR > 0.5)
            dc.DrawRoundedRectangle(null, borderPen, rect, cornerR, cornerR);
        else
            dc.DrawRectangle(null, borderPen, rect);

        // 重叠提示橙色虚线
        if (frame.IsOverlapping && !frame.IsOutOfBounds)
        {
            var overlapPen = new Pen(new SolidColorBrush(Color.FromRgb(217, 119, 6)), 1.4)
            {
                DashStyle = new DashStyle([4, 3], 0)
            };
            overlapPen.Freeze();
            dc.DrawRectangle(null, overlapPen, new Rect(rect.X + 1.5, rect.Y + 1.5, Math.Max(1, rect.Width - 3), Math.Max(1, rect.Height - 3)));
        }

        // 红色底边标识线（明确标示当前旋转角度下照片的底边方向，复刻参考图）
        DrawPhotoBottomEdgeIndicator(dc, rect, frame.SlotRotationDegrees);
    }

    private static void DrawPolaroidInnerPreview(DrawingContext dc, CustomLayoutEditorViewModel vm, DesignerFrameItem frame, Rect rect, double sx, double sy)
    {
        var p = frame.PolaroidStyle!;
        var frameBg = Brushes.White;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(p.FrameColorHex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            frameBg = b;
        }
        catch { }

        dc.DrawRectangle(frameBg, null, rect);

        bool quarter = LayoutSlot.IsQuarterTurnRotation(frame.SlotRotationDegrees);
        double cx = rect.X + rect.Width * 0.5;
        double cy = rect.Y + rect.Height * 0.5;
        double cardW = quarter ? rect.Height : rect.Width;
        double cardH = quarter ? rect.Width : rect.Height;
        double scale = (quarter ? sy : sx);
        double mL = p.MarginLeftMm * scale;
        double mR = p.MarginRightMm * scale;
        double mT = p.MarginTopMm * scale;
        double mB = p.MarginBottomMm * scale;

        var inner = new Rect(
            cx - cardW * 0.5 + mL,
            cy - cardH * 0.5 + mT,
            Math.Max(2, cardW - mL - mR),
            Math.Max(2, cardH - mT - mB));

        dc.PushTransform(new RotateTransform(frame.SlotRotationDegrees, cx, cy));
        dc.DrawRectangle(CheckerBrush, null, inner);
        if (vm.ShowSampleOverlay && vm.SamplePreviewImage != null)
        {
            dc.PushClip(new RectangleGeometry(inner));
            DrawSamplePhotoInRect(dc, vm.SamplePreviewImage, inner, 0, frame.FitModeOverride);
            dc.Pop();
        }
        var innerPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 100, 116, 139)), 0.8);
        innerPen.Freeze();
        dc.DrawRectangle(null, innerPen, inner);
        dc.Pop();
    }

    private static void DrawSamplePhotoInRect(DrawingContext dc, ImageSource img, Rect rect, double rotationDegrees, FitMode? fitMode)
    {
        double iw = img.Width;
        double ih = img.Height;
        if (iw <= 0 || ih <= 0 || rect.Width <= 1 || rect.Height <= 1) return;

        bool quarter = LayoutSlot.IsQuarterTurnRotation(rotationDegrees);
        double targetW = quarter ? rect.Height : rect.Width;
        double targetH = quarter ? rect.Width : rect.Height;
        double scale = fitMode == FitMode.Contain ? Math.Min(targetW / iw, targetH / ih) : Math.Max(targetW / iw, targetH / ih);
        double drawW = iw * scale;
        double drawH = ih * scale;
        double cx = rect.X + rect.Width * 0.5;
        double cy = rect.Y + rect.Height * 0.5;

        dc.PushOpacity(0.58);
        dc.PushTransform(new RotateTransform(rotationDegrees, cx, cy));
        dc.DrawImage(img, new Rect(cx - drawW * 0.5, cy - drawH * 0.5, drawW, drawH));
        dc.Pop();
        dc.Pop();
    }

    private static void DrawOrientationArrowAndBadge(DrawingContext dc, Rect rect, DesignerFrameItem frame, double dpi)
    {
        double cx = rect.X + rect.Width * 0.5;
        double cy = rect.Y + rect.Height * 0.5;
        double rot = DesignerFrameItem.NormalizeRotation(frame.SlotRotationDegrees);

        // 箭头指向“照片顶部”（0° 向上，90° 向右，180° 向下，270° 向左）
        double dirX = 0, dirY = -1;
        if (Math.Abs(rot - 90) < 1) { dirX = 1; dirY = 0; }
        else if (Math.Abs(rot - 180) < 1) { dirX = 0; dirY = 1; }
        else if (Math.Abs(rot - 270) < 1) { dirX = -1; dirY = 0; }

        double span = Math.Abs(dirX) > 0.5 ? rect.Width : rect.Height;
        if (span >= 28)
        {
            double halfLen = span * 0.44;
            var start = new Point(cx - dirX * halfLen, cy - dirY * halfLen);
            var end = new Point(cx + dirX * halfLen, cy + dirY * halfLen);

            var arrowColor = frame.IsSelected
                ? Color.FromArgb(195, 99, 102, 241)
                : Color.FromArgb(135, 100, 116, 139);
            var arrowBrush = new SolidColorBrush(arrowColor);
            arrowBrush.Freeze();
            var arrowPen = new Pen(arrowBrush, frame.IsSelected ? 2.4 : 1.6);
            arrowPen.Freeze();

            dc.DrawLine(arrowPen, start, end);

            // 实心箭头头部
            double headLen = Math.Clamp(span * 0.12, 6, 11);
            double headHalfW = headLen * 0.55;
            double perpX = -dirY;
            double perpY = dirX;
            var tip = end;
            var baseCenter = new Point(end.X - dirX * headLen, end.Y - dirY * headLen);
            var pLeft = new Point(baseCenter.X + perpX * headHalfW, baseCenter.Y + perpY * headHalfW);
            var pRight = new Point(baseCenter.X - perpX * headHalfW, baseCenter.Y - perpY * headHalfW);

            var headGeom = new StreamGeometry();
            using (var ctx = headGeom.Open())
            {
                ctx.BeginFigure(tip, isFilled: true, isClosed: true);
                ctx.LineTo(pLeft, true, false);
                ctx.LineTo(pRight, true, false);
            }
            headGeom.Freeze();
            dc.DrawGeometry(arrowBrush, null, headGeom);
        }

        // 中心编号圆角徽章
        string numText = $"{frame.Index + 1}";
        var ft = new FormattedText(numText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            BoldTypeface, 11, Brushes.White, dpi);
        double badgeW = Math.Max(20, ft.Width + 10);
        double badgeH = Math.Max(18, ft.Height + 4);
        var badgeRect = new Rect(cx - badgeW * 0.5, cy - badgeH * 0.5, badgeW, badgeH);
        var badgeBg = new SolidColorBrush(frame.IsSelected
            ? Color.FromArgb(230, 20, 115, 230)
            : Color.FromArgb(185, 30, 41, 59));
        badgeBg.Freeze();
        dc.DrawRoundedRectangle(badgeBg, null, badgeRect, badgeH * 0.5, badgeH * 0.5);
        dc.DrawText(ft, new Point(cx - ft.Width * 0.5, cy - ft.Height * 0.5));
    }

    private static void DrawPhotoBottomEdgeIndicator(DrawingContext dc, Rect rect, double rotationDegrees)
    {
        double rot = DesignerFrameItem.NormalizeRotation(rotationDegrees);
        var redPen = new Pen(new SolidColorBrush(Color.FromRgb(220, 38, 38)), 2.5);
        redPen.Freeze();

        if (Math.Abs(rot) < 1)
            dc.DrawLine(redPen, new Point(rect.Left, rect.Bottom), new Point(rect.Right, rect.Bottom));
        else if (Math.Abs(rot - 90) < 1)
            dc.DrawLine(redPen, new Point(rect.Left, rect.Top), new Point(rect.Left, rect.Bottom));
        else if (Math.Abs(rot - 180) < 1)
            dc.DrawLine(redPen, new Point(rect.Left, rect.Top), new Point(rect.Right, rect.Top));
        else if (Math.Abs(rot - 270) < 1)
            dc.DrawLine(redPen, new Point(rect.Right, rect.Top), new Point(rect.Right, rect.Bottom));
    }

    private static void DrawResizeHandles(DrawingContext dc, Rect rect)
    {
        const double size = 8.0;
        const double half = size * 0.5;
        var fill = Brushes.White;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(20, 115, 230)), 1.5);
        pen.Freeze();

        foreach (var (_, pt) in GetHandlePoints(rect))
        {
            dc.DrawRectangle(fill, pen, new Rect(pt.X - half, pt.Y - half, size, size));
        }
    }

    private static (ResizeHandle Handle, Point Pt)[] GetHandlePoints(Rect r) =>
    [
        (ResizeHandle.TopLeft, new Point(r.Left, r.Top)),
        (ResizeHandle.Top, new Point(r.Left + r.Width * 0.5, r.Top)),
        (ResizeHandle.TopRight, new Point(r.Right, r.Top)),
        (ResizeHandle.Right, new Point(r.Right, r.Top + r.Height * 0.5)),
        (ResizeHandle.BottomRight, new Point(r.Right, r.Bottom)),
        (ResizeHandle.Bottom, new Point(r.Left + r.Width * 0.5, r.Bottom)),
        (ResizeHandle.BottomLeft, new Point(r.Left, r.Bottom)),
        (ResizeHandle.Left, new Point(r.Left, r.Top + r.Height * 0.5))
    ];

    private static void DrawSnapGuides(DrawingContext dc, IReadOnlyList<SnapGuideLine> guides, double sx, double sy)
    {
        foreach (var g in guides)
        {
            Color color = g.Kind switch
            {
                SnapGuideKind.PaperCenter => Color.FromRgb(236, 72, 153),
                SnapGuideKind.SafeMargin => Color.FromRgb(239, 68, 68),
                _ => Color.FromRgb(14, 165, 233)
            };
            var pen = new Pen(new SolidColorBrush(color), 1.2)
            {
                DashStyle = new DashStyle([4, 3], 0)
            };
            pen.Freeze();

            if (g.IsVertical)
            {
                double x = g.PositionMm * sx;
                dc.DrawLine(pen, new Point(x, g.StartMm * sy), new Point(x, g.EndMm * sy));
            }
            else
            {
                double y = g.PositionMm * sy;
                dc.DrawLine(pen, new Point(g.StartMm * sx, y), new Point(g.EndMm * sx, y));
            }
        }
    }

    private void DrawLiveTransformHud(DrawingContext dc, Rect framePx, DesignerFrameItem frame, double dpi)
    {
        string text = $"X:{frame.XMm:0.#}  Y:{frame.YMm:0.#}  |  {frame.WidthMm:0.#} × {frame.HeightMm:0.#} mm";
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            BoldTypeface, 10.5, Brushes.White, dpi);
        double boxW = ft.Width + 12;
        double boxH = ft.Height + 6;
        double bx = Math.Clamp(framePx.X + (framePx.Width - boxW) * 0.5, 4, Math.Max(4, ActualWidth - boxW - 4));
        double by = framePx.Y >= boxH + 10 ? framePx.Y - boxH - 8 : Math.Min(ActualHeight - boxH - 4, framePx.Bottom + 8);

        var bg = new SolidColorBrush(Color.FromArgb(225, 15, 23, 42));
        bg.Freeze();
        dc.DrawRoundedRectangle(bg, null, new Rect(bx, by, boxW, boxH), 4, 4);
        dc.DrawText(ft, new Point(bx + 6, by + 3));
    }

    // === 命中检测 ===
    private ResizeHandle HitTestHandle(Point px)
    {
        if (ViewModel is not { IsSingleSelection: true, PrimarySelectedFrame: { } selected })
            return ResizeHandle.None;

        var rect = FrameToPx(selected);
        const double hitRadius = 7.5;
        foreach (var (handle, pt) in GetHandlePoints(rect))
        {
            if (Math.Abs(px.X - pt.X) <= hitRadius && Math.Abs(px.Y - pt.Y) <= hitRadius)
                return handle;
        }
        return ResizeHandle.None;
    }

    private DesignerFrameItem? HitTestFrame(Point px)
    {
        if (ViewModel is not { } vm) return null;
        for (int i = vm.Frames.Count - 1; i >= 0; i--)
        {
            var f = vm.Frames[i];
            if (FrameToPx(f).Contains(px))
                return f;
        }
        return null;
    }

    private static Cursor CursorForHandle(ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
        ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
        ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
        ResizeHandle.Left or ResizeHandle.Right => Cursors.SizeWE,
        _ => Cursors.Arrow
    };

    // === 鼠标交互 ===
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ViewModel is not { } vm) return;
        Focus();

        var pos = e.GetPosition(this);
        _activeSnapGuides = [];

        // 1. 优先检测缩放控制柄
        var handle = HitTestHandle(pos);
        if (handle != ResizeHandle.None && vm.PrimarySelectedFrame is { } resizeFrame)
        {
            _dragMode = DragMode.Resizing;
            _activeHandle = handle;
            _resizeTarget = resizeFrame;
            _resizeOriginalRect = resizeFrame.ToRect();
            _dragStartPx = pos;
            _isLiveTransforming = true;
            vm.BeginInteractiveTransform();
            CaptureMouse();
            Cursor = CursorForHandle(handle);
            e.Handled = true;
            return;
        }

        // 2. 检测点击图片框
        var hit = HitTestFrame(pos);
        if (hit != null)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                vm.ToggleFrameSelection(hit);
                e.Handled = true;
                return;
            }

            if (!hit.IsSelected)
                vm.SelectSingleFrame(hit);
            else
            {
                vm.PrimarySelectedFrame = hit;
                vm.SyncInspectorFromPrimary();
            }

            var selected = vm.GetSelectedFrames();
            _moveOriginals = selected.Select(f => (f, f.XMm, f.YMm)).ToList();
            _moveOriginalBounds = FreeformLayoutMath.GetBoundingBox(selected.Select(f => f.ToRect()));
            _dragStartPx = pos;
            _dragMode = DragMode.PendingMove;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // 3. 双击空白处直接在该位置添加新图片框
        if (e.ClickCount == 2)
        {
            var mm = PxToMm(pos);
            if (mm.X >= 0 && mm.X <= vm.PaperWidthMm && mm.Y >= 0 && mm.Y <= vm.PaperHeightMm)
                vm.AddFrameAt(mm.X, mm.Y);
            e.Handled = true;
            return;
        }

        // 4. 单击空白处：清除选中并准备框选
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            vm.ClearSelection();
        _dragStartPx = pos;
        _marqueeCurrentPx = pos;
        _dragMode = DragMode.Marquee;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (ViewModel is not { } vm) return;

        var pos = e.GetPosition(this);
        var mm = PxToMm(pos);
        vm.PointerCoordText = $"鼠标 X: {Math.Clamp(mm.X, 0, vm.PaperWidthMm):0.0} mm, Y: {Math.Clamp(mm.Y, 0, vm.PaperHeightMm):0.0} mm";

        if (_dragMode == DragMode.Resizing && _resizeTarget != null)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishDrag();
                return;
            }

            double dxMm = (pos.X - _dragStartPx.X) / Math.Max(0.01, ScaleX);
            double dyMm = (pos.Y - _dragStartPx.Y) / Math.Max(0.01, ScaleY);
            bool bypassSnap = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            bool lockRatio = vm.LockAspectRatio ^ Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var others = vm.Frames.Where(f => !ReferenceEquals(f, _resizeTarget)).Select(f => f.ToRect()).ToList();

            var res = FreeformLayoutMath.CalculateResizeSnap(
                _resizeOriginalRect,
                _activeHandle,
                dxMm, dyMm,
                vm.PaperWidthMm, vm.PaperHeightMm,
                others,
                lockAspectRatio: lockRatio,
                enableMagneticSnap: vm.EnableMagneticSnap && !bypassSnap,
                enableGridSnap: vm.EnableGridSnap && !bypassSnap,
                gridStepMm: vm.GridStepMm,
                safeMarginMm: vm.ShowSafeMargin ? vm.SafeMarginMm : 0,
                snapToleranceMm: Math.Clamp(6.0 / Math.Max(0.5, ScaleX), 0.8, 3.0));

            _resizeTarget.XMm = res.Rect.X;
            _resizeTarget.YMm = res.Rect.Y;
            _resizeTarget.WidthMm = res.Rect.Width;
            _resizeTarget.HeightMm = res.Rect.Height;
            _activeSnapGuides = res.Guides;
            vm.MarkInteractiveMutated();
            InvalidateVisual();
            return;
        }

        if (_dragMode is DragMode.PendingMove or DragMode.Moving)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishDrag();
                return;
            }

            if (_dragMode == DragMode.PendingMove)
            {
                if (Math.Abs(pos.X - _dragStartPx.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - _dragStartPx.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;

                _dragMode = DragMode.Moving;
                _isLiveTransforming = true;
                vm.BeginInteractiveTransform();
                Cursor = Cursors.SizeAll;
            }

            double dxMm = (pos.X - _dragStartPx.X) / Math.Max(0.01, ScaleX);
            double dyMm = (pos.Y - _dragStartPx.Y) / Math.Max(0.01, ScaleY);
            bool bypassSnap = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            var movingSet = _moveOriginals.Select(m => m.Item).ToHashSet();
            var others = vm.Frames.Where(f => !movingSet.Contains(f)).Select(f => f.ToRect()).ToList();

            var snap = FreeformLayoutMath.CalculateMoveSnap(
                _moveOriginalBounds,
                dxMm, dyMm,
                vm.PaperWidthMm, vm.PaperHeightMm,
                others,
                enableMagneticSnap: vm.EnableMagneticSnap && !bypassSnap,
                enableGridSnap: vm.EnableGridSnap && !bypassSnap,
                gridStepMm: vm.GridStepMm,
                safeMarginMm: vm.ShowSafeMargin ? vm.SafeMarginMm : 0,
                snapToleranceMm: Math.Clamp(6.0 / Math.Max(0.5, ScaleX), 0.8, 3.0));

            foreach (var (item, origX, origY) in _moveOriginals)
            {
                item.XMm = Math.Round(origX + snap.DeltaXMm, 2);
                item.YMm = Math.Round(origY + snap.DeltaYMm, 2);
            }
            _activeSnapGuides = snap.Guides;
            vm.MarkInteractiveMutated();
            InvalidateVisual();
            return;
        }

        if (_dragMode == DragMode.Marquee)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishDrag();
                return;
            }
            _marqueeCurrentPx = pos;
            InvalidateVisual();
            return;
        }

        // 悬停光标与高亮反馈
        var handle = HitTestHandle(pos);
        if (handle != ResizeHandle.None)
        {
            Cursor = CursorForHandle(handle);
            UpdateHover(null);
        }
        else
        {
            var hovered = HitTestFrame(pos);
            Cursor = hovered != null ? Cursors.SizeAll : Cursors.Arrow;
            UpdateHover(hovered?.Id);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragMode == DragMode.PendingMove && ViewModel is { SelectedCount: > 1 } vm &&
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var clicked = HitTestFrame(e.GetPosition(this));
            if (clicked != null)
                vm.SelectSingleFrame(clicked);
        }
        else if (_dragMode == DragMode.Marquee && ViewModel is { } vm2)
        {
            var selRect = MakeNormalizedRect(_dragStartPx, _marqueeCurrentPx);
            if (selRect.Width >= 4 || selRect.Height >= 4)
            {
                var hits = vm2.Frames.Where(f => FrameToPx(f).IntersectsWith(selRect)).ToList();
                if (hits.Count > 0)
                    vm2.SetSelectedFrames(hits, hits[^1]);
            }
        }
        FinishDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        FinishDrag();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (ViewModel != null) ViewModel.PointerCoordText = "";
        if (_dragMode == DragMode.None)
            UpdateHover(null);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (ViewModel is not { } vm) return;
        Focus();

        var pos = e.GetPosition(this);
        var hit = HitTestFrame(pos);
        if (hit != null && !hit.IsSelected)
            vm.SelectSingleFrame(hit);

        ShowContextMenu(vm, pos, hit != null || vm.HasSelection);
        e.Handled = true;
    }

    private void ShowContextMenu(CustomLayoutEditorViewModel vm, Point posPx, bool hasFrameTarget)
    {
        var menu = new ContextMenu();
        var mm = PxToMm(posPx);

        var addHere = new MenuItem { Header = "在此处添加图片框" };
        addHere.Click += (_, _) => vm.AddFrameAt(mm.X, mm.Y);
        menu.Items.Add(addHere);

        if (hasFrameTarget)
        {
            menu.Items.Add(new Separator());
            var dup = new MenuItem { Header = "复制图片框", InputGestureText = "Ctrl+D", Command = vm.DuplicateSelectedFramesCommand };
            var rot = new MenuItem { Header = "旋转照片方向 90°", InputGestureText = "R", Command = vm.RotateSelected90Command };
            var swap = new MenuItem { Header = "交换宽和高", Command = vm.SwapSelectedWidthHeightCommand };
            var center = new MenuItem { Header = "在纸张中居中", Command = vm.CenterOnPageCommand };
            var fitPaper = new MenuItem { Header = "铺满整张纸", Command = vm.FitSelectedToPaperCommand };
            menu.Items.Add(dup);
            menu.Items.Add(rot);
            menu.Items.Add(swap);
            menu.Items.Add(center);
            menu.Items.Add(fitPaper);

            if (vm.Frames.Count > 1 && vm.IsSingleSelection)
            {
                menu.Items.Add(new Separator());
                var front = new MenuItem { Header = "移至最顶层" };
                front.Click += (_, _) => vm.BringSelectedToFront();
                var back = new MenuItem { Header = "移至最底层" };
                back.Click += (_, _) => vm.SendSelectedToBack();
                menu.Items.Add(front);
                menu.Items.Add(back);
            }

            menu.Items.Add(new Separator());
            var del = new MenuItem { Header = "删除图片框", InputGestureText = "Delete", Command = vm.DeleteSelectedFramesCommand };
            menu.Items.Add(del);
        }

        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    private void UpdateHover(string? nextId)
    {
        if (_hoveredFrameId == nextId) return;
        _hoveredFrameId = nextId;
        InvalidateVisual();
    }

    private void FinishDrag()
    {
        if (_dragMode == DragMode.None) return;
        bool wasTransforming = _isLiveTransforming;
        _dragMode = DragMode.None;
        _activeHandle = ResizeHandle.None;
        _resizeTarget = null;
        _moveOriginals.Clear();
        _activeSnapGuides = [];
        _isLiveTransforming = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        if (wasTransforming)
            ViewModel?.EndInteractiveTransform();
        InvalidateVisual();
    }

    private static Rect MakeNormalizedRect(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
