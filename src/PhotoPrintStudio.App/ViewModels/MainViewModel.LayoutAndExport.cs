using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoPrintStudio.App.Printing;
using PhotoPrintStudio.App.Services;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Preflight;
using PhotoPrintStudio.Core.Portable;
using PhotoPrintStudio.Core.Rendering;

namespace PhotoPrintStudio.App.ViewModels;

public partial class MainViewModel
{
    private string? _projectPath;
    private string _savedWorkspace = "";
    [ObservableProperty] private bool _alignFlushToEdge;

    partial void OnActiveSheetChanged(PrintSheet? value)
    {
        if (value == null) return;
        bool wasSuppressed = _suppressLayoutRebuild;
        _suppressLayoutRebuild = true;
        try
        {
            ActiveScenario = value.Scenario;
            if (FilteredTemplates.Count == 0 || TemplateCategory(FilteredTemplates[0].Scenario) != TemplateCategory(value.Scenario))
                FilterTemplatesByScenario(value.Scenario, selectDefault: false);
            SelectedTemplate = FilteredTemplates.FirstOrDefault(t => t.Id == value.TemplateId);
            SelectedPaper = PaperSizes.FirstOrDefault(p => p.Id == value.PaperId)
                ?? new PaperSizeSpec(value.PaperId, $"工程纸张（{value.PaperWidthMm:0.#} × {value.PaperHeightMm:0.#} mm）",
                    value.PaperWidthMm, value.PaperHeightMm, "工程纸张", "");
            IsLandscape = value.IsLandscape;
            var settings = value.LayoutSettings;
            var slot = value.Slots.FirstOrDefault();
            var template = SelectedTemplate;
            LayoutMarginMm = settings?.MarginMm ?? (value.AlignFlushToEdge ? 0 : template?.DefaultMarginMm ?? 0);
            LayoutGapMm = settings?.GapMm ?? (value.AlignFlushToEdge ? 0 : template?.DefaultGapMm ?? 0);
            LayoutBleedMm = settings?.BleedMm ?? slot?.BleedMm ?? template?.DefaultBleedMm ?? 0;
            LayoutCornerRadiusMm = settings?.CornerRadiusMm ?? slot?.CornerRadiusMm ?? 0;
            CustomGridRows = settings?.Rows ?? template?.CustomRows ?? 2;
            CustomGridCols = settings?.Columns ?? template?.CustomCols ?? 2;
            SelectedIdSpec = IdPhotoSpecs.FirstOrDefault(s => s.Id == (settings?.IdSpecId ?? slot?.IdPhotoSpecId ?? template?.IdPhotoSpecId)) ?? IdPhotoSpecs[0];
            SelectedPolaroidSpec = PolaroidSpecs.FirstOrDefault(s => s.Id == (settings?.PolaroidSpecId ?? slot?.PolaroidStyle?.PolaroidSpecId ?? template?.PolaroidSpecId)) ?? PolaroidSpecs[0];
            PolaroidSafeFit = settings?.PolaroidSafeFit ?? template?.PolaroidSafeFit ?? false;
            NormalizeCenterMark(value);
            SelectedCropMarkStyle = value.CropMarkStyle;
            ShowCenterFoldTick = value.ShowCenterFoldTick;
            ShowSlugFooter = value.ShowSlugFooter;
            CanvasBackgroundHex = value.CanvasBackgroundHex;
            AlignFlushToEdge = value.AlignFlushToEdge;
            UpdateAutoPackSummary(value, template?.TemplateKind != "id_upright_grid");
        }
        finally
        {
            _suppressLayoutRebuild = wasSuppressed;
        }
        NotifyPolaroidSyncFlags();
    }

    private static void NormalizeCenterMark(PrintSheet sheet)
    {
        if (sheet.CropMarkStyle != CropMarkStyle.CenterFoldTicks) return;
        sheet.CropMarkStyle = CropMarkStyle.None;
        sheet.ShowCenterFoldTick = true;
    }

    partial void OnAlignFlushToEdgeChanged(bool value)
    {
        if (!_suppressLayoutRebuild && ActiveSheet is { } sheet && sheet.AlignFlushToEdge != value)
            RebuildActiveSheetFromParameters(true);
    }

    partial void OnSelectedTemplateChanged(LayoutTemplateDefinition? value)
    {
        if (value == null || _suppressLayoutRebuild) return;

        if (ApplySizeToAllPages && HasMultiplePages &&
            ActiveSheet is { Scenario: LayoutScenario.SinglePhoto, IsCustomFreeform: false } &&
            value.Scenario == LayoutScenario.SinglePhoto && !value.IsCustomFreeform)
        {
            var paper = PaperSizes.FirstOrDefault(p => p.Id == value.DefaultPaperId) ?? PaperSizes[0];
            if (!TryApplyPaperToAllPages(paper, value)) OnActiveSheetChanged(ActiveSheet);
            return;
        }

        _suppressLayoutRebuild = true;
        try
        {
            var paper = PaperSizes.FirstOrDefault(p => p.Id == value.DefaultPaperId)
                ?? (value.CustomPaperWidthMm is > 10 and double cw && value.CustomPaperHeightMm is > 10 and double ch
                    ? new PaperSizeSpec(value.DefaultPaperId, $"模板纸张（{cw:0.#} × {ch:0.#} mm）", cw, ch, "自定义", "", false)
                    : PaperSizes[0]);
            SelectedPaper = paper;
            IsLandscape = value.DefaultLandscape;
            LayoutMarginMm = value.DefaultMarginMm;
            LayoutGapMm = value.DefaultGapMm;
            LayoutBleedMm = value.DefaultBleedMm;
            LayoutCornerRadiusMm = value.CustomCornerRadiusMm;
            SelectedCropMarkStyle = value.DefaultCropMarkStyle == CropMarkStyle.CenterFoldTicks ? CropMarkStyle.None : value.DefaultCropMarkStyle;
            ShowCenterFoldTick = value.ShowCenterFoldTickByDefault;
            if (value.IsCustomFreeform && !string.IsNullOrWhiteSpace(value.CustomCanvasBackgroundHex))
            {
                CanvasBackgroundHex = value.CustomCanvasBackgroundHex;
            }
            else if (value.Scenario == LayoutScenario.SinglePhoto)
            {
                ShowSlugFooter = false;
                CanvasBackgroundHex = "#FFFFFF";
            }
            AlignFlushToEdge = false;

            if (!string.IsNullOrEmpty(value.IdPhotoSpecId))
                SelectedIdSpec = IdPhotoSpecs.FirstOrDefault(x => x.Id == value.IdPhotoSpecId) ?? SelectedIdSpec;

            if (!string.IsNullOrEmpty(value.PolaroidSpecId))
                SelectedPolaroidSpec = PolaroidSpecs.FirstOrDefault(x => x.Id == value.PolaroidSpecId) ?? SelectedPolaroidSpec;

            PolaroidSafeFit = value.PolaroidSafeFit;
            CustomGridRows = value.CustomRows;
            CustomGridCols = value.CustomCols;
        }
        finally
        {
            _suppressLayoutRebuild = false;
        }

        RebuildActiveSheetFromParameters(pushUndo: Sheets.Count > 0, applyTemplate: true);
    }

    public bool IsCustomPaperSelected => SelectedPaper is { IsBuiltIn: false };
    public bool IsCustomPolaroidSelected => SelectedPolaroidSpec is { IsBuiltIn: false };

    partial void OnSelectedPaperChanged(PaperSizeSpec value)
    {
        OnPropertyChanged(nameof(IsCustomPaperSelected));
        if (_suppressLayoutRebuild || value == null) return;
        if (ApplySizeToAllPages && HasMultiplePages)
        {
            if (!TryApplyPaperToAllPages(value)) OnActiveSheetChanged(ActiveSheet);
            return;
        }
        RebuildActiveSheetFromParameters(true);
    }
    partial void OnIsLandscapeChanged(bool value) { if (!_suppressLayoutRebuild) RebuildActiveSheetFromParameters(true); }
    partial void OnLayoutMarginMmChanged(double value) => RebuildAfterSliderChange();
    partial void OnLayoutGapMmChanged(double value) => RebuildAfterSliderChange();
    partial void OnLayoutBleedMmChanged(double value) => RebuildAfterSliderChange();
    partial void OnLayoutCornerRadiusMmChanged(double value) => RebuildAfterSliderChange();
    partial void OnSelectedIdSpecChanged(IdPhotoSpec value) { if (!_suppressLayoutRebuild) RebuildActiveSheetFromParameters(true); }
    partial void OnCustomGridRowsChanged(int value) => RebuildAfterSliderChange();
    partial void OnCustomGridColsChanged(int value) => RebuildAfterSliderChange();
    private void RebuildAfterSliderChange()
    {
        if (!_suppressLayoutRebuild)
        {
            MarkContinuousEditMutated();
            RebuildActiveSheetFromParameters(_pendingUndoSnapshot == null);
        }
    }
    partial void OnSelectedPolaroidSpecChanged(PolaroidSpec value)
    {
        OnPropertyChanged(nameof(IsCustomPolaroidSelected));
        if (!_suppressLayoutRebuild && value != null) RebuildActiveSheetFromParameters(true);
    }
    partial void OnPolaroidSafeFitChanged(bool value) { if (!_suppressLayoutRebuild) RebuildActiveSheetFromParameters(true); }

    internal void ApplyCustomTemplate(LayoutTemplateDefinition template)
    {
        if (TemplateCategory(ActiveScenario) != TemplateCategory(template.Scenario))
            FilterTemplatesByScenario(template.Scenario, selectDefault: false);
        SelectedTemplate = FilteredTemplates.FirstOrDefault(t => t.Id == template.Id);
    }

    public void ApplyCustomFreeformTemplate(LayoutTemplateDefinition template, bool saveToStore = true)
    {
        if (template == null) return;

        if (saveToStore)
        {
            DataStore.SaveCustomTemplate(template);
            ReloadTemplates();
            if (TemplateCategory(ActiveScenario) != TemplateCategory(template.Scenario))
                FilterTemplatesByScenario(template.Scenario, selectDefault: false);

            var target = FilteredTemplates.FirstOrDefault(t => t.Id == template.Id);
            if (target != null)
            {
                if (ReferenceEquals(SelectedTemplate, target) || SelectedTemplate?.Id == target.Id)
                {
                    _suppressLayoutRebuild = true;
                    try { SelectedTemplate = null; }
                    finally { _suppressLayoutRebuild = false; }
                }
                SelectedTemplate = target;
            }
            ShowToast($"已保存并应用自定义版式：{template.Name}");
            return;
        }

        // 仅应用到当前页（不存入模板库，保留当前已排照片并支持撤销）
        var paper = PaperSizes.FirstOrDefault(p => p.Id == template.DefaultPaperId)
            ?? (template.CustomPaperWidthMm is > 10 and double cw && template.CustomPaperHeightMm is > 10 and double ch
                ? new PaperSizeSpec(template.DefaultPaperId, $"模板纸张（{cw:0.#} × {ch:0.#} mm）", cw, ch, "自定义", "", false)
                : SelectedPaper);

        var previousSelection = SelectedSlot;
        var existingAssignments = ActiveSheet?.Slots.OrderBy(s => s.Index).ToList() ?? [];
        var built = LayoutEngine.BuildSheet(
            template,
            paper,
            isLandscapeOverride: template.DefaultLandscape,
            cropMarkOverride: template.DefaultCropMarkStyle);

        if (!FreeformLayoutMath.CanOutput(built))
        {
            ShowToast("当前纸张放不下此自由版式，请在编辑器中调整格位。");
            return;
        }

        if (Sheets.Count > 0) PushUndoSnapshot();
        if (ActiveSheet is { } previousSheet)
        {
            built.Id = previousSheet.Id;
            built.SheetName = previousSheet.SheetName;
            built.CropMarkColorHex = previousSheet.CropMarkColorHex;
            built.CropMarkThicknessMm = previousSheet.CropMarkThicknessMm;
            built.ShowSlugFooter = previousSheet.ShowSlugFooter;
            built.SlugCustomText = previousSheet.SlugCustomText;
        }
        else
        {
            built.SheetName = "第 1 页";
        }

        CopyFreeformAssignments(built, existingAssignments);

        if (Sheets.Count == 0)
        {
            Sheets.Add(built);
            ActiveSheetIndex = 0;
        }
        else
        {
            int idx = Math.Clamp(ActiveSheetIndex, 0, Sheets.Count - 1);
            Sheets[idx] = built;
        }

        ActiveSheet = built;
        UpdateAssetUsageCounts();
        SelectSlot(built.Slots.FirstOrDefault(slot => slot.Id == previousSelection?.Id)
            ?? built.Slots.ElementAtOrDefault(previousSelection?.Index ?? 0) ?? built.Slots.FirstOrDefault());
        UpdatePreflight();
        ScheduleRender();
        ShowToast("已将自由版式应用到当前页，可撤销");
    }

    public void ReloadCustomPhysicalSpecs(AppPortableConfig? initialConfig = null, string? selectPaperId = null, string? selectPolaroidId = null)
    {
        if (initialConfig != null)
        {
            _customPaperSizes.Clear();
            _customPaperSizes.AddRange(initialConfig.CustomPaperSizes.Select(x => x with { IsBuiltIn = false }));
            _customPolaroidSpecs.Clear();
            _customPolaroidSpecs.AddRange(initialConfig.CustomPolaroidSpecs.Select(x => x with { IsBuiltIn = false }));
        }

        string targetPaperId = selectPaperId ?? SelectedPaper?.Id ?? PaperSizeSpec.BuiltInPapers[0].Id;
        string targetPolaroidId = selectPolaroidId ?? SelectedPolaroidSpec?.Id ?? PolaroidSpec.BuiltInSpecs[0].Id;

        _suppressLayoutRebuild = true;
        try
        {
            PaperSizes.Clear();
            foreach (var p in PaperSizeSpec.BuiltInPapers) PaperSizes.Add(p);
            foreach (var cp in _customPaperSizes) PaperSizes.Add(cp);
            SelectedPaper = PaperSizes.FirstOrDefault(p => p.Id == targetPaperId) ?? PaperSizes[0];

            PolaroidSpecs.Clear();
            foreach (var s in PolaroidSpec.BuiltInSpecs) PolaroidSpecs.Add(s);
            foreach (var cs in _customPolaroidSpecs) PolaroidSpecs.Add(cs);
            SelectedPolaroidSpec = PolaroidSpecs.FirstOrDefault(s => s.Id == targetPolaroidId) ?? PolaroidSpecs[0];
        }
        finally
        {
            _suppressLayoutRebuild = false;
        }
        OnPropertyChanged(nameof(IsCustomPaperSelected));
        OnPropertyChanged(nameof(IsCustomPolaroidSelected));
    }

    public void SaveCustomPaperSpec(PaperSizeSpec spec)
    {
        var saved = spec with { IsBuiltIn = false };
        _customPaperSizes.Add(saved);
        SaveConfig();
        ReloadCustomPhysicalSpecs(selectPaperId: saved.Id);
        RebuildActiveSheetFromParameters(true);
        ShowToast($"已添加自定义相纸：{saved.Name}");
    }

    public void DeleteCustomPaperSpec(PaperSizeSpec spec)
    {
        if (spec.IsBuiltIn) return;
        _customPaperSizes.RemoveAll(x => x.Id == spec.Id);
        SaveConfig();
        ReloadCustomPhysicalSpecs(selectPaperId: PaperSizeSpec.BuiltInPapers[0].Id);
        RebuildActiveSheetFromParameters(true);
        ShowToast($"已删除自定义相纸：{spec.Name}");
    }

    public void SaveCustomPolaroidSpec(PolaroidSpec spec)
    {
        var saved = spec with { IsBuiltIn = false };
        _customPolaroidSpecs.Add(saved);
        SaveConfig();
        ReloadCustomPhysicalSpecs(selectPolaroidId: saved.Id);
        RebuildActiveSheetFromParameters(true);
        ShowToast($"已添加自定义拍立得尺寸：{saved.Name}");
    }

    public void DeleteCustomPolaroidSpec(PolaroidSpec spec)
    {
        if (spec.IsBuiltIn) return;
        _customPolaroidSpecs.RemoveAll(x => x.Id == spec.Id);
        SaveConfig();
        ReloadCustomPhysicalSpecs(selectPolaroidId: PolaroidSpec.BuiltInSpecs[0].Id);
        RebuildActiveSheetFromParameters(true);
        ShowToast($"已删除自定义拍立得尺寸：{spec.Name}");
    }

    partial void OnSelectedCropMarkStyleChanged(CropMarkStyle value)
    {
        if (_suppressLayoutRebuild || ActiveSheet == null) return;
        PushUndoSnapshot();
        ActiveSheet.CropMarkStyle = value;
        ScheduleRender();
    }

    partial void OnShowCenterFoldTickChanged(bool value)
    {
        if (_suppressLayoutRebuild || ActiveSheet == null) return;
        PushUndoSnapshot();
        ActiveSheet.ShowCenterFoldTick = value;
        ScheduleRender();
    }

    partial void OnShowSlugFooterChanged(bool value)
    {
        if (_suppressLayoutRebuild || ActiveSheet == null) return;
        PushUndoSnapshot();
        ActiveSheet.ShowSlugFooter = value;
        ScheduleRender();
    }

    partial void OnCanvasBackgroundHexChanged(string value)
    {
        if (_suppressLayoutRebuild || ActiveSheet == null) return;
        PushUndoSnapshot();
        ActiveSheet.CanvasBackgroundHex = value;
        ScheduleRender();
    }

    [RelayCommand]
    private void ClearOutputMarks()
    {
        if (ActiveSheet is not { } sheet ||
            (sheet.CropMarkStyle == CropMarkStyle.None && !sheet.ShowCenterFoldTick && !sheet.ShowSlugFooter)) return;
        PushUndoSnapshot();
        _suppressLayoutRebuild = true;
        try
        {
            sheet.CropMarkStyle = SelectedCropMarkStyle = CropMarkStyle.None;
            sheet.ShowCenterFoldTick = ShowCenterFoldTick = false;
            sheet.ShowSlugFooter = ShowSlugFooter = false;
        }
        finally { _suppressLayoutRebuild = false; }
        ScheduleRender();
        ShowToast("已关闭本页全部输出标记，可撤销");
    }

    public bool CanEditNonPolaroidCaption => SelectedSlot != null && SelectedSlot.PolaroidStyle == null;

    public string SelectedSlotCaption
    {
        get => SelectedSlot?.CustomCaption ?? "";
        set
        {
            if (_suppressLayoutRebuild || SelectedSlot == null || SelectedSlot.PolaroidStyle != null) return;
            string? normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(SelectedSlot.CustomCaption, normalized, StringComparison.Ordinal)) return;
            if (_pendingUndoSnapshot == null) PushUndoSnapshot();
            else MarkContinuousEditMutated();
            SelectedSlot.CustomCaption = normalized;
            OnPropertyChanged();
            ScheduleRender();
        }
    }

    public void UpdateSelectedSlotCaption(string? caption)
    {
        if (SelectedSlot == null) return;
        string? normalized = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (SelectedSlot.PolaroidStyle != null)
        {
            PolaroidCaptionTemplate = normalized ?? "";
            return;
        }
        if (string.Equals(SelectedSlot.CustomCaption, normalized, StringComparison.Ordinal)) return;
        PushUndoSnapshot();
        SelectedSlot.CustomCaption = normalized;
        OnPropertyChanged(nameof(SelectedSlotCaption));
        ScheduleRender();
        ShowToast(normalized == null ? $"已清除第 {SelectedSlot.Index + 1} 格标注，可撤销" : $"已更新第 {SelectedSlot.Index + 1} 格标注，可撤销");
    }

    // === 拍立得样式属性实时同步到当前选中的卡片或全部卡片 ===
    partial void OnPolaroidFrameColorHexChanged(string value) => UpdatePolaroidStyleField(s => s.FrameColorHex = value);
    partial void OnPolaroidTextColorHexChanged(string value) => UpdatePolaroidStyleField(s => s.TextColorHex = value);
    partial void OnPolaroidFontFamilyChanged(string value) => UpdatePolaroidStyleField(s => s.FontFamily = value);
    partial void OnPolaroidFontSizePtChanged(double value) => UpdatePolaroidStyleField(s => s.FontSizePt = value);
    partial void OnPolaroidTextAlignChanged(PolaroidTextAlign value) => UpdatePolaroidStyleField(s => s.TextAlignment = value);
    partial void OnPolaroidInnerShadowChanged(bool value) => UpdatePolaroidStyleField(s => s.ShowInnerShadow = value);
    partial void OnPolaroidPaperGrainChanged(bool value) => UpdatePolaroidStyleField(s => s.ShowPaperGrain = value);

    partial void OnPolaroidCaptionTemplateChanged(string value)
    {
        if (_suppressLayoutRebuild || ActiveSheet == null) return;
        if (SelectedSlot?.PolaroidStyle != null)
        {
            if (_pendingUndoSnapshot == null) PushUndoSnapshot();
            else MarkContinuousEditMutated();
            SelectedSlot.CustomCaption = value;
            SelectedSlot.PolaroidStyle.CaptionTemplate = value;
            ScheduleRender();
        }
    }

    private void UpdatePolaroidStyleField(Action<PolaroidSlotStyle> updater)
    {
        if (_suppressLayoutRebuild || SelectedSlot?.PolaroidStyle is not { } style) return;
        if (_pendingUndoSnapshot == null) PushUndoSnapshot();
        else MarkContinuousEditMutated();
        updater(style);
        ScheduleRender();
    }

    [RelayCommand]
    private void ApplyPolaroidFrameColor(string hex) => PolaroidFrameColorHex = hex;

    [RelayCommand]
    private void InsertPolaroidToken(string token)
    {
        PolaroidCaptionTemplate = string.IsNullOrEmpty(PolaroidCaptionTemplate)
            ? token
            : $"{PolaroidCaptionTemplate} {token}";
    }

    public bool HasMultiplePolaroidSlotsOnActiveSheet =>
        ActiveSheet?.Slots.Count(x => x.PolaroidStyle != null) > 1;

    public bool HasPolaroidSlotsAcrossMultiplePages =>
        Sheets.Count(s => s.Slots.Any(x => x.PolaroidStyle != null)) > 1;

    private void NotifyPolaroidSyncFlags()
    {
        OnPropertyChanged(nameof(HasMultiplePolaroidSlotsOnActiveSheet));
        OnPropertyChanged(nameof(HasPolaroidSlotsAcrossMultiplePages));
    }

    private void CopyCurrentPolaroidStyleTo(PolaroidSlotStyle target, LayoutSlot slot)
    {
        target.FrameColorHex = PolaroidFrameColorHex;
        target.TextColorHex = PolaroidTextColorHex;
        target.FontFamily = PolaroidFontFamily;
        target.FontSizePt = PolaroidFontSizePt;
        target.TextAlignment = PolaroidTextAlign;
        target.ShowInnerShadow = PolaroidInnerShadow;
        target.ShowPaperGrain = PolaroidPaperGrain;
        target.CaptionTemplate = PolaroidCaptionTemplate;
        slot.CustomCaption = PolaroidCaptionTemplate;
    }

    [RelayCommand]
    private void ApplyPolaroidStyleToAllSlots()
    {
        if (ActiveSheet == null) return;
        var targets = ActiveSheet.Slots.Where(x => x.PolaroidStyle != null).ToList();
        if (targets.Count <= 1) return;
        PushUndoSnapshot();
        foreach (var s in targets)
            CopyCurrentPolaroidStyleTo(s.PolaroidStyle!, s);
        ScheduleRender();
        ShowToast($"已将当前拍立得样式同步到本页 {targets.Count} 张卡片，可撤销");
    }

    [RelayCommand]
    private void ApplyPolaroidStyleToAllPages()
    {
        var targets = Sheets.SelectMany(sheet => sheet.Slots).Where(x => x.PolaroidStyle != null).ToList();
        if (targets.Count <= 1) return;
        PushUndoSnapshot();
        foreach (var s in targets)
            CopyCurrentPolaroidStyleTo(s.PolaroidStyle!, s);
        PagePreviewsInvalidated?.Invoke();
        ScheduleRender();
        ShowToast($"已将当前拍立得样式同步到全部 {targets.Count} 张卡片，可撤销");
    }

    public void RebuildActiveSheetFromParameters(bool pushUndo, bool applyTemplate = false)
    {
        if (!applyTemplate && ActiveSheet is { IsCustomFreeform: true } current)
        {
            var updated = ResizeFreeformPage(current, SelectedPaper, IsLandscape);
            if (!FreeformLayoutMath.CanOutput(updated))
            {
                OnActiveSheetChanged(current);
                ShowToast("新纸张放不下当前自由版式，请在编辑器中调整格位。");
                return;
            }

            if (pushUndo) PushUndoSnapshot();
            Sheets[ActiveSheetIndex] = updated;
            SwitchToSheet(ActiveSheetIndex);
            return;
        }

        if (SelectedTemplate == null && ActiveSheet != null)
        {
            ShowToast("找不到此页原用的模板，请先选一个新模板再调整版式。");
            return;
        }
        var tpl = SelectedTemplate ?? FilteredTemplates.FirstOrDefault() ?? LayoutEngine.BuiltInTemplates[0];
        var previousSelection = SelectedSlot;

        var assignmentSource = _pendingUndoSnapshot?.Sheets.FirstOrDefault(s => s.Id == ActiveSheet?.Id) ?? ActiveSheet;
        var existingAssignments = assignmentSource?.Slots
            .OrderBy(s => s.Index)
            .ToList() ?? [];
        bool sameTemplate = assignmentSource?.TemplateId == tpl.Id;

        var basePolaroid = new PolaroidSlotStyle
        {
            PolaroidSpecId = SelectedPolaroidSpec.Id,
            FrameColorHex = PolaroidFrameColorHex,
            TextColorHex = PolaroidTextColorHex,
            CaptionTemplate = PolaroidCaptionTemplate,
            FontFamily = PolaroidFontFamily,
            FontSizePt = PolaroidFontSizePt,
            TextAlignment = PolaroidTextAlign,
            ShowInnerShadow = PolaroidInnerShadow,
            ShowPaperGrain = PolaroidPaperGrain
        };

        var built = LayoutEngine.BuildSheet(
            tpl,
            SelectedPaper,
            isLandscapeOverride: IsLandscape,
            marginOverride: LayoutMarginMm,
            gapOverride: LayoutGapMm,
            bleedOverride: LayoutBleedMm,
            idSpecOverride: SelectedIdSpec,
            polaroidSpecOverride: SelectedPolaroidSpec,
            basePolaroidStyle: basePolaroid,
            customRowsOverride: CustomGridRows,
            customColsOverride: CustomGridCols,
            cropMarkOverride: SelectedCropMarkStyle,
            polaroidSafeFitOverride: PolaroidSafeFit,
            flushToEdge: AlignFlushToEdge);

        if (built.IsCustomFreeform && !FreeformLayoutMath.CanOutput(built))
        {
            OnActiveSheetChanged(ActiveSheet);
            ShowToast("当前纸张放不下此自由版式，请在编辑器中调整格位。");
            return;
        }
        if (pushUndo && Sheets.Count > 0) PushUndoSnapshot();

        built.ShowCenterFoldTick = ShowCenterFoldTick;
        if (ActiveSheet is { } previousSheet)
        {
            built.Id = previousSheet.Id;
            built.CropMarkColorHex = previousSheet.CropMarkColorHex;
            built.CropMarkThicknessMm = previousSheet.CropMarkThicknessMm;
            built.SlugCustomText = previousSheet.SlugCustomText;
        }
        built.LayoutSettings = new(LayoutMarginMm, LayoutGapMm, LayoutBleedMm, LayoutCornerRadiusMm,
            CustomGridRows, CustomGridCols, SelectedIdSpec.Id, SelectedPolaroidSpec.Id, PolaroidSafeFit);
        built.ShowSlugFooter = ShowSlugFooter;
        built.CanvasBackgroundHex = CanvasBackgroundHex;
        built.SheetName = ActiveSheet?.SheetName ?? "第 1 页";

        if (built.IsCustomFreeform)
        {
            CopyFreeformAssignments(built, existingAssignments);
        }
        else
        {
            for (int i = 0; i < built.Slots.Count; i++)
            {
                if (i < existingAssignments.Count)
                    CopySlotAssignment(built.Slots[i], existingAssignments[i], sameTemplate);
                else if (Assets.Count > 0 && built.Scenario == LayoutScenario.IdPhoto && existingAssignments.Count > 0)
                    built.Slots[i].PhotoAssetId = existingAssignments[0].PhotoAssetId;

                if (built.Scenario == LayoutScenario.FreeGrid || (LayoutCornerRadiusMm > 0.01 && built.Slots[i].CornerRadiusMm < 0.01))
                    built.Slots[i].CornerRadiusMm = LayoutCornerRadiusMm;
            }
        }

        if (Sheets.Count == 0)
        {
            Sheets.Add(built);
            ActiveSheetIndex = 0;
        }
        else
        {
            int idx = Math.Clamp(ActiveSheetIndex, 0, Sheets.Count - 1);
            Sheets[idx] = built;
        }

        ActiveSheet = built;
        UpdateAssetUsageCounts();
        SelectSlot(built.Slots.FirstOrDefault(slot => slot.Id == previousSelection?.Id)
            ?? built.Slots.ElementAtOrDefault(previousSelection?.Index ?? 0) ?? built.Slots.FirstOrDefault());
        UpdatePreflight();
        ScheduleRender();
    }

    private static PrintSheet ResizeFreeformPage(PrintSheet page, PaperSizeSpec paper, bool landscape)
    {
        var updated = page.Clone(preserveId: true);
        double longSide = Math.Max(paper.WidthMm, paper.HeightMm);
        double shortSide = Math.Min(paper.WidthMm, paper.HeightMm);
        updated.PaperId = paper.Id;
        updated.IsLandscape = landscape;
        updated.PaperWidthMm = landscape ? longSide : shortSide;
        updated.PaperHeightMm = landscape ? shortSide : longSide;
        return updated;
    }

    private static void CopyFreeformAssignments(PrintSheet destination, IReadOnlyList<LayoutSlot> previous)
    {
        var byId = previous.ToDictionary(slot => slot.Id);
        if (destination.Slots.Any(slot => byId.ContainsKey(slot.Id)))
        {
            foreach (var slot in destination.Slots)
                if (byId.TryGetValue(slot.Id, out var source))
                    CopySlotAssignment(slot, source, preservePlacement: true);
        }
        else
        {
            for (int i = 0; i < Math.Min(destination.Slots.Count, previous.Count); i++)
                CopySlotAssignment(destination.Slots[i], previous[i], preservePlacement: true);
        }
    }

    private static void CopySlotAssignment(LayoutSlot destination, LayoutSlot previous, bool preservePlacement)
    {
        destination.PhotoAssetId = previous.PhotoAssetId;
        if (preservePlacement || previous.CustomCaption != null)
            destination.CustomCaption = previous.CustomCaption;
        if (!preservePlacement) return;

        destination.FitModeOverride ??= previous.FitModeOverride;
        destination.SlotZoom = previous.SlotZoom;
        destination.SlotPanX = previous.SlotPanX;
        destination.SlotPanY = previous.SlotPanY;
        if (destination.PolaroidStyle is { } geometry && previous.PolaroidStyle != null)
        {
            var style = previous.PolaroidStyle.Clone();
            style.PolaroidSpecId = geometry.PolaroidSpecId;
            style.MarginTopMm = geometry.MarginTopMm;
            style.MarginLeftMm = geometry.MarginLeftMm;
            style.MarginRightMm = geometry.MarginRightMm;
            style.MarginBottomMm = geometry.MarginBottomMm;
            destination.PolaroidStyle = style;
        }
    }

    [RelayCommand]
    private void OneClickRemoveBorders()
    {
        int beforeCuts = ActiveSheet != null ? LayoutEngine.EstimateCutCount(ActiveSheet) : 0;
        PushUndoSnapshot();

        var tpl = SelectedTemplate ?? FilteredTemplates.FirstOrDefault() ?? LayoutEngine.BuiltInTemplates[0];
        bool needsWasteTicks = tpl.Scenario == LayoutScenario.IdPhoto
            || tpl.Scenario == LayoutScenario.Polaroid
            || tpl.TemplateKind == "six_2in1_card_duplex";

        _suppressLayoutRebuild = true;
        try
        {
            LayoutMarginMm = 0.0;
            LayoutGapMm = 0.0;
            LayoutBleedMm = 0.0;
            if (tpl.Scenario == LayoutScenario.FreeGrid)
                LayoutCornerRadiusMm = 0.0;

            AlignFlushToEdge = true;
            ShowCenterFoldTick = false;

            if (SelectedCropMarkStyle is not (CropMarkStyle.None or CropMarkStyle.WhiteBackgroundGuide))
                SelectedCropMarkStyle = needsWasteTicks ? CropMarkStyle.OuterCornerTicks : CropMarkStyle.None;
        }
        finally
        {
            _suppressLayoutRebuild = false;
        }

        RebuildActiveSheetFromParameters(pushUndo: false);
        int afterCuts = ActiveSheet != null ? LayoutEngine.EstimateCutCount(ActiveSheet) : 0;
        string summary = beforeCuts > afterCuts
            ? $"已取消边框并紧凑贴边：裁切刀数从 {beforeCuts} 刀减少到 {afterCuts} 刀"
            : $"已进入无边框紧凑排布：当前约 {afterCuts} 刀裁切";
        ShowToast(summary);
    }

    private void UpdateAutoPackSummary(PrintSheet sheet, bool allowRotate = true)
    {
        int cuts = LayoutEngine.EstimateCutCount(sheet);
        if (sheet.IsCustomFreeform)
        {
            AutoPackInfoText = $"自由自定义版式，共 {sheet.Slots.Count} 个图片框，纸张 {sheet.PaperWidthMm:0.#}×{sheet.PaperHeightMm:0.#} mm，约 {cuts} 刀裁切";
            return;
        }

        if (sheet.Scenario == LayoutScenario.SinglePhoto)
        {
            AutoPackInfoText = sheet.Slots.FirstOrDefault() is { } photo
                ? $"每页 1 张，照片 {photo.WidthMm:0.#} × {photo.HeightMm:0.#} mm"
                : "当前纸张和边距放不下照片。";
            return;
        }

        if (sheet.Scenario == LayoutScenario.IdPhoto)
        {
            var pack = LayoutEngine.CalculateBestPack(
                sheet.PaperWidthMm, sheet.PaperHeightMm,
                SelectedIdSpec.WidthMm, SelectedIdSpec.HeightMm,
                LayoutMarginMm, LayoutGapMm, allowRotate);
            AutoPackInfoText = $"本页共 {sheet.Slots.Count} 格，排布 {pack.Columns}×{pack.Rows} ({(pack.RotatedSlot ? "横放90°" : "正放")})，利用率 {pack.UtilizationPercent:0.0}%，约 {cuts} 刀裁切";
        }
        else if (sheet.Scenario == LayoutScenario.SixInchTwoInOne)
        {
            var s0 = sheet.Slots.FirstOrDefault();
            AutoPackInfoText = s0 != null
                ? $"六寸二合一，单格成品 {s0.WidthMm:0.#}×{s0.HeightMm:0.#} mm，出血 {s0.BleedMm:0.#} mm，约 {cuts} 刀裁切"
                : "";
        }
        else if (sheet.Scenario == LayoutScenario.Polaroid)
        {
            var s0 = sheet.Slots.FirstOrDefault();
            AutoPackInfoText = s0 != null
                ? $"每页 {sheet.Slots.Count} 张，成品 {Math.Min(s0.WidthMm, s0.HeightMm):0.#} × {Math.Max(s0.WidthMm, s0.HeightMm):0.#} mm，约 {cuts} 刀裁切"
                : "当前纸张和边距放不下此尺寸。请调整边距、换纸或选择等比缩放。";
        }
        else
        {
            AutoPackInfoText = $"网格排版，共 {sheet.Slots.Count} 格，纸张 {sheet.PaperWidthMm:0.#}×{sheet.PaperHeightMm:0.#} mm，约 {cuts} 刀裁切";
        }
    }

    // === 多页管理 ===
    [RelayCommand]
    private void AddBlankSheet()
    {
        if (ActiveSheet == null) return;
        PushUndoSnapshot();
        var next = ActiveSheet.Clone();
        next.SheetName = $"第 {Sheets.Count + 1} 页";
        foreach (var s in next.Slots) ClearSlotPlacement(s);
        Sheets.Add(next);
        SwitchToSheet(Sheets.Count - 1);
    }

    [RelayCommand]
    private void DuplicateActiveSheet()
    {
        if (ActiveSheet == null) return;
        PushUndoSnapshot();
        var copy = ActiveSheet.Clone();
        int insertIdx = Math.Clamp(ActiveSheetIndex + 1, 0, Sheets.Count);
        Sheets.Insert(insertIdx, copy);
        for (int i = 0; i < Sheets.Count; i++)
            Sheets[i].SheetName = $"第 {i + 1} 页";
        SwitchToSheet(insertIdx);
        ShowToast($"已复制当前页为第 {insertIdx + 1} 页");
    }

    [RelayCommand]
    private void AutoPaginateAllAssets()
    {
        if (ActiveSheet == null || Assets.Count == 0)
        {
            StatusText = "请先导入照片后再自动分页";
            return;
        }
        if (ActiveSheet.Slots.Count == 0)
        {
            ShowToast("当前版式没有可用格位，请先调整版式。");
            return;
        }
        if ((Sheets.Count > 1 || Sheets.Any(s => s.Slots.Any(slot => !string.IsNullOrEmpty(slot.PhotoAssetId)))) &&
            !Dialogs.Confirm("将以当前版式重新排列素材池中的全部照片，并替换现有页面。是否继续？", "替换全部页面")) return;
        PushUndoSnapshot();
        var generated = LayoutEngine.BuildPaginatedSheets(ActiveSheet, Assets.Select(a => a.Asset).ToList(),
            orientSinglePhotoPages: true);
        Sheets.Clear();
        foreach (var s in generated) Sheets.Add(s);
        SwitchToSheet(0);
        StatusText = $"已将 {Assets.Count} 张照片自动分页生成 {Sheets.Count} 页排版";
    }

    [RelayCommand]
    private void RemoveActiveSheet()
    {
        if (Sheets.Count <= 1 || ActiveSheet == null) return;
        PushUndoSnapshot();
        int idx = Math.Clamp(ActiveSheetIndex, 0, Sheets.Count - 1);
        Sheets.RemoveAt(idx);
        for (int i = 0; i < Sheets.Count; i++) Sheets[i].SheetName = $"第 {i + 1} 页";
        SwitchToSheet(Math.Clamp(idx, 0, Sheets.Count - 1));
        ShowToast("已删除当前页，可撤销");
    }

    [RelayCommand]
    private void PrevSheet()
    {
        if (ActiveSheetIndex > 0) SwitchToSheet(ActiveSheetIndex - 1);
    }

    [RelayCommand]
    private void NextSheet()
    {
        if (ActiveSheetIndex < Sheets.Count - 1) SwitchToSheet(ActiveSheetIndex + 1);
    }

    public void SwitchToSheet(int index)
    {
        if (Sheets.Count == 0) return;
        ActiveSheetIndex = Math.Clamp(index, 0, Sheets.Count - 1);
        ActiveSheet = Sheets[ActiveSheetIndex];
        UpdateAssetUsageCounts();
        NotifyPolaroidSyncFlags();
        if (ViewMode != CanvasViewMode.SheetOverview && !HasPlacedPhotos(ActiveSheet))
        {
            ViewMode = CanvasViewMode.SheetOverview;
            IsTwoPointStraightenActive = false;
            UpdateToolBanner();
        }
        SelectSlot(ActiveSheet.Slots.FirstOrDefault());
        UpdatePreflight();
        ScheduleRender();
    }

    public void UpdatePreflight()
    {
        if (ActiveSheet == null) return;
        UpdatePreflight(Assets.ToDictionary(a => a.Id, a => a.Asset));
    }

    private void UpdatePreflight(IReadOnlyDictionary<string, PhotoAsset> assetsById)
    {
        if (ActiveSheet == null) return;
        PreflightReport = PreflightChecker.InspectSheet(
            ActiveSheet,
            assetsById,
            CurrentCalibration,
            _latestHistograms,
            portraitDiagnosticsByAssetId: _latestPortraitDiagnostics);
        HasPreflightIssues = PreflightReport.Issues.Count > 0;
    }

    // === 预设、校准与工程存取 ===
    [RelayCommand]
    private void SaveCurrentCalibration()
    {
        if (string.IsNullOrWhiteSpace(SelectedPrinterName)) return;
        Interlocked.Increment(ref _printerCalibrationVersion);
        var profile = CurrentCalibration;
        profile.PrinterName = SelectedPrinterName;
        string savedDevMode = PrintService.GetSavedDevModeBase64(SelectedPrinterName);
        if (!string.IsNullOrWhiteSpace(savedDevMode))
            profile.SavedDevModeBase64 = savedDevMode;
        if (!string.IsNullOrWhiteSpace(SelectedIccPath) && File.Exists(SelectedIccPath))
            profile.CustomIccProfilePath = SelectedIccPath;
        DataStore.SavePrinterProfile(profile);
        OnPropertyChanged(nameof(PrinterBorderless));
        UpdatePreflight();
        ScheduleRender();
        ShowToast($"已保存打印机校准 ({SelectedPrinterName})");
    }

    [RelayCommand]
    private void SaveCustomColorPreset()
        => SaveCustomColorPresetWithName($"自定义预设 {DateTime.Now:MMdd-HHmm}");

    public void SaveCustomColorPresetWithName(string name)
    {
        if (CurrentEdit == null || string.IsNullOrWhiteSpace(name)) return;
        string trimmed = name.Trim();
        var item = new ColorPresetItem
        {
            Id = $"user_{Guid.NewGuid():N}",
            Name = trimmed,
            Category = "我的预设",
            Description = "保存于便携目录 presets/",
            Parameters = CurrentEdit.Clone()
        };
        DataStore.SaveCustomPreset(item);
        _suppressPresetApply = true;
        try
        {
            ReloadPresets();
            SelectedPreset = Presets.FirstOrDefault(p => p.Id == item.Id);
        }
        finally
        {
            _suppressPresetApply = false;
        }
        ShowToast($"已保存调色预设：{trimmed}");
    }

    public void RenameCustomPreset(ColorPresetItem preset, string newName)
    {
        if (preset.IsBuiltIn || string.IsNullOrWhiteSpace(newName)) return;
        string trimmed = newName.Trim();
        preset.Name = trimmed;
        DataStore.SaveCustomPreset(preset);
        _suppressPresetApply = true;
        try
        {
            string targetId = preset.Id;
            ReloadPresets();
            SelectedPreset = Presets.FirstOrDefault(p => p.Id == targetId);
        }
        finally
        {
            _suppressPresetApply = false;
        }
        ShowToast($"已重命名调色预设：{trimmed}");
    }

    public void DeleteCustomPreset(ColorPresetItem preset)
    {
        if (preset.IsBuiltIn) return;
        string removedName = preset.Name;
        DataStore.DeleteCustomPreset(preset.Id);
        _suppressPresetApply = true;
        try
        {
            ReloadPresets();
            SelectedPreset = Presets.FirstOrDefault(p => p.Parameters.ColorEquals(CurrentEdit));
        }
        finally
        {
            _suppressPresetApply = false;
        }
        ShowToast($"已删除自定义预设：{removedName}");
    }

    public void RenameCustomTemplate(LayoutTemplateDefinition tpl, string newName)
    {
        if (tpl.IsBuiltIn || string.IsNullOrWhiteSpace(newName)) return;
        string trimmed = newName.Trim();
        string targetId = tpl.Id;
        bool wasSelected = SelectedTemplate?.Id == targetId;
        tpl.Name = trimmed;
        DataStore.SaveCustomTemplate(tpl);
        _suppressLayoutRebuild = true;
        try
        {
            ReloadTemplates();
            if (wasSelected)
                SelectedTemplate = FilteredTemplates.FirstOrDefault(t => t.Id == targetId);
        }
        finally
        {
            _suppressLayoutRebuild = false;
        }
        ShowToast($"已重命名自定义模板：{trimmed}");
    }

    public void DeleteCustomTemplate(LayoutTemplateDefinition tpl)
    {
        if (tpl.IsBuiltIn) return;
        string removedName = tpl.Name;
        bool wasSelected = SelectedTemplate?.Id == tpl.Id;
        DataStore.DeleteCustomTemplate(tpl.Id);
        ReloadTemplates();
        if (wasSelected || SelectedTemplate == null)
        {
            SelectedTemplate = FilteredTemplates.FirstOrDefault();
        }
        ShowToast($"已删除自定义模板：{removedName}");
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectAiModel))]
    [NotifyCanExecuteChangedFor(nameof(RefreshAiModelsCommand))]
    private bool _isExportBusy;
    [ObservableProperty] private string _exportProgressText = "";
    [ObservableProperty] private double _exportProgressPercent;
    [ObservableProperty] private bool _isExportIndeterminate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastExportDirectory))]
    private string? _lastExportDirectory;
    public bool HasLastExportDirectory => LastExportDirectory != null;
    private CancellationTokenSource? _exportCts;
    public bool IsPrinting { get; private set; }

    private bool CanCancelExport() => _exportCts != null;

    [RelayCommand(CanExecute = nameof(CanCancelExport))]
    private void CancelExport() => _exportCts?.Cancel();

    [RelayCommand]
    private void OpenLastExportFolder()
    {
        if (LastExportDirectory is { } folder && Directory.Exists(folder))
            Dialogs.OpenPathInShell(folder);
    }

    public IReadOnlyDictionary<int, string> DigitalIdMaxSizeKbOptions { get; } = new Dictionary<int, string>
    {
        [0] = "不限体积（最高画质）",
        [30] = "≤ 30 KB（严格网办）",
        [50] = "≤ 50 KB（常见报名）",
        [100] = "≤ 100 KB",
        [200] = "≤ 200 KB（高清电子照）"
    };

    [ObservableProperty] private int _digitalIdMaxSizeKb;

    [RelayCommand]
    private async Task ExportDigitalIdPhotoAsync()
    {
        if (IsExportBusy || IsAiModelLoading || ActiveSheet?.Scenario != LayoutScenario.IdPhoto) return;
        var assetVm = SelectedAsset;
        if (assetVm == null)
        {
            ShowToast("请先选择需要导出电子照的证件照");
            return;
        }

        var sourceSlot = SelectedSlot?.PhotoAssetId == assetVm.Id ? SelectedSlot : null;
        var spec = sourceSlot?.IdPhotoSpecId is string sid
            ? IdPhotoSpec.BuiltInSpecs.FirstOrDefault(s => s.Id == sid) ?? SelectedIdSpec
            : SelectedIdSpec;
        int dpi = ExportDpi;
        int targetW = UnitConverter.MmToPixelsRounded(spec.WidthMm, dpi);
        int targetH = UnitConverter.MmToPixelsRounded(spec.HeightMm, dpi);
        int maxKb = DigitalIdMaxSizeKb;
        string sizeTag = maxKb > 0 ? $"_{maxKb}KB" : "";

        string? fileName = Dialogs.ShowSaveFileDialog(
            "导出单张电子证件照",
            "JPEG 电子证件照 (*.jpg)|*.jpg",
            "jpg",
            $"电子证件照_{spec.Name}_{targetW}x{targetH}px{sizeTag}.jpg");
        if (string.IsNullOrWhiteSpace(fileName)) return;

        var assetSnap = assetVm.Asset.CloneMetadataOnly();
        var slotSnap = sourceSlot?.Clone();
        double outputSharpen = OutputSharpeningAmount;
        bool embedIcc = EmbedIccProfileOnExport;

        IsExportBusy = true;
        IsExportIndeterminate = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _exportCts = cts;
        CancelExportCommand.NotifyCanExecuteChanged();
        ExportProgressText = $"正在生成 {targetW}×{targetH} px 电子证件照...";
        StatusText = ExportProgressText;

        try
        {
            var result = await Task.Run(() =>
                ExportService.ExportDigitalIdPhoto(
                    fileName,
                    assetSnap,
                    spec,
                    (asset, edit) =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        using var decoded = ImageCache.DecodeFullResolution(asset);
                        return ProcessBitmapForExportWithCompactMatte(decoded, asset, edit, cts.Token);
                    },
                    dpi,
                    maxKb,
                    outputSharpen,
                    embedIcc,
                    slotSnap,
                    cts.Token), cts.Token);

            ExportProgressPercent = 100;
            LastExportDirectory = Path.GetDirectoryName(fileName);
            double kb = result.FileSizeBytes / 1024.0;
            ShowToast($"电子证件照已导出（{result.PixelWidth}×{result.PixelHeight} px，{kb:0.1} KB）");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            ShowToast("已取消电子证件照导出");
        }
        catch (Exception ex)
        {
            Logger.Error("导出单张电子证件照失败", ex);
            Dialogs.ShowError($"导出电子证件照失败：{ex.Message}", "错误");
        }
        finally
        {
            _exportCts = null;
            CancelExportCommand.NotifyCanExecuteChanged();
            IsExportIndeterminate = false;
            IsExportBusy = false;
            ExportProgressText = "";
        }
    }

    [RelayCommand]
    private Task ExportCurrentJpegAsync() => ExportActiveSheetImageAsync(ExportImageFormat.Jpeg);

    [RelayCommand]
    private Task ExportCurrentPngAsync() => ExportActiveSheetImageAsync(ExportImageFormat.Png);

    [RelayCommand]
    private Task ExportBatchJpegAsync() => ExportAllSheetsBatchImagesAsync(ExportImageFormat.Jpeg);

    [RelayCommand]
    private Task ExportBatchPngAsync() => ExportAllSheetsBatchImagesAsync(ExportImageFormat.Png);

    private async Task ExportActiveSheetImageAsync(ExportImageFormat format)
    {
        if (ActiveSheet == null || IsExportBusy || IsAiModelLoading) return;
        string ext = format == ExportImageFormat.Png ? "png" : "jpg";
        string? fileName = Dialogs.ShowSaveFileDialog(
            "导出单页图像",
            format == ExportImageFormat.Png ? "PNG 图像 (*.png)|*.png" : "JPEG 图像 (*.jpg)|*.jpg",
            ext,
            $"排版输出_{ActiveSheet.PaperWidthMm:0}x{ActiveSheet.PaperHeightMm:0}mm_{ExportDpi}DPI.{ext}");
        if (string.IsNullOrWhiteSpace(fileName)) return;

        var sheetSnapshot = ActiveSheet.Clone();
        var map = Assets.ToDictionary(a => a.Id, a => a.Asset.CloneMetadataOnly());
        int dpi = ExportDpi;
        int quality = JpegQuality;
        double outputSharpen = OutputSharpeningAmount;
        bool embedIcc = EmbedIccProfileOnExport;

        IsExportBusy = true;
        IsExportIndeterminate = true;
        ExportProgressText = $"正在后台渲染 {dpi} DPI 高分辨率输出文件...";
        StatusText = ExportProgressText;

        try
        {
            var resolver = CreateSheetScopedExportResolver([sheetSnapshot], dpi);
            await Task.Run(() =>
                ExportService.ExportSingleSheetImage(
                    fileName,
                    sheetSnapshot,
                    map,
                    resolver,
                    format,
                    dpi,
                    quality,
                    outputSharpen,
                    embedIcc));
            ExportProgressPercent = 100;
            LastExportDirectory = Path.GetDirectoryName(fileName);
            ShowToast($"导出成功 ({dpi} DPI)：{Path.GetFileName(fileName)}");
        }
        catch (Exception ex)
        {
            Logger.Error("导出图片失败", ex);
            Dialogs.ShowError($"导出失败：{ex.Message}", "错误");
        }
        finally
        {
            IsExportIndeterminate = false;
            IsExportBusy = false;
            ExportProgressText = "";
        }
    }

    private async Task ExportAllSheetsBatchImagesAsync(ExportImageFormat format)
    {
        if (Sheets.Count == 0 || IsExportBusy || IsAiModelLoading) return;
        string? folderName = Dialogs.ShowOpenFolderDialog(
            $"选择批量导出 {(format == ExportImageFormat.Png ? "PNG" : "JPG")} 的目标文件夹");
        if (string.IsNullOrWhiteSpace(folderName)) return;

        var sheetsSnapshot = Sheets.Select(s => s.Clone()).ToList();
        var map = Assets.ToDictionary(a => a.Id, a => a.Asset.CloneMetadataOnly());
        int dpi = ExportDpi;
        int quality = JpegQuality;
        double outputSharpen = OutputSharpeningAmount;
        bool embedIcc = EmbedIccProfileOnExport;
        string baseName = $"冲印批量_{DateTime.Now:yyyyMMdd_HHmmss_fffffff}";

        using var cts = new CancellationTokenSource();
        _exportCts = cts;
        CancelExportCommand.NotifyCanExecuteChanged();

        IsExportBusy = true;
        ExportProgressPercent = 0;
        int completedPages = 0;
        ExportProgressText = $"准备导出共 {sheetsSnapshot.Count} 页...";
        var progress = new Progress<(int current, int total, string sheetName)>(p =>
        {
            completedPages = p.current;
            ExportProgressPercent = p.total > 0 ? p.current * 100.0 / p.total : 0;
            ExportProgressText = $"已写出 {p.current}/{p.total} 页";
            StatusText = ExportProgressText;
        });

        try
        {
            var resolver = CreateSheetScopedExportResolver(sheetsSnapshot, dpi, cts.Token);
            var files = await Task.Run(() =>
                ExportService.ExportSheetsBatchImages(
                    folderName,
                    baseName,
                    sheetsSnapshot,
                    map,
                    resolver,
                    format,
                    dpi,
                    quality,
                    progress,
                    cts.Token,
                    outputSharpen,
                    embedIcc),
                cts.Token);
            ExportProgressPercent = 100;
            LastExportDirectory = folderName;
            ShowToast($"已批量导出 {files.Count} 页 ({dpi} DPI) 到：{Path.GetFileName(folderName)}");
        }
        catch (OperationCanceledException)
        {
            if (completedPages > 0) LastExportDirectory = folderName;
            ShowToast($"已停止导出，保留 {completedPages} 页。");
        }
        catch (Exception ex)
        {
            Logger.Error("批量导出图片失败", ex);
            Dialogs.ShowError($"批量导出失败：{ex.Message}", "错误");
        }
        finally
        {
            _exportCts = null;
            CancelExportCommand.NotifyCanExecuteChanged();
            IsExportBusy = false;
            ExportProgressText = "";
        }
    }

    [RelayCommand]
    private async Task ExportAllToPdfAsync()
    {
        if (Sheets.Count == 0 || IsExportBusy || IsAiModelLoading) return;
        string? fileName = Dialogs.ShowSaveFileDialog(
            "导出多页 PDF",
            "PDF 排版文档 (*.pdf)|*.pdf",
            "pdf",
            $"冲印排版_{Sheets.Count}页_{ExportDpi}DPI.pdf");
        if (string.IsNullOrWhiteSpace(fileName)) return;

        var sheetsSnapshot = Sheets.Select(s => s.Clone()).ToList();
        var map = Assets.ToDictionary(a => a.Id, a => a.Asset.CloneMetadataOnly());
        int dpi = ExportDpi;
        double outputSharpen = OutputSharpeningAmount;

        IsExportBusy = true;
        IsExportIndeterminate = true;
        using var cts = new CancellationTokenSource();
        _exportCts = cts;
        CancelExportCommand.NotifyCanExecuteChanged();
        ExportProgressText = $"正在生成 {sheetsSnapshot.Count} 页高精度 PDF...";
        StatusText = ExportProgressText;

        try
        {
            var resolver = CreateSheetScopedExportResolver(sheetsSnapshot, dpi, cts.Token);
            await Task.Run(() =>
                ExportService.ExportSheetsToPdf(fileName, sheetsSnapshot, map, resolver, dpi, cts.Token, outputSharpen), cts.Token);
            ExportProgressPercent = 100;
            LastExportDirectory = Path.GetDirectoryName(fileName);
            ShowToast($"多页 PDF 导出成功：{Path.GetFileName(fileName)}");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StatusText = "已取消 PDF 导出。";
            ShowToast(StatusText);
        }
        catch (Exception ex)
        {
            Logger.Error("导出 PDF 失败", ex);
            Dialogs.ShowError($"导出 PDF 失败：{ex.Message}", "错误");
        }
        finally
        {
            _exportCts = null;
            CancelExportCommand.NotifyCanExecuteChanged();
            IsExportIndeterminate = false;
            IsExportBusy = false;
            ExportProgressText = "";
        }
    }

    public void OpenNativePrinterPreferences(IntPtr ownerHwnd)
    {
        if (IsExportBusy) return;
        if (string.IsNullOrWhiteSpace(SelectedPrinterName) || !InstalledPrinters.Contains(SelectedPrinterName))
        {
            Dialogs.ShowInfo("当前系统未检测到可用打印设备。", "打印首选项");
            return;
        }
        if (!string.Equals(CurrentCalibration.PrinterName, SelectedPrinterName, StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("正在读取此打印机的设置，请稍后再试。");
            return;
        }

        if (PrintService.ShowPrinterPreferencesDialog(SelectedPrinterName, ownerHwnd))
        {
            Interlocked.Increment(ref _printerCalibrationVersion);
            string devModeB64 = PrintService.GetSavedDevModeBase64(SelectedPrinterName);
            if (!string.IsNullOrWhiteSpace(devModeB64) &&
                string.Equals(CurrentCalibration.PrinterName, SelectedPrinterName, StringComparison.OrdinalIgnoreCase))
            {
                CurrentCalibration.SavedDevModeBase64 = devModeB64;
                DataStore.SavePrinterProfile(CurrentCalibration);
            }
            ShowToast($"已保存「{SelectedPrinterName}」原生驱动首选项至便携打印机配置");
        }
    }

    partial void OnPrintCopiesChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 99);
        if (clamped != value) PrintCopies = clamped;
    }

    [RelayCommand]
    private void DecreasePrintCopies()
    {
        if (PrintCopies > 1) PrintCopies--;
    }

    [RelayCommand]
    private void IncreasePrintCopies()
    {
        if (PrintCopies < 99) PrintCopies++;
    }

    [RelayCommand]
    private Task PrintActiveSheetAsync() => ExecutePrintAsync(allSheets: false, duplexBack: false);

    [RelayCommand]
    private Task PrintAllSheetsAsync() => ExecutePrintAsync(allSheets: true, duplexBack: false);

    [RelayCommand]
    private Task PrintDuplexBackSheetAsync() => ExecutePrintAsync(allSheets: false, duplexBack: true);

    private async Task ExecutePrintAsync(bool allSheets, bool duplexBack)
    {
        if (ActiveSheet == null || IsExportBusy || IsAiModelLoading) return;
        if (string.IsNullOrWhiteSpace(SelectedPrinterName) || !InstalledPrinters.Contains(SelectedPrinterName))
        {
            Dialogs.ShowInfo("请先选择打印机。若使用 PDF 等虚拟设备，可在设置中开启显示虚拟打印机。", "打印提示");
            return;
        }

        UpdatePreflight();
        var toPrint = allSheets ? Sheets.ToList() : [ActiveSheet];
        var map = Assets.ToDictionary(a => a.Id, a => a.Asset);
        if (!string.Equals(CurrentCalibration.PrinterName, SelectedPrinterName, StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("正在读取此打印机的设置，请稍后再试。");
            return;
        }
        short copies = (short)Math.Clamp(PrintCopies, 1, 99);
        var snapshot = toPrint.Select(sheet => sheet.Clone(preserveId: true)).ToList();
        if (snapshot.FirstOrDefault(sheet => !FreeformLayoutMath.CanOutput(sheet)) is { } invalid)
        {
            ShowToast($"{invalid.SheetName} 的照片框超出纸张或没有有效照片区域，请调整后再打印。");
            return;
        }
        var placed = snapshot.SelectMany(sheet => sheet.Slots).Select(slot => slot.PhotoAssetId).OfType<string>().ToHashSet();
        var printAssets = map.Where(pair => placed.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value.CloneMetadataOnly());
        string printer = SelectedPrinterName;
        var calibration = CurrentCalibration.Clone();
        int dpi = PrintDpi;
        double outputSharpen = OutputSharpeningAmount;
        using var cts = new CancellationTokenSource();
        _exportCts = cts;
        CancelExportCommand.NotifyCanExecuteChanged();

        IsPrinting = true;
        IsExportBusy = true;
        IsExportIndeterminate = true;
        try
        {
            ExportProgressText = "正在检查打印页面…";
            var portraitDiagnostics = await Task.Run(() => InspectPortraits(snapshot, printAssets, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            var warnings = snapshot.SelectMany(sheet => PreflightChecker.InspectSheet(sheet, printAssets, calibration, _latestHistograms,
                    portraitDiagnosticsByAssetId: portraitDiagnostics)
                .Issues.Where(issue => issue.Severity >= PreflightSeverity.Warning)
                .Select(issue => $"{sheet.SheetName}：{issue.Message}")).ToList();
            string papers = string.Join("、", snapshot.Select(s => $"{s.PaperWidthMm:0.##} × {s.PaperHeightMm:0.##} mm").Distinct());
            string summary = $"打印机：{printer}\n版面纸张：{papers}\n范围：{(allSheets ? "全部页面" : snapshot[0].SheetName)}，共 {snapshot.Count} 页，每页 {copies} 份\n精度：{dpi} DPI";
            if (duplexBack) summary += "\n使用背面校准偏移。";
            if (snapshot.Any(sheet => WindowsPrintService.ShouldPreferBorderlessPaper(sheet, calibration)))
                summary += "\n如果驱动启用无边距扩张，成品可能偏大。需要精确尺寸时请用相同纸型和驱动设置校准。";
            if (warnings.Count > 0)
            {
                summary += "\n\n预检提醒：\n" + string.Join("\n", warnings.Take(6));
                if (warnings.Count > 6) summary += $"\n另有 {warnings.Count - 6} 条，请按页检查。";
            }
            if (!Dialogs.Confirm(summary, "确认打印", isWarning: warnings.Count > 0)) return;

            ExportProgressText = $"正在发送 {snapshot.Count} 页到打印机 {printer}...";
            StatusText = ExportProgressText;
            SaveCurrentCalibration();
            var resolver = CreateSheetScopedExportResolver(snapshot, dpi, cts.Token);
            await Task.Run(() => PrintService.PrintSheets(snapshot, printAssets, resolver, printer, calibration, copies, dpi, duplexBack, outputSharpen, cts.Token), cts.Token);
            RememberPrintedPrinter(printer);
            StatusText = $"已提交打印任务至 {printer}";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StatusText = "已取消打印任务。";
            ShowToast(StatusText);
        }
        catch (Exception ex)
        {
            Logger.Error("打印执行失败", ex);
            Dialogs.ShowError($"打印失败：{ex.Message}", "打印错误");
        }
        finally
        {
            _exportCts = null;
            CancelExportCommand.NotifyCanExecuteChanged();
            IsPrinting = false;
            IsExportIndeterminate = false;
            IsExportBusy = false;
            ExportProgressText = "";
        }
    }

    [RelayCommand]
    private Task PrintCalibrationCardAsync() => PrintCalibrationCardWithProfileAsync(CurrentCalibration);

    public async Task<bool> PrintCalibrationCardWithProfileAsync(PrinterCalibrationProfile calibration)
    {
        if (IsExportBusy) return false;
        if (ActiveSheet is not { } sheet || !InstalledPrinters.Contains(SelectedPrinterName))
        {
            Dialogs.ShowInfo("请先选择有效的本机打印机。", "提示");
            return false;
        }

        double width = sheet.PaperWidthMm;
        double height = sheet.PaperHeightMm;
        string printer = SelectedPrinterName;
        int dpi = PrintDpi;
        var snapshot = calibration.Clone();
        snapshot.DefaultBorderless = WindowsPrintService.ShouldPreferBorderlessPaper(sheet, snapshot);
        IsPrinting = true;
        IsExportBusy = true;
        IsExportIndeterminate = true;
        ExportProgressText = "正在发送打印校准测试卡...";
        try
        {
            await Task.Run(() => PrintService.PrintCalibrationCard(width, height, printer, snapshot, dpi));
            RememberPrintedPrinter(printer);
            var (horizontal, vertical) = CalibrationCardRenderer.GetRuleLengths(width, height);
            StatusText = $"已发送测试卡，请测量横向 {horizontal:0} mm 和纵向 {vertical:0} mm 标线。";
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("打印校准测试卡失败", ex);
            Dialogs.ShowError($"打印测试卡失败：{ex.Message}", "错误");
            return false;
        }
        finally
        {
            IsPrinting = false;
            IsExportIndeterminate = false;
            IsExportBusy = false;
            ExportProgressText = "";
        }
    }

    private void RememberPrintedPrinter(string printer)
    {
        if (string.Equals(_lastPrintedPrinterName, printer, StringComparison.Ordinal)) return;
        _lastPrintedPrinterName = printer;
        try
        {
            SaveConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("打印任务已提交，但未能保存上次使用的打印机", ex);
        }
    }

    [RelayCommand]
    private void NewProject()
    {
        if (IsPrinting || IsExportBusy || _isPackingProject)
        {
            ShowToast("当前有正在进行的打印或导出任务，请稍候。");
            return;
        }

        if (_projectPath != null)
        {
            if (!ConfirmReplaceWorkspace("新建空白工程")) return;
        }
        else if (Assets.Count > 0 || Sheets.Count > 1 || HasPlacedPhotosOnActiveSheet)
        {
            if (!Dialogs.Confirm(
                    $"将清空当前工作区中的 {Assets.Count} 张照片与 {Sheets.Count} 页排版并重新开始，原始图片文件不会被删除。是否继续？",
                    "新建空白工程"))
                return;
        }

        if (IsHotFolderWatching) StopHotFolderWatch();
        _workspaceRevision++;
        _autoFrameCancellation?.Cancel();
        _batchFramingCancellation?.Cancel();
        _workspaceThumbnailCts?.Cancel();
        _renderCts?.Cancel();
        _undoStack.Clear();
        _pendingUndoSnapshot = null;
        _continuousEditMutated = false;
        _appliedAutoFrames.Clear();
        SyncUndoRedoFlags();
        _compactMatteCache.Clear();
        _faceDetectionCache.Clear();
        _relightShadingCache.Clear();
        _latestHistograms.Clear();
        _portraitDiagnosticsCache.Clear();
        _latestPortraitDiagnostics.Clear();
        PreviewCache.ClearAllCaches();

        foreach (var asset in Assets)
            ImageCache.RemoveAsset(asset.Id);
        Assets.Clear();
        Sheets.Clear();
        ActiveSheet = null;
        SelectedSlot = null;
        HasSelectedSlot = false;
        SelectAsset(null);
        UpdateAssetUsageCounts();
        NotifyPolaroidSyncFlags();

        _projectPath = null;
        ClearWorkspaceDirty();
        OnPropertyChanged(nameof(WindowTitle));
        RebuildActiveSheetFromParameters(pushUndo: false);
        PagePreviewsInvalidated?.Invoke();
        DataStore.ClearTemporaryWorkspace();
        ClearAutoRecovery();
        _savedWorkspace = WorkspaceContent();
        ShowToast("已新建空白工程");
    }

    [RelayCommand]
    private void SaveProject() => TrySaveCurrentWorkspace(showFeedback: true);

    [RelayCommand]
    private void SaveProjectAs() => TrySaveProject(choosePath: true);

    [RelayCommand]
    private async Task PackProjectAsync()
    {
        if (ChooseProjectPath(pack: true) is not { } path) return;
        var project = new PrintProject
        {
            ProjectName = Path.GetFileNameWithoutExtension(path),
            Assets = Assets.Select(a => a.Asset.CloneMetadataOnly()).ToList(),
            Sheets = Sheets.Select(sheet => sheet.Clone(preserveId: true)).ToList(),
            ActiveSheetIndex = ActiveSheetIndex
        };
        string? snapshotContent = string.Equals(path, _projectPath, StringComparison.OrdinalIgnoreCase)
            ? WorkspaceContent() : null;
        _isPackingProject = true;
        StatusText = "正在复制工程原图…";
        try
        {
            await Task.Run(() => DataStore.SaveProject(path, project, copyAssetsBesideProject: true));
            if (snapshotContent != null)
            {
                _savedWorkspace = snapshotContent;
                if (!HasUnsavedWork())
                {
                    ClearWorkspaceDirty();
                    ClearAutoRecovery();
                }
            }
            StatusText = $"已打包工程：{project.ProjectName}";
            ShowToast("已打包工程。分享时请将工程文件和旁边的素材文件夹一起发送。");
        }
        catch (Exception ex)
        {
            Logger.Error("打包工程失败", ex);
            StatusText = "打包工程失败";
            Dialogs.ShowError($"打包工程失败：{ex.Message}", "保存失败");
        }
        finally { _isPackingProject = false; }
    }

    private bool _isPackingProject;

    private PrintProject CaptureProject() => new()
    {
        Assets = Assets.Select(a => a.Asset).ToList(),
        Sheets = Sheets.ToList(),
        ActiveSheetIndex = ActiveSheetIndex
    };

    private string WorkspaceContent() => JsonSerializer.Serialize(
        new { Assets = Assets.Select(a => a.Asset), Sheets }, PortablePaths.JsonOptions);

    private bool HasUnsavedWork() => WorkspaceContent() != _savedWorkspace;

    public bool ConfirmReplaceWorkspace(string action)
    {
        if (!HasUnsavedWork()) return true;
        if (_projectPath == null) return TrySaveCurrentWorkspace();
        var result = Dialogs.ConfirmYesNoCancel($"当前工程有未保存的修改。是否先保存再{action}？", "未保存的修改");
        return result == DialogChoice.No || (result == DialogChoice.Yes && TrySaveProject());
    }

    private void SaveTemporaryWorkspace()
    {
        var project = CaptureProject();
        project.ProjectName = "临时工程";
        DataStore.SaveTemporaryWorkspace(project);
    }

    private bool TrySaveCurrentWorkspace(bool showFeedback = false)
    {
        if (_projectPath != null) return TrySaveProject();
        try
        {
            SaveTemporaryWorkspace();
            _savedWorkspace = WorkspaceContent();
            ClearAutoRecovery();
            if (showFeedback) ShowToast("已保留当前临时工程，下次打开可继续。");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("保存临时工程失败", ex);
            Dialogs.ShowError($"无法保留临时工程：{ex.Message}", "保存失败");
            return false;
        }
    }

    public bool TryCloseWorkspace()
    {
        if (_isPackingProject)
        {
            ShowToast("正在复制工程原图，请稍候。");
            return false;
        }
        if (!ConfirmReplaceWorkspace("关闭")) return false;
        ClearAutoRecovery();
        return true;
    }

    private string? ChooseProjectPath(bool pack) =>
        Dialogs.ShowSaveFileDialog(
            pack ? "打包工程与原图" : "保存工程",
            "冲印工程文件 (*.ppsproj)|*.ppsproj",
            "ppsproj",
            _projectPath == null ? $"排版工程_{DateTime.Now:yyyyMMdd_HHmm}.ppsproj" : Path.GetFileName(_projectPath),
            _projectPath == null ? Paths.ProjectsDirectory : Path.GetDirectoryName(_projectPath));

    private bool TrySaveProject(bool choosePath = false)
    {
        if (_isPackingProject)
        {
            ShowToast("正在复制工程原图，请稍候。");
            return false;
        }
        string? path = choosePath || _projectPath == null ? ChooseProjectPath(pack: false) : _projectPath;
        if (path == null) return false;
        try
        {
            var project = CaptureProject();
            project.ProjectName = Path.GetFileNameWithoutExtension(path);
            DataStore.SaveProject(path, project);
            bool wasTemporary = _projectPath == null;
            _projectPath = path;
            ClearWorkspaceDirty();
            OnPropertyChanged(nameof(WindowTitle));
            _savedWorkspace = WorkspaceContent();
            ClearAutoRecovery();
            if (wasTemporary) DataStore.ClearTemporaryWorkspace();
            ShowToast($"工程已保存：{Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("保存工程失败", ex);
            Dialogs.ShowError($"保存工程失败：{ex.Message}", "保存失败");
            return false;
        }
    }

    [RelayCommand]
    private void OpenProject()
    {
        if (_isPackingProject)
        {
            ShowToast("正在复制工程原图，请稍候。");
            return;
        }
        string? fileName = Dialogs.ShowOpenFileDialog(
            "打开冲印工程",
            "冲印工程文件 (*.ppsproj)|*.ppsproj",
            Paths.ProjectsDirectory);
        if (string.IsNullOrWhiteSpace(fileName)) return;

        try
        {
            var proj = DataStore.LoadProject(fileName);
            bool relinked = proj.Assets.Any(a => !File.Exists(a.FilePath));
            if (relinked && !RelinkMissingPhotos(proj)) return;
            if (!ConfirmReplaceWorkspace("打开其他工程")) return;
            LoadProjectIntoWorkspace(proj);
            _projectPath = fileName;
            if (relinked) MarkWorkspaceDirty();
            else ClearWorkspaceDirty();
            OnPropertyChanged(nameof(WindowTitle));
            if (!relinked) _savedWorkspace = WorkspaceContent();
            ClearAutoRecovery();
            StatusText = $"已打开工程：{proj.ProjectName}";
        }
        catch (Exception ex)
        {
            Logger.Error("打开工程失败", ex);
            Dialogs.ShowError($"打开工程失败：{ex.Message}", "错误");
        }
    }

    private bool RelinkMissingPhotos(PrintProject project)
    {
        var missing = project.Assets.Where(a => !File.Exists(a.FilePath)).ToList();
        if (!Dialogs.Confirm(
            $"找不到 {missing.Count} 张原图，例如「{Path.GetFileName(missing[0].FilePath)}」。是否立即选择原图所在的文件夹？",
            "查找原图",
            isWarning: true)) return false;

        string? folderName = Dialogs.ShowOpenFolderDialog("请选择缺失原图所在的文件夹");
        if (string.IsNullOrWhiteSpace(folderName)) return false;

        // 先完整校验所有候选路径，全部存在后再一次性应用，避免中途失败污染原工程状态
        var resolved = new List<(PhotoAsset Asset, string Path)>(missing.Count);
        foreach (var asset in missing)
        {
            string path = Path.Combine(folderName, Path.GetFileName(asset.FilePath));
            if (!File.Exists(path))
            {
                Dialogs.ShowInfo($"此文件夹仍缺少「{Path.GetFileName(path)}」，当前工程未改变。", "未找到全部原图");
                return false;
            }
            resolved.Add((asset, path));
        }
        foreach (var (asset, path) in resolved)
            asset.FilePath = path;
        return true;
    }
}
