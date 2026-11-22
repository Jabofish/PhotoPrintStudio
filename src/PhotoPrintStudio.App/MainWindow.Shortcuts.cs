using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoPrintStudio.App.Controls;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.App.Views;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App;

public partial class MainWindow
{
    private const ModifierKeys ControlShift = ModifierKeys.Control | ModifierKeys.Shift;
    private const ModifierKeys AltShift = ModifierKeys.Alt | ModifierKeys.Shift;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = HandleShortcut(key, Keyboard.Modifiers, e.IsRepeat, Keyboard.FocusedElement as DependencyObject);
    }

    internal bool HandleShortcut(Key key, ModifierKeys modifiers, bool isRepeat, DependencyObject? focus)
    {
        if ((key, modifiers) == (Key.F1, ModifierKeys.None))
            return RunOnce(ShowShortcutsHelp, isRepeat);

        if (key == Key.S && modifiers is ModifierKeys.Control or ControlShift)
        {
            if (isRepeat) return true;
            if (focus is TextBox box)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                FindFocusAncestor<StudioSliderRow>(box)?.CommitPendingValue();
            }
            return ExecuteShortcut(modifiers == ModifierKeys.Control ? _vm.SaveProjectCommand : _vm.SaveProjectAsCommand);
        }

        if (key == Key.OemComma && modifiers is ModifierKeys.Control or ControlShift)
            return RunOnce(() =>
            {
                if (modifiers == ModifierKeys.Control) Settings_Click(this, new RoutedEventArgs());
                else PreviewSettings_Click(this, new RoutedEventArgs());
            }, isRepeat);

        // 输入、下拉列表和参数控件保留自己的按键。工作区切换使用单独的 Alt 键组。
        if (FindFocusAncestor<ComboBox>(focus)?.IsDropDownOpen == true) return false;
        if (TrySwitchWorkspace(key, modifiers, isRepeat)) return true;
        if (FindFocusAncestor<TextBox>(focus) != null) return false;

        bool photosFocused = FindFocusAncestor<ListBox>(focus) == AssetsList;
        bool pagesFocused = FindFocusAncestor<ListBox>(focus) == PagePreviewList;
        bool canvasFocused = IsWithinElement(focus, ViewportHost);

        if (modifiers == ModifierKeys.None)
        {
            if (key == Key.F2)
            {
                if (FindFocusAncestor<ListBox>(focus) == TemplatesList &&
                    TemplatesList.SelectedItem is LayoutTemplateDefinition { IsBuiltIn: false } template)
                    return RunOnce(() => RenameCustomTemplate(template), isRepeat);
                if (FindFocusAncestor<ComboBox>(focus) == ColorPresetCombo && _vm.IsCustomPresetSelected)
                    return RunOnce(() => RenamePreset_Click(ColorPresetCombo, new RoutedEventArgs()), isRepeat);
            }
            if (key == Key.F6)
                return RunOnce(() => _vm.SetViewModeCommand.Execute(_vm.ViewMode == CanvasViewMode.SheetOverview
                    ? CanvasViewMode.SingleRetouchCrop : CanvasViewMode.SheetOverview), isRepeat);

            if (key is Key.Delete or Key.Back)
            {
                if (pagesFocused && key == Key.Delete) return RunOnce(DeleteSelectedPages, isRepeat);
                if (photosFocused && key == Key.Delete && AssetsList.SelectedItems.Count > 0)
                    return RunOnce(() => RemoveSelectedPhotos(AssetsList.SelectedItems.Cast<AssetItemViewModel>().ToList()), isRepeat);
                if (canvasFocused && (_vm.ViewMode == CanvasViewMode.SheetOverview ||
                    _vm.SelectedSlot?.PhotoAssetId == _vm.SelectedAsset?.Id))
                    return ExecuteShortcut(_vm.ClearSelectedSlotCommand, isRepeat);
            }

            bool editingControl = FindFocusAncestor<ListBox>(focus) != null || FindFocusAncestor<ComboBox>(focus) != null ||
                FindFocusAncestor<StudioSliderRow>(focus) != null;
            if (key is Key.PageUp or Key.PageDown && (canvasFocused || !editingControl))
                return ExecuteShortcut(key == Key.PageUp ? _vm.PrevSheetCommand : _vm.NextSheetCommand);

            if (key == Key.Escape)
            {
                if (_vm.IsTwoPointStraightenActive)
                {
                    _firstHorizonPoint = null;
                    OverlayMarksCanvas.Children.Clear();
                    return ExecuteShortcut(_vm.ToggleTwoPointStraightenCommand);
                }
                if (_vm.ViewMode != CanvasViewMode.SheetOverview)
                {
                    ExecuteShortcut(_vm.SetViewModeCommand, parameter: CanvasViewMode.SheetOverview);
                    ViewportHost.Focus();
                    return true;
                }
            }

            if (canvasFocused)
                return key switch
                {
                    Key.Left or Key.Up => ExecuteShortcut(_vm.PrevSheetCommand),
                    Key.Right or Key.Down => ExecuteShortcut(_vm.NextSheetCommand),
                    Key.Home => RunOnce(() => _vm.SwitchToSheet(0), isRepeat),
                    Key.End => RunOnce(() => _vm.SwitchToSheet(_vm.Sheets.Count - 1), isRepeat),
                    Key.G => ExecuteShortcut(_vm.SetViewModeCommand, isRepeat, CanvasViewMode.SheetOverview),
                    Key.R when _vm.HasSelectedAsset => ExecuteShortcut(_vm.SetViewModeCommand, isRepeat, CanvasViewMode.SingleRetouchCrop),
                    Key.Y when _vm.HasSelectedAsset || _vm.ViewMode == CanvasViewMode.SplitCompare =>
                        ExecuteShortcut(_vm.SetViewModeCommand, isRepeat,
                            _vm.ViewMode == CanvasViewMode.SplitCompare ? _viewBeforeCompare : CanvasViewMode.SplitCompare),
                    Key.J => RunOnce(() => _vm.ShowClippingOverlay = !_vm.ShowClippingOverlay, isRepeat),
                    Key.Space => RunOnce(() => _vm.IsHoldingCompare = true, isRepeat),
                    _ => false
                };
            return false;
        }

        if (modifiers == ModifierKeys.Alt && pagesFocused && PagePreviewList.SelectedItems.Count == 1 &&
            PagePreviewList.SelectedItem is PrintSheet page && key is Key.Left or Key.Right)
            return RunOnce(() =>
            {
                if (_vm.MoveSheet(page, key == Key.Left ? -1 : 1))
                {
                    ActivatePageFromTray(page);
                    SyncPageTraySelection();
                    PagePreviewList.UpdateLayout();
                    if (PagePreviewList.ItemContainerGenerator.ContainerFromItem(page) is ListBoxItem movedItem)
                        movedItem.Focus();
                    else
                        PagePreviewList.Focus();
                }
            }, isRepeat);

        if (modifiers == ModifierKeys.Shift && key == Key.T && canvasFocused && _vm.HasSelectedAsset)
            return ExecuteShortcut(_vm.ToggleTwoPointStraightenCommand, isRepeat);

        if (modifiers == ControlShift)
            return key switch
            {
                Key.O => ExecuteShortcut(_vm.OpenProjectCommand, isRepeat),
                Key.E => RunOnce(() => QuickExportMenu_Click(ExportMenuButton, new RoutedEventArgs()), isRepeat),
                Key.Z => ExecuteShortcut(_vm.RedoCommand),
                Key.N => ExecuteShortcut(_vm.AddBlankSheetCommand, isRepeat),
                Key.D => ExecuteShortcut(_vm.DuplicateActiveSheetCommand, isRepeat),
                Key.OemPlus => RunOnce(() => ZoomIn_Click(this, new RoutedEventArgs()), false),
                _ => false
            };

        if (modifiers != ModifierKeys.Control) return false;
        return key switch
        {
            Key.A when photosFocused || pagesFocused => RunOnce((photosFocused ? AssetsList : PagePreviewList).SelectAll, isRepeat),
            Key.Enter when photosFocused && AssetsList.SelectedItems.Count > 0 =>
                RunOnce(() => SelectedPhotos_Click(SelectedPhotosButton, new RoutedEventArgs()), isRepeat),
            Key.N => ExecuteShortcut(_vm.NewProjectCommand, isRepeat),
            Key.O => RunOnce(() => ImportPhotos_Click(this, new RoutedEventArgs()), isRepeat),
            Key.Z => ExecuteShortcut(_vm.UndoCommand),
            Key.Y => ExecuteShortcut(_vm.RedoCommand),
            Key.P => ExecuteShortcut(_vm.PrintActiveSheetCommand, isRepeat),
            Key.E => ExecuteShortcut(_vm.ExportCurrentJpegCommand, isRepeat),
            Key.G => RunOnce(() => JumpToPage_Click(this, new RoutedEventArgs()), isRepeat),
            Key.C => ExecuteShortcut(_vm.CopyColorParametersCommand, isRepeat),
            Key.V when photosFocused && AssetsList.SelectedItems.Count > 0 =>
                RunOnce(() => _vm.PasteColorToAssets(GetSelectedAssetsInTrayOrder()), isRepeat),
            Key.V => ExecuteShortcut(_vm.PasteColorToSelectedCommand, isRepeat),
            Key.OemOpenBrackets => ExecuteShortcut(_vm.RotateQuarterLeftCommand, isRepeat),
            Key.OemCloseBrackets => ExecuteShortcut(_vm.RotateQuarterRightCommand, isRepeat),
            Key.OemPlus or Key.Add => RunOnce(() => ZoomIn_Click(this, new RoutedEventArgs()), false),
            Key.OemMinus or Key.Subtract => RunOnce(() => ZoomOut_Click(this, new RoutedEventArgs()), false),
            Key.D0 or Key.NumPad0 => RunOnce(() => ZoomFit_Click(this, new RoutedEventArgs()), isRepeat),
            _ => false
        };
    }

    private bool TrySwitchWorkspace(Key key, ModifierKeys modifiers, bool isRepeat)
    {
        if (modifiers == AltShift && key is >= Key.D1 and <= Key.D3)
        {
            if (isRepeat) return true;
            _vm.RightTabIndex = 0;
            _vm.ColorSubTabIndex = (int)key - (int)Key.D1;
            return true;
        }
        if (modifiers != ModifierKeys.Alt) return false;
        if (key is >= Key.D1 and <= Key.D3)
        {
            if (!isRepeat) _vm.RightTabIndex = (int)key - (int)Key.D1;
            return true;
        }

        return key switch
        {
            Key.F => RunOnce(() => FileMenuBtn_Click(FileMenuBtn, new RoutedEventArgs()), isRepeat),
            Key.D4 => RunOnce(() => TemplatesList.Focus(), isRepeat),
            Key.D5 => RunOnce(() => ViewportHost.Focus(), isRepeat),
            Key.D6 => RunOnce(() => ShowPageTray(false), isRepeat),
            Key.D7 => RunOnce(() => ShowPageTray(true), isRepeat),
            _ => false
        };
    }

    private static bool ExecuteShortcut(ICommand command, bool suppress = false, object? parameter = null)
    {
        if (!suppress && command.CanExecute(parameter)) command.Execute(parameter);
        return true;
    }

    private static bool RunOnce(Action action, bool suppress)
    {
        if (!suppress) action();
        return true;
    }

    private static T? FindFocusAncestor<T>(DependencyObject? focus) where T : DependencyObject
    {
        for (var node = focus; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : null)
            if (node is T match) return match;
        return null;
    }

    private static bool IsWithinElement(DependencyObject? focus, DependencyObject ancestor)
    {
        for (var node = focus; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : null)
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    private void ShowShortcutsHelp() => new ShortcutsDialog { Owner = this }.ShowDialog();

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _vm.IsHoldingCompare)
            _vm.IsHoldingCompare = false;
    }
}
