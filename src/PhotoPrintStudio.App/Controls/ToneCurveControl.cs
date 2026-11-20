using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.Controls;

public sealed class ToneCurveControl : FrameworkElement
{
    public static readonly DependencyProperty CurvesProperty =
        DependencyProperty.Register(nameof(Curves), typeof(ToneCurveData), typeof(ToneCurveControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActiveChannelProperty =
        DependencyProperty.Register(nameof(ActiveChannel), typeof(int), typeof(ToneCurveControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public ToneCurveData? Curves
    {
        get => (ToneCurveData?)GetValue(CurvesProperty);
        set => SetValue(CurvesProperty, value);
    }

    /// <summary>0=Master, 1=Red, 2=Green, 3=Blue</summary>
    public int ActiveChannel
    {
        get => (int)GetValue(ActiveChannelProperty);
        set => SetValue(ActiveChannelProperty, value);
    }

    public event EventHandler? CurveChanged;
    public event EventHandler? CurveEditStarted;
    public event EventHandler? CurveEditCompleted;

    private static readonly Brush FallbackBgBrush = Freeze(new SolidColorBrush(Color.FromRgb(24, 26, 32)));
    private static readonly Brush FallbackBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(62, 68, 84)));
    private static readonly Pen GridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(45, 148, 163, 184))), 1));
    private static readonly Pen DiagPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(80, 148, 163, 184))), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen PointBorderPen = Freeze(new Pen(Brushes.White, 1.5));
    private static readonly Brush RedBrush = Freeze(new SolidColorBrush(Color.FromRgb(239, 68, 68)));
    private static readonly Brush GreenBrush = Freeze(new SolidColorBrush(Color.FromRgb(34, 197, 94)));
    private static readonly Brush BlueBrush = Freeze(new SolidColorBrush(Color.FromRgb(59, 130, 246)));
    private static readonly Brush MasterDarkBrush = Freeze(new SolidColorBrush(Color.FromRgb(226, 232, 240)));
    private static readonly Brush MasterLightBrush = Freeze(new SolidColorBrush(Color.FromRgb(30, 41, 59)));
    private static readonly Pen RedPen = Freeze(new Pen(RedBrush, 2.0));
    private static readonly Pen GreenPen = Freeze(new Pen(GreenBrush, 2.0));
    private static readonly Pen BluePen = Freeze(new Pen(BlueBrush, 2.0));
    private static readonly Pen MasterDarkPen = Freeze(new Pen(MasterDarkBrush, 2.0));
    private static readonly Pen MasterLightPen = Freeze(new Pen(MasterLightBrush, 2.0));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }

    private int _dragIndex = -1;
    private bool _hasStartedDrag;

    public ToneCurveControl()
    {
        Cursor = Cursors.Cross;
        ClipToBounds = true;
    }

    private List<CurvePoint> GetActivePoints()
    {
        Curves ??= new ToneCurveData();
        return ActiveChannel switch
        {
            1 => Curves.Red,
            2 => Curves.Green,
            3 => Curves.Blue,
            _ => Curves.Master
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 12 || h <= 12) return;

        const double pad = 8;
        double innerW = w - pad * 2;
        double innerH = h - pad * 2;

        var bg = TryFindResource("BrushBgInput") as Brush ?? FallbackBgBrush;
        var border = TryFindResource("BrushBorderStrong") as Brush ?? FallbackBorderBrush;
        var borderPen = new Pen(border, 1);
        if (borderPen.CanFreeze) borderPen.Freeze();
        dc.DrawRoundedRectangle(bg, borderPen, new Rect(0, 0, w, h), 6, 6);

        // 4x4 辅助网格 + 对角基准线
        for (int i = 1; i <= 3; i++)
        {
            double gx = pad + innerW * i / 4.0;
            double gy = pad + innerH * i / 4.0;
            dc.DrawLine(GridPen, new Point(gx, pad), new Point(gx, pad + innerH));
            dc.DrawLine(GridPen, new Point(pad, gy), new Point(pad + innerW, gy));
        }

        dc.DrawLine(DiagPen, new Point(pad, pad + innerH), new Point(pad + innerW, pad));

        var pts = GetActivePoints();
        var lut = ToneCurveData.BuildLut(pts, 128);

        var (ptBrush, linePen) = ActiveChannel switch
        {
            1 => (RedBrush, RedPen),
            2 => (GreenBrush, GreenPen),
            3 => (BlueBrush, BluePen),
            _ => Themes.ThemeManager.IsDarkTheme ? (MasterDarkBrush, MasterDarkPen) : (MasterLightBrush, MasterLightPen)
        };

        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            for (int i = 0; i < lut.Length; i++)
            {
                double x = pad + (i / (double)(lut.Length - 1)) * innerW;
                double y = pad + (1.0 - lut[i]) * innerH;
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else ctx.LineTo(new Point(x, y), true, false);
            }
        }
        geom.Freeze();
        dc.DrawGeometry(null, linePen, geom);

        for (int i = 0; i < pts.Count; i++)
        {
            double cx = pad + pts[i].X * innerW;
            double cy = pad + (1.0 - pts[i].Y) * innerH;
            dc.DrawEllipse(i == _dragIndex ? Brushes.White : ptBrush, PointBorderPen, new Point(cx, cy), 4.5, 4.5);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            CurveEditStarted?.Invoke(this, EventArgs.Empty);
            var list = GetActivePoints();
            list.Clear();
            list.AddRange(ToneCurveData.DefaultLinear());
            InvalidateVisual();
            CurveChanged?.Invoke(this, EventArgs.Empty);
            CurveEditCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        var pos = e.GetPosition(this);
        (double nx, double ny) = ToNormalized(pos);
        var pts = GetActivePoints();

        int hit = FindNearestPointIndex(pos, 10.0);
        CurveEditStarted?.Invoke(this, EventArgs.Empty);
        _hasStartedDrag = true;

        if (hit >= 0)
        {
            _dragIndex = hit;
        }
        else
        {
            var newPt = new CurvePoint(nx, ny);
            pts.Add(newPt);
            pts.Sort((a, b) => a.X.CompareTo(b.X));
            _dragIndex = pts.IndexOf(newPt);
            InvalidateVisual();
            CurveChanged?.Invoke(this, EventArgs.Empty);
        }

        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex < 0 || !IsMouseCaptured) return;

        var pts = GetActivePoints();
        if (_dragIndex >= pts.Count) return;

        (double nx, double ny) = ToNormalized(e.GetPosition(this));
        double minX = _dragIndex == 0 ? 0.0 : pts[_dragIndex - 1].X + 0.015;
        double maxX = _dragIndex == pts.Count - 1 ? 1.0 : pts[_dragIndex + 1].X - 0.015;
        if (_dragIndex == 0) maxX = Math.Min(0.45, maxX);
        if (_dragIndex == pts.Count - 1) minX = Math.Max(0.55, minX);

        pts[_dragIndex].X = Math.Clamp(nx, minX, maxX);
        pts[_dragIndex].Y = Math.Clamp(ny, 0.0, 1.0);

        InvalidateVisual();
        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_dragIndex >= 0)
        {
            _dragIndex = -1;
            InvalidateVisual();
        }
        if (_hasStartedDrag)
        {
            _hasStartedDrag = false;
            CurveEditCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var pts = GetActivePoints();
        int hit = FindNearestPointIndex(e.GetPosition(this), 10.0);
        if (hit > 0 && hit < pts.Count - 1)
        {
            CurveEditStarted?.Invoke(this, EventArgs.Empty);
            pts.RemoveAt(hit);
            InvalidateVisual();
            CurveChanged?.Invoke(this, EventArgs.Empty);
            CurveEditCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private (double nx, double ny) ToNormalized(Point p)
    {
        const double pad = 8;
        double innerW = Math.Max(1, ActualWidth - pad * 2);
        double innerH = Math.Max(1, ActualHeight - pad * 2);
        double nx = Math.Clamp((p.X - pad) / innerW, 0.0, 1.0);
        double ny = Math.Clamp(1.0 - (p.Y - pad) / innerH, 0.0, 1.0);
        return (nx, ny);
    }

    private int FindNearestPointIndex(Point pos, double maxDist)
    {
        const double pad = 8;
        double innerW = Math.Max(1, ActualWidth - pad * 2);
        double innerH = Math.Max(1, ActualHeight - pad * 2);
        var pts = GetActivePoints();
        int best = -1;
        double bestDist = maxDist;
        for (int i = 0; i < pts.Count; i++)
        {
            double cx = pad + pts[i].X * innerW;
            double cy = pad + (1.0 - pts[i].Y) * innerH;
            double d = Math.Sqrt((pos.X - cx) * (pos.X - cx) + (pos.Y - cy) * (pos.Y - cy));
            if (d <= bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }
}
