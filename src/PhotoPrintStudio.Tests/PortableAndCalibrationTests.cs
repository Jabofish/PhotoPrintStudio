using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Persistence;
using PhotoPrintStudio.Core.Portable;
using PhotoPrintStudio.Core.Preflight;
using PhotoPrintStudio.Core.Rendering;
using Xunit;

namespace PhotoPrintStudio.Tests;

public class PortableAndCalibrationTests
{
    [Fact]
    public void PortableDataStore_ShouldIsolateAllFilesInsideBaseDirectory()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_port_{Guid.NewGuid():N}");
        try
        {
            var paths = new PortablePaths(tempRoot);
            var logger = new AppLogger(paths);
            var store = new PortableDataStore(paths, logger);

            var cfg = new AppPortableConfig { IsDarkTheme = false, DefaultExportDpi = 600 };
            store.SaveConfig(cfg);
            var loadedCfg = store.LoadConfig();

            Assert.False(loadedCfg.IsDarkTheme);
            Assert.Equal(600, loadedCfg.DefaultExportDpi);
            Assert.True(File.Exists(Path.Combine(tempRoot, "portable_data", "config.json")));

            var cal = new PrinterCalibrationProfile
            {
                PrinterName = "Canon SELPHY CP1500",
                OverscanCompensationPercent = 97.5,
                OffsetXMm = 0.8
            };
            store.SavePrinterProfile(cal);
            var loadedCal = store.LoadPrinterProfile("Canon SELPHY CP1500");
            Assert.Equal(97.5, loadedCal.OverscanCompensationPercent);
            Assert.Equal(0.8, loadedCal.OffsetXMm);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void CalibrationCard_InverseScaleMath_ShouldCorrectUndersizedPrint()
    {
        var current = new PrinterCalibrationProfile { ScaleXPercent = 100.0, ScaleYPercent = 100.0 };
        // 若期望 100mm 实际打出来只有 98mm (偏小2%)，应放大约 102.04%
        var updated = CalibrationCardRenderer.ComputeCalibrationFromMeasured(current, 100, 98, 50, 50);
        Assert.InRange(updated.ScaleXPercent, 102.0, 102.1);
        Assert.Equal(100.0, updated.ScaleYPercent);

        using var cardBmp = CalibrationCardRenderer.RenderCalibrationCard(152.4, 101.6, 300.0, updated);
        Assert.Equal(1800, cardBmp.Width);
        Assert.Equal(1200, cardBmp.Height);
    }

    [Fact]
    public void PreflightChecker_ShouldDetectLowResolutionAndEmptySlots()
    {
        var paper6 = PaperSizeSpec.BuiltInPapers.First(p => p.Id == "paper_6inch");
        var tpl = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_6in_2in1_border");
        var sheet = LayoutEngine.BuildSheet(tpl, paper6);

        var lowResAsset = new PhotoAsset
        {
            Id = "low1",
            DisplayName = "TinyThumb",
            PixelWidth = 200,
            PixelHeight = 250
        };
        sheet.Slots[0].PhotoAssetId = lowResAsset.Id;
        // Slots[1] intentional empty

        var report = PreflightChecker.InspectSheet(
            sheet,
            new Dictionary<string, PhotoAsset> { [lowResAsset.Id] = lowResAsset });

        Assert.Equal(PreflightSeverity.Error, report.OverallSeverity);
        Assert.Contains(report.Issues, i => i.Category == "分辨率过低");
        Assert.Contains(report.Issues, i => i.Category == "空白格位");
    }

    [Fact]
    public void PreflightChecker_ShouldAvoidWhiteBackdropFalsePositive_VirtualPrinterMargin_AndDuplicateAlerts()
    {
        var paper6 = PaperSizeSpec.BuiltInPapers.First(p => p.Id == "paper_6inch");
        var tplFull2in1 = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_6in_2in1_full");
        var sheet = LayoutEngine.BuildSheet(tplFull2in1, paper6);

        var whiteBackdropAsset = new PhotoAsset
        {
            Id = "id_white",
            DisplayName = "StudioWhite",
            PixelWidth = 1200,
            PixelHeight = 1600
        };
        sheet.Slots[0].PhotoAssetId = whiteBackdropAsset.Id;
        sheet.Slots[1].PhotoAssetId = whiteBackdropAsset.Id;

        var histMap = new Dictionary<string, PhotoPrintStudio.Core.Imaging.HistogramData>
        {
            [whiteBackdropAsset.Id] = new()
            {
                TotalPixels = 10000,
                HighlightClippedRatio = 0.55,
                InteriorHighlightClippedRatio = 0.02,
                BorderWhiteRatio = 0.78,
                LooksLikeWhiteBackdrop = true
            }
        };

        // 1. 虚拟/无边距打印机不应误报安全边距，且白底人像不应误报高光溢出
        var virtualPrinter = new PrinterCalibrationProfile
        {
            PrinterName = "Microsoft Print to PDF",
            DefaultBorderless = true,
            SafeMarginMm = 0.0
        };

        var cleanReport = PreflightChecker.InspectSheet(
            sheet,
            new Dictionary<string, PhotoAsset> { [whiteBackdropAsset.Id] = whiteBackdropAsset },
            virtualPrinter,
            histMap);

        Assert.Equal(PreflightSeverity.Ok, cleanReport.OverallSeverity);
        Assert.Empty(cleanReport.Issues);

        // 2. 非无边距物理机在两格都贴边时应聚合为单条安全边距警告，而不是重复两条
        var physicalPrinter = new PrinterCalibrationProfile
        {
            PrinterName = "Canon Inkjet",
            DefaultBorderless = false,
            SafeMarginMm = 4.0
        };

        var aggReport = PreflightChecker.InspectSheet(
            sheet,
            new Dictionary<string, PhotoAsset> { [whiteBackdropAsset.Id] = whiteBackdropAsset },
            physicalPrinter,
            histMap);

        Assert.Single(aggReport.Issues);
        Assert.Equal("安全边距", aggReport.Issues[0].Category);
        Assert.Contains("#1, #2", aggReport.Issues[0].Message);
    }

    [Fact]
    public void PolaroidCaptionTokens_ShouldSubstituteExifDateCameraAndIndex()
    {
        var slot = new LayoutSlot { Index = 1 };
        var asset = new PhotoAsset
        {
            DisplayName = "IMG_0099",
            DateTaken = new DateTime(2026, 11, 17),
            CameraModel = "FUJIFILM X-T5"
        };
        var style = new PolaroidSlotStyle { CaptionTemplate = "#{Index} · {Date} · {Camera}" };

        string text = SheetRenderer.ResolvePolaroidCaption(slot, asset, style);
        Assert.Equal("#2 · 2026.11.17 · FUJIFILM X-T5", text);
    }

    [Fact]
    public void PortableDataStore_CustomPresetsAndTemplates_ShouldSaveRenameAndDeleteCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_crud_{Guid.NewGuid():N}");
        try
        {
            var paths = new PortablePaths(tempRoot);
            var store = new PortableDataStore(paths, new AppLogger(paths));

            // Preset Save -> Load -> Rename -> Delete
            var preset = new ColorPresetItem
            {
                Id = "user_preset_01",
                Name = "初版冷调证件",
                Parameters = new PhotoEditState { Exposure = 0.25, Contrast = 12 }
            };
            store.SaveCustomPreset(preset);
            var loadedPresets = store.LoadAllPresets();
            Assert.Contains(loadedPresets, p => p.Id == "user_preset_01" && !p.IsBuiltIn && p.Name == "初版冷调证件");

            preset.Name = "终版高透证件";
            store.SaveCustomPreset(preset);
            loadedPresets = store.LoadAllPresets();
            Assert.Contains(loadedPresets, p => p.Id == "user_preset_01" && p.Name == "终版高透证件");
            Assert.DoesNotContain(loadedPresets, p => p.Id == "user_preset_01" && p.Name == "初版冷调证件");

            Assert.True(store.DeleteCustomPreset("user_preset_01"));
            Assert.False(store.DeleteCustomPreset("user_preset_01"));
            loadedPresets = store.LoadAllPresets();
            Assert.DoesNotContain(loadedPresets, p => p.Id == "user_preset_01");
            Assert.NotEmpty(loadedPresets); // 内置预设完好保留

            // Template Save -> Load -> Rename -> Delete
            var tpl = new LayoutTemplateDefinition
            {
                Id = "tpl_user_custom_01",
                Name = "六寸紧凑2x3",
                Scenario = LayoutScenario.FreeGrid,
                TemplateKind = "free_grid",
                CustomRows = 2,
                CustomCols = 3
            };
            store.SaveCustomTemplate(tpl);
            var loadedTpls = store.LoadCustomTemplates();
            Assert.Single(loadedTpls);
            Assert.Equal("六寸紧凑2x3", loadedTpls[0].Name);

            tpl.Name = "六寸紧凑六宫格";
            store.SaveCustomTemplate(tpl);
            loadedTpls = store.LoadCustomTemplates();
            Assert.Single(loadedTpls);
            Assert.Equal("六寸紧凑六宫格", loadedTpls[0].Name);

            Assert.True(store.DeleteCustomTemplate("tpl_user_custom_01"));
            Assert.Empty(store.LoadCustomTemplates());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void CustomPaperAndPolaroidSpecs_ShouldPersistInConfig_AndBuildExactPhysicalLayout()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_phys_{Guid.NewGuid():N}");
        try
        {
            var paths = new PortablePaths(tempRoot);
            var store = new PortableDataStore(paths, new AppLogger(paths));

            var customPaper = new PaperSizeSpec("paper_user_8r", "自定义 10寸 (203.2×254)", 203.2, 254.0, "自定义", IsBuiltIn: false);
            var customPol = new PolaroidSpec("pol_user_slim", "窄边方卡", 70.0, 88.0, 62.0, 66.0, 4.0, 4.0, 4.0, 18.0, 1.5, IsBuiltIn: false);

            var cfg = store.LoadConfig();
            cfg.CustomPaperSizes.Add(customPaper);
            cfg.CustomPolaroidSpecs.Add(customPol);
            store.SaveConfig(cfg);

            var reloaded = store.LoadConfig();
            Assert.Single(reloaded.CustomPaperSizes);
            Assert.Single(reloaded.CustomPolaroidSpecs);
            Assert.Equal(203.2, reloaded.CustomPaperSizes[0].WidthMm, 2);
            Assert.Equal(18.0, reloaded.CustomPolaroidSpecs[0].MarginBottomMm, 2);

            var tpl = LayoutEngine.BuiltInTemplates.First(t => t.Scenario == LayoutScenario.Polaroid);
            var sheet = LayoutEngine.BuildSheet(
                tpl,
                reloaded.CustomPaperSizes[0],
                isLandscapeOverride: true,
                polaroidSpecOverride: reloaded.CustomPolaroidSpecs[0]);

            Assert.Equal(254.0, sheet.PaperWidthMm, 2);
            Assert.Equal(203.2, sheet.PaperHeightMm, 2);
            Assert.True(sheet.Slots.Count >= 4); // 254x203 可排至少 3x2 或 2x2 张 70x88 卡片
            Assert.Equal(18.0, sheet.Slots[0].PolaroidStyle!.MarginBottomMm, 2);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
