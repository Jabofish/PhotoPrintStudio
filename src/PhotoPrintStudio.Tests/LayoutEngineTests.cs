using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.Tests;

public class LayoutEngineTests
{
    [Fact]
    public void UnitConverter_MmToPixels_And_Points_ShouldBeExact()
    {
        Assert.Equal(1200, UnitConverter.MmToPixelsRounded(101.6, 300.0)); // 4 inch * 300 DPI
        Assert.Equal(1800, UnitConverter.MmToPixelsRounded(152.4, 300.0)); // 6 inch * 300 DPI
        Assert.Equal(3600, UnitConverter.MmToPixelsRounded(152.4, 600.0)); // 6 inch * 600 DPI
        Assert.InRange(UnitConverter.MmToPoints(25.4), 71.99, 72.01);
    }

    [Fact]
    public void SixInch_IdPhoto_1Inch_ShouldPack8UprightAnd9AutoMaxWithNoOverlap()
    {
        var paper6 = PaperSizeSpec.BuiltInPapers.First(p => p.Id == "paper_6inch");
        var tpl8 = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_id_6in_1in_8");
        var sheet8 = LayoutEngine.BuildSheet(tpl8, paper6);
        Assert.Equal(8, sheet8.Slots.Count);
        AssertSlotsWithinPaperAndDisjoint(sheet8);

        var tpl9 = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_id_6in_1in_9");
        var sheet9 = LayoutEngine.BuildSheet(tpl9, paper6);
        Assert.Equal(9, sheet9.Slots.Count);
        AssertSlotsWithinPaperAndDisjoint(sheet9);
    }

    [Fact]
    public void SixInch_Mixed_1And2Inch_ShouldContain4OneInchAnd2TwoInch()
    {
        var paper6 = PaperSizeSpec.BuiltInPapers.First(p => p.Id == "paper_6inch");
        var tpl = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_id_6in_mixed_1_2");
        var sheet = LayoutEngine.BuildSheet(tpl, paper6);

        Assert.Equal(6, sheet.Slots.Count);
        Assert.Equal(4, sheet.Slots.Count(s => s.IdPhotoSpecId == "id_1inch"));
        Assert.Equal(2, sheet.Slots.Count(s => s.IdPhotoSpecId == "id_2inch"));
        AssertSlotsWithinPaperAndDisjoint(sheet);
    }

    [Theory]
    [InlineData("tpl_6in_2in1_full")]
    [InlineData("tpl_6in_2in1_border")]
    [InlineData("tpl_6in_2in1_vert")]
    [InlineData("tpl_6in_2in1_card_duplex")]
    [InlineData("tpl_6in_2in1_left")]
    public void SixInchTwoInOne_AllTemplates_ShouldProduceExactlyTwoSlots(string templateId)
    {
        var paper6 = PaperSizeSpec.BuiltInPapers.First(p => p.Id == "paper_6inch");
        var tpl = LayoutEngine.BuiltInTemplates.First(t => t.Id == templateId);
        var sheet = LayoutEngine.BuildSheet(tpl, paper6);

        Assert.Equal(2, sheet.Slots.Count);
        AssertSlotsWithinPaperAndDisjoint(sheet);
    }

    [Fact]
    public void Polaroid_InstaxMini_On6Inch_ShouldFitTwoTrueOneToOneCards()
    {
        var paper6 = PaperSizeSpec.BuiltInPapers.First(p => p.Id == "paper_6inch");
        var tpl = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_pol_6in_mini_2");
        var sheet = LayoutEngine.BuildSheet(tpl, paper6);

        Assert.Equal(2, sheet.Slots.Count);
        foreach (var s in sheet.Slots)
        {
            Assert.Equal(54.0, s.WidthMm, 2);
            Assert.Equal(86.0, s.HeightMm, 2);
            Assert.NotNull(s.PolaroidStyle);
            Assert.Equal(20.0, s.PolaroidStyle!.MarginBottomMm, 2);
        }
        AssertSlotsWithinPaperAndDisjoint(sheet);
    }

    [Fact]
    public void Polaroid_InstaxSquare_On6Inch_ShouldFitTwoTrueOneToOneCards()
    {
        var paper6 = PaperSizeSpec.BuiltInPapers.First(p => p.Id == "paper_6inch");
        var tpl = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_pol_6in_square_2");
        var sheet = LayoutEngine.BuildSheet(tpl, paper6);

        Assert.Equal(2, sheet.Slots.Count);
        Assert.Equal(72.0, sheet.Slots[0].WidthMm, 2);
        Assert.Equal(86.0, sheet.Slots[0].HeightMm, 2);
        AssertSlotsWithinPaperAndDisjoint(sheet);
    }

    [Fact]
    public void Polaroid_InstaxMini_OnA4_ShouldFitNineTrueOneToOneCards()
    {
        var paperA4 = PaperSizeSpec.BuiltInPapers.First(p => p.Id == "paper_a4");
        var tpl = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_pol_a4_mini_9");
        var sheet = LayoutEngine.BuildSheet(tpl, paperA4);

        Assert.Equal(9, sheet.Slots.Count);
        AssertSlotsWithinPaperAndDisjoint(sheet);
    }

    [Fact]
    public void BuildPaginatedSheets_ShouldCreateCorrectNumberOfPages()
    {
        var paper6 = PaperSizeSpec.BuiltInPapers.First(p => p.Id == "paper_6inch");
        var tpl = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_pol_6in_mini_2");
        var proto = LayoutEngine.BuildSheet(tpl, paper6);

        var assets = Enumerable.Range(1, 5).Select(i => new PhotoAsset { Id = $"a{i}", DisplayName = $"P{i}" }).ToList();
        var sheets = LayoutEngine.BuildPaginatedSheets(proto, assets);

        Assert.Equal(3, sheets.Count);
        Assert.Equal("a1", sheets[0].Slots[0].PhotoAssetId);
        Assert.Equal("a2", sheets[0].Slots[1].PhotoAssetId);
        Assert.Equal("a5", sheets[2].Slots[0].PhotoAssetId);
        Assert.Null(sheets[2].Slots[1].PhotoAssetId);
    }

    [Fact]
    public void BorderlessFlushLayout_ShouldDramaticallyReduceEstimateCutCount_AcrossTemplates()
    {
        var paper6 = PaperSizeSpec.BuiltInPapers.First(p => p.Id == "paper_6inch");

        // 1) 六寸横二合一 (有框 6刀 -> 无框贴边 1刀)
        var tpl2in1 = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_6in_2in1_border");
        var normal2in1 = LayoutEngine.BuildSheet(tpl2in1, paper6);
        var borderless2in1 = LayoutEngine.BuildSheet(tpl2in1, paper6, marginOverride: 0, gapOverride: 0, bleedOverride: 0, flushToEdge: true);
        Assert.Equal(6, LayoutEngine.EstimateCutCount(normal2in1));
        Assert.Equal(1, LayoutEngine.EstimateCutCount(borderless2in1));
        Assert.True(borderless2in1.AlignFlushToEdge);
        AssertSlotsWithinPaperAndDisjoint(borderless2in1);

        // 2) 六寸四宫格 (8刀 -> 2刀)
        var tplGrid = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_grid_6in_2x2");
        var normalGrid = LayoutEngine.BuildSheet(tplGrid, paper6);
        var borderlessGrid = LayoutEngine.BuildSheet(tplGrid, paper6, marginOverride: 0, gapOverride: 0, bleedOverride: 0, flushToEdge: true);
        Assert.Equal(8, LayoutEngine.EstimateCutCount(normalGrid));
        Assert.Equal(2, LayoutEngine.EstimateCutCount(borderlessGrid));
        AssertSlotsWithinPaperAndDisjoint(borderlessGrid);

        // 3) 宝丽来 Instax Mini 2张 (6刀 -> 3刀, 贴纸张左上角排布)
        var tplPol = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_pol_6in_mini_2");
        var normalPol = LayoutEngine.BuildSheet(tplPol, paper6);
        var borderlessPol = LayoutEngine.BuildSheet(tplPol, paper6, marginOverride: 0, gapOverride: 0, bleedOverride: 0, flushToEdge: true);
        Assert.Equal(6, LayoutEngine.EstimateCutCount(normalPol));
        Assert.Equal(3, LayoutEngine.EstimateCutCount(borderlessPol));
        Assert.Equal(0.0, borderlessPol.Slots[0].XMm, 2);
        Assert.Equal(0.0, borderlessPol.Slots[0].YMm, 2);
        Assert.Equal(54.0, borderlessPol.Slots[1].XMm, 2);
        AssertSlotsWithinPaperAndDisjoint(borderlessPol);

        // 4) 一寸+二寸混排 (10刀 -> 5刀)
        var tplMixed = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_id_6in_mixed_1_2");
        var normalMixed = LayoutEngine.BuildSheet(tplMixed, paper6);
        var borderlessMixed = LayoutEngine.BuildSheet(tplMixed, paper6, marginOverride: 0, gapOverride: 0, bleedOverride: 0, flushToEdge: true);
        Assert.True(LayoutEngine.EstimateCutCount(borderlessMixed) < LayoutEngine.EstimateCutCount(normalMixed));
        Assert.Equal(5, LayoutEngine.EstimateCutCount(borderlessMixed));
        AssertSlotsWithinPaperAndDisjoint(borderlessMixed);

        // 5) 一寸8张贴边排布 (紧贴纸张左上角 0,0)
        var tplId8 = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_id_6in_1in_8");
        var borderlessId = LayoutEngine.BuildSheet(tplId8, paper6, marginOverride: 0, gapOverride: 0, bleedOverride: 0, flushToEdge: true);
        Assert.Equal(0.0, borderlessId.Slots[0].XMm, 2);
        Assert.Equal(0.0, borderlessId.Slots[0].YMm, 2);
        AssertSlotsWithinPaperAndDisjoint(borderlessId);
    }

    [Fact]
    public void OuterCornerTicks_WithFlushTouchingSlots_ShouldNotDrawInsideNeighborPhotos()
    {
        var sheet = new PrintSheet
        {
            PaperWidthMm = 152.4,
            PaperHeightMm = 101.6,
            CropMarkStyle = CropMarkStyle.OuterCornerTicks,
            CropMarkColorHex = "#222222",
            Slots =
            [
                new LayoutSlot { Index = 0, XMm = 0, YMm = 0, WidthMm = 50, HeightMm = 50, PhotoAssetId = "blue" },
                new LayoutSlot { Index = 1, XMm = 50, YMm = 0, WidthMm = 50, HeightMm = 50, PhotoAssetId = "blue" }
            ]
        };

        var asset = new PhotoAsset { Id = "blue", PixelWidth = 200, PixelHeight = 200 };
        using var blueBmp = new SKBitmap(200, 200, SKColorType.Bgra8888, SKAlphaType.Premul);
        blueBmp.Erase(new SKColor(15, 110, 220));

        const double dpi = 100.0;
        using var rendered = SheetRenderer.RenderSheetBitmap(
            sheet,
            new Dictionary<string, PhotoAsset> { ["blue"] = asset },
            (_, _) => blueBmp.Copy(),
            new SheetRenderOptions
            {
                Dpi = dpi,
                IsScreenPreview = false,
                ShowSafeMarginOverlay = false,
                ShowBleedOverlay = false,
                ShowIdGuidelinesOnSelected = false
            });

        int pxPerMmX(double mm) => UnitConverter.MmToPixelsRounded(mm, dpi);

        // 相邻共享边界上方约 2mm (x=48..52, y=0) 和右侧相邻照片内部 (x=52, y=50) 不应有裁切线落入蓝色照片区
        var insideRightSlot = rendered.GetPixel(pxPerMmX(51.5), pxPerMmX(49.5));
        Assert.True(insideRightSlot.Blue > 180 && insideRightSlot.Red < 60,
            $"Unexpected tick pixel inside touching slot at (51.5,49.5): {insideRightSlot}");

        // 而底边外的废边空白区 (x=50, y=51.8) 应当保留清晰的角标裁切指引线
        bool foundOuterTick = false;
        int tickY = pxPerMmX(51.8);
        int seamX = pxPerMmX(50.0);
        for (int dx = -2; dx <= 2; dx++)
        {
            var c = rendered.GetPixel(seamX + dx, tickY);
            if (c.Red < 120 && c.Green < 120 && c.Blue < 120)
            {
                foundOuterTick = true;
                break;
            }
        }
        Assert.True(foundOuterTick, "Expected outer crop tick below touching seam at (50mm, 51.8mm).");
    }

    [Fact]
    public void BuiltInThermalSublimationPapers_And_AutoOrientHelper_ShouldWorkCleanly()
    {
        var postcard = PaperSizeSpec.BuiltInPapers.FirstOrDefault(p => p.Id == "paper_postcard");
        var tab6 = PaperSizeSpec.BuiltInPapers.FirstOrDefault(p => p.Id == "paper_6inch_tab");
        Assert.NotNull(postcard);
        Assert.NotNull(tab6);

        var tplPol = LayoutEngine.BuiltInTemplates.First(t => t.Id == "tpl_pol_6in_mini_2");
        var sheetPostcard = LayoutEngine.BuildSheet(tplPol, postcard!);
        Assert.Equal(2, sheetPostcard.Slots.Count);
        AssertSlotsWithinPaperAndDisjoint(sheetPostcard);

        // 横向 4:3 图放入 1:1.4 竖向证件格时应提示旋转 90°；再旋转一次后应无需旋转
        Assert.True(PortraitAutoFrameHelper.NeedsQuarterTurnForSlotAspect(1600, 1200, currentQuarterTurns: 0, targetWidthMm: 25, targetHeightMm: 35));
        Assert.False(PortraitAutoFrameHelper.NeedsQuarterTurnForSlotAspect(1600, 1200, currentQuarterTurns: 1, targetWidthMm: 25, targetHeightMm: 35));
        Assert.False(PortraitAutoFrameHelper.NeedsQuarterTurnForSlotAspect(1200, 1600, currentQuarterTurns: 0, targetWidthMm: 25, targetHeightMm: 35));
    }

    private static void AssertSlotsWithinPaperAndDisjoint(PrintSheet sheet)
    {
        foreach (var s in sheet.Slots)
        {
            Assert.True(s.XMm >= -0.01, $"Slot {s.Index} X < 0: {s.XMm}");
            Assert.True(s.YMm >= -0.01, $"Slot {s.Index} Y < 0: {s.YMm}");
            Assert.True(s.XMm + s.WidthMm <= sheet.PaperWidthMm + 0.01, $"Slot {s.Index} overflows right");
            Assert.True(s.YMm + s.HeightMm <= sheet.PaperHeightMm + 0.01, $"Slot {s.Index} overflows bottom");
        }

        for (int i = 0; i < sheet.Slots.Count; i++)
        {
            for (int j = i + 1; j < sheet.Slots.Count; j++)
            {
                var a = sheet.Slots[i];
                var b = sheet.Slots[j];
                bool overlap = a.XMm < b.XMm + b.WidthMm - 0.01 &&
                               a.XMm + a.WidthMm > b.XMm + 0.01 &&
                               a.YMm < b.YMm + b.HeightMm - 0.01 &&
                               a.YMm + a.HeightMm > b.YMm + 0.01;
                Assert.False(overlap, $"Slots {i} and {j} overlap");
            }
        }
    }
}
