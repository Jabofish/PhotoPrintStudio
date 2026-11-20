using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using PhotoPrintStudio.App.Themes;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.App.Views;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Preflight;

namespace PhotoPrintStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private double _manualZoom = 1.0;
    private CanvasViewMode _viewBeforeCompare;
    private Point _assetDragStart;
    private AssetItemViewModel? _assetDragCandidate;
    private bool _selectingFromAssetList;
    private bool _importInProgress;
    private GridLength? _expandedAssetTrayHeight;
    private bool _isPanningSingle;
    private (PrintSheet Sheet, LayoutSlot Slot, string AssetId)? _overviewDrag;
    private bool _isPanningOverview;
    private bool _isDraggingSplit;
    private bool _syncingAssetSelection;
    private Point _lastMousePos;
    private Point? _firstHorizonPoint;
    private string? _hoveredSlotId;

    public MainWindow(MainViewModel vm, bool confirmOnClose = true)
    {
        _vm = vm;
        _viewBeforeCompare = vm.ViewMode;
        DataContext = _vm;
        InitializeComponent();
        RightInspector.AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler(RightInspector_QueryCursor), handledEventsToo: true);
        InitializeTrayInteractions();
        Deactivated += (_, _) => _vm.IsHoldingCompare = false;
        if (confirmOnClose) Closing += (_, e) =>
        {
            if (e.Cancel) return;
            if (_vm.IsPrinting || _vm.IsExportBusy)
            {
                e.Cancel = true;
                _vm.ShowToast(_vm.IsPrinting ? "正在提交打印任务，请稍候。" : "正在执行导出任务，请先等待完成或点击取消。");
                return;
            }
            e.Cancel = !_vm.TryCloseWorkspace();
        };

        HwndSource? hwndSource = null;
        SourceInitialized += (_, _) =>
        {
            ThemeManager.ApplyWindowTitleBarTheme(this, _vm.IsDarkTheme);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                hwndSource = source;
                source.AddHook(HandleWindowMessage);
            }
        };
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= Vm_PropertyChanged;
            hwndSource?.RemoveHook(HandleWindowMessage);
        };
        Activated += (_, _) => RefreshSystemAppearance();
        Loaded += (_, _) =>
        {
            UpdateViewportLayoutAndRulers();
            UpdatePreflightUi();
            ViewportHost.Focus();
        };

        _vm.PropertyChanged += Vm_PropertyChanged;
    }

    private IntPtr HandleWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmSettingChange = 0x001A;
        if (message == WmSettingChange && _vm.Appearance == AppearanceMode.System)
            Dispatcher.BeginInvoke(RefreshSystemAppearance);
        return IntPtr.Zero;
    }

    private void RefreshSystemAppearance()
    {
        if (_vm.Appearance == AppearanceMode.System) _vm.RefreshAppearance();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsAiModelLoading) && _vm.IsAiModelLoading)
        {
            _pagePreviewTimer.Stop();
            _pagePreviewCancellation?.Cancel();
        }
        if (e.PropertyName == nameof(MainViewModel.ViewMode) && _vm.ViewMode != CanvasViewMode.SplitCompare)
            _viewBeforeCompare = _vm.ViewMode;

        if ((e.PropertyName == nameof(MainViewModel.IsTwoPointStraightenActive) && !_vm.IsTwoPointStraightenActive) ||
            e.PropertyName is nameof(MainViewModel.ActiveSheet) or nameof(MainViewModel.SelectedAsset))
        {
            _firstHorizonPoint = null;
            _hoveredSlotId = null;
            OverlayMarksCanvas.Children.Clear();
        }
        if (e.PropertyName is nameof(MainViewModel.ActiveSheet) or
            nameof(MainViewModel.ViewMode) or
            nameof(MainViewModel.CanvasPreviewBitmap) or
            nameof(MainViewModel.ShowRulers) or
            nameof(MainViewModel.IsDarkTheme))
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateViewportLayoutAndRulers();
                if (e.PropertyName == nameof(MainViewModel.IsDarkTheme))
                {
                    TopRuler?.InvalidateVisual();
                    LeftRuler?.InvalidateVisual();
                    HistogramCtrl?.InvalidateVisual();
                    ToneCurveEditor?.InvalidateVisual();
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (e.PropertyName == nameof(MainViewModel.PreflightReport))
        {
            Dispatcher.BeginInvoke(UpdatePreflightUi);
        }

        if (e.PropertyName is nameof(MainViewModel.ActiveToneCurves) or nameof(MainViewModel.ActiveCurveChannel))
        {
            Dispatcher.BeginInvoke(() => ToneCurveEditor?.InvalidateVisual());
        }

        if (e.PropertyName == nameof(MainViewModel.ActiveSheet))
            SyncPageTraySelection();

        if (e.PropertyName == nameof(MainViewModel.SelectedAsset) && !_selectingFromAssetList)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (PagePreviewList.IsVisible || _trayMarquee != null) return;
                _syncingAssetSelection = true;
                try
                {
                    var selected = _vm.SelectedAsset;
                    if (selected == null || !AssetsList.SelectedItems.Contains(selected))
                        AssetsList.SelectedItem = selected;
                    if (selected != null) AssetsList.ScrollIntoView(selected);
                }
                finally { _syncingAssetSelection = false; }
            });
        }
    }

    private void UpdatePreflightUi()
    {
        var report = _vm.PreflightReport;
        int count = report.Issues.Count;
        string brushKey;
        switch (report.OverallSeverity)
        {
            case PreflightSeverity.Error:
                brushKey = "BrushStatusError";
                PreflightBadgeText.Text = $"预检风险 ({count})";
                PreflightTabStatus.Text = $"存在 {count} 项风险";
                break;
            case PreflightSeverity.Warning:
                brushKey = "BrushStatusWarn";
                PreflightBadgeText.Text = $"预检提示 ({count})";
                PreflightTabStatus.Text = $"{count} 项注意事项";
                break;
            case PreflightSeverity.Info:
                brushKey = "BrushStatusOk";
                PreflightBadgeText.Text = $"预检良好 ({count})";
                PreflightTabStatus.Text = $"良好 ({count} 条提示)";
                break;
            default:
                brushKey = "BrushStatusOk";
                PreflightBadgeText.Text = "预检通过";
                PreflightTabStatus.Text = "全部通过";
                break;
        }
        PreflightDot.SetResourceReference(Shape.FillProperty, brushKey);
        PreflightTabStatus.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    private void UpdateViewportLayoutAndRulers()
    {
        if (_vm.ActiveSheet == null || ViewportHost.ActualWidth < 50 || ViewportHost.ActualHeight < 50)
            return;

        double hostW = Math.Max(120, CanvasScroller.ActualWidth - 52);
        double hostH = Math.Max(120, CanvasScroller.ActualHeight - 52);

        double targetWmm = _vm.ActiveSheet.PaperWidthMm;
        double targetHmm = _vm.ActiveSheet.PaperHeightMm;

        if (_vm.ViewMode != CanvasViewMode.SheetOverview && _vm.CanvasPreviewBitmap != null)
        {
            double aspect = _vm.CanvasPreviewBitmap.PixelWidth / (double)Math.Max(1, _vm.CanvasPreviewBitmap.PixelHeight);
            double h = Math.Min(hostH, hostW / aspect) * _manualZoom;
            double w = h * aspect;
            PaperShadowBorder.Width = Math.Max(60, w);
            PaperShadowBorder.Height = Math.Max(60, h);
            RulerCornerBox.Visibility = Visibility.Collapsed;
            TopRuler.Visibility = Visibility.Collapsed;
            LeftRuler.Visibility = Visibility.Collapsed;
        }
        else
        {
            var rulerVis = _vm.ShowRulers ? Visibility.Visible : Visibility.Collapsed;
            RulerCornerBox.Visibility = rulerVis;
            TopRuler.Visibility = rulerVis;
            LeftRuler.Visibility = rulerVis;
            double fitScale = Math.Min(hostW / targetWmm, hostH / targetHmm);
            double pxPerMm = Math.Max(0.4, fitScale * _manualZoom);
            PaperShadowBorder.Width = targetWmm * pxPerMm;
            PaperShadowBorder.Height = targetHmm * pxPerMm;

            TopRuler.LengthMm = targetWmm;
            TopRuler.PixelsPerMm = pxPerMm;
            LeftRuler.LengthMm = targetHmm;
            LeftRuler.PixelsPerMm = pxPerMm;

            try
            {
                var pt = PaperShadowBorder.TranslatePoint(new Point(0, 0), ViewportHost);
                double leftMargin = _vm.ShowRulers ? 22 : 0;
                double topMargin = _vm.ShowRulers ? 22 : 0;
                TopRuler.OriginPixelOffset = pt.X - leftMargin;
                LeftRuler.OriginPixelOffset = pt.Y - topMargin;
            }
            catch
            {
                // Visual not connected yet
            }
        }

        ZoomPercentText.Text = Math.Abs(_manualZoom - 1.0) < 0.02 ? "适应" : $"{_manualZoom * 100:0}%";
        OverlayMarksCanvas.Width = PaperShadowBorder.Width;
        OverlayMarksCanvas.Height = PaperShadowBorder.Height;

        if (PresentationSource.FromVisual(this) != null)
        {
            var dpiInfo = VisualTreeHelper.GetDpi(this);
            double physLong = Math.Max(PaperShadowBorder.Width * dpiInfo.DpiScaleX, PaperShadowBorder.Height * dpiInfo.DpiScaleY);
            _vm.UpdatePreviewViewportSize(physLong);
        }
    }

    // === 顶部文件菜单、场景与输出 ===
    private void FileMenuBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var importItem = new MenuItem { Header = "导入本地照片... (Ctrl+O)" };
        importItem.Click += ImportPhotos_Click;

        var sampleItem = new MenuItem { Header = "载入内置高分辨率测试样片" };
        sampleItem.Click += (_, _) => _vm.LoadSamplePhotosCommand.Execute(null);

        var openProjItem = new MenuItem { Header = "打开排版工程 (.ppsproj)...", InputGestureText = "Ctrl+Shift+O" };
        openProjItem.Click += (_, _) => _vm.OpenProjectCommand.Execute(null);

        var saveProjItem = new MenuItem { Header = "保存当前工作 (Ctrl+S)" };
        saveProjItem.Click += (_, _) => _vm.SaveProjectCommand.Execute(null);

        var openDataItem = new MenuItem { Header = "打开便携数据目录 (portable_data)" };
        openDataItem.Click += (_, _) => _vm.OpenPortableDataFolderCommand.Execute(null);

        var helpItem = new MenuItem { Header = "快捷键与使用说明 (F1)" };
        helpItem.Click += (_, _) => ShowShortcutsHelp();

        var hotFolderItem = new MenuItem { Header = "联机拍摄热文件夹监视…" };
        hotFolderItem.Click += (_, _) => new HotFolderWatchDialog(_vm) { Owner = this }.ShowDialog();

        menu.Items.Add(importItem);
        menu.Items.Add(hotFolderItem);
        menu.Items.Add(sampleItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(openProjItem);
        menu.Items.Add(saveProjItem);
        menu.Items.Add(new MenuItem { Header = "另存为…", Command = _vm.SaveProjectAsCommand });
        menu.Items.Add(new MenuItem { Header = "打包工程与原图…", Command = _vm.PackProjectCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(openDataItem);
        menu.Items.Add(helpItem);

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ScenarioId_Click(object sender, RoutedEventArgs e) => _vm.SetScenarioCommand.Execute(LayoutScenario.IdPhoto);
    private void ScenarioPolaroid_Click(object sender, RoutedEventArgs e) => _vm.SetScenarioCommand.Execute(LayoutScenario.Polaroid);

    private void ScenarioDailyPrint_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsDailyPrintScenario) _vm.SetScenarioCommand.Execute(LayoutScenario.SinglePhoto);
    }

    private void TemplatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplatesList.SelectedItem is LayoutTemplateDefinition template)
            Dispatcher.BeginInvoke(() => TemplatesList.ScrollIntoView(template), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void JumpToTemplateGroup_Click(object sender, RoutedEventArgs e)
    {
        var scenario = (LayoutScenario)((Button)sender).Tag;
        if (_vm.FilteredTemplates.FirstOrDefault(t => t.Scenario == scenario) is { } template)
            TemplatesList.ScrollIntoView(template);
    }

    private async void ImportPhotos_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "选择要导入打印的照片",
            Filter = "图像文件 (*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            await ImportPhotosAsync(dlg.FileNames);
        }
    }

    private void JumpToExportTab_Click(object sender, RoutedEventArgs e) => _vm.RightTabIndex = 2;

    private void QuickExportMenu_Click(object sender, RoutedEventArgs e)
    {
        _vm.RightTabIndex = 2;
        var menu = new ContextMenu();
        var jpgItem = new MenuItem { Header = $"导出当前页为高清 JPG ({_vm.ExportDpi} DPI)" };
        jpgItem.Click += (_, _) => _vm.ExportCurrentJpegCommand.Execute(null);
        var pngItem = new MenuItem { Header = $"导出当前页为无损 PNG ({_vm.ExportDpi} DPI)" };
        pngItem.Click += (_, _) => _vm.ExportCurrentPngCommand.Execute(null);
        var pdfItem = new MenuItem { Header = $"导出全部页为多页 PDF ({_vm.Sheets.Count} 页)" };
        pdfItem.Click += (_, _) => _vm.ExportAllToPdfCommand.Execute(null);
        menu.Items.Add(jpgItem);
        menu.Items.Add(pngItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(pdfItem);
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OpenCalibrationDialog_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.InstalledPrinters.Contains(_vm.SelectedPrinterName) ||
            !string.Equals(_vm.CurrentCalibration.PrinterName, _vm.SelectedPrinterName, StringComparison.OrdinalIgnoreCase))
        {
            _vm.ShowToast("请先选择打印机，等待设备设置载入。");
            return;
        }
        var dlg = new CalibrationDialog(_vm) { Owner = this };
        dlg.ShowDialog();
    }

    private void OpenCustomTemplateDialog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CustomTemplateDialog(_vm) { Owner = this };
        dlg.ShowDialog();
    }

    private void AddCustomPaper_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CustomPaperDialog(_vm.IsDarkTheme, _vm.SelectedPaper.WidthMm, _vm.SelectedPaper.HeightMm) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.ResultPaper != null)
            _vm.SaveCustomPaperSpec(dlg.ResultPaper);
    }

    private void DeleteCustomPaper_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPaper is { IsBuiltIn: false } paper &&
            MessageBox.Show(this, $"确定删除自定义相纸规格「{paper.Name}」吗？", "删除相纸规格",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _vm.DeleteCustomPaperSpec(paper);
        }
    }

    private void AddCustomPolaroid_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CustomPolaroidDialog(_vm.SelectedPolaroidSpec, _vm.IsDarkTheme) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.ResultSpec != null)
            _vm.SaveCustomPolaroidSpec(dlg.ResultSpec);
    }

    private void DeleteCustomPolaroid_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPolaroidSpec is { IsBuiltIn: false } spec &&
            MessageBox.Show(this, $"确定删除自定义拍立得样式「{spec.Name}」吗？", "删除拍立得尺寸",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _vm.DeleteCustomPolaroidSpec(spec);
        }
    }

    private void SaveCustomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSelectedAsset) return;
        var dlg = new PromptInputDialog("保存调色预设", "输入新预设名称：", $"自定义预设 {DateTime.Now:MMdd-HHmm}", _vm.IsDarkTheme)
        {
            Owner = this
        };
        if (dlg.ShowDialog() == true)
            _vm.SaveCustomColorPresetWithName(dlg.InputText);
    }

    private void RenamePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPreset is not { IsBuiltIn: false } preset) return;
        var dlg = new PromptInputDialog("重命名调色预设", "输入新的预设名称：", preset.Name, _vm.IsDarkTheme)
        {
            Owner = this
        };
        if (dlg.ShowDialog() == true)
            _vm.RenameCustomPreset(preset, dlg.InputText);
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPreset is not { IsBuiltIn: false } preset) return;
        if (MessageBox.Show(this, $"确定删除自定义调色预设「{preset.Name}」吗？", "删除预设",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _vm.DeleteCustomPreset(preset);
        }
    }

    private void RenameTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not LayoutTemplateDefinition tpl || tpl.IsBuiltIn) return;
        var dlg = new PromptInputDialog("重命名自定义模板", "输入新的模板名称：", tpl.Name, _vm.IsDarkTheme)
        {
            Owner = this
        };
        if (dlg.ShowDialog() == true)
            _vm.RenameCustomTemplate(tpl, dlg.InputText);
    }

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not LayoutTemplateDefinition tpl || tpl.IsBuiltIn) return;
        if (MessageBox.Show(this, $"确定删除自定义排版模板「{tpl.Name}」吗？", "删除模板",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _vm.DeleteCustomTemplate(tpl);
        }
    }

    private void RefreshPrinters_Click(object sender, RoutedEventArgs e) => _vm.RefreshPrinters(_vm.SelectedPrinterName);

    private void OpenPrinterPreferences_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _vm.OpenNativePrinterPreferences(hwnd);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsDialog(_vm) { Owner = this }.ShowDialog();
    }

    private void HotFolderWatch_Click(object sender, RoutedEventArgs e)
    {
        new HotFolderWatchDialog(_vm) { Owner = this }.ShowDialog();
    }

    private void PreviewSettings_Click(object sender, RoutedEventArgs e) =>
        new SettingsDialog(_vm, SettingsSection.Preview) { Owner = this }.ShowDialog();

    private void OutputSettings_Click(object sender, RoutedEventArgs e) =>
        new SettingsDialog(_vm, SettingsSection.Output) { Owner = this }.ShowDialog();

    private void ApplyCanvasBg_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string hex)
            _vm.CanvasBackgroundHex = hex;
    }

    private void PickIdBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSelectedAsset) return;
        var dlg = new ColorPickerDialog(_vm.EditReplacementBgHex, "选择证件照背景颜色", _vm.IsDarkTheme) { Owner = this };
        if (dlg.ShowDialog() == true)
            _vm.SetCustomIdBackgroundColors(dlg.SelectedHex);
    }

    private void PickIdSecondaryBgColor_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSelectedAsset) return;
        var dlg = new ColorPickerDialog(_vm.EditReplacementBgSecondaryHex, "选择渐变中心高光颜色", _vm.IsDarkTheme) { Owner = this };
        if (dlg.ShowDialog() == true)
            _vm.SetCustomIdBackgroundColors(_vm.EditReplacementBgHex, dlg.SelectedHex, useGradient: true);
    }

    private void PickPolaroidFrameColor_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ColorPickerDialog(_vm.PolaroidFrameColorHex, "选择拍立得相框底色", _vm.IsDarkTheme) { Owner = this };
        if (dlg.ShowDialog() == true)
            _vm.PolaroidFrameColorHex = dlg.SelectedHex;
    }

    private void PickPolaroidTextColor_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ColorPickerDialog(_vm.PolaroidTextColorHex, "选择拍立得签名文字颜色", _vm.IsDarkTheme) { Owner = this };
        if (dlg.ShowDialog() == true)
            _vm.PolaroidTextColorHex = dlg.SelectedHex;
    }

    private void PickCanvasBgColor_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ColorPickerDialog(_vm.CanvasBackgroundHex, "选择整页纸张底色", _vm.IsDarkTheme) { Owner = this };
        if (dlg.ShowDialog() == true)
            _vm.CanvasBackgroundHex = dlg.SelectedHex;
    }

    // === 视图控制与缩放 ===
    private void ViewModeSheet_Click(object sender, RoutedEventArgs e) => _vm.SetViewModeCommand.Execute(CanvasViewMode.SheetOverview);
    private void ViewModeSingle_Click(object sender, RoutedEventArgs e) => _vm.SetViewModeCommand.Execute(CanvasViewMode.SingleRetouchCrop);
    private void ViewModeSplit_Click(object sender, RoutedEventArgs e) => _vm.SetViewModeCommand.Execute(CanvasViewMode.SplitCompare);

    private void HoldCompareBtn_Down(object sender, MouseButtonEventArgs e) => _vm.IsHoldingCompare = true;
    private void HoldCompareBtn_Up(object sender, MouseButtonEventArgs e) => _vm.IsHoldingCompare = false;
    private void HoldCompareBtn_Leave(object sender, MouseEventArgs e) => _vm.IsHoldingCompare = false;

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _manualZoom = Math.Clamp(_manualZoom * 1.2, 0.35, 4.0);
        UpdateViewportLayoutAndRulers();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _manualZoom = Math.Clamp(_manualZoom / 1.2, 0.35, 4.0);
        UpdateViewportLayoutAndRulers();
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        _manualZoom = 1.0;
        UpdateViewportLayoutAndRulers();
    }

    private void ViewportHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewportLayoutAndRulers();
    private void CanvasScroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateViewportLayoutAndRulers();

    private void ViewportHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm.ViewMode == CanvasViewMode.SheetOverview && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            var slot = FindSlotAtCanvasPosition(e.GetPosition(CanvasPreviewImage));
            if (slot?.PhotoAssetId != null)
            {
                _vm.SelectSlot(slot);
                _vm.BeginContinuousEdit();
                _vm.AdjustPanZoom(0, 0, e.Delta > 0 ? 1.06 : 1 / 1.06);
                _vm.EndContinuousEdit();
                e.Handled = true;
            }
            return;
        }
        if (_vm.ViewMode == CanvasViewMode.SingleRetouchCrop && Keyboard.Modifiers == ModifierKeys.None)
        {
            double factor = e.Delta > 0 ? 1.06 : 1 / 1.06;
            _vm.BeginContinuousEdit();
            _vm.AdjustPanZoom(0, 0, factor);
            _vm.EndContinuousEdit();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control || _vm.ViewMode == CanvasViewMode.SheetOverview)
        {
            _manualZoom = Math.Clamp(_manualZoom * (e.Delta > 0 ? 1.12 : 1 / 1.12), 0.35, 4.0);
            UpdateViewportLayoutAndRulers();
            e.Handled = true;
        }
    }

    // === 胶片素材条拖拽 & 画布交互 ===
    private void AssetsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedPhotosButton == null || AssetsList == null) return;
        int count = AssetsList.SelectedItems.Count;
        SelectedPhotosButton.Content = $"已选 {count} 张";
        SelectedPhotosButton.IsEnabled = count > 0;
        if (_syncingAssetSelection) return;
        var asset = e.AddedItems.OfType<AssetItemViewModel>().LastOrDefault()
            ?? AssetsList.SelectedItem as AssetItemViewModel;
        SelectAssetFromTray(asset);
    }

    private void SelectAssetFromTray(AssetItemViewModel? asset)
    {
        if (asset == null) return;
        _selectingFromAssetList = true;
        try { _vm.SelectAsset(asset, syncSlotOnSheet: true); }
        finally { _selectingFromAssetList = false; }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void AssetsList_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => TrayList_PreviewMouseWheel(sender, e);

    private void TrayList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindVisualChild<ScrollViewer>((ListBox)sender) is not { ScrollableWidth: > 0 } scroll) return;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta * 0.75);
        e.Handled = true;
    }

    private void ShowMenu(object sender, params MenuItem[] items)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var item in items)
            menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private void SelectedPhotos_Click(object sender, RoutedEventArgs e)
    {
        var selected = AssetsList.SelectedItems.Cast<AssetItemViewModel>().ToHashSet();
        var photos = _vm.Assets.Where(selected.Contains).ToList();
        if (photos.Count == 0) return;
        var arrange = new MenuItem { Header = "按当前版式排入新页", IsEnabled = _vm.ActiveSheet?.Slots.Count > 0 };
        arrange.Click += (_, _) => _vm.PaginateSelectedPhotos(photos);
        var strategy = new MenuItem { Header = "选择排版策略…", IsEnabled = arrange.IsEnabled };
        strategy.Click += (_, _) =>
        {
            var dlg = new ImportPlacementDialog(_vm, photos.Count, arrangingSelection: true) { Owner = this };
            if (dlg.ShowDialog() == true) _vm.PaginateSelectedPhotos(photos, dlg.Arrangement);
        };
        var remove = new MenuItem { Header = $"从工程移除 {photos.Count} 张" };
        remove.Click += (_, _) => RemoveSelectedPhotos(photos);
        ShowMenu(sender, arrange, strategy, remove);
    }

    private void RemoveSelectedPhotos(IReadOnlyList<AssetItemViewModel> photos)
    {
        int uses = photos.Sum(photo => photo.UsageCount);
        if (uses > 0 && MessageBox.Show(this,
            $"这会清空工程中引用所选照片的 {uses} 个格位。原始文件不会删除。是否继续？",
            "移除照片", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _vm.RemovePhotos(photos);
        _vm.ShowToast($"已从工程移除 {photos.Count} 张照片，可撤销。");
    }

    private void AssetMore_Click(object sender, RoutedEventArgs e) => ShowMenu(sender,
        new MenuItem { Header = "用当前照片填满本页", Command = _vm.FillAllSlotsWithSelectedAssetCommand, IsEnabled = _vm.HasSelectedAsset },
        new MenuItem { Header = "按素材顺序填满本页", Command = _vm.SequentialFillAllAssetsCommand, IsEnabled = _vm.Assets.Count > 0 },
        new MenuItem { Header = "加载样片", Command = _vm.LoadSamplePhotosCommand },
        AssetTrayToggleItem());

    private MenuItem AssetTrayToggleItem()
    {
        var item = new MenuItem { Header = _expandedAssetTrayHeight == null ? "收起底栏" : "展开底栏" };
        item.Click += (_, _) =>
        {
            if (_expandedAssetTrayHeight is { } height)
            {
                AssetTrayRow.Height = height;
                TrayContent.Visibility = Visibility.Visible;
                QueuePagePreviews();
                _expandedAssetTrayHeight = null;
            }
            else
            {
                _expandedAssetTrayHeight = AssetTrayRow.Height;
                TrayContent.Visibility = Visibility.Collapsed;
                _pagePreviewCancellation?.Cancel();
                AssetTrayRow.Height = GridLength.Auto;
            }
        };
        return item;
    }

    private void AssetToolbar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        AssetTrayRow.MinHeight = Math.Max(48, e.NewSize.Height);

    private void PageMore_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<MenuItem>
        {
            new() { Header = "新增空白页", Command = _vm.AddBlankSheetCommand },
            new() { Header = "复制当前页", Command = _vm.DuplicateActiveSheetCommand },
            new() { Header = "重新分页全部照片（替换现有页）", Command = _vm.AutoPaginateAllAssetsCommand, IsEnabled = _vm.Assets.Count > 0 },
            new() { Header = "删除当前页", Command = _vm.RemoveActiveSheetCommand, IsEnabled = _vm.Sheets.Count > 1 }
        };
        if (PagePreviewList.IsVisible && PagePreviewList.SelectedItems.Count > 1)
        {
            var selected = new MenuItem { Header = $"删除所选 {PagePreviewList.SelectedItems.Count} 页" };
            selected.Click += (_, _) => DeleteSelectedPages();
            items.Add(selected);
        }
        var empty = new MenuItem { Header = "删除所有空白页", IsEnabled = _vm.HasRemovableEmptySheets };
        empty.Click += (_, _) => _vm.RemoveEmptySheets();
        items.Add(empty);
        ShowMenu(sender, items.ToArray());
    }

    private void JumpToPage_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Sheets.Count <= 1) return;
        var dlg = new PromptInputDialog("跳转页面", $"输入页码（1 至 {_vm.Sheets.Count}）：",
            (_vm.ActiveSheetIndex + 1).ToString(), _vm.IsDarkTheme) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (int.TryParse(dlg.InputText, out int page) && page >= 1 && page <= _vm.Sheets.Count)
            _vm.SwitchToSheet(page - 1);
        else
            _vm.ShowToast($"请输入 1 至 {_vm.Sheets.Count} 之间的页码。");
    }

    private void PreflightIssue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PreflightIssue issue && !string.IsNullOrEmpty(issue.SlotId))
        {
            var slot = _vm.ActiveSheet?.Slots.FirstOrDefault(s => s.Id == issue.SlotId);
            if (slot != null)
            {
                if (_vm.ViewMode != CanvasViewMode.SheetOverview)
                    _vm.SetViewModeCommand.Execute(CanvasViewMode.SheetOverview);
                _vm.SelectSlot(slot);
                _vm.ShowToast($"已定位至格位 #{slot.Index + 1}");
            }
        }
    }

    private void AssetsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _assetDragStart = e.GetPosition(null);
        _assetDragCandidate = e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(AssetsList, element) is ListBoxItem item
                ? item.DataContext as AssetItemViewModel : null;
        if ((_assetDragCandidate == null || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) && TryBeginTrayMarquee(AssetsList, e))
            _assetDragCandidate = null;
    }

    private void AssetsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_trayMarquee is { } drag && drag.List == AssetsList)
        {
            if (e.LeftButton == MouseButtonState.Pressed) UpdateTrayMarquee(e.GetPosition(drag.Viewport));
            e.Handled = true;
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed || _assetDragCandidate == null ||
            Keyboard.Modifiers != ModifierKeys.None || AssetsList.SelectedItems.Count > 1) return;
        Point pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _assetDragStart.X) > 6 || Math.Abs(pos.Y - _assetDragStart.Y) > 6)
        {
            var asset = _assetDragCandidate;
            _assetDragCandidate = null;
            DragDrop.DoDragDrop(AssetsList, new DataObject(DataFormats.UnicodeText, $"PhotoPrintAsset:{asset.Id}"), DragDropEffects.Copy);
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                await ImportPhotosAsync(files);
        }
    }

    private async Task ImportPhotosAsync(string[] files, LayoutSlot? dropTarget = null)
    {
        if (_importInProgress) return;
        _importInProgress = true;
        try
        {
            await _vm.ImportFilesAsync(files, count =>
            {
                if (count == 1 && dropTarget != null && _vm.ActiveSheet?.Slots.Contains(dropTarget) == true)
                    return new ImportArrangement(ImportPlacement.PoolOnly);
                var dlg = new ImportPlacementDialog(_vm, count) { Owner = this };
                return dlg.ShowDialog() == true ? dlg.Arrangement : null;
            }, dropTarget);
        }
        finally { _importInProgress = false; }
    }

    private void CanvasImage_DragOver(object sender, DragEventArgs e)
    {
        if ((e.Data.GetDataPresent(DataFormats.UnicodeText) &&
             e.Data.GetData(DataFormats.UnicodeText) is string s &&
             s.StartsWith("PhotoPrintAsset:", StringComparison.Ordinal)) ||
            e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void CanvasImage_Drop(object sender, DragEventArgs e)
    {
        var pos = e.GetPosition(CanvasPreviewImage);
        var slot = _vm.ViewMode == CanvasViewMode.SheetOverview ? FindSlotAtCanvasPosition(pos) : _vm.SelectedSlot;

        if (e.Data.GetDataPresent(DataFormats.UnicodeText) &&
            e.Data.GetData(DataFormats.UnicodeText) is string tag &&
            tag.StartsWith("PhotoPrintAsset:", StringComparison.Ordinal) &&
            slot != null)
        {
            string assetId = tag["PhotoPrintAsset:".Length..];
            var assetVm = _vm.Assets.FirstOrDefault(a => a.Id == assetId);
            if (assetVm != null)
            {
                _vm.AssignAssetToSlot(assetVm, slot);
                e.Handled = true;
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                 e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            await ImportPhotosAsync(files, slot);
        }
    }

    private void CanvasPreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ViewportHost.Focus();
        var pos = e.GetPosition(CanvasPreviewImage);
        if (_vm.ViewMode == CanvasViewMode.SheetOverview)
        {
            var slot = FindSlotAtCanvasPosition(pos);
            if (slot != null)
            {
                _vm.SelectSlot(slot);
                if (e.ClickCount == 2)
                {
                    _vm.SetViewModeCommand.Execute(CanvasViewMode.SingleRetouchCrop);
                }
                else if (_vm.ActiveSheet is { } sheet && slot.PhotoAssetId is { } assetId)
                {
                    _overviewDrag = (sheet, slot, assetId);
                    _lastMousePos = pos;
                    CanvasPreviewImage.CaptureMouse();
                    CanvasPreviewImage.Cursor = Cursors.Hand;
                }
            }
            return;
        }

        if (_vm.ViewMode == CanvasViewMode.SplitCompare)
        {
            _isDraggingSplit = true;
            CanvasPreviewImage.CaptureMouse();
            CanvasPreviewImage.Cursor = Cursors.SizeWE;
            _vm.SplitCompareRatio = Math.Clamp(pos.X / Math.Max(1, CanvasPreviewImage.ActualWidth), 0.05, 0.95);
            return;
        }

        if (_vm.ViewMode == CanvasViewMode.SingleRetouchCrop)
        {
            if (_vm.IsTwoPointStraightenActive)
            {
                HandleTwoPointStraightenClick(pos);
                return;
            }
            if (e.ClickCount == 2)
            {
                _vm.SetViewModeCommand.Execute(CanvasViewMode.SheetOverview);
                return;
            }
            _isPanningSingle = true;
            _lastMousePos = pos;
            _vm.BeginContinuousEdit();
            CanvasPreviewImage.CaptureMouse();
            CanvasPreviewImage.Cursor = Cursors.Hand;
        }
    }

    private void CanvasPreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(CanvasPreviewImage);
        if (_isDraggingSplit)
        {
            _vm.SplitCompareRatio = Math.Clamp(pos.X / Math.Max(1, CanvasPreviewImage.ActualWidth), 0.05, 0.95);
            return;
        }

        if (_isPanningSingle)
        {
            double dx = (pos.X - _lastMousePos.X) / Math.Max(1, CanvasPreviewImage.ActualWidth) * 2.0;
            double dy = (pos.Y - _lastMousePos.Y) / Math.Max(1, CanvasPreviewImage.ActualHeight) * 2.0;
            _lastMousePos = pos;
            _vm.AdjustPanZoom(dx, dy, 1.0);
            return;
        }

        if (_overviewDrag is { } drag)
        {
            if (_vm.ViewMode != CanvasViewMode.SheetOverview || _vm.ActiveSheet != drag.Sheet ||
                _vm.SelectedSlot != drag.Slot || _vm.SelectedAsset?.Id != drag.AssetId)
            {
                CanvasPreviewImage.ReleaseMouseCapture();
                return;
            }
            var delta = pos - _lastMousePos;
            if (!_isPanningOverview)
            {
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _vm.BeginContinuousEdit();
                _isPanningOverview = true;
            }

            double dxMm = delta.X * drag.Sheet.PaperWidthMm / Math.Max(1, CanvasPreviewImage.ActualWidth);
            double dyMm = delta.Y * drag.Sheet.PaperHeightMm / Math.Max(1, CanvasPreviewImage.ActualHeight);
            double radians = drag.Slot.SlotRotationDegrees * Math.PI / 180;
            var (width, height, bleed) = PortraitAutoFrameHelper.ResolveUprightSlotPhotoFrameMm(drag.Slot);
            double dx = (dxMm * Math.Cos(radians) + dyMm * Math.Sin(radians)) * 2 / (width + 2 * bleed);
            double dy = (-dxMm * Math.Sin(radians) + dyMm * Math.Cos(radians)) * 2 / (height + 2 * bleed);
            _lastMousePos = pos;
            _vm.AdjustPanZoom(dx, dy, 1.0);
            return;
        }

        if (_vm.ViewMode == CanvasViewMode.SheetOverview)
        {
            var hovered = FindSlotAtCanvasPosition(pos);
            string? nextId = (hovered != null && hovered.Id != _vm.SelectedSlot?.Id) ? hovered.Id : null;
            if (nextId != _hoveredSlotId)
            {
                _hoveredSlotId = nextId;
                RenderHoverOverlay(hovered);
            }
            CanvasPreviewImage.Cursor = hovered?.PhotoAssetId != null ? Cursors.Hand : Cursors.Arrow;
        }
        else
        {
            CanvasPreviewImage.Cursor = _vm.IsTwoPointStraightenActive ? Cursors.Cross
                : _vm.ViewMode == CanvasViewMode.SplitCompare ? Cursors.SizeWE : Cursors.Hand;
        }
    }

    private void RightInspector_QueryCursor(object sender, QueryCursorEventArgs e)
    {
        if (e.Cursor != Cursors.Hand) return;
        e.Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private void CanvasPreviewImage_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoveredSlotId != null)
        {
            _hoveredSlotId = null;
            OverlayMarksCanvas.Children.Clear();
        }
    }

    private void RenderHoverOverlay(LayoutSlot? hovered)
    {
        if (_vm.IsTwoPointStraightenActive) return;
        OverlayMarksCanvas.Children.Clear();
        if (hovered == null || _vm.ActiveSheet == null || hovered.Id == _vm.SelectedSlot?.Id) return;

        double scaleX = PaperShadowBorder.Width / Math.Max(1, _vm.ActiveSheet.PaperWidthMm);
        double scaleY = PaperShadowBorder.Height / Math.Max(1, _vm.ActiveSheet.PaperHeightMm);

        var rect = new Rectangle
        {
            Width = Math.Max(4, hovered.WidthMm * scaleX),
            Height = Math.Max(4, hovered.HeightMm * scaleY),
            Stroke = FindResource("BrushAccent") as Brush ?? Brushes.DodgerBlue,
            StrokeThickness = 1.6,
            StrokeDashArray = [4, 3],
            Fill = new SolidColorBrush(Color.FromArgb(20, 59, 130, 246))
        };
        Canvas.SetLeft(rect, hovered.XMm * scaleX);
        Canvas.SetTop(rect, hovered.YMm * scaleY);
        OverlayMarksCanvas.Children.Add(rect);
    }

    private void CanvasPreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (CanvasPreviewImage.IsMouseCaptured)
            CanvasPreviewImage.ReleaseMouseCapture();
        FinishCanvasDrag();
    }

    private void CanvasPreviewImage_LostMouseCapture(object sender, MouseEventArgs e) => FinishCanvasDrag();

    private void FinishCanvasDrag()
    {
        if (_isPanningSingle || _isPanningOverview)
        {
            _isPanningSingle = false;
            _isPanningOverview = false;
            _vm.EndContinuousEdit();
        }
        _overviewDrag = null;
        _isDraggingSplit = false;
    }

    private void CanvasPreviewImage_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm.ViewMode == CanvasViewMode.SheetOverview)
        {
            var slot = FindSlotAtCanvasPosition(e.GetPosition(CanvasPreviewImage));
            if (slot != null) _vm.SelectSlot(slot);
        }
    }

    private void CtxSingleEdit_Click(object sender, RoutedEventArgs e)
    {
        _vm.SetViewModeCommand.Execute(CanvasViewMode.SingleRetouchCrop);
    }

    private void HandleTwoPointStraightenClick(Point pos)
    {
        if (_firstHorizonPoint == null)
        {
            _firstHorizonPoint = pos;
            OverlayMarksCanvas.Children.Clear();
            DrawPointMarker(pos);
            _vm.StatusText = "已标记第 1 点，请点击第 2 点（另一侧眼角或水平端点）";
        }
        else
        {
            var p1 = _firstHorizonPoint.Value;
            _firstHorizonPoint = null;
            OverlayMarksCanvas.Children.Clear();
            _vm.ApplyTwoPointHorizon(p1, pos);
        }
    }

    private void DrawPointMarker(Point pt)
    {
        var el = new Ellipse
        {
            Width = 12,
            Height = 12,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Fill = FindResource("BrushAccent") as Brush
        };
        Canvas.SetLeft(el, pt.X - 6);
        Canvas.SetTop(el, pt.Y - 6);
        OverlayMarksCanvas.Children.Add(el);
    }

    private LayoutSlot? FindSlotAtCanvasPosition(Point pos)
    {
        if (_vm.ActiveSheet == null || CanvasPreviewImage.ActualWidth <= 1 || CanvasPreviewImage.ActualHeight <= 1)
            return null;

        double mmX = pos.X / CanvasPreviewImage.ActualWidth * _vm.ActiveSheet.PaperWidthMm;
        double mmY = pos.Y / CanvasPreviewImage.ActualHeight * _vm.ActiveSheet.PaperHeightMm;

        return _vm.ActiveSheet.Slots.FirstOrDefault(s =>
            mmX >= s.XMm && mmX <= s.XMm + s.WidthMm &&
            mmY >= s.YMm && mmY <= s.YMm + s.HeightMm);
    }

    // === 滑块 Undo 钩子 & 曲线/HSL 按钮 ===
    private void Slider_EditStarted(object? sender, EventArgs e) => _vm.BeginContinuousEdit();
    private void Slider_EditCompleted(object? sender, EventArgs e) => _vm.EndContinuousEdit();
    private void ToneCurveEditor_CurveChanged(object? sender, EventArgs e) => _vm.OnEditMutated();

    private void CurveCh0_Click(object sender, RoutedEventArgs e) => _vm.ActiveCurveChannel = 0;
    private void CurveCh1_Click(object sender, RoutedEventArgs e) => _vm.ActiveCurveChannel = 1;
    private void CurveCh2_Click(object sender, RoutedEventArgs e) => _vm.ActiveCurveChannel = 2;
    private void CurveCh3_Click(object sender, RoutedEventArgs e) => _vm.ActiveCurveChannel = 3;

    private void HslCh_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int idx))
            _vm.ActiveHslChannel = idx;
    }

    // === 键盘快捷键 ===
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.S && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            if (Keyboard.FocusedElement is TextBox box)
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (modifiers == ModifierKeys.Control) _vm.SaveProjectCommand.Execute(null);
            else _vm.SaveProjectAsCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.OemComma && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            if (modifiers == ModifierKeys.Control) Settings_Click(this, e);
            else PreviewSettings_Click(this, e);
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBox) return;

        if (modifiers == ModifierKeys.Control && e.Key == Key.A && AssetsList.IsKeyboardFocusWithin)
        {
            AssetsList.SelectAll();
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.Control && e.Key == Key.A && PagePreviewList.IsKeyboardFocusWithin)
        {
            PagePreviewList.SelectAll();
            e.Handled = true;
            return;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.O)
        {
            if (!e.IsRepeat) _vm.OpenProjectCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.OemPlus)
        {
            ZoomIn_Click(this, e);
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.Control && e.Key == Key.G)
        {
            JumpToPage_Click(this, e);
            e.Handled = true;
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z)
        {
            _vm.RedoCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.Alt && e.SystemKey is Key.D1 or Key.D2 or Key.D3)
        {
            _vm.RightTabIndex = (int)e.SystemKey - (int)Key.D1;
            e.Handled = true;
            return;
        }
        if (ViewportHost.IsKeyboardFocused && modifiers == ModifierKeys.Shift && e.Key == Key.T && _vm.HasSelectedAsset)
        {
            if (!e.IsRepeat) _vm.ToggleTwoPointStraightenCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.None &&
            (ViewportHost.IsKeyboardFocused ||
             ((e.Key is Key.PageUp or Key.PageDown) && !IsWithinEditingControl(Keyboard.FocusedElement as DependencyObject))))
        {
            switch (e.Key)
            {
                case Key.PageUp:
                case Key.Left:
                case Key.Up: _vm.PrevSheetCommand.Execute(null); e.Handled = true; return;
                case Key.PageDown:
                case Key.Right:
                case Key.Down: _vm.NextSheetCommand.Execute(null); e.Handled = true; return;
                case Key.Home: _vm.SwitchToSheet(0); e.Handled = true; return;
                case Key.End: _vm.SwitchToSheet(_vm.Sheets.Count - 1); e.Handled = true; return;
                case Key.G:
                    if (!e.IsRepeat) _vm.SetViewModeCommand.Execute(CanvasViewMode.SheetOverview);
                    e.Handled = true;
                    return;
                case Key.R when _vm.HasSelectedAsset:
                    if (!e.IsRepeat) _vm.SetViewModeCommand.Execute(CanvasViewMode.SingleRetouchCrop);
                    e.Handled = true;
                    return;
                case Key.Y when _vm.HasSelectedAsset || _vm.ViewMode == CanvasViewMode.SplitCompare:
                    if (!e.IsRepeat) _vm.SetViewModeCommand.Execute(_vm.ViewMode == CanvasViewMode.SplitCompare
                        ? _viewBeforeCompare : CanvasViewMode.SplitCompare);
                    e.Handled = true;
                    return;
                case Key.J:
                    if (!e.IsRepeat) _vm.ShowClippingOverlay = !_vm.ShowClippingOverlay;
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Escape)
        {
            if (_vm.IsTwoPointStraightenActive)
            {
                _firstHorizonPoint = null;
                OverlayMarksCanvas.Children.Clear();
                _vm.ToggleTwoPointStraightenCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (_vm.ViewMode != CanvasViewMode.SheetOverview)
            {
                _vm.SetViewModeCommand.Execute(CanvasViewMode.SheetOverview);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Space && modifiers == ModifierKeys.None && ViewportHost.IsKeyboardFocused && !e.IsRepeat)
        {
            _vm.IsHoldingCompare = true;
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.O: ImportPhotos_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.Z: _vm.UndoCommand.Execute(null); e.Handled = true; break;
                case Key.Y: _vm.RedoCommand.Execute(null); e.Handled = true; break;
                case Key.P: _vm.PrintActiveSheetCommand.Execute(null); e.Handled = true; break;
                case Key.E: _vm.ExportCurrentJpegCommand.Execute(null); e.Handled = true; break;
                case Key.C: _vm.CopyColorParametersCommand.Execute(null); e.Handled = true; break;
                case Key.V: _vm.PasteColorToSelectedCommand.Execute(null); e.Handled = true; break;
                case Key.OemOpenBrackets:
                    if (!e.IsRepeat) _vm.RotateQuarterLeftCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.OemCloseBrackets:
                    if (!e.IsRepeat) _vm.RotateQuarterRightCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.OemPlus:
                case Key.Add: ZoomIn_Click(this, e); e.Handled = true; break;
                case Key.OemMinus:
                case Key.Subtract: ZoomOut_Click(this, e); e.Handled = true; break;
                case Key.D0:
                case Key.NumPad0: ZoomFit_Click(this, e); e.Handled = true; break;
            }
        }
        else if (modifiers == ModifierKeys.None && e.Key == Key.Delete && PagePreviewList.IsKeyboardFocusWithin)
        {
            if (!e.IsRepeat) DeleteSelectedPages();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.None && e.Key == Key.Delete &&
                 (ViewportHost.IsKeyboardFocused || AssetsList.IsKeyboardFocusWithin))
        {
            if (AssetsList.IsKeyboardFocusWithin)
            {
                if (AssetsList.SelectedItems.Count == 0) return;
                RemoveSelectedPhotos(AssetsList.SelectedItems.Cast<AssetItemViewModel>().ToList());
            }
            else
            {
                _vm.ClearSelectedSlotCommand.Execute(null);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            var next = _vm.ViewMode == CanvasViewMode.SheetOverview
                ? CanvasViewMode.SingleRetouchCrop
                : CanvasViewMode.SheetOverview;
            _vm.SetViewModeCommand.Execute(next);
            e.Handled = true;
        }
        else if (e.Key == Key.F1)
        {
            ShowShortcutsHelp();
            e.Handled = true;
        }
    }

    private static bool IsWithinEditingControl(DependencyObject? element)
    {
        for (var parent = element; parent is Visual; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ComboBox or ListBox or PhotoPrintStudio.App.Controls.StudioSliderRow)
                return true;
        }
        return false;
    }

    private void ShowShortcutsHelp()
    {
        const string guide =
            """
            【工程与输出】
            Ctrl+O 导入照片    Ctrl+Shift+O 打开工程
            Ctrl+S 保存当前工作    Ctrl+Shift+S 另存为
            Ctrl+Z 撤销    Ctrl+Y 或 Ctrl+Shift+Z 重做
            Ctrl+E 导出当前页 JPG    Ctrl+P 打印当前页
            Ctrl+逗号 设置    Ctrl+Shift+逗号 预览设置

            【画布与排版】
            Ctrl+= / Ctrl+- 缩放画布    Ctrl+0 适应窗口
            Page Up / Page Down 翻页    Ctrl+G 跳转页码
            点击画布后，方向键翻页，Home / End 跳到首页或末页
            点击画布后，G 排版总览，R 单图裁剪，Y 开关分屏对比
            点击画布后，J 开关溢出提示，Shift+T 两点拉直，按住空格对比原图
            排版总览中拖动照片调整构图，Shift+滚轮缩放照片
            F6 切换排版总览和单图裁剪    Alt+1 / 2 / 3 切换右侧面板
            素材栏拖空白框选，Alt+拖动可从卡片开始，Ctrl+A 全选
            Delete 在打印页面删除所选页，在素材池移除所选照片，在画布清空当前格位

            【照片与参数】
            Ctrl+[ / Ctrl+] 逆时针或顺时针旋转照片 90°
            Ctrl+C / Ctrl+V 复制或粘贴照片调色参数
            聚焦滑块后，方向键微调，Shift+方向键调整十步
            Enter 或 F2 输入数值，Esc 取消，Alt+Backspace 恢复该项默认值
            """;
        MessageBox.Show(this, guide, "光影印坊快捷键与使用指南",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _vm.IsHoldingCompare)
        {
            _vm.IsHoldingCompare = false;
        }
    }
}
