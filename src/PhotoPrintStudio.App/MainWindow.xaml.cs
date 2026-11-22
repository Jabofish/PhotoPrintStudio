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
    private bool _deferAssetMultiSelectReset;
    private bool _selectingFromAssetList;
    private bool _importInProgress;
    private GridLength? _expandedAssetTrayHeight;
    private bool _isPanningSingle;
    private (PrintSheet Sheet, LayoutSlot Slot, string AssetId)? _overviewDrag;
    private (PrintSheet Sheet, LayoutSlot SourceSlot)? _slotSwapDrag;
    private LayoutSlot? _slotSwapTarget;
    private bool _isPanningOverview;
    private bool _isDraggingSplit;
    private bool _isMiddlePanningCanvas;
    private Point _middlePanStart;
    private double _middlePanStartH;
    private double _middlePanStartV;
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
        if (e.PropertyName == nameof(MainViewModel.ViewMode))
        {
            if (_vm.ViewMode != CanvasViewMode.SplitCompare)
                _viewBeforeCompare = _vm.ViewMode;
            _manualZoom = 1.0;
        }

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

        var importItem = new MenuItem { Header = "导入照片…", InputGestureText = "Ctrl+O" };
        importItem.Click += ImportPhotos_Click;

        var hotFolderItem = new MenuItem { Header = "联机拍摄监视…" };
        hotFolderItem.Click += (_, _) => new HotFolderWatchDialog(_vm) { Owner = this }.ShowDialog();

        var sampleItem = new MenuItem { Header = "载入示例照片" };
        sampleItem.Click += (_, _) => _vm.LoadSamplePhotosCommand.Execute(null);

        var newProjItem = new MenuItem { Header = "新建空白工程", InputGestureText = "Ctrl+N", Command = _vm.NewProjectCommand };

        var openProjItem = new MenuItem { Header = "打开排版工程…", InputGestureText = "Ctrl+Shift+O" };
        openProjItem.Click += (_, _) => _vm.OpenProjectCommand.Execute(null);

        var saveProjItem = new MenuItem { Header = "保存", InputGestureText = "Ctrl+S" };
        saveProjItem.Click += (_, _) => _vm.SaveProjectCommand.Execute(null);

        var openDataItem = new MenuItem { Header = "打开数据目录" };
        openDataItem.Click += (_, _) => _vm.OpenPortableDataFolderCommand.Execute(null);

        var helpItem = new MenuItem { Header = "键盘快捷键", InputGestureText = "F1" };
        helpItem.Click += (_, _) => ShowShortcutsHelp();

        menu.Items.Add(importItem);
        menu.Items.Add(hotFolderItem);
        menu.Items.Add(sampleItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(newProjItem);
        menu.Items.Add(openProjItem);
        menu.Items.Add(saveProjItem);
        menu.Items.Add(new MenuItem { Header = "另存为…", InputGestureText = "Ctrl+Shift+S", Command = _vm.SaveProjectAsCommand });
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
        var group = _vm.FilteredTemplates.Where(t => t.Scenario == scenario).ToList();
        if (group.Count == 0) return;

        TemplatesList.ScrollIntoView(group[0]);
        TemplatesList.UpdateLayout();
        if (FindVisualChild<ScrollViewer>(TemplatesList) is { } sv &&
            FindVisualChild<ScrollContentPresenter>(sv) is { } presenter &&
            TemplatesList.ItemContainerGenerator.ContainerFromItem(group[0]) is FrameworkElement firstItem)
        {
            // Scroll the group header (when grouped) or the first item cleanly to the top of the list viewport.
            var anchor = FindFocusAncestor<GroupItem>(firstItem) as FrameworkElement ?? firstItem;
            double offsetY = anchor.TranslatePoint(new Point(0, 0), presenter).Y;
            sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset + offsetY, 0, sv.ScrollableHeight));
        }
    }

    private async void ImportPhotos_Click(object sender, RoutedEventArgs e)
    {
        await PromptAndImportPhotosAsync();
    }

    private async Task PromptAndImportPhotosAsync(LayoutSlot? targetSlot = null)
    {
        if (_importInProgress) return;
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = targetSlot != null ? $"选择放入第 {targetSlot.Index + 1} 格的照片" : "选择要导入打印的照片",
            Filter = "图像文件 (*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            await ImportPhotosAsync(dlg.FileNames, targetSlot);
        }
    }

    private void JumpToExportTab_Click(object sender, RoutedEventArgs e) => _vm.RightTabIndex = 2;

    private void QuickExportMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var jpgItem = new MenuItem { Header = $"当前页 JPG（{_vm.ExportDpi} DPI）…", InputGestureText = "Ctrl+E" };
        jpgItem.Click += (_, _) => _vm.ExportCurrentJpegCommand.Execute(null);
        var pngItem = new MenuItem { Header = $"当前页 PNG（{_vm.ExportDpi} DPI）…" };
        pngItem.Click += (_, _) => _vm.ExportCurrentPngCommand.Execute(null);
        menu.Items.Add(jpgItem);
        menu.Items.Add(pngItem);

        if (_vm.HasMultiplePages)
        {
            menu.Items.Add(new Separator());
            var batchJpg = new MenuItem { Header = $"批量导出全部 JPG（{_vm.Sheets.Count} 页）…" };
            batchJpg.Click += (_, _) => _vm.ExportBatchJpegCommand.Execute(null);
            var batchPng = new MenuItem { Header = $"批量导出全部 PNG（{_vm.Sheets.Count} 页）…" };
            batchPng.Click += (_, _) => _vm.ExportBatchPngCommand.Execute(null);
            menu.Items.Add(batchJpg);
            menu.Items.Add(batchPng);
        }

        menu.Items.Add(new Separator());
        var pdfItem = new MenuItem { Header = $"全部页 PDF（{_vm.Sheets.Count} 页）…" };
        pdfItem.Click += (_, _) => _vm.ExportAllToPdfCommand.Execute(null);
        menu.Items.Add(pdfItem);

        if (_vm.ActiveScenario == LayoutScenario.IdPhoto && _vm.HasSelectedAsset)
        {
            menu.Items.Add(new Separator());
            var digitalId = new MenuItem { Header = "单张电子证件照 JPG…" };
            digitalId.Click += (_, _) => _vm.ExportDigitalIdPhotoCommand.Execute(null);
            menu.Items.Add(digitalId);
        }

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void OpenCalibrationDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveSheet is not { } sheet || !_vm.InstalledPrinters.Contains(_vm.SelectedPrinterName) ||
            !string.Equals(_vm.CurrentCalibration.PrinterName, _vm.SelectedPrinterName, StringComparison.OrdinalIgnoreCase))
        {
            _vm.ShowToast("请先选择打印机，等待设备设置载入。");
            return;
        }
        string printer = _vm.SelectedPrinterName;
        int dpi = _vm.PrintDpi;
        var page = sheet.Clone();
        var profile = _vm.CurrentCalibration.Clone();
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var key = await Task.Run(() => _vm.PrintService.GetGeometryCalibrationKey(page, printer, profile, dpi));
            if (!ReferenceEquals(sheet, _vm.ActiveSheet) || printer != _vm.SelectedPrinterName || dpi != _vm.PrintDpi)
                return;
            var dlg = new CalibrationDialog(_vm, key == null ? profile : profile.ForOutput(key)) { Owner = this };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            _vm.Dialogs.ShowError($"无法打开打印校准：{ex.Message}", "打印校准");
        }
        finally { button.IsEnabled = true; }
    }

    private void OpenCustomTemplateDialog_Click(object sender, RoutedEventArgs e)
    {
        var existing = _vm.SelectedTemplate is { IsCustomFreeform: true } tpl ? tpl : null;
        OpenCustomLayoutEditor(existing, editCurrentPage: _vm.IsCustomFreeformLayout);
    }

    private void EditTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not LayoutTemplateDefinition tpl || tpl.IsBuiltIn) return;
        OpenCustomLayoutEditor(tpl);
    }

    private void OpenCustomLayoutEditor(LayoutTemplateDefinition? existingTemplate, bool editCurrentPage = false)
    {
        var dlg = new CustomLayoutEditorWindow(_vm, existingTemplate, editCurrentPage) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.ResultTemplate != null)
        {
            bool saveToStore = dlg.ApplyMode is CustomLayoutApplyMode.SaveAndApply or CustomLayoutApplyMode.SaveAsNewTemplate;
            _vm.ApplyCustomFreeformTemplate(dlg.ResultTemplate, saveToStore);
        }
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
        RenameCustomTemplate(tpl);
    }

    private void RenameCustomTemplate(LayoutTemplateDefinition tpl)
    {
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

    private void PrintCopies_Commit(object sender, RoutedEventArgs e)
    {
        if (e is KeyEventArgs key)
        {
            if (key.Key != Key.Enter) return;
            key.Handled = true;
        }

        var input = (TextBox)sender;
        if (int.TryParse(input.Text, out int copies))
            _vm.PrintCopies = copies;
        input.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }

    private void OpenPrinterPreferences_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _vm.OpenNativePrinterPreferences(hwnd);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsDialog(_vm) { Owner = this }.ShowDialog();
    }

    private void ShortcutsHelp_Click(object sender, RoutedEventArgs e) => ShowShortcutsHelp();

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

    private void SetManualZoom(double nextZoom)
    {
        double clamped = Math.Clamp(nextZoom, 0.35, 4.0);
        if (Math.Abs(clamped - _manualZoom) < 0.001) return;

        double relCenterX = CanvasScroller.ExtentWidth > 1
            ? (CanvasScroller.HorizontalOffset + CanvasScroller.ViewportWidth * 0.5) / CanvasScroller.ExtentWidth
            : 0.5;
        double relCenterY = CanvasScroller.ExtentHeight > 1
            ? (CanvasScroller.VerticalOffset + CanvasScroller.ViewportHeight * 0.5) / CanvasScroller.ExtentHeight
            : 0.5;

        _manualZoom = clamped;
        UpdateViewportLayoutAndRulers();

        if (_manualZoom > 1.01)
        {
            Dispatcher.BeginInvoke(() =>
            {
                CanvasScroller.ScrollToHorizontalOffset(Math.Max(0, relCenterX * CanvasScroller.ExtentWidth - CanvasScroller.ViewportWidth * 0.5));
                CanvasScroller.ScrollToVerticalOffset(Math.Max(0, relCenterY * CanvasScroller.ExtentHeight - CanvasScroller.ViewportHeight * 0.5));
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetManualZoom(_manualZoom * 1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetManualZoom(_manualZoom / 1.2);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => SetManualZoom(1.0);

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
            SetManualZoom(_manualZoom * (e.Delta > 0 ? 1.12 : 1 / 1.12));
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
        SelectedPhotosButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void ShowMenu(object sender, params object[] items)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = (UIElement)sender,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        foreach (var item in items)
            menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private List<AssetItemViewModel> GetSelectedAssetsInTrayOrder()
    {
        var selected = AssetsList.SelectedItems.Cast<AssetItemViewModel>().ToHashSet();
        return _vm.Assets.Where(selected.Contains).ToList();
    }

    private void PopulateAssetSelectionMenu(ItemsControl menu, IReadOnlyList<AssetItemViewModel> photos)
    {
        if (photos.Count == 0) return;
        bool hasSlots = _vm.ActiveSheet?.Slots.Count > 0;
        bool hasEmptySlots = _vm.ActiveSheet?.Slots.Any(s => string.IsNullOrEmpty(s.PhotoAssetId)) == true;

        if (photos.Count == 1)
        {
            var photo = photos[0];
            var putSlot = new MenuItem
            {
                Header = _vm.SelectedSlot != null ? $"放入第 {_vm.SelectedSlot.Index + 1} 格" : "放入当前格位",
                IsEnabled = hasSlots
            };
            putSlot.Click += (_, _) =>
            {
                var slot = _vm.SelectedSlot ?? _vm.ActiveSheet?.Slots.FirstOrDefault();
                if (slot != null)
                {
                    _vm.AssignAssetToSlot(photo, slot);
                    _vm.ShowToast($"已将 {photo.DisplayName} 放入第 {slot.Index + 1} 格");
                }
            };
            menu.Items.Add(putSlot);

            if (_vm.ActiveSheet?.Slots.Count > 1)
            {
                var fillAll = new MenuItem { Header = "用此照片填满本页" };
                fillAll.Click += (_, _) => _vm.FillAllSlotsWithAsset(photo);
                menu.Items.Add(fillAll);
            }

            if (photo.UsageCount > 0)
            {
                int? firstPage = _vm.GetFirstPlacedPageNumber(photo.Id);
                var locate = new MenuItem
                {
                    Header = firstPage is { } pg ? $"定位到所在页面（第 {pg} 页）" : "定位到所在页面"
                };
                locate.Click += (_, _) => _vm.LocateAssetOnPages(photo);
                menu.Items.Add(locate);
            }
        }
        else if (hasSlots)
        {
            if (hasEmptySlots)
            {
                var fillEmpty = new MenuItem { Header = $"填入本页空位（所选 {photos.Count} 张）" };
                fillEmpty.Click += (_, _) => _vm.FillEmptySlotsWithAssets(photos);
                menu.Items.Add(fillEmpty);
            }
            var seqFill = new MenuItem { Header = $"按顺序填满本页（所选 {photos.Count} 张）" };
            seqFill.Click += (_, _) => _vm.SequentialFillCurrentSheet(photos);
            menu.Items.Add(seqFill);
        }

        var arrange = new MenuItem { Header = "按当前版式排入新页", IsEnabled = hasSlots };
        arrange.Click += (_, _) => _vm.PaginateSelectedPhotos(photos);
        var strategy = new MenuItem { Header = "选择排版策略…", IsEnabled = hasSlots };
        strategy.Click += async (_, _) =>
        {
            var dlg = new ImportPlacementDialog(_vm, photos.Count, arrangingSelection: true) { Owner = this };
            if (dlg.ShowDialog() == true)
                await _vm.ArrangeSelectedPhotosAsync(photos, dlg.Arrangement);
        };
        menu.Items.Add(arrange);
        menu.Items.Add(strategy);

        menu.Items.Add(new Separator());
        if (photos.Count == 1)
        {
            var copyColor = new MenuItem { Header = "复制调色参数", InputGestureText = "Ctrl+C" };
            copyColor.Click += (_, _) =>
            {
                SelectAssetFromTray(photos[0]);
                _vm.CopyColorParametersCommand.Execute(null);
            };
            menu.Items.Add(copyColor);
        }
        var pasteColor = new MenuItem
        {
            Header = photos.Count == 1 ? "粘贴调色参数" : $"粘贴调色参数到所选 {photos.Count} 张",
            InputGestureText = photos.Count == 1 ? "Ctrl+V" : "",
            IsEnabled = _vm.HasCopiedColorEdit
        };
        pasteColor.Click += (_, _) => _vm.PasteColorToAssets(photos);
        menu.Items.Add(pasteColor);

        if (photos.Count == 1)
        {
            var reveal = new MenuItem { Header = "在资源管理器中显示原图" };
            reveal.Click += (_, _) => _vm.RevealAssetInExplorer(photos[0]);
            menu.Items.Add(reveal);
        }

        menu.Items.Add(new Separator());
        var remove = new MenuItem
        {
            Header = photos.Count == 1 ? "从工程移除" : $"从工程移除 {photos.Count} 张",
            InputGestureText = "Delete"
        };
        remove.Click += (_, _) => RemoveSelectedPhotos(photos);
        menu.Items.Add(remove);
    }

    private void SelectedPhotos_Click(object sender, RoutedEventArgs e)
    {
        var photos = GetSelectedAssetsInTrayOrder();
        if (photos.Count == 0) return;
        var menu = new ContextMenu
        {
            PlacementTarget = (UIElement)sender,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        PopulateAssetSelectionMenu(menu, photos);
        menu.IsOpen = true;
    }

    private void SelectUnplacedPhotos_Click(object sender, RoutedEventArgs e)
    {
        var unplaced = _vm.Assets.Where(a => a.UsageCount == 0).ToList();
        if (unplaced.Count == 0) return;
        ShowPageTray(false);
        _syncingAssetSelection = true;
        try
        {
            AssetsList.ReplaceSelection(unplaced);
            AssetsList.ScrollIntoView(unplaced[0]);
        }
        finally { _syncingAssetSelection = false; }
        SelectAssetFromTray(unplaced[0]);
        _vm.ShowToast($"已选中 {unplaced.Count} 张未排版照片，可点击「已选 {unplaced.Count} 张」排版或移除");
    }

    private void AssetsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var asset = e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(AssetsList, element) is ListBoxItem item
                ? item.DataContext as AssetItemViewModel : null;
        if (asset == null) return;
        if (!_vm.QuickPlaceAsset(asset) && _vm.SelectedSlot?.PhotoAssetId == asset.Id)
            _vm.SetViewModeCommand.Execute(CanvasViewMode.SingleRetouchCrop);
        e.Handled = true;
    }

    private void AssetsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var asset = e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(AssetsList, element) is ListBoxItem item
                ? item.DataContext as AssetItemViewModel : null;
        if (asset != null && !AssetsList.SelectedItems.Contains(asset))
        {
            _syncingAssetSelection = true;
            try { AssetsList.ReplaceSelection(new[] { asset }); }
            finally { _syncingAssetSelection = false; }
            SelectAssetFromTray(asset);
        }
    }

    private void AssetsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (AssetsList.ContextMenu is not { } menu) return;
        menu.Items.Clear();

        var clickedAsset = e.CursorLeft < 0 ? AssetsList.SelectedItem as AssetItemViewModel :
            e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(AssetsList, element) is ListBoxItem item
                ? item.DataContext as AssetItemViewModel : null;
        var photos = GetSelectedAssetsInTrayOrder();

        if (clickedAsset != null && photos.Count > 0)
        {
            PopulateAssetSelectionMenu(menu, photos);
            return;
        }

        var importItem = new MenuItem { Header = "导入照片…", InputGestureText = "Ctrl+O" };
        importItem.Click += ImportPhotos_Click;
        menu.Items.Add(importItem);

        if (_vm.Assets.Count > 0)
        {
            var selectAll = new MenuItem { Header = "全选照片", InputGestureText = "Ctrl+A" };
            selectAll.Click += (_, _) => AssetsList.SelectAll();
            menu.Items.Add(selectAll);
        }
        if (_vm.UnplacedPhotoCount > 0)
        {
            var selectUnplaced = new MenuItem { Header = $"选中未排版照片（{_vm.UnplacedPhotoCount} 张）" };
            selectUnplaced.Click += SelectUnplacedPhotos_Click;
            menu.Items.Add(selectUnplaced);

            var removeUnplaced = new MenuItem { Header = $"移除未排版照片（{_vm.UnplacedPhotoCount} 张）" };
            removeUnplaced.Click += (_, _) => _vm.RemoveUnplacedPhotos();
            menu.Items.Add(removeUnplaced);
        }
        if (_vm.Assets.Count > 0)
        {
            menu.Items.Add(new Separator());
            var clearPool = new MenuItem { Header = $"清空全部素材（{_vm.Assets.Count} 张）" };
            clearPool.Click += (_, _) => RemoveSelectedPhotos(_vm.Assets.ToList());
            menu.Items.Add(clearPool);
        }
    }

    private void RemoveSelectedPhotos(IReadOnlyList<AssetItemViewModel> photos)
    {
        if (photos.Count == 0) return;
        int uses = photos.Sum(photo => photo.UsageCount);
        if (uses > 0 && MessageBox.Show(this,
            $"这会清空工程中引用所选照片的 {uses} 个格位。原始文件不会删除。是否继续？",
            "移除照片", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _vm.RemovePhotos(photos);
        _vm.ShowToast($"已从工程移除 {photos.Count} 张照片，可撤销。");
    }

    private void AssetMore_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<object>
        {
            new MenuItem { Header = "用当前照片填满本页", Command = _vm.FillAllSlotsWithSelectedAssetCommand, IsEnabled = _vm.HasSelectedAsset },
            new MenuItem { Header = "按素材顺序填满本页", Command = _vm.SequentialFillAllAssetsCommand, IsEnabled = _vm.Assets.Count > 0 },
            new MenuItem { Header = "清空本页照片", Command = _vm.ClearActiveSheetSlotsCommand, IsEnabled = _vm.HasPlacedPhotosOnActiveSheet },
            new Separator()
        };

        if (_vm.UnplacedPhotoCount > 0)
        {
            var selectUnplaced = new MenuItem { Header = $"选中未排版照片（{_vm.UnplacedPhotoCount} 张）" };
            selectUnplaced.Click += SelectUnplacedPhotos_Click;
            items.Add(selectUnplaced);

            var removeUnplaced = new MenuItem { Header = $"移除未排版照片（{_vm.UnplacedPhotoCount} 张）" };
            removeUnplaced.Click += (_, _) => _vm.RemoveUnplacedPhotos();
            items.Add(removeUnplaced);
        }

        var clearPool = new MenuItem { Header = "清空全部素材", IsEnabled = _vm.Assets.Count > 0 };
        clearPool.Click += (_, _) => RemoveSelectedPhotos(_vm.Assets.ToList());
        items.Add(clearPool);

        items.Add(new Separator());
        items.Add(new MenuItem { Header = "载入示例照片", Command = _vm.LoadSamplePhotosCommand });
        items.Add(AssetTrayToggleItem());
        ShowMenu(sender, items.ToArray());
    }

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
        var items = new List<object>
        {
            new MenuItem { Header = "新增空白页", InputGestureText = "Ctrl+Shift+N", Command = _vm.AddBlankSheetCommand },
            new MenuItem { Header = "复制当前页", InputGestureText = "Ctrl+Shift+D", Command = _vm.DuplicateActiveSheetCommand }
        };
        if (_vm.HasMultiplePages && _vm.ActiveSheet is { } active)
        {
            int idx = _vm.Sheets.IndexOf(active);
            var movePrev = new MenuItem { Header = "前移当前页", IsEnabled = idx > 0 };
            movePrev.Click += (_, _) => { if (_vm.MoveSheet(active, -1)) SyncPageTraySelection(); };
            var moveNext = new MenuItem { Header = "后移当前页", IsEnabled = idx >= 0 && idx < _vm.Sheets.Count - 1 };
            moveNext.Click += (_, _) => { if (_vm.MoveSheet(active, 1)) SyncPageTraySelection(); };
            items.Add(movePrev);
            items.Add(moveNext);
        }
        items.Add(new Separator());
        items.Add(new MenuItem { Header = "清空本页照片", Command = _vm.ClearActiveSheetSlotsCommand, IsEnabled = _vm.HasPlacedPhotosOnActiveSheet });
        items.Add(new MenuItem { Header = "重新分页全部照片（替换现有页）", Command = _vm.AutoPaginateAllAssetsCommand, IsEnabled = _vm.Assets.Count > 0 });
        items.Add(new Separator());
        items.Add(new MenuItem { Header = "删除当前页", Command = _vm.RemoveActiveSheetCommand, IsEnabled = _vm.Sheets.Count > 1 });
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
        _deferAssetMultiSelectReset = false;
        _assetDragCandidate = e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(AssetsList, element) is ListBoxItem item
                ? item.DataContext as AssetItemViewModel : null;
        if ((_assetDragCandidate == null || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) && TryBeginTrayMarquee(AssetsList, e))
        {
            _assetDragCandidate = null;
            return;
        }
        // Keep multi-selection alive on single mouse-down so the user can drag all selected photos onto the canvas.
        if (e.ClickCount == 1 && _assetDragCandidate != null && Keyboard.Modifiers == ModifierKeys.None &&
            AssetsList.SelectedItems.Count > 1 && AssetsList.SelectedItems.Contains(_assetDragCandidate))
        {
            _deferAssetMultiSelectReset = true;
            AssetsList.Focus();
            e.Handled = true;
        }
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
            Keyboard.Modifiers != ModifierKeys.None) return;
        Point pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _assetDragStart.X) > 6 || Math.Abs(pos.Y - _assetDragStart.Y) > 6)
        {
            var asset = _assetDragCandidate;
            _assetDragCandidate = null;
            _deferAssetMultiSelectReset = false;
            var selected = AssetsList.SelectedItems.Contains(asset) && AssetsList.SelectedItems.Count > 1
                ? GetSelectedAssetsInTrayOrder()
                : [asset];
            string payload = selected.Count > 1
                ? $"PhotoPrintAssets:{string.Join(",", selected.Select(a => a.Id))}"
                : $"PhotoPrintAsset:{asset.Id}";
            DragDrop.DoDragDrop(AssetsList, new DataObject(DataFormats.UnicodeText, payload), DragDropEffects.Copy);
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

    private void CanvasScroller_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            if (CanvasScroller.ScrollableWidth > 0 || CanvasScroller.ScrollableHeight > 0)
            {
                _isMiddlePanningCanvas = true;
                _middlePanStart = e.GetPosition(CanvasScroller);
                _middlePanStartH = CanvasScroller.HorizontalOffset;
                _middlePanStartV = CanvasScroller.VerticalOffset;
                CanvasScroller.CaptureMouse();
                CanvasScroller.Cursor = Cursors.ScrollAll;
                e.Handled = true;
            }
            return;
        }
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject src &&
            FindFocusAncestor<Button>(src) == null)
        {
            ViewportHost.Focus();
        }
    }

    private void CanvasScroller_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMiddlePanningCanvas) return;
        if (e.MiddleButton != MouseButtonState.Pressed)
        {
            EndMiddlePanCanvas();
            return;
        }
        var current = e.GetPosition(CanvasScroller);
        CanvasScroller.ScrollToHorizontalOffset(_middlePanStartH - (current.X - _middlePanStart.X));
        CanvasScroller.ScrollToVerticalOffset(_middlePanStartV - (current.Y - _middlePanStart.Y));
        e.Handled = true;
    }

    private void CanvasScroller_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _isMiddlePanningCanvas)
        {
            EndMiddlePanCanvas();
            e.Handled = true;
        }
    }

    private void CanvasScroller_LostMouseCapture(object sender, MouseEventArgs e) => EndMiddlePanCanvas();

    private void EndMiddlePanCanvas()
    {
        if (!_isMiddlePanningCanvas) return;
        _isMiddlePanningCanvas = false;
        if (CanvasScroller.IsMouseCaptured) CanvasScroller.ReleaseMouseCapture();
        CanvasScroller.Cursor = null;
    }

    private void CanvasImage_DragOver(object sender, DragEventArgs e)
    {
        bool isAssetPayload = e.Data.GetDataPresent(DataFormats.UnicodeText) &&
            e.Data.GetData(DataFormats.UnicodeText) is string s &&
            (s.StartsWith("PhotoPrintAsset:", StringComparison.Ordinal) ||
             s.StartsWith("PhotoPrintAssets:", StringComparison.Ordinal));
        if (isAssetPayload || e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            if (_vm.ViewMode == CanvasViewMode.SheetOverview)
            {
                var target = FindSlotAtCanvasPosition(e.GetPosition(CanvasPreviewImage));
                RenderDropTargetOverlay(target);
            }
        }
    }

    private void CanvasImage_DragLeave(object sender, DragEventArgs e)
    {
        if (!_vm.IsTwoPointStraightenActive)
            OverlayMarksCanvas.Children.Clear();
    }

    private async void CanvasImage_Drop(object sender, DragEventArgs e)
    {
        if (!_vm.IsTwoPointStraightenActive)
            OverlayMarksCanvas.Children.Clear();

        var pos = e.GetPosition(CanvasPreviewImage);
        var slot = _vm.ViewMode == CanvasViewMode.SheetOverview ? FindSlotAtCanvasPosition(pos) : _vm.SelectedSlot;

        if (e.Data.GetDataPresent(DataFormats.UnicodeText) &&
            e.Data.GetData(DataFormats.UnicodeText) is string tag)
        {
            if (tag.StartsWith("PhotoPrintAssets:", StringComparison.Ordinal))
            {
                var ids = tag["PhotoPrintAssets:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
                var lookup = _vm.Assets.ToDictionary(a => a.Id);
                var photos = ids.Select(id => lookup.TryGetValue(id, out var vm) ? vm : null).OfType<AssetItemViewModel>().ToList();
                if (photos.Count > 0)
                {
                    _vm.AssignAssetsStartingAtSlot(photos, slot);
                    e.Handled = true;
                }
                return;
            }
            if (tag.StartsWith("PhotoPrintAsset:", StringComparison.Ordinal))
            {
                string assetId = tag["PhotoPrintAsset:".Length..];
                var assetVm = _vm.Assets.FirstOrDefault(a => a.Id == assetId);
                if (assetVm != null)
                {
                    var target = slot ?? _vm.ActiveSheet?.Slots.FirstOrDefault(s => string.IsNullOrEmpty(s.PhotoAssetId));
                    if (target != null)
                    {
                        _vm.AssignAssetToSlot(assetVm, target);
                        _vm.ShowToast($"已将 {assetVm.DisplayName} 放入第 {target.Index + 1} 格");
                    }
                    else
                    {
                        _vm.ShowToast("请将照片拖放到具体格位上进行替换");
                    }
                    e.Handled = true;
                }
                return;
            }
        }
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            await ImportPhotosAsync(files, slot);
        }
    }

    private async void CanvasPreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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
                    if (!string.IsNullOrEmpty(slot.PhotoAssetId))
                        _vm.SetViewModeCommand.Execute(CanvasViewMode.SingleRetouchCrop);
                    else
                        await PromptAndImportPhotosAsync(slot);
                }
                else if (_vm.ActiveSheet is { } sheet && slot.PhotoAssetId is { } assetId)
                {
                    if (Keyboard.Modifiers == ModifierKeys.Alt && sheet.Slots.Count > 1)
                    {
                        _slotSwapDrag = (sheet, slot);
                        _slotSwapTarget = null;
                        CanvasPreviewImage.CaptureMouse();
                        CanvasPreviewImage.Cursor = Cursors.SizeAll;
                    }
                    else
                    {
                        _overviewDrag = (sheet, slot, assetId);
                        _lastMousePos = pos;
                        CanvasPreviewImage.CaptureMouse();
                        CanvasPreviewImage.Cursor = Cursors.Hand;
                    }
                }
            }
            return;
        }

        if (_vm.ViewMode == CanvasViewMode.SplitCompare)
        {
            if (e.ClickCount == 2)
            {
                _vm.SplitCompareRatio = 0.5;
                return;
            }
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

        if (_slotSwapDrag is { } swap)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _vm.ViewMode != CanvasViewMode.SheetOverview || _vm.ActiveSheet != swap.Sheet)
            {
                if (CanvasPreviewImage.IsMouseCaptured) CanvasPreviewImage.ReleaseMouseCapture();
                FinishCanvasDrag();
                return;
            }
            var candidate = FindSlotAtCanvasPosition(pos);
            _slotSwapTarget = candidate != null && !ReferenceEquals(candidate, swap.SourceSlot) ? candidate : null;
            RenderDropTargetOverlay(_slotSwapTarget);
            CanvasPreviewImage.Cursor = _slotSwapTarget != null ? Cursors.SizeAll : Cursors.No;
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
            CanvasPreviewImage.Cursor = hovered?.PhotoAssetId != null
                ? (Keyboard.Modifiers == ModifierKeys.Alt && _vm.ActiveSheet?.Slots.Count > 1 ? Cursors.SizeAll : Cursors.Hand)
                : Cursors.Arrow;
        }
        else
        {
            CanvasPreviewImage.Cursor = _vm.IsTwoPointStraightenActive ? Cursors.Cross
                : _vm.ViewMode == CanvasViewMode.SplitCompare ? Cursors.SizeWE : Cursors.Hand;
            if (_vm.IsTwoPointStraightenActive && _firstHorizonPoint is { } p1)
                RenderTwoPointStraightenGuide(p1, pos);
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
        if (_vm.IsTwoPointStraightenActive && _firstHorizonPoint is { } p1)
        {
            OverlayMarksCanvas.Children.Clear();
            DrawPointMarker(p1);
            return;
        }
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
            Fill = new SolidColorBrush(Color.FromArgb(20, 59, 130, 246))
        };
        Canvas.SetLeft(rect, hovered.XMm * scaleX);
        Canvas.SetTop(rect, hovered.YMm * scaleY);
        OverlayMarksCanvas.Children.Add(rect);
    }

    private void RenderDropTargetOverlay(LayoutSlot? target)
    {
        if (_vm.IsTwoPointStraightenActive) return;
        OverlayMarksCanvas.Children.Clear();
        if (target == null || _vm.ActiveSheet == null) return;

        double scaleX = PaperShadowBorder.Width / Math.Max(1, _vm.ActiveSheet.PaperWidthMm);
        double scaleY = PaperShadowBorder.Height / Math.Max(1, _vm.ActiveSheet.PaperHeightMm);

        var rect = new Rectangle
        {
            Width = Math.Max(4, target.WidthMm * scaleX),
            Height = Math.Max(4, target.HeightMm * scaleY),
            Fill = new SolidColorBrush(Color.FromArgb(46, 59, 130, 246)),
            Stroke = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
            StrokeThickness = 2.2,
            StrokeDashArray = [5, 3]
        };
        Canvas.SetLeft(rect, target.XMm * scaleX);
        Canvas.SetTop(rect, target.YMm * scaleY);
        OverlayMarksCanvas.Children.Add(rect);
    }

    private void CanvasPreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var swap = _slotSwapDrag;
        var swapTarget = _slotSwapTarget;
        _slotSwapDrag = null;
        _slotSwapTarget = null;
        if (CanvasPreviewImage.IsMouseCaptured)
            CanvasPreviewImage.ReleaseMouseCapture();
        FinishCanvasDrag();
        if (swap is { } s)
        {
            OverlayMarksCanvas.Children.Clear();
            CanvasPreviewImage.Cursor = null;
            if (swapTarget != null && _vm.ActiveSheet == s.Sheet)
                _vm.SwapOrMoveSlot(s.SourceSlot, swapTarget);
        }
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
        if (_slotSwapDrag != null)
        {
            _slotSwapDrag = null;
            _slotSwapTarget = null;
            OverlayMarksCanvas.Children.Clear();
            CanvasPreviewImage.Cursor = null;
        }
        _overviewDrag = null;
        _isDraggingSplit = false;
    }

    private void CanvasPreviewImage_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (CanvasPreviewImage.ContextMenu is not { } menu) return;
        menu.Items.Clear();

        if (_vm.ViewMode != CanvasViewMode.SheetOverview)
        {
            var backOverview = new MenuItem { Header = "返回排版总览", InputGestureText = "Esc" };
            backOverview.Click += (_, _) => _vm.SetViewModeCommand.Execute(CanvasViewMode.SheetOverview);
            menu.Items.Add(backOverview);
            if (_vm.HasSelectedAsset)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "逆时针旋转 90°", InputGestureText = "Ctrl+[", Command = _vm.RotateQuarterLeftCommand });
                menu.Items.Add(new MenuItem { Header = "顺时针旋转 90°", InputGestureText = "Ctrl+]", Command = _vm.RotateQuarterRightCommand });
                menu.Items.Add(new MenuItem { Header = "水平镜像翻转", Command = _vm.ToggleFlipHorizontalCommand });
                menu.Items.Add(new MenuItem { Header = "重置裁剪与旋转", Command = _vm.ResetCropGeometryCommand, IsEnabled = _vm.HasCropGeometryEdits });
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "复制调色参数", InputGestureText = "Ctrl+C", Command = _vm.CopyColorParametersCommand });
                menu.Items.Add(new MenuItem { Header = "粘贴调色参数", InputGestureText = "Ctrl+V", Command = _vm.PasteColorToSelectedCommand, IsEnabled = _vm.CanPasteColorToSelected });
            }
            return;
        }

        var slot = e.CursorLeft >= 0
            ? FindSlotAtCanvasPosition(Mouse.GetPosition(CanvasPreviewImage))
            : _vm.SelectedSlot;
        if (slot != null && (slot != _vm.SelectedSlot || slot.PhotoAssetId != null && slot.PhotoAssetId != _vm.SelectedAsset?.Id))
            _vm.SelectSlot(slot);
        bool multiSlot = _vm.ActiveSheet?.Slots.Count > 1;
        if (slot != null && !string.IsNullOrEmpty(slot.PhotoAssetId))
        {
            var singleEdit = new MenuItem { Header = "进入单图裁剪精修", InputGestureText = "R" };
            singleEdit.Click += CtxSingleEdit_Click;
            menu.Items.Add(singleEdit);

            var replacePhoto = new MenuItem { Header = "替换为本地照片…" };
            replacePhoto.Click += async (_, _) => await PromptAndImportPhotosAsync(slot);
            menu.Items.Add(replacePhoto);

            if (multiSlot)
                menu.Items.Add(new MenuItem { Header = "用此照片填满本页", Command = _vm.FillAllSlotsWithSelectedAssetCommand });
            if (_vm.CanForkIndependentAsset)
                menu.Items.Add(new MenuItem { Header = "拆分独立微调", Command = _vm.ForkIndependentAssetForSelectedSlotCommand });

            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "逆时针旋转 90°", InputGestureText = "Ctrl+[", Command = _vm.RotateQuarterLeftCommand });
            menu.Items.Add(new MenuItem { Header = "顺时针旋转 90°", InputGestureText = "Ctrl+]", Command = _vm.RotateQuarterRightCommand });
            menu.Items.Add(new MenuItem { Header = "旋转以适配格位方向", Command = _vm.AutoOrientPhotoToSlotCommand });
            menu.Items.Add(new MenuItem { Header = "水平镜像翻转", Command = _vm.ToggleFlipHorizontalCommand });
            var fitToggle = new MenuItem { Header = _vm.IsCoverFitMode ? "切换为完整显示（留白）" : "切换为裁切填满格位" };
            fitToggle.Click += (_, _) => _vm.IsCoverFitMode = !_vm.IsCoverFitMode;
            menu.Items.Add(fitToggle);
            if (slot.PolaroidStyle == null)
            {
                var editCaption = new MenuItem { Header = string.IsNullOrWhiteSpace(slot.CustomCaption) ? "添加格位标注…" : "编辑格位标注…" };
                editCaption.Click += (_, _) =>
                {
                    var dlg = new PromptInputDialog("格位标注", "输入显示在照片底部的角标文字（留空可清除）：",
                        slot.CustomCaption ?? "", _vm.IsDarkTheme, allowEmpty: true) { Owner = this };
                    if (dlg.ShowDialog() == true)
                        _vm.UpdateSelectedSlotCaption(dlg.InputText);
                };
                menu.Items.Add(editCaption);
            }
            menu.Items.Add(new MenuItem { Header = "重置裁剪与旋转", Command = _vm.ResetCropGeometryCommand, IsEnabled = _vm.HasCropGeometryEdits });

            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "复制调色参数", InputGestureText = "Ctrl+C", Command = _vm.CopyColorParametersCommand });
            menu.Items.Add(new MenuItem { Header = "粘贴调色参数", InputGestureText = "Ctrl+V", Command = _vm.PasteColorToSelectedCommand, IsEnabled = _vm.CanPasteColorToSelected });

            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "清空此格位", InputGestureText = "Delete", Command = _vm.ClearSelectedSlotCommand });
            if (multiSlot)
                menu.Items.Add(new MenuItem { Header = "清空本页全部照片", Command = _vm.ClearActiveSheetSlotsCommand, IsEnabled = _vm.HasPlacedPhotosOnActiveSheet });
            return;
        }

        if (slot != null)
        {
            if (_vm.SelectedAsset is { } selAsset)
            {
                var putSelected = new MenuItem { Header = $"放入「{selAsset.DisplayName}」" };
                putSelected.Click += (_, _) =>
                {
                    _vm.AssignAssetToSlot(selAsset, slot);
                    _vm.ShowToast($"已将 {selAsset.DisplayName} 放入第 {slot.Index + 1} 格");
                };
                menu.Items.Add(putSelected);
            }
            var importToSlot = new MenuItem { Header = "导入照片到此格位…" };
            importToSlot.Click += async (_, _) => await PromptAndImportPhotosAsync(slot);
            menu.Items.Add(importToSlot);

            if (_vm.Assets.Count > 0)
                menu.Items.Add(new MenuItem { Header = "按素材顺序填满本页", Command = _vm.SequentialFillAllAssetsCommand });
            if (_vm.HasPlacedPhotosOnActiveSheet)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "清空本页全部照片", Command = _vm.ClearActiveSheetSlotsCommand });
            }
            return;
        }

        var importPage = new MenuItem { Header = "导入照片…", InputGestureText = "Ctrl+O" };
        importPage.Click += ImportPhotos_Click;
        menu.Items.Add(importPage);
        if (_vm.Assets.Count > 0)
            menu.Items.Add(new MenuItem { Header = "按素材顺序填满本页", Command = _vm.SequentialFillAllAssetsCommand });
        if (_vm.HasPlacedPhotosOnActiveSheet)
            menu.Items.Add(new MenuItem { Header = "清空本页全部照片", Command = _vm.ClearActiveSheetSlotsCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "新增空白页", InputGestureText = "Ctrl+Shift+N", Command = _vm.AddBlankSheetCommand });
        menu.Items.Add(new MenuItem { Header = "复制当前页", InputGestureText = "Ctrl+Shift+D", Command = _vm.DuplicateActiveSheetCommand });
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

    private void RenderTwoPointStraightenGuide(Point p1, Point p2)
    {
        OverlayMarksCanvas.Children.Clear();
        var shadowLine = new Line
        {
            X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
            Stroke = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
            StrokeThickness = 3.0
        };
        var guideLine = new Line
        {
            X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
            StrokeDashArray = [5, 3]
        };
        OverlayMarksCanvas.Children.Add(shadowLine);
        OverlayMarksCanvas.Children.Add(guideLine);
        DrawPointMarker(p1);
        DrawPointMarker(p2);

        double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
        if (Math.Sqrt(dx * dx + dy * dy) >= 8)
        {
            double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (angle > 90) angle -= 180;
            if (angle < -90) angle += 180;
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(205, 15, 23, 42)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock
                {
                    Text = $"{-angle:+0.0;-0.0;0.0}°",
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold
                }
            };
            double maxLeft = Math.Max(4, OverlayMarksCanvas.ActualWidth - 54);
            double maxTop = Math.Max(4, OverlayMarksCanvas.ActualHeight - 24);
            Canvas.SetLeft(badge, Math.Clamp((p1.X + p2.X) * 0.5 + 8, 4, maxLeft));
            Canvas.SetTop(badge, Math.Clamp((p1.Y + p2.Y) * 0.5 - 24, 4, maxTop));
            OverlayMarksCanvas.Children.Add(badge);
        }
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

    private void CaptionTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _vm.EndContinuousEdit();
    }
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
}
