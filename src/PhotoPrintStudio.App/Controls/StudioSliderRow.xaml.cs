using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoPrintStudio.App.Controls;

public partial class StudioSliderRow : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(StudioSliderRow), new PropertyMetadata("参数"));

    public static readonly DependencyProperty LabelWidthProperty =
        DependencyProperty.Register(nameof(LabelWidth), typeof(GridLength), typeof(StudioSliderRow), new PropertyMetadata(new GridLength(96)));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(StudioSliderRow), new PropertyMetadata(-100.0));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(StudioSliderRow), new PropertyMetadata(100.0));

    public static readonly DependencyProperty DefaultValueProperty =
        DependencyProperty.Register(nameof(DefaultValue), typeof(double), typeof(StudioSliderRow),
            new PropertyMetadata(0.0, (d, _) => ((StudioSliderRow)d).UpdateBadge()));

    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(double), typeof(StudioSliderRow), new PropertyMetadata(1.0));

    public static readonly DependencyProperty ValueFormatProperty =
        DependencyProperty.Register(nameof(ValueFormat), typeof(string), typeof(StudioSliderRow),
            new PropertyMetadata("0", (d, _) => ((StudioSliderRow)d).UpdateBadge()));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(StudioSliderRow),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public GridLength LabelWidth { get => (GridLength)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double DefaultValue { get => (double)GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string ValueFormat { get => (string)GetValue(ValueFormatProperty); set => SetValue(ValueFormatProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public event EventHandler? EditStarted;
    public event EventHandler? EditCompleted;
    public event EventHandler? ValueAdjusted;

    private bool _isDragging;
    private Key? _adjustmentKey;

    public StudioSliderRow()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateBadge();
        UpdateBadge();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (StudioSliderRow)d;
        row.UpdateBadge();
        row.ValueAdjusted?.Invoke(row, EventArgs.Empty);
    }

    public void RefreshVisualState() => UpdateBadge();

    private void UpdateBadge()
    {
        if (ValueBadge == null) return;
        double v = Value;
        string fmt = ValueFormat ?? "0";
        string text = v.ToString(fmt);
        if (Minimum < 0 && v > 0.0001 && !text.StartsWith('+'))
            text = "+" + text;
        ValueBadge.Text = text;

        bool isModified = Math.Abs(v - DefaultValue) > 0.005;
        ValueBadge.SetResourceReference(TextBlock.ForegroundProperty, isModified ? "BrushAccent" : "BrushTextPrimary");
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ResetToDefault();
    }

    private void Badge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ResetToDefault();
            e.Handled = true;
            return;
        }
        BeginBadgeEdit();
        e.Handled = true;
    }

    private void Badge_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ResetToDefault();
        e.Handled = true;
    }

    private void BeginBadgeEdit()
    {
        if (BadgeEditor == null || ValueBadge == null) return;
        string fmt = ValueFormat ?? "0";
        BadgeEditor.Text = Value.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
        ValueBadge.Visibility = Visibility.Collapsed;
        BadgeEditor.Visibility = Visibility.Visible;
        BadgeEditor.Focus();
        BadgeEditor.SelectAll();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!PartSlider.IsKeyboardFocusWithin && !BadgeBorder.IsKeyboardFocused) return;
        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (modifiers == ModifierKeys.Alt && key == Key.Back)
        {
            if (!e.IsRepeat) ResetToDefault();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Enter or Key.F2)
        {
            BeginBadgeEdit();
            e.Handled = true;
            return;
        }
        int direction = e.Key switch
        {
            Key.Up or Key.Right => 1,
            Key.Down or Key.Left => -1,
            _ => 0
        };
        if (direction == 0 || (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Shift)) return;
        e.Handled = true;
        double value = Math.Clamp(Math.Round(Value + direction * Step * (modifiers == ModifierKeys.Shift ? 10 : 1), 8),
            Minimum, Maximum);
        if (value == Value) return;
        if (_adjustmentKey != e.Key)
        {
            FinishKeyboardEdit();
            _adjustmentKey = e.Key;
            EditStarted?.Invoke(this, EventArgs.Empty);
        }
        SetCurrentValue(ValueProperty, value);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (_adjustmentKey == e.Key) FinishKeyboardEdit();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        FinishKeyboardEdit();
    }

    private void FinishKeyboardEdit()
    {
        if (_adjustmentKey == null) return;
        _adjustmentKey = null;
        EditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void EndBadgeEdit(bool commit)
    {
        if (BadgeEditor == null || ValueBadge == null || BadgeEditor.Visibility != Visibility.Visible) return;
        string raw = BadgeEditor.Text;
        BadgeEditor.Visibility = Visibility.Collapsed;
        ValueBadge.Visibility = Visibility.Visible;

        if (!commit) return;
        if ((double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) ||
            double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out parsed)) && !double.IsNaN(parsed))
        {
            double clamped = Math.Clamp(parsed, Minimum, Maximum);
            EditStarted?.Invoke(this, EventArgs.Empty);
            SetCurrentValue(ValueProperty, clamped);
            EditCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void BadgeEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            EndBadgeEdit(commit: true);
            PartSlider.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndBadgeEdit(commit: false);
            PartSlider.Focus();
            e.Handled = true;
        }
    }

    private void BadgeEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => EndBadgeEdit(commit: true);

    internal void CommitPendingValue()
    {
        EndBadgeEdit(commit: true);
        PartSlider.Focus();
    }

    private void PartSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ResetToDefault();

    private void ResetToDefault()
    {
        FinishKeyboardEdit();
        if (Value == DefaultValue) return;
        EditStarted?.Invoke(this, EventArgs.Empty);
        SetCurrentValue(ValueProperty, DefaultValue);
        EditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void PartSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            _isDragging = true;
            EditStarted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PartSlider_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (_isDragging) e.Handled = true;
    }

    private void PartSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndMouseEdit();

    private void PartSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!PartSlider.IsMouseCaptureWithin) EndMouseEdit();
    }

    private void EndMouseEdit()
    {
        if (_isDragging)
        {
            _isDragging = false;
            EditCompleted?.Invoke(this, EventArgs.Empty);
        }
    }
}
