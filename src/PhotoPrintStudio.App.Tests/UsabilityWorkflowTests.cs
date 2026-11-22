using System.IO;
using PhotoPrintStudio.App.Printing;
using PhotoPrintStudio.App.Services;
using PhotoPrintStudio.App.ViewModels;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Persistence;
using PhotoPrintStudio.Core.Portable;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.App.Tests;

public class UsabilityWorkflowTests
{
    private sealed class StubDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public DialogChoice YesNoCancelResult { get; set; } = DialogChoice.No;
        public string? LastRevealedFile { get; private set; }
        public void ShowInfo(string message, string title) { }
        public void ShowError(string message, string title) { }
        public bool Confirm(string message, string title, bool isWarning = false) => ConfirmResult;
        public DialogChoice ConfirmYesNoCancel(string message, string title) => YesNoCancelResult;
        public string? ShowSaveFileDialog(string title, string filter, string defaultExt, string fileName, string? initialDir = null) => null;
        public string? ShowOpenFileDialog(string title, string filter, string? initialDir = null) => null;
        public string? ShowOpenFolderDialog(string title, string? initialDir = null) => null;
        public void OpenPathInShell(string path) { }
        public void RevealFileInShell(string filePath) => LastRevealedFile = filePath;
    }

    private sealed class StubPrintService : IPrintService
    {
        public bool HasSavedDriverPreferences(string printerName) => false;
        public string GetSavedDevModeBase64(string printerName) => "";
        public void LoadSavedDevModeBase64(string printerName, string? base64) { }
        public bool ShowPrinterPreferencesDialog(string printerName, IntPtr ownerHwnd) => false;
        public IReadOnlyList<string> GetInstalledPrinters() => ["Mock Printer"];
        public PrinterDeviceSummary? InspectPrinter(string printerName) => new(printerName, 0, 0, false, true, ["4x6"]);
        public void PrintSheets(IReadOnlyList<PrintSheet> sheets, IReadOnlyDictionary<string, PhotoAsset> assetsById,
            Func<PhotoAsset, PhotoEditState, SKBitmap> resolveFullResBitmap, string printerName, PrinterCalibrationProfile calibration,
            short copies = 1, int printDpi = 300, bool duplexMirrorBackSide = false, double outputSharpeningAmount = 0, CancellationToken cancellationToken = default) { }
        public void PrintCalibrationCard(double paperWidthMm, double paperHeightMm, string printerName, PrinterCalibrationProfile calibration, int dpi = 300) { }
    }

    private static string WritePng(string dir, string name, SKColor color)
    {
        string path = Path.Combine(dir, name);
        using var bmp = new SKBitmap(new SKImageInfo(180, 240, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        bmp.Erase(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static MainViewModel CreateVm(string root, StubDialogService? dialogs = null)
    {
        var paths = new PortablePaths(root);
        return new MainViewModel(
            checkRecovery: false,
            paths: paths,
            dataStore: new PortableDataStore(paths, new AppLogger(paths)),
            printService: new StubPrintService(),
            dialogs: dialogs ?? new StubDialogService(),
            uiDispatcher: new InlineUiDispatcher(),
            loadBackgroundServices: false);
    }

    private static AssetItemViewModel AddSampleAsset(MainViewModel vm, string dir, string name, SKColor color)
    {
        string path = WritePng(dir, name, color);
        var asset = ExifAndColorHelper.InspectAsset(path);
        var item = new AssetItemViewModel(asset, null);
        vm.Assets.Add(item);
        return item;
    }

    [Fact]
    public void ClearActiveSheetSlots_And_ClearSelectedSlot_ShouldClearSafelyUpdateContextAndSupportUndo()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_clear_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            var p1 = AddSampleAsset(vm, tempRoot, "a.png", SKColors.SteelBlue);
            vm.FillAllSlotsWithAsset(p1);

            Assert.Equal(8, vm.ActiveSheet!.Slots.Count);
            Assert.True(vm.HasPlacedPhotosOnActiveSheet);
            Assert.True(vm.HasSelectedSlotPhoto);

            // Clear single slot updates active context immediately
            vm.ClearSelectedSlotCommand.Execute(null);
            Assert.Null(vm.ActiveSheet.Slots[0].PhotoAssetId);
            Assert.False(vm.HasSelectedSlotPhoto);
            Assert.Contains("空", vm.ActiveEditTargetTitle);
            Assert.True(vm.HasPlacedPhotosOnActiveSheet);

            // Clearing an already empty slot must be a no-op (does not push redundant undo)
            vm.ClearSelectedSlotCommand.Execute(null);
            vm.UndoCommand.Execute(null);
            Assert.Equal(p1.Id, vm.ActiveSheet.Slots[0].PhotoAssetId);

            // Clear all slots on active page in one click
            vm.ClearActiveSheetSlotsCommand.Execute(null);
            Assert.All(vm.ActiveSheet.Slots, slot => Assert.Null(slot.PhotoAssetId));
            Assert.False(vm.HasPlacedPhotosOnActiveSheet);
            Assert.False(vm.HasSelectedSlotPhoto);
            Assert.Single(vm.Assets);
            Assert.Equal(0, vm.Assets[0].UsageCount);
            Assert.Equal("未排", vm.Assets[0].UsageBadgeText);
            Assert.Equal(1, vm.UnplacedPhotoCount);

            // Single Undo restores all 8 slots
            vm.UndoCommand.Execute(null);
            Assert.All(vm.ActiveSheet.Slots, slot => Assert.Equal(p1.Id, slot.PhotoAssetId));
            Assert.True(vm.HasPlacedPhotosOnActiveSheet);
            Assert.Equal("×8", vm.Assets[0].UsageBadgeText);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void QuickPlaceAsset_ShouldFillEmptySlotsSequentiallyThenReplaceSelected()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_quick_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            vm.SetScenarioCommand.Execute(LayoutScenario.FreeGrid);
            Assert.Equal(4, vm.ActiveSheet!.Slots.Count);

            var p1 = AddSampleAsset(vm, tempRoot, "p1.png", SKColors.Red);
            var p2 = AddSampleAsset(vm, tempRoot, "p2.png", SKColors.Green);
            var p3 = AddSampleAsset(vm, tempRoot, "p3.png", SKColors.Blue);
            var p4 = AddSampleAsset(vm, tempRoot, "p4.png", SKColors.Orange);

            Assert.True(vm.QuickPlaceAsset(p1));
            Assert.True(vm.QuickPlaceAsset(p2));
            Assert.True(vm.QuickPlaceAsset(p3));
            Assert.True(vm.QuickPlaceAsset(p4));

            Assert.Equal([p1.Id, p2.Id, p3.Id, p4.Id], vm.ActiveSheet.Slots.Select(s => s.PhotoAssetId ?? "").ToArray());

            // Page is full: selecting slot 1 and quick-placing p1 replaces slot 1
            vm.SelectSlot(vm.ActiveSheet.Slots[1]);
            Assert.True(vm.QuickPlaceAsset(p1));
            Assert.Equal(p1.Id, vm.ActiveSheet.Slots[1].PhotoAssetId);

            // Quick-placing the exact same photo on full page returns false (allowing double-click to open crop view)
            Assert.False(vm.QuickPlaceAsset(p1));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void AssignAssetsStartingAtSlot_And_FillEmptySlotsWithAssets_ShouldWorkCorrectly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_multi_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            vm.SetScenarioCommand.Execute(LayoutScenario.FreeGrid);
            var p1 = AddSampleAsset(vm, tempRoot, "p1.png", SKColors.Red);
            var p2 = AddSampleAsset(vm, tempRoot, "p2.png", SKColors.Green);
            var p3 = AddSampleAsset(vm, tempRoot, "p3.png", SKColors.Blue);
            var p4 = AddSampleAsset(vm, tempRoot, "p4.png", SKColors.Yellow);

            // Dragging 3 photos onto Slot 1 fills Slots 1, 2, 3
            int placed = vm.AssignAssetsStartingAtSlot([p1, p2, p3], vm.ActiveSheet!.Slots[1]);
            Assert.Equal(3, placed);
            Assert.Null(vm.ActiveSheet.Slots[0].PhotoAssetId);
            Assert.Equal(p1.Id, vm.ActiveSheet.Slots[1].PhotoAssetId);
            Assert.Equal(p2.Id, vm.ActiveSheet.Slots[2].PhotoAssetId);
            Assert.Equal(p3.Id, vm.ActiveSheet.Slots[3].PhotoAssetId);

            // Clear Slot 2, then FillEmptySlots with 3 photos fills only Slots 0 and 2
            vm.ActiveSheet.Slots[2].PhotoAssetId = null;
            int filledEmpty = vm.FillEmptySlotsWithAssets([p4, p2, p1]);
            Assert.Equal(2, filledEmpty);
            Assert.Equal(p4.Id, vm.ActiveSheet.Slots[0].PhotoAssetId);
            Assert.Equal(p1.Id, vm.ActiveSheet.Slots[1].PhotoAssetId);
            Assert.Equal(p2.Id, vm.ActiveSheet.Slots[2].PhotoAssetId);
            Assert.Equal(p3.Id, vm.ActiveSheet.Slots[3].PhotoAssetId);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void SwapOrMoveSlot_ShouldSwapFilledAndMoveToEmptyWithUndo()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_swap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            vm.SetScenarioCommand.Execute(LayoutScenario.FreeGrid);
            var p1 = AddSampleAsset(vm, tempRoot, "p1.png", SKColors.Red);
            var p2 = AddSampleAsset(vm, tempRoot, "p2.png", SKColors.Blue);

            var s0 = vm.ActiveSheet!.Slots[0];
            var s1 = vm.ActiveSheet.Slots[1];
            var s2 = vm.ActiveSheet.Slots[2];
            s0.PhotoAssetId = p1.Id;
            s0.CustomCaption = "Left";
            s1.PhotoAssetId = p2.Id;
            s1.CustomCaption = "Right";

            Assert.True(vm.SwapOrMoveSlot(s0, s1));
            Assert.Equal(p2.Id, s0.PhotoAssetId);
            Assert.Equal("Right", s0.CustomCaption);
            Assert.Equal(p1.Id, s1.PhotoAssetId);
            Assert.Equal("Left", s1.CustomCaption);

            // Move to empty slot
            Assert.True(vm.SwapOrMoveSlot(s0, s2));
            Assert.Null(s0.PhotoAssetId);
            Assert.Equal(p2.Id, s2.PhotoAssetId);

            // Undo move and undo swap
            vm.UndoCommand.Execute(null);
            Assert.Equal(p2.Id, vm.ActiveSheet.Slots[0].PhotoAssetId);
            Assert.Null(vm.ActiveSheet.Slots[2].PhotoAssetId);
            vm.UndoCommand.Execute(null);
            Assert.Equal(p1.Id, vm.ActiveSheet.Slots[0].PhotoAssetId);
            Assert.Equal(p2.Id, vm.ActiveSheet.Slots[1].PhotoAssetId);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void PageManagement_MoveSheet_InsertBlankAfter_And_ClearMultiplePages_ShouldRenumberAndUndo()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_pages_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            vm.SetScenarioCommand.Execute(LayoutScenario.SixInchTwoInOne);
            var p1 = AddSampleAsset(vm, tempRoot, "p1.png", SKColors.Red);
            var p2 = AddSampleAsset(vm, tempRoot, "p2.png", SKColors.Green);

            vm.FillAllSlotsWithAsset(p1);
            vm.AddBlankSheetCommand.Execute(null);
            vm.FillAllSlotsWithAsset(p2);

            Assert.Equal(2, vm.Sheets.Count);
            var firstId = vm.Sheets[0].Id;
            var secondId = vm.Sheets[1].Id;

            // Move page 0 after page 1
            Assert.True(vm.MoveSheet(vm.Sheets[0], +1));
            Assert.Equal(secondId, vm.Sheets[0].Id);
            Assert.Equal(firstId, vm.Sheets[1].Id);
            Assert.Equal("第 1 页", vm.Sheets[0].SheetName);
            Assert.Equal("第 2 页", vm.Sheets[1].SheetName);

            // Insert blank sheet after first page
            vm.InsertBlankSheetAfter(vm.Sheets[0]);
            Assert.Equal(3, vm.Sheets.Count);
            Assert.Equal(1, vm.ActiveSheetIndex);
            Assert.False(vm.HasPlacedPhotosOnActiveSheet);

            // Clear multiple pages in one batch
            int cleared = vm.ClearSheetsSlots([vm.Sheets[0], vm.Sheets[2]]);
            Assert.Equal(4, cleared);
            Assert.All(vm.Sheets.SelectMany(s => s.Slots), slot => Assert.Null(slot.PhotoAssetId));

            // Undo restores both pages' photos
            vm.UndoCommand.Execute(null);
            Assert.Equal(p2.Id, vm.Sheets[0].Slots[0].PhotoAssetId);
            Assert.Equal(p1.Id, vm.Sheets[2].Slots[0].PhotoAssetId);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void BatchPhotoOperations_PasteSubset_RemoveUnplaced_And_EmptyStateGuard_ShouldWork()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_batch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var dialogs = new StubDialogService();
            using var vm = CreateVm(tempRoot, dialogs);
            var p1 = AddSampleAsset(vm, tempRoot, "p1.png", SKColors.Coral);
            var p2 = AddSampleAsset(vm, tempRoot, "p2.png", SKColors.Teal);
            var p3 = AddSampleAsset(vm, tempRoot, "p3.png", SKColors.Gold);

            vm.FillAllSlotsWithAsset(p1);
            vm.EditExposure = 0.75;
            vm.CopyColorParametersCommand.Execute(null);

            // Selective paste to p2 only; p3 must stay untouched
            vm.PasteColorToAssets([p2]);
            Assert.Equal(0.75, p2.Asset.EditState.Exposure, 3);
            Assert.Equal(0.0, p3.Asset.EditState.Exposure, 3);

            // Reveal in Explorer delegates to IDialogService
            vm.RevealAssetInExplorer(p1);
            Assert.Equal(p1.Asset.FilePath, dialogs.LastRevealedFile);

            // Remove unplaced removes p2 & p3 while keeping p1
            Assert.Equal(2, vm.RemoveUnplacedPhotos());
            Assert.Single(vm.Assets);
            Assert.Equal(p1.Id, vm.Assets[0].Id);

            // Enter single crop view, then remove last photo -> must return to SheetOverview & clear stale state
            vm.SetViewModeCommand.Execute(CanvasViewMode.SingleRetouchCrop);
            Assert.Equal(CanvasViewMode.SingleRetouchCrop, vm.ViewMode);

            vm.RemovePhotos([p1]);
            Assert.Empty(vm.Assets);
            Assert.False(vm.HasSelectedAsset);
            Assert.Equal(CanvasViewMode.SheetOverview, vm.ViewMode);
            Assert.Null(vm.ActiveHistogram);
            Assert.Equal("", vm.ClippingWarningText);

            // Guard: cannot enter SingleRetouchCrop or SplitCompare when no asset is selected
            vm.SetViewModeCommand.Execute(CanvasViewMode.SingleRetouchCrop);
            Assert.Equal(CanvasViewMode.SheetOverview, vm.ViewMode);
            vm.SetViewModeCommand.Execute(CanvasViewMode.SplitCompare);
            Assert.Equal(CanvasViewMode.SheetOverview, vm.ViewMode);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task ImportFilesAsync_WithFolderDropAndFillEmptySlots_ShouldExpandFolderAndFillOnlyEmptySlots()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_folder_{Guid.NewGuid():N}");
        string dropDir = Path.Combine(tempRoot, "vacation");
        Directory.CreateDirectory(dropDir);
        try
        {
            using var vm = CreateVm(tempRoot);
            vm.SetScenarioCommand.Execute(LayoutScenario.FreeGrid);
            var existing = AddSampleAsset(vm, tempRoot, "existing.png", SKColors.Black);
            vm.AssignAssetToSlot(existing, vm.ActiveSheet!.Slots[0]);

            WritePng(dropDir, "01.png", SKColors.Red);
            WritePng(dropDir, "02.png", SKColors.Green);
            WritePng(dropDir, "03.png", SKColors.Blue);
            File.WriteAllText(Path.Combine(dropDir, "notes.txt"), "ignore me");

            await vm.ImportFilesAsync([dropDir], _ => new ImportArrangement(ImportPlacement.FillEmptySlots));

            Assert.Equal(4, vm.Assets.Count);
            Assert.Equal(existing.Id, vm.ActiveSheet.Slots[0].PhotoAssetId);
            Assert.All(vm.ActiveSheet.Slots, slot => Assert.False(string.IsNullOrEmpty(slot.PhotoAssetId)));
            Assert.Equal(0, vm.UnplacedPhotoCount);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void NewProject_FilterVisibility_And_PrintCopiesStepper_ShouldWorkCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_newproj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            var p1 = AddSampleAsset(vm, tempRoot, "p1.png", SKColors.CornflowerBlue);
            vm.FillAllSlotsWithAsset(p1);
            vm.AddBlankSheetCommand.Execute(null);

            Assert.False(vm.IsFilterActive);
            vm.EditFilterId = "fuji_fresh";
            Assert.True(vm.IsFilterActive);

            // Print copies stepper & clamping
            Assert.Equal(1, vm.PrintCopies);
            vm.DecreasePrintCopiesCommand.Execute(null);
            Assert.Equal(1, vm.PrintCopies);
            vm.IncreasePrintCopiesCommand.Execute(null);
            Assert.Equal(2, vm.PrintCopies);
            vm.PrintCopies = 150;
            Assert.Equal(99, vm.PrintCopies);
            vm.PrintCopies = 0;
            Assert.Equal(1, vm.PrintCopies);

            // New blank project clears workspace cleanly
            vm.NewProjectCommand.Execute(null);
            Assert.Empty(vm.Assets);
            Assert.Single(vm.Sheets);
            Assert.False(vm.HasPlacedPhotosOnActiveSheet);
            Assert.False(vm.CanUndo);
            Assert.Equal("光影印坊", vm.WindowTitle);
            Assert.False(vm.IsFilterActive);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void ContinuousEditWithoutMutation_ShouldNotPushGhostUndo_And_PresetSyncAndIndicators_ShouldStayAccurate()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_iter2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            var p1 = AddSampleAsset(vm, tempRoot, "p1.png", SKColors.SteelBlue);
            var p2 = AddSampleAsset(vm, tempRoot, "p2.png", SKColors.Coral);
            vm.SelectAsset(p1);

            // Click without changing value must not push a no-op undo entry
            Assert.False(vm.CanUndo);
            vm.BeginContinuousEdit();
            vm.EndContinuousEdit();
            Assert.False(vm.CanUndo);

            // Real edit updates indicators and switches preset to custom placeholder
            Assert.False(vm.HasLightColorEdits);
            Assert.Equal("preset_original", vm.SelectedPreset?.Id);
            vm.BeginContinuousEdit();
            vm.EditExposure = 0.5;
            vm.EndContinuousEdit();
            Assert.True(vm.CanUndo);
            Assert.True(vm.HasLightColorEdits);
            Assert.Null(vm.SelectedPreset);
            Assert.True(vm.ShowCustomPresetPlaceholder);

            // Applying preset to p1, switching to untouched p2 resets SelectedPreset so same preset can be applied to p2
            var warmPreset = vm.Presets.First(p => p.Id == "preset_kodak_warm");
            vm.SelectedPreset = warmPreset;
            Assert.Equal(warmPreset.Parameters.Temperature, p1.Asset.EditState.Temperature, 3);

            vm.SelectAsset(p2);
            Assert.Equal("preset_original", vm.SelectedPreset?.Id);
            vm.SelectedPreset = warmPreset;
            Assert.Equal(warmPreset.Parameters.Temperature, p2.Asset.EditState.Temperature, 3);

            // Curve & HSL indicators reflect modifications
            vm.ActiveHslChannel = 1; // Orange
            vm.HslLightness = 15;
            Assert.True(vm.HasHslOrangeEdit);
            Assert.True(vm.HasCurveHslEdits);
            Assert.False(vm.HasHslRedEdit);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void NonPolaroidSlotCaption_ShouldEditAndClearWithUndo()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_caption_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            vm.SetScenarioCommand.Execute(LayoutScenario.SixInchTwoInOne);
            vm.SelectedTemplate = vm.FilteredTemplates.First(t => t.Id == "tpl_6in_2in1_border");

            Assert.True(vm.CanEditNonPolaroidCaption);
            vm.UpdateSelectedSlotCaption("修图前");
            Assert.Equal("修图前", vm.SelectedSlotCaption);
            Assert.Equal("修图前", vm.SelectedSlot!.CustomCaption);

            vm.UpdateSelectedSlotCaption("   ");
            Assert.Equal("", vm.SelectedSlotCaption);
            Assert.Null(vm.SelectedSlot.CustomCaption);

            vm.UndoCommand.Execute(null);
            Assert.Equal("修图前", vm.SelectedSlotCaption);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void GranularResetFlags_ShouldPreventNoOpUndo_And_ResetOnlyModifiedGroup()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_resetflags_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            var p1 = AddSampleAsset(vm, tempRoot, "p1.png", SKColors.DarkSlateBlue);
            vm.SelectAsset(p1);

            Assert.False(vm.HasLightingGroupEdits);
            Assert.False(vm.HasColorFilterGroupEdits);
            Assert.False(vm.HasCurvesLevelsEdits);
            Assert.False(vm.HasHslEdits);
            Assert.False(vm.HasCropGeometryEdits);
            Assert.False(vm.HasDetailBgEdits);
            Assert.False(vm.HasAnyColorEdits);

            // Resetting unmodified groups must be a no-op (no ghost undo entries)
            vm.ResetLightingGroupCommand.Execute(null);
            vm.ResetWhiteBalanceGroupCommand.Execute(null);
            vm.ResetCropGeometryCommand.Execute(null);
            vm.ResetDetailAndBgGroupCommand.Execute(null);
            vm.ResetColorAdjustmentsCommand.Execute(null);
            Assert.False(vm.CanUndo);

            // Modify lighting + color filter independently
            vm.EditExposure = 0.6;
            vm.EditSaturation = 20;
            Assert.True(vm.HasLightingGroupEdits);
            Assert.True(vm.HasColorFilterGroupEdits);
            Assert.True(vm.HasAnyColorEdits);
            Assert.False(vm.HasCropGeometryEdits);

            // Reset only lighting: color filter stays intact
            vm.ResetLightingGroupCommand.Execute(null);
            Assert.False(vm.HasLightingGroupEdits);
            Assert.True(vm.HasColorFilterGroupEdits);
            Assert.Equal(0.0, vm.EditExposure, 3);
            Assert.Equal(20.0, vm.EditSaturation, 3);
            Assert.True(vm.CanUndo);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void LocateAssetOnPages_And_SwitchToEmptySheet_ShouldNavigateCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_locate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            vm.SetScenarioCommand.Execute(LayoutScenario.SixInchTwoInOne);
            var p1 = AddSampleAsset(vm, tempRoot, "p1.png", SKColors.OrangeRed);
            var p2 = AddSampleAsset(vm, tempRoot, "p2.png", SKColors.MediumSeaGreen);

            vm.AssignAssetToSlot(p1, vm.ActiveSheet!.Slots[0]);
            vm.AddBlankSheetCommand.Execute(null); // Page 2 (empty)
            vm.AddBlankSheetCommand.Execute(null); // Page 3
            vm.AssignAssetToSlot(p1, vm.Sheets[2].Slots[1]);
            vm.AssignAssetToSlot(p2, vm.Sheets[2].Slots[0]);

            Assert.Equal(1, vm.GetFirstPlacedPageNumber(p1.Id));
            Assert.Equal(3, vm.GetFirstPlacedPageNumber(p2.Id));

            // From page 3 slot 0 (p2), locating p1 first jumps to page 1 slot 0, then cycles to page 3 slot 1
            vm.SelectSlot(vm.Sheets[2].Slots[0]);
            Assert.True(vm.LocateAssetOnPages(p1));
            Assert.Equal(0, vm.ActiveSheetIndex);
            Assert.Equal(vm.Sheets[0].Slots[0].Id, vm.SelectedSlot?.Id);

            Assert.True(vm.LocateAssetOnPages(p1));
            Assert.Equal(2, vm.ActiveSheetIndex);
            Assert.Equal(vm.Sheets[2].Slots[1].Id, vm.SelectedSlot?.Id);

            // Entering SingleRetouchCrop on page 3 and switching to empty page 2 must return to SheetOverview
            vm.SetViewModeCommand.Execute(CanvasViewMode.SingleRetouchCrop);
            Assert.Equal(CanvasViewMode.SingleRetouchCrop, vm.ViewMode);
            vm.SwitchToSheet(1);
            Assert.Equal(1, vm.ActiveSheetIndex);
            Assert.Equal(CanvasViewMode.SheetOverview, vm.ViewMode);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void PolaroidStyleSync_AcrossMultiplePages_ShouldUpdateAllPagesAndSupportUndo()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_polaroid_sync_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            vm.SetScenarioCommand.Execute(LayoutScenario.Polaroid);
            Assert.True(vm.HasMultiplePolaroidSlotsOnActiveSheet);
            Assert.False(vm.HasPolaroidSlotsAcrossMultiplePages);

            vm.AddBlankSheetCommand.Execute(null);
            Assert.True(vm.HasPolaroidSlotsAcrossMultiplePages);

            // Customize active slot on page 2
            vm.PolaroidCaptionTemplate = "2026 夏日回忆";
            vm.PolaroidFrameColorHex = "#FFF8E7";

            // Sync across all pages
            vm.ApplyPolaroidStyleToAllPagesCommand.Execute(null);
            Assert.All(vm.Sheets.SelectMany(s => s.Slots), slot =>
            {
                Assert.Equal("2026 夏日回忆", slot.PolaroidStyle?.CaptionTemplate);
                Assert.Equal("#FFF8E7", slot.PolaroidStyle?.FrameColorHex);
            });

            // Undo restores page 1's original border/caption
            vm.UndoCommand.Execute(null);
            Assert.NotEqual("2026 夏日回忆", vm.Sheets[0].Slots[0].PolaroidStyle?.CaptionTemplate);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void ClearSlotAndSheet_ShouldResetSlotOverrides_ExitSingleView_And_RestoreOnUndo()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_iter4_clear_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            vm.SetScenarioCommand.Execute(LayoutScenario.SixInchTwoInOne);
            var p1 = AddSampleAsset(vm, tempRoot, "p1.png", SKColors.Crimson);
            var p2 = AddSampleAsset(vm, tempRoot, "p2.png", SKColors.Navy);

            var s0 = vm.ActiveSheet!.Slots[0];
            var s1 = vm.ActiveSheet.Slots[1];
            vm.AssignAssetToSlot(p1, s0);
            vm.AssignAssetToSlot(p2, s1);
            s0.SlotZoom = 1.8;
            s0.SlotPanX = 0.25;
            s0.SlotPanY = -0.15;
            s0.FitModeOverride = FitMode.Contain;
            s1.SlotZoom = 1.5;

            // Enter SingleRetouchCrop on slot 0 and clear slot 0
            vm.SelectSlot(s0);
            vm.SetViewModeCommand.Execute(CanvasViewMode.SingleRetouchCrop);
            Assert.Equal(CanvasViewMode.SingleRetouchCrop, vm.ViewMode);

            vm.ClearSelectedSlotCommand.Execute(null);
            Assert.Equal(CanvasViewMode.SheetOverview, vm.ViewMode);
            Assert.Null(s0.PhotoAssetId);
            Assert.Equal(1.0, s0.SlotZoom, 3);
            Assert.Equal(0.0, s0.SlotPanX, 3);
            Assert.Equal(0.0, s0.SlotPanY, 3);
            Assert.Null(s0.FitModeOverride);

            // Undo restores slot 0 photo AND its per-slot zoom/pan/fit overrides
            vm.UndoCommand.Execute(null);
            Assert.Equal(p1.Id, vm.ActiveSheet.Slots[0].PhotoAssetId);
            Assert.Equal(1.8, vm.ActiveSheet.Slots[0].SlotZoom, 3);
            Assert.Equal(0.25, vm.ActiveSheet.Slots[0].SlotPanX, 3);
            Assert.Equal(FitMode.Contain, vm.ActiveSheet.Slots[0].FitModeOverride);

            // Clear entire sheet while in SingleRetouchCrop -> returns to SheetOverview, resets all slot overrides, notifies page previews
            int pagePreviewInvalidations = 0;
            vm.PagePreviewsInvalidated += () => pagePreviewInvalidations++;
            vm.SetViewModeCommand.Execute(CanvasViewMode.SingleRetouchCrop);
            vm.ClearActiveSheetSlotsCommand.Execute(null);
            Assert.Equal(CanvasViewMode.SheetOverview, vm.ViewMode);
            Assert.All(vm.ActiveSheet.Slots, slot =>
            {
                Assert.Null(slot.PhotoAssetId);
                Assert.Equal(1.0, slot.SlotZoom, 3);
                Assert.Equal(0.0, slot.SlotPanX, 3);
                Assert.Equal(0.0, slot.SlotPanY, 3);
                Assert.Null(slot.FitModeOverride);
            });
            Assert.True(pagePreviewInvalidations > 0);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void UndoDuringContinuousEdit_And_RestoreSnapshot_ShouldSyncStateCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_iter4_undo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            vm.SetScenarioCommand.Execute(LayoutScenario.Polaroid);
            var p1 = AddSampleAsset(vm, tempRoot, "p1.png", SKColors.ForestGreen);
            vm.SelectAsset(p1);

            // Add second page -> HasPolaroidSlotsAcrossMultiplePages becomes true
            vm.AddBlankSheetCommand.Execute(null);
            Assert.True(vm.HasPolaroidSlotsAcrossMultiplePages);

            // Start continuous edit, mutate exposure, then trigger Undo BEFORE EndContinuousEdit
            vm.BeginContinuousEdit();
            vm.EditExposure = 1.25;
            Assert.True(vm.HasLightingGroupEdits);

            int pagePreviewInvalidations = 0;
            vm.PagePreviewsInvalidated += () => pagePreviewInvalidations++;
            vm.UndoCommand.Execute(null);

            Assert.Equal(0.0, vm.EditExposure, 3);
            Assert.False(vm.HasLightingGroupEdits);
            Assert.True(pagePreviewInvalidations > 0);

            // Releasing the original drag afterwards must NOT push a stale ghost undo
            int beforeRedo = vm.CanRedo ? 1 : 0;
            vm.EndContinuousEdit();
            Assert.Equal(beforeRedo, vm.CanRedo ? 1 : 0);

            // Another Undo removes page 2 -> HasPolaroidSlotsAcrossMultiplePages must sync back to false
            vm.UndoCommand.Execute(null);
            Assert.Single(vm.Sheets);
            Assert.False(vm.HasPolaroidSlotsAcrossMultiplePages);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task BugFixes_NewProjectUnplacedReset_PasteAvailability_SlotCleanup_And_SmartArrangeUndo_ShouldHold()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_bugfix_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            vm.SetScenarioCommand.Execute(LayoutScenario.SixInchTwoInOne);

            // Create a landscape photo so AutoRotate triggers during SmartPaginate into portrait 2-in-1 slot
            string landscapePath = Path.Combine(tempRoot, "wide.png");
            using (var bmp = new SKBitmap(new SKImageInfo(900, 450, SKColorType.Bgra8888, SKAlphaType.Premul)))
            {
                bmp.Erase(SKColors.SkyBlue);
                using var canvas = new SKCanvas(bmp);
                using var paint = new SKPaint { Color = SKColors.DarkOrange };
                canvas.DrawRect(200, 100, 260, 260, paint);
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(landscapePath, data.ToArray());
            }
            var wide = new AssetItemViewModel(ExifAndColorHelper.InspectAsset(landscapePath), null);
            vm.Assets.Add(wide);
            var unplaced = AddSampleAsset(vm, tempRoot, "unplaced.png", SKColors.Purple);

            // Verify SmartPaginate Undo reverts BOTH pages AND QuarterTurns/Zoom/Pan mutations on the photo
            Assert.Equal(0, wide.Asset.EditState.QuarterTurns);
            await vm.ArrangeSelectedPhotosAsync([wide], new ImportArrangement(ImportPlacement.SmartPaginate, AutoRotate: true));
            Assert.Equal(1, wide.Asset.EditState.QuarterTurns);

            vm.UndoCommand.Execute(null);
            Assert.Equal(0, vm.Assets[0].Asset.EditState.QuarterTurns);
            Assert.Equal(1.0, vm.Assets[0].Asset.EditState.Zoom, 3);
            Assert.False(vm.HasPlacedPhotosOnActiveSheet);

            // Verify AddBlankSheet & RemovePhotos reset per-slot zoom/pan/fit overrides
            vm.AssignAssetToSlot(vm.Assets[0], vm.ActiveSheet!.Slots[0]);
            vm.ActiveSheet.Slots[0].SlotZoom = 1.9;
            vm.ActiveSheet.Slots[0].FitModeOverride = FitMode.Contain;
            vm.AddBlankSheetCommand.Execute(null);
            Assert.Equal(1.0, vm.ActiveSheet.Slots[0].SlotZoom, 3);
            Assert.Null(vm.ActiveSheet.Slots[0].FitModeOverride);

            // Verify Copy color -> NewProject resets UnplacedPhotoCount and disables CanPasteColor*
            vm.SelectAsset(vm.Assets[0]);
            vm.EditExposure = 0.4;
            vm.CopyColorParametersCommand.Execute(null);
            Assert.True(vm.CanPasteColorToSelected);
            Assert.True(vm.CanPasteColorToAll);
            Assert.True(vm.UnplacedPhotoCount > 0);

            vm.NewProjectCommand.Execute(null);
            Assert.Equal(0, vm.UnplacedPhotoCount);
            Assert.False(vm.CanPasteColorToSelected);
            Assert.False(vm.CanPasteColorToAll);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void DeepAuditFixes_FilterUndo_PolaroidSwapSync_SlotReset_And_RebuildPreservation_ShouldHold()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_ux_deepaudit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var vm = CreateVm(tempRoot);
            var p1 = AddSampleAsset(vm, tempRoot, "p1.png", SKColors.CornflowerBlue);
            var p2 = AddSampleAsset(vm, tempRoot, "p2.png", SKColors.Coral);
            vm.SelectAsset(p1);

            // 1) Filter selection supports Undo, and returning to "none" resets FilterIntensity so original preset matches again
            vm.EditFilterId = "fuji_fresh";
            vm.EditFilterIntensity = 55;
            Assert.True(vm.CanUndo);
            Assert.True(vm.ShowCustomPresetPlaceholder);

            vm.EditFilterId = "none";
            Assert.Equal(100.0, vm.EditFilterIntensity, 3);
            Assert.Equal("preset_original", vm.SelectedPreset?.Id);
            Assert.False(vm.ShowCustomPresetPlaceholder);

            vm.UndoCommand.Execute(null);
            Assert.Equal("fuji_fresh", vm.EditFilterId);

            // 2) No-op numeric write during continuous edit must NOT create a ghost undo
            vm.UndoCommand.Execute(null); // back to initial
            while (vm.CanUndo) vm.UndoCommand.Execute(null);
            vm.BeginContinuousEdit();
            vm.EditExposure = 0.0; // identical value
            vm.EndContinuousEdit();
            Assert.False(vm.CanUndo);

            // 3) Overwriting slot with a different photo resets stale SlotZoom/SlotPan
            vm.SetScenarioCommand.Execute(LayoutScenario.SixInchTwoInOne);
            vm.SelectedTemplate = vm.FilteredTemplates.First(t => t.Id == "tpl_6in_2in1_border");
            vm.AssignAssetToSlot(vm.Assets[0], vm.ActiveSheet!.Slots[0]);
            vm.ActiveSheet.Slots[0].SlotZoom = 2.4;
            vm.ActiveSheet.Slots[0].SlotPanX = 0.3;
            vm.AssignAssetToSlot(vm.Assets[1], vm.ActiveSheet.Slots[0]);
            Assert.Equal(1.0, vm.ActiveSheet.Slots[0].SlotZoom, 3);
            Assert.Equal(0.0, vm.ActiveSheet.Slots[0].SlotPanX, 3);

            // 4) RebuildActiveSheetFromParameters preserves SelectedSlot index AND intentionally cleared caption
            vm.SelectSlot(vm.ActiveSheet.Slots[1]);
            vm.UpdateSelectedSlotCaption("待清除标注");
            vm.UpdateSelectedSlotCaption("");
            Assert.Null(vm.ActiveSheet.Slots[1].CustomCaption);
            vm.LayoutMarginMm = 8.0;
            Assert.Equal(1, vm.SelectedSlot?.Index);
            Assert.Null(vm.ActiveSheet.Slots[1].CustomCaption);

            // 5) Polaroid SwapOrMoveSlot keeps PolaroidStyle.CaptionTemplate synchronized with CustomCaption
            vm.SetScenarioCommand.Execute(LayoutScenario.Polaroid);
            vm.AssignAssetToSlot(vm.Assets[0], vm.ActiveSheet!.Slots[0]);
            vm.AssignAssetToSlot(vm.Assets[1], vm.ActiveSheet.Slots[1]);
            vm.SelectSlot(vm.ActiveSheet.Slots[0]);
            vm.PolaroidCaptionTemplate = "卡片A";
            vm.SelectSlot(vm.ActiveSheet.Slots[1]);
            vm.PolaroidCaptionTemplate = "卡片B";

            Assert.True(vm.SwapOrMoveSlot(vm.ActiveSheet.Slots[0], vm.ActiveSheet.Slots[1]));
            Assert.Equal("卡片B", vm.ActiveSheet.Slots[0].CustomCaption);
            Assert.Equal("卡片B", vm.ActiveSheet.Slots[0].PolaroidStyle?.CaptionTemplate);
            Assert.Equal("卡片A", vm.ActiveSheet.Slots[1].CustomCaption);
            Assert.Equal("卡片A", vm.ActiveSheet.Slots[1].PolaroidStyle?.CaptionTemplate);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
