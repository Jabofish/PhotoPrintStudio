using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoPrintStudio.App.Controls;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App;

public partial class MainWindow
{
    private sealed record TrayMarquee(AssetTrayListBox List, ScrollViewer Scroll, ScrollContentPresenter Viewport, Point Start,
        Point FirstCard, double CardWidth, double CardHeight, double Stride, object[] InitialSelection,
        ModifierKeys Modifiers, PrintSheet? ClickedPage);

    private TrayMarquee? _trayMarquee;
    private bool _trayMarqueeStarted;
    private bool _syncingPages;
    private PrintSheet? _pageSelectionAnchor;
    private readonly DispatcherTimer _trayMarqueeTimer = new() { Interval = TimeSpan.FromMilliseconds(32) };
    private readonly DispatcherTimer _pagePreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly HashSet<Image> _pagePreviewImages = [];
    private readonly HashSet<TextBlock> _pagePreviewLabels = [];
    private CancellationTokenSource? _pagePreviewCancellation;
    private bool _pagePreviewsDirty;

    private void InitializeTrayInteractions()
    {
        _trayMarqueeTimer.Tick += ScrollTrayMarquee;
        _pagePreviewTimer.Tick += RefreshPagePreviews;
        _vm.PagePreviewsInvalidated += QueuePagePreviews;
        Closed += (_, _) =>
        {
            _vm.PagePreviewsInvalidated -= QueuePagePreviews;
            _trayMarqueeTimer.Stop();
            _pagePreviewTimer.Stop();
            _pagePreviewCancellation?.Cancel();
        };
    }

    private bool TryBeginTrayMarquee(AssetTrayListBox list, MouseButtonEventArgs e, PrintSheet? clickedPage = null)
    {
        if (FindVisualChild<ScrollViewer>(list) is not { } scroll ||
            FindVisualChild<ScrollContentPresenter>(scroll) is not { } viewport ||
            FindVisualChild<VirtualizingStackPanel>(viewport) is not { } panel ||
            panel.Children.OfType<ListBoxItem>().FirstOrDefault() is not { } card) return false;

        Point pos = e.GetPosition(viewport);
        if (!new Rect(viewport.RenderSize).Contains(pos)) return false;
        int index = list.ItemContainerGenerator.IndexFromContainer(card);
        if (index < 0 || card.ActualWidth <= 0) return false;

        double stride = card.ActualWidth + card.Margin.Left + card.Margin.Right;
        Point cardOrigin = card.TranslatePoint(new Point(), viewport);
        var firstCard = new Point(cardOrigin.X + scroll.HorizontalOffset - index * stride, cardOrigin.Y);
        var modifiers = Keyboard.Modifiers;
        var initial = modifiers.HasFlag(ModifierKeys.Control)
            ? list.SelectedItems.Cast<object>().ToArray() : [];

        list.Focus();
        if (!list.CaptureMouse()) return false;
        _trayMarquee = new TrayMarquee(list, scroll, viewport, new Point(pos.X + scroll.HorizontalOffset, pos.Y),
            firstCard, card.ActualWidth, card.ActualHeight, stride, initial, modifiers, clickedPage);
        e.Handled = true;
        return true;
    }

    private void UpdateTrayMarquee(Point pos)
    {
        if (_trayMarquee is not { } drag) return;
        double width = drag.Viewport.ActualWidth, height = drag.Viewport.ActualHeight;
        var end = new Point(Math.Clamp(pos.X, 0, width) + drag.Scroll.HorizontalOffset, Math.Clamp(pos.Y, 0, height));
        if (!_trayMarqueeStarted)
        {
            if (Math.Abs(end.X - drag.Start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(end.Y - drag.Start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _trayMarqueeStarted = true;
            _trayMarqueeTimer.Start();
        }

        var bounds = new Rect(drag.Start, end);
        double offset = drag.Scroll.HorizontalOffset;
        var start = drag.Viewport.TranslatePoint(new Point(Math.Clamp(bounds.Left - offset, 0, width), bounds.Top), TraySelectionCanvas);
        Canvas.SetLeft(TraySelectionRect, start.X);
        Canvas.SetTop(TraySelectionRect, start.Y);
        TraySelectionRect.Width = Math.Max(1, Math.Clamp(bounds.Right - offset, 0, width) - Math.Clamp(bounds.Left - offset, 0, width));
        TraySelectionRect.Height = Math.Max(1, bounds.Height);
        TraySelectionRect.Visibility = Visibility.Visible;

        var selected = drag.InitialSelection.ToHashSet();
        if (bounds.Bottom >= drag.FirstCard.Y && bounds.Top <= drag.FirstCard.Y + drag.CardHeight)
        {
            int first = Math.Max(0, (int)Math.Ceiling((bounds.Left - drag.FirstCard.X - drag.CardWidth) / drag.Stride));
            int last = Math.Min(drag.List.Items.Count - 1, (int)Math.Floor((bounds.Right - drag.FirstCard.X) / drag.Stride));
            for (int i = first; i <= last; i++) selected.Add(drag.List.Items[i]);
        }
        _syncingAssetSelection = true;
        _syncingPages = true;
        try { drag.List.ReplaceSelection(selected); }
        finally { _syncingAssetSelection = _syncingPages = false; }
        if (drag.List == PagePreviewList) UpdateSelectedPagesButton();
    }

    private void ScrollTrayMarquee(object? sender, EventArgs e)
    {
        if (_trayMarquee is not { } drag) return;
        Point pos = Mouse.GetPosition(drag.Viewport);
        double direction = pos.X < 20 ? -1 : pos.X > drag.Viewport.ActualWidth - 20 ? 1 : 0;
        if (direction == 0 || drag.Scroll.ScrollableWidth <= 0) return;
        drag.Scroll.ScrollToHorizontalOffset(drag.Scroll.HorizontalOffset + direction * 24);
        UpdateTrayMarquee(pos);
    }

    private void AssetsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var clicked = _assetDragCandidate;
        bool deferredReset = _deferAssetMultiSelectReset;
        _assetDragCandidate = null;
        _deferAssetMultiSelectReset = false;
        if (_trayMarquee is { } drag && drag.List == AssetsList)
        {
            if (_trayMarqueeStarted) UpdateTrayMarquee(e.GetPosition(drag.Viewport));
            else if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) AssetsList.UnselectAll();
            AssetsList.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (deferredReset && clicked != null)
        {
            _syncingAssetSelection = true;
            try { AssetsList.ReplaceSelection(new[] { clicked }); }
            finally { _syncingAssetSelection = false; }
            SelectAssetFromTray(clicked);
        }
    }

    private void AssetsList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!EndTrayMarquee(AssetsList)) return;
        if (!AssetsList.SelectedItems.Contains(_vm.SelectedAsset))
            SelectAssetFromTray(AssetsList.SelectedItem as AssetItemViewModel);
    }

    private bool EndTrayMarquee(AssetTrayListBox list)
    {
        if (_trayMarquee?.List != list) return false;
        _trayMarquee = null;
        _trayMarqueeStarted = false;
        _trayMarqueeTimer.Stop();
        TraySelectionRect.Visibility = Visibility.Collapsed;
        return true;
    }

    private void TrayPhotos_Click(object sender, RoutedEventArgs e) => ShowPageTray(false);
    private void TrayPages_Click(object sender, RoutedEventArgs e) => ShowPageTray(true);

    private void ShowPageTray(bool showPages)
    {
        PhotosTrayTab.IsChecked = !showPages;
        PagesTrayTab.IsChecked = showPages;
        if (_expandedAssetTrayHeight is { } height)
        {
            AssetTrayRow.Height = height;
            _expandedAssetTrayHeight = null;
            TrayContent.Visibility = Visibility.Visible;
        }
        AssetActions.Visibility = AssetsList.Visibility = showPages ? Visibility.Collapsed : Visibility.Visible;
        PageActions.Visibility = PagePreviewList.Visibility = showPages ? Visibility.Visible : Visibility.Collapsed;
        if (showPages)
        {
            PagePreviewList.Focus();
            SyncPageTraySelection();
            UpdateSelectedPagesButton();
            QueuePagePreviews();
        }
        else
        {
            _pagePreviewTimer.Stop();
            _pagePreviewCancellation?.Cancel();
            AssetsList.Focus();
        }
    }

    private void UpdateSelectedPagesButton()
    {
        if (SelectedPagesButton == null || PagePreviewList == null) return;
        int count = PagePreviewList.SelectedItems.Count;
        SelectedPagesButton.Content = $"已选 {count} 页";
        SelectedPagesButton.IsEnabled = count > 1;
        SelectedPagesButton.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectedPages_Click(object sender, RoutedEventArgs e)
    {
        var pages = PagePreviewList.SelectedItems.Cast<PrintSheet>().ToArray();
        if (pages.Length == 0) return;

        var clearItem = new MenuItem
        {
            Header = $"清空所选 {pages.Length} 页照片",
            IsEnabled = pages.Any(MainViewModel.HasPlacedPhotos)
        };
        clearItem.Click += (_, _) => ClearSelectedPages(pages);

        var deleteItem = new MenuItem
        {
            Header = $"删除所选 {pages.Length} 页",
            InputGestureText = "Delete",
            IsEnabled = _vm.Sheets.Count > 1
        };
        deleteItem.Click += (_, _) => DeleteSelectedPages();

        ShowMenu(sender, clearItem, deleteItem);
    }

    private void ClearSelectedPages(IReadOnlyCollection<PrintSheet> pages)
    {
        int cleared = _vm.ClearSheetsSlots(pages);
        if (cleared > 0)
            _vm.ShowToast($"已清空所选 {pages.Count} 页中的 {cleared} 个格位（照片仍保留在素材池，可撤销）");
    }

    private void PagePreviewList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var page = e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(PagePreviewList, element) is ListBoxItem item
            ? item.DataContext as PrintSheet : null;
        if (page != null && !PagePreviewList.SelectedItems.Contains(page))
        {
            _syncingPages = true;
            try
            {
                PagePreviewList.ReplaceSelection(new[] { page });
                _pageSelectionAnchor = page;
                ActivatePageFromTray(page);
            }
            finally { _syncingPages = false; }
            UpdateSelectedPagesButton();
        }
    }

    private void PagePreviewList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (PagePreviewList.ContextMenu is not { } menu) return;
        menu.Items.Clear();

        var clickedPage = e.CursorLeft < 0 ? PagePreviewList.SelectedItem as PrintSheet :
            e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(PagePreviewList, element) is ListBoxItem item
            ? item.DataContext as PrintSheet : null;
        var selected = PagePreviewList.SelectedItems.Cast<PrintSheet>().ToArray();

        if (clickedPage != null && selected.Length > 1 && selected.Contains(clickedPage))
        {
            var clearMulti = new MenuItem
            {
                Header = $"清空所选 {selected.Length} 页照片",
                IsEnabled = selected.Any(MainViewModel.HasPlacedPhotos)
            };
            clearMulti.Click += (_, _) => ClearSelectedPages(selected);
            var deleteMulti = new MenuItem
            {
                Header = $"删除所选 {selected.Length} 页",
                InputGestureText = "Delete",
                IsEnabled = _vm.Sheets.Count > 1
            };
            deleteMulti.Click += (_, _) => DeleteSelectedPages();
            menu.Items.Add(clearMulti);
            menu.Items.Add(deleteMulti);
            return;
        }

        if (clickedPage != null)
        {
            int idx = _vm.Sheets.IndexOf(clickedPage);
            var dupItem = new MenuItem { Header = "复制此页", InputGestureText = "Ctrl+Shift+D" };
            dupItem.Click += (_, _) =>
            {
                ActivatePageFromTray(clickedPage);
                _vm.DuplicateActiveSheetCommand.Execute(null);
            };
            var insertAfter = new MenuItem { Header = "在此页后新增空白页" };
            insertAfter.Click += (_, _) => _vm.InsertBlankSheetAfter(clickedPage);

            var moveLeft = new MenuItem { Header = "前移一页", IsEnabled = idx > 0 };
            moveLeft.Click += (_, _) => { if (_vm.MoveSheet(clickedPage, -1)) SyncPageTraySelection(); };
            var moveRight = new MenuItem { Header = "后移一页", IsEnabled = idx >= 0 && idx < _vm.Sheets.Count - 1 };
            moveRight.Click += (_, _) => { if (_vm.MoveSheet(clickedPage, 1)) SyncPageTraySelection(); };

            var clearItem = new MenuItem
            {
                Header = "清空此页照片",
                IsEnabled = MainViewModel.HasPlacedPhotos(clickedPage)
            };
            clearItem.Click += (_, _) => ClearSelectedPages([clickedPage]);

            var deleteItem = new MenuItem
            {
                Header = "删除此页",
                InputGestureText = "Delete",
                IsEnabled = _vm.Sheets.Count > 1
            };
            deleteItem.Click += (_, _) =>
            {
                int removed = _vm.RemoveSheets([clickedPage]);
                if (removed > 0) _vm.ShowToast("已删除该页，可撤销");
                SyncPageTraySelection();
            };

            menu.Items.Add(dupItem);
            menu.Items.Add(insertAfter);
            menu.Items.Add(new Separator());
            menu.Items.Add(moveLeft);
            menu.Items.Add(moveRight);
            menu.Items.Add(new Separator());
            menu.Items.Add(clearItem);
            menu.Items.Add(deleteItem);
            return;
        }

        menu.Items.Add(new MenuItem { Header = "新增空白页", InputGestureText = "Ctrl+Shift+N", Command = _vm.AddBlankSheetCommand });
        var removeEmpty = new MenuItem { Header = "删除所有空白页", IsEnabled = _vm.HasRemovableEmptySheets };
        removeEmpty.Click += (_, _) => { _vm.RemoveEmptySheets(); SyncPageTraySelection(); };
        menu.Items.Add(removeEmpty);
    }

    private void PagePreviewList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedPagesButton();
        if (_syncingPages || !PagePreviewList.IsVisible || PagePreviewList.SelectedItems.Count != 1 ||
            e.AddedItems.Count != 1 || e.AddedItems[0] is not PrintSheet page) return;
        _pageSelectionAnchor = page;
        ActivatePageFromTray(page);
    }

    private void SyncPageTraySelection()
    {
        if (_syncingPages || !PagePreviewList.IsVisible || _vm.ActiveSheet is not { } page) return;
        _syncingPages = true;
        try
        {
            if (!PagePreviewList.SelectedItems.Contains(page))
            {
                PagePreviewList.ReplaceSelection(new[] { page });
                _pageSelectionAnchor = page;
            }
            PagePreviewList.ScrollIntoView(page);
        }
        finally { _syncingPages = false; }
        UpdateSelectedPagesButton();
    }

    private void ActivatePageFromTray(PrintSheet page)
    {
        int index = _vm.Sheets.IndexOf(page);
        if (index < 0) return;
        if (page != _vm.ActiveSheet)
            _vm.SwitchToSheet(index);
        ShowPageOverview();
    }

    private void PagePreviewList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var page = e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(PagePreviewList, element) is ListBoxItem item
            ? item.DataContext as PrintSheet : null;
        TryBeginTrayMarquee(PagePreviewList, e, page);
    }

    private void PagePreviewList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_trayMarquee is not { } drag || drag.List != PagePreviewList || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateTrayMarquee(e.GetPosition(drag.Viewport));
        e.Handled = true;
    }

    private void PagePreviewList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_trayMarquee is not { } drag || drag.List != PagePreviewList) return;
        if (_trayMarqueeStarted)
            UpdateTrayMarquee(e.GetPosition(drag.Viewport));
        else
            SelectPageClick(drag.ClickedPage, drag.Modifiers);
        PagePreviewList.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void PagePreviewList_LostMouseCapture(object sender, MouseEventArgs e) => EndTrayMarquee(PagePreviewList);

    private void SelectPageClick(PrintSheet? page, ModifierKeys modifiers)
    {
        bool extend = modifiers.HasFlag(ModifierKeys.Shift);
        bool toggle = modifiers.HasFlag(ModifierKeys.Control);
        _syncingPages = true;
        try
        {
            if (page != null)
                (PagePreviewList.ItemContainerGenerator.ContainerFromItem(page) as ListBoxItem)?.Focus();
            if (page == null)
            {
                if (!toggle) PagePreviewList.UnselectAll();
            }
            else if (extend && _pageSelectionAnchor is { } anchor && _vm.Sheets.IndexOf(anchor) is >= 0 and var start)
            {
                int end = _vm.Sheets.IndexOf(page);
                var range = _vm.Sheets.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1);
                if (toggle) range = range.Union(PagePreviewList.SelectedItems.Cast<PrintSheet>());
                PagePreviewList.ReplaceSelection(range.ToArray());
            }
            else if (toggle)
            {
                if (PagePreviewList.SelectedItems.Contains(page)) PagePreviewList.SelectedItems.Remove(page);
                else PagePreviewList.SelectedItems.Add(page);
                _pageSelectionAnchor = page;
            }
            else
            {
                PagePreviewList.ReplaceSelection(new[] { page });
                _pageSelectionAnchor = page;
                ActivatePageFromTray(page);
            }
        }
        finally { _syncingPages = false; }
        UpdateSelectedPagesButton();
    }

    private void DeleteSelectedPages()
    {
        var pages = PagePreviewList.SelectedItems.Cast<PrintSheet>().ToArray();
        if (pages.Length == 0) return;
        int removed = _vm.RemoveSheets(pages);
        if (removed == 0) _vm.ShowToast("工程至少保留一页");
        else _vm.ShowToast($"已删除 {removed} 页，可撤销");
        PagePreviewList.Focus();
        SyncPageTraySelection();
    }

    private void ShowPageOverview()
    {
        if (_vm.ViewMode != CanvasViewMode.SheetOverview)
            _vm.SetViewModeCommand.Execute(CanvasViewMode.SheetOverview);
    }

    private void PagePreviewImage_Loaded(object sender, RoutedEventArgs e)
    {
        _pagePreviewImages.Add((Image)sender);
        QueuePagePreviews();
    }

    private void PagePreviewImage_Unloaded(object sender, RoutedEventArgs e) => _pagePreviewImages.Remove((Image)sender);
    private void PagePreviewLabel_Loaded(object sender, RoutedEventArgs e) => _pagePreviewLabels.Add((TextBlock)sender);
    private void PagePreviewLabel_Unloaded(object sender, RoutedEventArgs e) => _pagePreviewLabels.Remove((TextBlock)sender);

    private void QueuePagePreviews()
    {
        if (!PagePreviewList.IsVisible || _vm.IsAiModelLoading) return;
        _pagePreviewsDirty = true;
        _pagePreviewTimer.Stop();
        _pagePreviewTimer.Start();
    }

    private async void RefreshPagePreviews(object? sender, EventArgs e)
    {
        _pagePreviewTimer.Stop();
        if (!PagePreviewList.IsVisible) return;
        if (_pagePreviewCancellation is { } running)
        {
            running.Cancel();
            return;
        }

        _pagePreviewsDirty = false;
        foreach (var label in _pagePreviewLabels)
            if (label.DataContext is PrintSheet p) label.SetCurrentValue(TextBlock.TextProperty, p.SheetName);
        using var cancellation = new CancellationTokenSource();
        var work = _pagePreviewImages.Where(image => image.IsVisible && image.DataContext is PrintSheet)
            .Select(image => (Image: image, Page: (PrintSheet)image.DataContext,
                Render: _vm.CreatePageThumbnailRenderer((PrintSheet)image.DataContext, cancellation.Token))).ToArray();
        if (work.Length == 0) return;

        _pagePreviewCancellation = cancellation;
        try
        {
            var images = await Task.Run(() =>
            {
                var results = new BitmapSource?[work.Length];
                for (int i = 0; i < work.Length; i++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    try { results[i] = work[i].Render(); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _vm.Logger.Error($"页面预览生成失败：{work[i].Page.SheetName}", ex); }
                }
                return results;
            }, cancellation.Token);
            if (!cancellation.IsCancellationRequested && !_pagePreviewsDirty && PagePreviewList.IsVisible)
            {
                for (int i = 0; i < work.Length; i++)
                    if (work[i].Image.DataContext == work[i].Page) work[i].Image.Source = images[i];
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            _pagePreviewCancellation = null;
            if (_pagePreviewsDirty) QueuePagePreviews();
        }
    }
}
