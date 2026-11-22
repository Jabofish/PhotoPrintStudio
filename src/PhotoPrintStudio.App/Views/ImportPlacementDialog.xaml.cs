using System.Windows;
using System.Windows.Controls;
using PhotoPrintStudio.App.Themes;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.Views;

public partial class ImportPlacementDialog : Window
{
    private readonly int _count;
    private readonly int _capacity;
    private readonly int _emptyCount;
    private readonly bool _replaceBlankPage;
    private readonly bool _arrangingSelection;
    private readonly bool _orientSinglePhotoPages;

    private ImportPlacement Placement => FillEmptyChoice.IsChecked == true ? ImportPlacement.FillEmptySlots
        : RepeatChoice.IsChecked == true ? ImportPlacement.OnePhotoPerPage
        : SmartChoice.IsChecked == true ? ImportPlacement.SmartPaginate
        : PaginateChoice.IsChecked == true ? ImportPlacement.Paginate
        : ImportPlacement.PoolOnly;

    public ImportArrangement Arrangement => new(Placement,
        (SlotFillOrder)((ComboBoxItem)OrderCombo.SelectedItem).Tag,
        (FitCombo.SelectedItem as ComboBoxItem)?.Tag as FitMode?,
        Placement == ImportPlacement.SmartPaginate && AutoRotateChoice.IsChecked == true);

    public ImportPlacementDialog(MainViewModel vm, int count, bool arrangingSelection = false)
    {
        _count = count;
        _arrangingSelection = arrangingSelection;
        _orientSinglePhotoPages = vm.ActiveSheet is { Scenario: LayoutScenario.SinglePhoto, IsCustomFreeform: false, Slots.Count: 1 };
        _capacity = vm.ActiveSheet?.Slots.Count ?? 0;
        _emptyCount = vm.ActiveSheet?.Slots.Count(s => string.IsNullOrEmpty(s.PhotoAssetId)) ?? 0;
        _replaceBlankPage = vm.Sheets.Count == 1 && _capacity > 0 && _emptyCount == _capacity;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, vm.IsDarkTheme);
        string template = vm.SelectedTemplate?.Name ?? "当前版式";
        SummaryText.Text = $"{(arrangingSelection ? "已选择" : "已读取")} {count} 张照片\n{template}，每页 {_capacity} 格";

        bool hasPartialEmpty = _emptyCount > 0 && _emptyCount < _capacity;
        FillEmptyChoice.Visibility = hasPartialEmpty ? Visibility.Visible : Visibility.Collapsed;
        FillEmptyChoice.Content = $"填入本页空位（剩余 {_emptyCount} 格）";

        PaginateChoice.Content = arrangingSelection ? "按当前版式自动分页" : "自动分页";
        PaginateChoice.IsEnabled = _capacity > 0;
        SmartChoice.Content = arrangingSelection ? "智能构图并分页" : "智能导入";
        SmartChoice.Visibility = _capacity > 0 && vm.ActiveSheet?.Scenario != LayoutScenario.IdPhoto
            ? Visibility.Visible : Visibility.Collapsed;
        RepeatChoice.Content = "每张单独一页";
        RepeatChoice.Visibility = vm.ActiveSheet?.Scenario == LayoutScenario.IdPhoto && _capacity > 1
            ? Visibility.Visible : Visibility.Collapsed;

        if (arrangingSelection)
        {
            Title = "所选照片排版";
            PoolChoice.Visibility = Visibility.Collapsed;
        }

        if (count == 1 && _replaceBlankPage && RepeatChoice.Visibility == Visibility.Visible)
            RepeatChoice.IsChecked = true;
        else if (PaginateChoice.IsEnabled)
            PaginateChoice.IsChecked = true;
        else if (!arrangingSelection)
            PoolChoice.IsChecked = true;
        else
        {
            PlacementOptions.Visibility = Visibility.Collapsed;
            AutoRotateChoice.Visibility = Visibility.Collapsed;
            ResultText.Text = "当前版式放不下照片，请先调整纸张、边距或版式。";
            ConfirmButton.IsEnabled = false;
        }
    }

    private void Choice_Checked(object sender, RoutedEventArgs e)
    {
        if (ConfirmButton == null) return;
        var placement = Placement;
        PlacementOptions.Visibility = placement != ImportPlacement.PoolOnly ? Visibility.Visible : Visibility.Collapsed;
        AutoRotateChoice.Visibility = placement == ImportPlacement.SmartPaginate ? Visibility.Visible : Visibility.Collapsed;
        bool showOrder = placement switch
        {
            ImportPlacement.FillEmptySlots => _count > 1 && _emptyCount > 1,
            ImportPlacement.Paginate or ImportPlacement.SmartPaginate => _count > 1 && _capacity > 1,
            _ => false
        };
        OrderOptions.Visibility = showOrder ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(FitOptions, showOrder ? 2 : 0);
        if (placement == ImportPlacement.FillEmptySlots)
        {
            int placed = Math.Min(_count, _emptyCount);
            int remaining = _count - placed;
            ResultText.Text = remaining > 0
                ? $"先填满当前页剩余的 {placed} 个空位，其余 {remaining} 张留在素材池。"
                : $"将 {placed} 张照片填入当前页空位，已有格位保持不变。";
            if (showOrder)
                ResultText.Text += Arrangement.Order switch
                {
                    SlotFillOrder.RowFirst => "从左到右逐行铺放。",
                    SlotFillOrder.ColumnFirst => "从上到下逐列铺放。",
                    _ => ""
                };
            ResultText.Text += Arrangement.Fit switch
            {
                FitMode.Cover => "必要时裁掉照片边缘来填满格位。",
                FitMode.Contain => "按原比例放入格位，允许留白。",
                _ => ""
            };
            ConfirmButton.Content = _arrangingSelection ? $"填入 {placed} 格" : $"导入并填入 {placed} 格";
        }
        else if (placement is ImportPlacement.Paginate or ImportPlacement.OnePhotoPerPage or ImportPlacement.SmartPaginate)
        {
            int pages = placement == ImportPlacement.OnePhotoPerPage ? _count : (int)Math.Ceiling(_count / (double)_capacity);
            ResultText.Text = _replaceBlankPage ? $"使用当前空白页，共排 {pages} 页。"
                : $"在现有页面后追加 {pages} 页，现有内容保持不变。";
            if (placement != ImportPlacement.OnePhotoPerPage && _count % _capacity != 0)
                ResultText.Text += $"最后一页放 {_count % _capacity} 张。";
            if (placement == ImportPlacement.OnePhotoPerPage)
                ResultText.Text += "同一张照片会填满该页的证件照格位。";
            if (placement == ImportPlacement.SmartPaginate)
                ResultText.Text += AutoRotateChoice.IsChecked == true
                    ? "按格位优化裁剪，照片与格位横竖方向不一致时旋转 90°。"
                    : "按格位优化裁剪，保留照片方向。";
            if (_orientSinglePhotoPages)
                ResultText.Text += "单张版式会随照片自动选用横纸或竖纸。";
            if (showOrder)
                ResultText.Text += Arrangement.Order switch
                {
                    SlotFillOrder.RowFirst => "从左到右逐行铺放。",
                    SlotFillOrder.ColumnFirst => "从上到下逐列铺放。",
                    _ => ""
                };
            ResultText.Text += Arrangement.Fit switch
            {
                FitMode.Cover => "必要时裁掉照片边缘来填满格位。",
                FitMode.Contain => "按原比例放入格位，允许留白。",
                _ => ""
            };
            ConfirmButton.Content = _arrangingSelection ? $"排成 {pages} 页" : $"导入并排成 {pages} 页";
        }
        else
        {
            ResultText.Text = "照片加入素材池，当前排版保持不变。";
            ConfirmButton.Content = "导入照片";
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
