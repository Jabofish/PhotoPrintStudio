using CommunityToolkit.Mvvm.ComponentModel;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private bool _applySizeToAllPages;

    internal bool HasRemovableEmptySheets => Sheets.Count > 1 && Sheets.Any(IsEmptySheet);

    internal void RemoveEmptySheets()
    {
        int removed = RemoveSheets(Sheets.Where(IsEmptySheet).ToArray());
        if (removed > 0) ShowToast($"已删除 {removed} 个空白页，可撤销");
    }

    internal int RemoveSheets(IReadOnlyCollection<PrintSheet> selected)
    {
        var pages = Sheets.Where(selected.Contains).ToHashSet();
        if (pages.Count == 0) return 0;
        if (pages.Count == Sheets.Count)
            pages.Remove(ActiveSheet ?? Sheets[0]);
        if (pages.Count == 0) return 0;

        int nextIndex = Sheets.Take(ActiveSheetIndex).Count(page => !pages.Contains(page));
        PushUndoSnapshot();
        for (int i = Sheets.Count - 1; i >= 0; i--)
        {
            if (pages.Contains(Sheets[i])) Sheets.RemoveAt(i);
        }
        for (int i = 0; i < Sheets.Count; i++) Sheets[i].SheetName = $"第 {i + 1} 页";
        SwitchToSheet(nextIndex);
        return pages.Count;
    }

    private static bool IsEmptySheet(PrintSheet sheet) =>
        sheet.Slots.All(slot => string.IsNullOrEmpty(slot.PhotoAssetId));

    private bool TryApplyPaperToAllPages(PaperSizeSpec paper, LayoutTemplateDefinition? singlePhotoTemplate = null)
    {
        var pages = new List<PrintSheet>(Sheets.Count);
        int overflowPageCount = 0;
        string? activeId = ActiveSheet?.Id;
        foreach (var previous in Sheets)
        {
            var template = previous.Scenario == LayoutScenario.SinglePhoto && singlePhotoTemplate != null
                ? singlePhotoTemplate
                : _allTemplates.FirstOrDefault(t => t.Id == previous.TemplateId);
            if (template == null)
            {
                ShowToast($"{previous.SheetName} 的版式已不存在，无法批量更改尺寸。");
                return false;
            }

            var oldSlot = previous.Slots.FirstOrDefault();
            var settings = previous.LayoutSettings ?? new SheetLayoutSettings(
                previous.AlignFlushToEdge ? 0 : template.DefaultMarginMm,
                previous.AlignFlushToEdge ? 0 : template.DefaultGapMm,
                previous.AlignFlushToEdge ? 0 : oldSlot?.BleedMm ?? template.DefaultBleedMm,
                oldSlot?.CornerRadiusMm ?? template.CustomCornerRadiusMm, template.CustomRows, template.CustomCols,
                oldSlot?.IdPhotoSpecId ?? template.IdPhotoSpecId, oldSlot?.PolaroidStyle?.PolaroidSpecId ?? template.PolaroidSpecId,
                template.PolaroidSafeFit);
            var prototype = LayoutEngine.BuildSheet(
                template, paper, previous.IsLandscape, settings.MarginMm, settings.GapMm, settings.BleedMm,
                IdPhotoSpecs.FirstOrDefault(spec => spec.Id == settings.IdSpecId),
                PolaroidSpecs.FirstOrDefault(spec => spec.Id == settings.PolaroidSpecId),
                oldSlot?.PolaroidStyle,
                settings.Rows, settings.Columns, previous.CropMarkStyle, settings.PolaroidSafeFit, previous.AlignFlushToEdge);
            if (prototype.Slots.Count == 0)
            {
                ShowToast($"{previous.SheetName} 的版式放不进所选纸张，页面尺寸未更改。");
                return false;
            }

            prototype.Id = previous.Id;
            prototype.ShowCenterFoldTick = previous.ShowCenterFoldTick;
            prototype.CropMarkColorHex = previous.CropMarkColorHex;
            prototype.CropMarkThicknessMm = previous.CropMarkThicknessMm;
            prototype.ShowSlugFooter = previous.ShowSlugFooter;
            prototype.SlugCustomText = previous.SlugCustomText;
            prototype.CanvasBackgroundHex = previous.CanvasBackgroundHex;
            prototype.LayoutSettings = settings;

            foreach (var slot in prototype.Slots)
                if (prototype.Scenario == LayoutScenario.FreeGrid || (settings.CornerRadiusMm > 0.01 && slot.CornerRadiusMm < 0.01))
                    slot.CornerRadiusMm = settings.CornerRadiusMm;

            var ordered = previous.Slots.OrderBy(slot => slot.Index).ToList();
            int capacity = prototype.Slots.Count;
            var overflow = ordered.Skip(capacity).Where(slot => !string.IsNullOrEmpty(slot.PhotoAssetId)).ToList();
            var first = prototype.Clone(preserveId: true);
            for (int i = 0; i < Math.Min(capacity, ordered.Count); i++)
                CopySlotAssignment(first.Slots[i], ordered[i], preservePlacement: true);
            pages.Add(first);
            for (int start = 0; start < overflow.Count; start += capacity)
            {
                var extra = prototype.Clone();
                for (int i = 0; i < Math.Min(capacity, overflow.Count - start); i++)
                    CopySlotAssignment(extra.Slots[i], overflow[start + i], preservePlacement: true);
                pages.Add(extra);
                overflowPageCount++;
            }
        }

        PushUndoSnapshot();
        Sheets.Clear();
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i].SheetName = $"第 {i + 1} 页";
            Sheets.Add(pages[i]);
        }
        SwitchToSheet(pages.FindIndex(page => page.Id == activeId));
        ShowToast(overflowPageCount == 0
            ? $"已更新全部 {pages.Count} 页的纸张尺寸，可撤销"
            : $"已更新全部页面的纸张尺寸，新增 {overflowPageCount} 页保留剩余照片，可撤销");
        return true;
    }
}
