using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoPrintStudio.App.Themes;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.Views;

public enum CustomLayoutApplyMode
{
    None,
    CurrentSheetOnly,
    SaveAndApply,
    SaveAsNewTemplate
}

public partial class CustomLayoutEditorWindow : Window
{
    private readonly CustomLayoutEditorViewModel _vm;
    private readonly bool _isDarkTheme;
    private double _manualZoom = 1.0;
    private bool _syncingListSelection;
    private bool _isMiddlePanning;
    private Point _middlePanStart;
    private double _middlePanStartH;
    private double _middlePanStartV;

    public CustomLayoutApplyMode ApplyMode { get; private set; } = CustomLayoutApplyMode.None;
    public LayoutTemplateDefinition? ResultTemplate { get; private set; }

    public CustomLayoutEditorWindow(MainViewModel mainVm, LayoutTemplateDefinition? existingTemplate = null, bool editCurrentPage = false)
    {
        _isDarkTheme = mainVm.IsDarkTheme;
        _vm = new CustomLayoutEditorViewModel(mainVm, existingTemplate, editCurrentPage);
        DataContext = _vm;
        InitializeComponent();

        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, _isDarkTheme);
        Loaded += (_, _) =>
        {
            if (ImportTemplateCombo.Items.Count > 0)
                ImportTemplateCombo.SelectedIndex = 0;
            SyncListSelectionFromVm();
            UpdateViewportAndRulers();
        };
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm.CanvasVisualChanged -= Vm_CanvasVisualChanged;
        };

        _vm.PropertyChanged += Vm_PropertyChanged;
        _vm.CanvasVisualChanged += Vm_CanvasVisualChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CustomLayoutEditorViewModel.PaperWidthMm) or nameof(CustomLayoutEditorViewModel.PaperHeightMm))
        {
            Dispatcher.BeginInvoke(UpdateViewportAndRulers, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (e.PropertyName is nameof(CustomLayoutEditorViewModel.PrimarySelectedFrame) or nameof(CustomLayoutEditorViewModel.SelectedCount))
        {
            SyncListSelectionFromVm();
        }
    }

    private void Vm_CanvasVisualChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Vm_CanvasVisualChanged);
            return;
        }
        MarginDiagram?.InvalidateVisual();
    }

    private void SyncListSelectionFromVm()
    {
        if (_syncingListSelection || FramesList == null) return;
        _syncingListSelection = true;
        try
        {
            FramesList.SelectedItems.Clear();
            foreach (var f in _vm.Frames)
            {
                if (f.IsSelected)
                    FramesList.SelectedItems.Add(f);
            }
            if (_vm.PrimarySelectedFrame != null)
                FramesList.ScrollIntoView(_vm.PrimarySelectedFrame);
        }
        finally
        {
            _syncingListSelection = false;
        }
    }

    private void FramesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingListSelection) return;
        _syncingListSelection = true;
        try
        {
            var selected = FramesList.SelectedItems.OfType<DesignerFrameItem>().ToList();
            var primary = e.AddedItems.OfType<DesignerFrameItem>().LastOrDefault() ?? selected.LastOrDefault();
            _vm.SetSelectedFrames(selected, primary);
        }
        finally
        {
            _syncingListSelection = false;
        }
    }

    // === 视口缩放与物理毫米标尺同步 ===
    private void UpdateViewportAndRulers()
    {
        if (ViewportHost.ActualWidth < 80 || ViewportHost.ActualHeight < 80) return;

        double hostW = Math.Max(140, CanvasScroller.ActualWidth - 76);
        double hostH = Math.Max(140, CanvasScroller.ActualHeight - 76);
        double paperW = Math.Max(10, _vm.PaperWidthMm);
        double paperH = Math.Max(10, _vm.PaperHeightMm);

        double fitScale = Math.Min(hostW / paperW, hostH / paperH);
        double pxPerMm = Math.Max(0.6, fitScale * _manualZoom);

        DesignerCanvas.PixelsPerMm = pxPerMm;
        DesignerCanvas.Width = paperW * pxPerMm;
        DesignerCanvas.Height = paperH * pxPerMm;
        PaperShadowHost.Width = DesignerCanvas.Width;
        PaperShadowHost.Height = DesignerCanvas.Height;

        TopRuler.LengthMm = paperW;
        TopRuler.PixelsPerMm = pxPerMm;
        LeftRuler.LengthMm = paperH;
        LeftRuler.PixelsPerMm = pxPerMm;

        try
        {
            var pt = PaperShadowHost.TranslatePoint(new Point(0, 0), ViewportHost);
            TopRuler.OriginPixelOffset = pt.X - 22;
            LeftRuler.OriginPixelOffset = pt.Y - 22;
        }
        catch { }

        ZoomPercentText.Text = Math.Abs(_manualZoom - 1.0) < 0.02 ? "适应" : $"{_manualZoom * 100:0}%";
        DesignerCanvas.InvalidateVisual();
    }

    private void SetZoom(double next)
    {
        double clamped = Math.Clamp(next, 0.35, 4.5);
        if (Math.Abs(clamped - _manualZoom) < 0.001) return;
        _manualZoom = clamped;
        _vm.Zoom = clamped;
        UpdateViewportAndRulers();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_manualZoom * 1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_manualZoom / 1.2);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void ViewportHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewportAndRulers();
    private void CanvasScroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateViewportAndRulers();

    private void ViewportHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            SetZoom(_manualZoom * (e.Delta > 0 ? 1.12 : 1.0 / 1.12));
            e.Handled = true;
        }
    }

    private void CanvasScroller_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle &&
            (CanvasScroller.ScrollableWidth > 0 || CanvasScroller.ScrollableHeight > 0))
        {
            _isMiddlePanning = true;
            _middlePanStart = e.GetPosition(CanvasScroller);
            _middlePanStartH = CanvasScroller.HorizontalOffset;
            _middlePanStartV = CanvasScroller.VerticalOffset;
            CanvasScroller.CaptureMouse();
            CanvasScroller.Cursor = Cursors.ScrollAll;
            e.Handled = true;
        }
    }

    private void CanvasScroller_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMiddlePanning) return;
        if (e.MiddleButton != MouseButtonState.Pressed)
        {
            EndMiddlePan();
            return;
        }
        var cur = e.GetPosition(CanvasScroller);
        CanvasScroller.ScrollToHorizontalOffset(_middlePanStartH - (cur.X - _middlePanStart.X));
        CanvasScroller.ScrollToVerticalOffset(_middlePanStartV - (cur.Y - _middlePanStart.Y));
        e.Handled = true;
    }

    private void CanvasScroller_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _isMiddlePanning)
        {
            EndMiddlePan();
            e.Handled = true;
        }
    }

    private void CanvasScroller_LostMouseCapture(object sender, MouseEventArgs e) => EndMiddlePan();

    private void EndMiddlePan()
    {
        if (!_isMiddlePanning) return;
        _isMiddlePanning = false;
        if (CanvasScroller.IsMouseCaptured) CanvasScroller.ReleaseMouseCapture();
        CanvasScroller.Cursor = null;
    }

    // === 精确数值框提交与方向键微调 ===
    private void NumericBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key == Key.Enter)
        {
            CommitNumericBox(tb);
            tb.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Up or Key.Down)
        {
            if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double cur))
            {
                double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5.0
                    : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.1 : 0.5;
                double next = Math.Round(cur + (e.Key == Key.Up ? step : -step), 2);
                tb.Text = next.ToString("0.##", CultureInfo.InvariantCulture);
                CommitNumericBox(tb);
                tb.SelectAll();
                e.Handled = true;
            }
        }
    }

    private void NumericBox_Commit(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) CommitNumericBox(tb);
    }

    private void CommitNumericBox(TextBox tb)
    {
        if (!_vm.HasSelection) return;
        if (!double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) || !double.IsFinite(val))
        {
            _vm.SyncInspectorFromPrimary();
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            return;
        }

        switch (tb.Tag as string)
        {
            case "X":
                _vm.SetFramePosition(val, _vm.SelectedYMm);
                break;
            case "Y":
                _vm.SetFramePosition(_vm.SelectedXMm, val);
                break;
            case "W":
                _vm.SetFrameSize(val, _vm.SelectedHeightMm, widthDriven: true);
                break;
            case "H":
                _vm.SetFrameSize(_vm.SelectedWidthMm, val, widthDriven: false);
                break;
            case "R":
                _vm.SetFrameRightMargin(val);
                break;
            case "B":
                _vm.SetFrameBottomMargin(val);
                break;
            case "Radius":
                _vm.SelectedCornerRadiusMm = val;
                break;
            case "Bleed":
                _vm.SelectedBleedMm = val;
                break;
        }
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }

    private void SizePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SizePresetCombo.SelectedItem is FrameSizePreset preset && _vm.HasSelection)
        {
            _vm.ApplyPresetSizeToSelected(preset);
            // 重置选择以允许下次再次应用同一预设
            Dispatcher.BeginInvoke(() => SizePresetCombo.SelectedItem = null);
        }
    }

    // === 快速生成弹窗 ===
    private void QuickGenBtn_Click(object sender, RoutedEventArgs e)
    {
        QuickGenPopup.IsOpen = !QuickGenPopup.IsOpen;
    }

    private void CommitPopupGridInputs()
    {
        PopupGridRowsBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        PopupGridColsBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        PopupGridMarginBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        PopupGridGapBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void ApplyQuickGrid_Click(object sender, RoutedEventArgs e)
    {
        CommitPopupGridInputs();
        _vm.ApplyQuickGrid();
        QuickGenPopup.IsOpen = false;
    }

    private void ApplyQuickFixedTile_Click(object sender, RoutedEventArgs e)
    {
        CommitPopupGridInputs();
        _vm.ApplyQuickFixedSizePack();
        QuickGenPopup.IsOpen = false;
    }

    private void ImportSelectedTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (ImportTemplateCombo.SelectedItem is LayoutTemplateDefinition tpl)
        {
            _vm.ImportFromTemplate(tpl);
            QuickGenPopup.IsOpen = false;
        }
    }

    // === 快捷键处理 ===
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool inTextBox = Keyboard.FocusedElement is TextBox;
        var mods = Keyboard.Modifiers;

        if (mods == ModifierKeys.Control && e.Key == Key.Z && !inTextBox)
        {
            _vm.Undo();
            e.Handled = true;
            return;
        }
        if (((mods == ModifierKeys.Control && e.Key == Key.Y) ||
             (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z)) && !inTextBox)
        {
            _vm.Redo();
            e.Handled = true;
            return;
        }

        if (inTextBox) return;

        if (mods == ModifierKeys.Control && e.Key == Key.D)
        {
            _vm.DuplicateSelectedFrames();
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.Control && e.Key == Key.A)
        {
            _vm.SelectAllFrames();
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.None && e.Key == Key.R && _vm.HasSelection)
        {
            _vm.RotateSelected90();
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.None && e.Key is Key.Delete or Key.Back && _vm.HasSelection)
        {
            _vm.DeleteSelectedFrames();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && _vm.HasSelection)
        {
            double step = mods.HasFlag(ModifierKeys.Shift) ? 5.0
                : mods.HasFlag(ModifierKeys.Control) ? 0.1 : 0.5;
            double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
            double dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
            _vm.NudgeSelected(dx, dy);
            e.Handled = true;
        }
    }

    // === 底部确认操作 ===
    private void CommitAllFocusedInputs()
    {
        if (Keyboard.FocusedElement is TextBox tb)
        {
            CommitNumericBox(tb);
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ApplyMode = CustomLayoutApplyMode.None;
        DialogResult = false;
        Close();
    }

    private void ApplyOnly_Click(object sender, RoutedEventArgs e)
    {
        CommitAllFocusedInputs();
        if (!_vm.CanSave) return;
        ResultTemplate = _vm.BuildTemplate(forceNewId: true);
        ResultTemplate.Id = $"tpl_adhoc_{Guid.NewGuid():N}";
        ApplyMode = CustomLayoutApplyMode.CurrentSheetOnly;
        DialogResult = true;
        Close();
    }

    private void SaveAsNew_Click(object sender, RoutedEventArgs e)
    {
        CommitAllFocusedInputs();
        if (!_vm.CanSave) return;
        ResultTemplate = _vm.BuildTemplate(forceNewId: true);
        ApplyMode = CustomLayoutApplyMode.SaveAsNewTemplate;
        DialogResult = true;
        Close();
    }

    private void SaveAndApply_Click(object sender, RoutedEventArgs e)
    {
        CommitAllFocusedInputs();
        if (!_vm.CanSave) return;
        ResultTemplate = _vm.BuildTemplate(forceNewId: false);
        ApplyMode = CustomLayoutApplyMode.SaveAndApply;
        DialogResult = true;
        Close();
    }
}
