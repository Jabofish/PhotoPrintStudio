using PhotoPrintStudio.Core.Layout;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Persistence;
using PhotoPrintStudio.Core.Portable;
using PhotoPrintStudio.Core.Preflight;
using PhotoPrintStudio.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.Tests;

public class CustomFreeformLayoutTests
{
    [Fact]
    public void LayoutEngine_BuildSheet_CustomFreeform_ShouldPreserveExactSlotGeometryAndAttributes()
    {
        var paper6 = PaperSizeSpec.BuiltInPapers.First(p => p.Id == "paper_6inch");
        var tpl = new LayoutTemplateDefinition
        {
            Id = "tpl_user_free1",
            Name = "自由混排 3 框",
            Scenario = LayoutScenario.FreeGrid,
            DefaultPaperId = paper6.Id,
            DefaultLandscape = true,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            TemplateKind = LayoutTemplateDefinition.KindCustomFreeform,
            CustomPaperWidthMm = 101.6,
            CustomPaperHeightMm = 152.4,
            CustomCanvasBackgroundHex = "#F8FAFC",
            IsBuiltIn = false,
            CustomSlots =
            [
                new CustomSlotDefinition
                {
                    Id = "s1", XMm = 4.0, YMm = 6.0, WidthMm = 63.1, HeightMm = 90.0,
                    CornerRadiusMm = 2.0, BleedMm = 1.0, SlotRotationDegrees = 90,
                    Label = "左侧主图", FitModeOverride = FitMode.Contain
                },
                new CustomSlotDefinition
                {
                    Id = "s2", XMm = 74.0, YMm = 6.0, WidthMm = 35.0, HeightMm = 45.0,
                    SlotRotationDegrees = 270, Label = "右上小图", IdPhotoSpecId = "id_small2"
                },
                new CustomSlotDefinition
                {
                    Id = "s3", XMm = 74.0, YMm = 56.0, WidthMm = 54.0, HeightMm = 40.0,
                    SlotRotationDegrees = 180, Label = "右下横图",
                    PolaroidStyle = PolaroidSlotStyle.FromSpec(PolaroidSpec.BuiltInSpecs[0])
                }
            ]
        };

        var sheet = LayoutEngine.BuildSheet(tpl, paper6);

        Assert.Equal(152.4, sheet.PaperWidthMm, 2);
        Assert.Equal(101.6, sheet.PaperHeightMm, 2);
        Assert.Equal("#F8FAFC", sheet.CanvasBackgroundHex);
        Assert.Equal(3, sheet.Slots.Count);

        Assert.Equal(4.0, sheet.Slots[0].XMm, 2);
        Assert.Equal(6.0, sheet.Slots[0].YMm, 2);
        Assert.Equal(63.1, sheet.Slots[0].WidthMm, 2);
        Assert.Equal(90.0, sheet.Slots[0].HeightMm, 2);
        Assert.Equal(2.0, sheet.Slots[0].CornerRadiusMm, 2);
        Assert.Equal(1.0, sheet.Slots[0].BleedMm, 2);
        Assert.Equal(90, sheet.Slots[0].SlotRotationDegrees, 1);
        Assert.Equal(FitMode.Contain, sheet.Slots[0].TemplateFitMode);
        Assert.Equal("左侧主图", sheet.Slots[0].Label);

        Assert.Equal(270, sheet.Slots[1].SlotRotationDegrees, 1);
        Assert.True(sheet.Slots[1].IsQuarterTurnRotated);
        Assert.Equal("id_small2", sheet.Slots[1].IdPhotoSpecId);

        Assert.Equal(180, sheet.Slots[2].SlotRotationDegrees, 1);
        Assert.False(sheet.Slots[2].IsQuarterTurnRotated);
        Assert.NotNull(sheet.Slots[2].PolaroidStyle);
    }

    [Fact]
    public void PortableDataStore_ShouldRoundTripCustomFreeformTemplate()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"pps_freeform_store_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = new PortablePaths(tempDir);
            var store = new PortableDataStore(paths, new AppLogger(paths));

            var tpl = new LayoutTemplateDefinition
            {
                Id = "tpl_user_roundtrip",
                Name = "往返测试自由版式",
                Subtitle = "2 框自定义",
                Scenario = LayoutScenario.FreeGrid,
                DefaultPaperId = "paper_6inch",
                DefaultLandscape = true,
                TemplateKind = LayoutTemplateDefinition.KindCustomFreeform,
                CustomPaperWidthMm = 101.6,
                CustomPaperHeightMm = 152.4,
                CustomSlots =
                [
                    new CustomSlotDefinition { XMm = 5, YMm = 6, WidthMm = 63.1, HeightMm = 90.0, SlotRotationDegrees = 270, Label = "图片框 1" },
                    new CustomSlotDefinition { XMm = 76, YMm = 10, WidthMm = 65.0, HeightMm = 80.0, CornerRadiusMm = 3.0, BleedMm = 1.5, Label = "图片框 2" }
                ]
            };

            store.SaveCustomTemplate(tpl);
            var loaded = store.LoadCustomTemplates();
            var match = Assert.Single(loaded);
            Assert.True(match.IsCustomFreeform);
            Assert.Equal("往返测试自由版式", match.Name);
            Assert.Equal(101.6, match.CustomPaperWidthMm);
            Assert.Equal(152.4, match.CustomPaperHeightMm);
            Assert.NotNull(match.CustomSlots);
            Assert.Equal(2, match.CustomSlots!.Count);
            Assert.Equal(63.1, match.CustomSlots[0].WidthMm, 2);
            Assert.Equal(270, match.CustomSlots[0].SlotRotationDegrees, 1);
            Assert.Equal(3.0, match.CustomSlots[1].CornerRadiusMm, 2);
            Assert.Equal(1.5, match.CustomSlots[1].BleedMm, 2);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FreeformLayoutMath_MoveSnap_ShouldSnapToCenterSafeMarginOtherSlotAndClamp()
    {
        // 纸张 152.4 x 101.6，中心在 (76.2, 50.8)
        var moving = new LayoutRectMm(20, 10, 40, 50); // 初始中心 X=40, Y=35

        // 移动到接近纸张中心（dx=35.5 -> 中心 X=75.5 距离 76.2 仅 0.7mm <= 2mm）
        var snapCenter = FreeformLayoutMath.CalculateMoveSnap(
            moving, proposedDxMm: 35.5, proposedDyMm: 15.2,
            paperWidthMm: 152.4, paperHeightMm: 101.6,
            otherSlots: [],
            enableMagneticSnap: true, enableGridSnap: false, gridStepMm: 5, safeMarginMm: 4, snapToleranceMm: 2.0);

        Assert.Equal(76.2, moving.X + snapCenter.DeltaXMm + moving.Width * 0.5, 2);
        Assert.Equal(50.8, moving.Y + snapCenter.DeltaYMm + moving.Height * 0.5, 2);
        Assert.Contains(snapCenter.Guides, g => g.IsVertical && g.Kind == SnapGuideKind.PaperCenter);
        Assert.Contains(snapCenter.Guides, g => !g.IsVertical && g.Kind == SnapGuideKind.PaperCenter);

        // 吸附到另一图片框右边线（X=90）与顶边（Y=12）
        var other = new LayoutRectMm(60, 12, 30, 60);
        var snapOther = FreeformLayoutMath.CalculateMoveSnap(
            moving, proposedDxMm: 69.2, proposedDyMm: 1.4,
            paperWidthMm: 152.4, paperHeightMm: 101.6,
            otherSlots: [other],
            enableMagneticSnap: true, enableGridSnap: false, gridStepMm: 5, safeMarginMm: 0, snapToleranceMm: 2.0);
        Assert.Equal(90.0, moving.X + snapOther.DeltaXMm, 2);
        Assert.Equal(12.0, moving.Y + snapOther.DeltaYMm, 2);

        // 越界拖出时必须钳制在纸张范围内
        var clamped = FreeformLayoutMath.CalculateMoveSnap(
            moving, proposedDxMm: 500, proposedDyMm: -200,
            paperWidthMm: 152.4, paperHeightMm: 101.6,
            otherSlots: [],
            enableMagneticSnap: false, enableGridSnap: false, gridStepMm: 5, safeMarginMm: 0);
        Assert.Equal(112.4, moving.X + clamped.DeltaXMm, 2);
        Assert.Equal(0.0, moving.Y + clamped.DeltaYMm, 2);
    }

    [Theory]
    [InlineData(ResizeHandle.TopLeft, -10, -5, 10, 15, 50, 45)]
    [InlineData(ResizeHandle.Top, 0, -5, 20, 15, 40, 45)]
    [InlineData(ResizeHandle.TopRight, 10, -5, 20, 15, 50, 45)]
    [InlineData(ResizeHandle.Right, 10, 0, 20, 20, 50, 40)]
    [InlineData(ResizeHandle.BottomRight, 10, 10, 20, 20, 50, 50)]
    [InlineData(ResizeHandle.Bottom, 0, 10, 20, 20, 40, 50)]
    [InlineData(ResizeHandle.BottomLeft, -10, 10, 10, 20, 50, 50)]
    [InlineData(ResizeHandle.Left, -10, 0, 10, 20, 50, 40)]
    public void FreeformLayoutMath_ResizeSnap_AllEightHandles_ShouldComputeExactRect(
        ResizeHandle handle, double dx, double dy,
        double expX, double expY, double expW, double expH)
    {
        var orig = new LayoutRectMm(20, 20, 40, 40);
        var res = FreeformLayoutMath.CalculateResizeSnap(
            orig, handle, dx, dy,
            paperWidthMm: 152.4, paperHeightMm: 101.6,
            otherSlots: [],
            lockAspectRatio: false,
            enableMagneticSnap: false, enableGridSnap: false, gridStepMm: 5, safeMarginMm: 0);

        Assert.Equal(expX, res.Rect.X, 2);
        Assert.Equal(expY, res.Rect.Y, 2);
        Assert.Equal(expW, res.Rect.Width, 2);
        Assert.Equal(expH, res.Rect.Height, 2);
    }

    [Fact]
    public void FreeformLayoutMath_ResizeSnap_WithAspectLockAndMinSize_ShouldPreserveRatioAndStayInPaper()
    {
        var orig = new LayoutRectMm(10, 10, 40, 20); // 2:1 宽高比
        var res = FreeformLayoutMath.CalculateResizeSnap(
            orig, ResizeHandle.BottomRight, pointerDeltaXMm: 20, pointerDeltaYMm: 2,
            paperWidthMm: 152.4, paperHeightMm: 101.6,
            otherSlots: [],
            lockAspectRatio: true,
            enableMagneticSnap: false, enableGridSnap: false, gridStepMm: 5, safeMarginMm: 0);

        Assert.Equal(60.0, res.Rect.Width, 2);
        Assert.Equal(30.0, res.Rect.Height, 2);

        // 极度缩小时保持最小尺寸 >= 5mm
        var tiny = FreeformLayoutMath.CalculateResizeSnap(
            orig, ResizeHandle.BottomRight, pointerDeltaXMm: -200, pointerDeltaYMm: -200,
            paperWidthMm: 152.4, paperHeightMm: 101.6,
            otherSlots: [],
            lockAspectRatio: false,
            enableMagneticSnap: false, enableGridSnap: false, gridStepMm: 5, safeMarginMm: 0);
        Assert.True(tiny.Rect.Width >= FreeformLayoutMath.MinSlotSizeMm);
        Assert.True(tiny.Rect.Height >= FreeformLayoutMath.MinSlotSizeMm);
    }

    [Fact]
    public void FreeformLayoutMath_AlignDistributeAndValidate_ShouldWorkCorrectly()
    {
        var s1 = new CustomSlotDefinition { Id = "1", XMm = 10, YMm = 10, WidthMm = 20, HeightMm = 30 };
        var s2 = new CustomSlotDefinition { Id = "2", XMm = 45, YMm = 25, WidthMm = 20, HeightMm = 30 };
        var s3 = new CustomSlotDefinition { Id = "3", XMm = 110, YMm = 40, WidthMm = 20, HeightMm = 30 };

        // 水平等距分布：总跨度 10..130 = 120，总宽 60，等间距 30 => X = 10, 60, 110
        FreeformLayoutMath.DistributeSlots([s1, s2, s3], horizontal: true);
        Assert.Equal(10, s1.XMm, 2);
        Assert.Equal(60, s2.XMm, 2);
        Assert.Equal(110, s3.XMm, 2);

        // 顶对齐（多选相对选区顶边 Y=10）
        FreeformLayoutMath.AlignSlots([s1, s2, s3], FrameAlignment.Top, 152.4, 101.6);
        Assert.Equal(10, s2.YMm, 2);
        Assert.Equal(10, s3.YMm, 2);

        // 单选页面居中
        FreeformLayoutMath.AlignSlots([s1], FrameAlignment.CenterOnPage, 152.4, 101.6);
        Assert.Equal(66.2, s1.XMm, 2);
        Assert.Equal(35.8, s1.YMm, 2);

        // 校验越界与重叠
        var overlapA = new CustomSlotDefinition { Id = "a", XMm = 10, YMm = 10, WidthMm = 40, HeightMm = 40 };
        var overlapB = new CustomSlotDefinition { Id = "b", XMm = 30, YMm = 20, WidthMm = 40, HeightMm = 40 };
        var oob = new CustomSlotDefinition { Id = "c", XMm = 130, YMm = 10, WidthMm = 40, HeightMm = 40 };

        var val = FreeformLayoutMath.ValidateSlots([overlapA, overlapB, oob], 152.4, 101.6);
        Assert.False(val.IsValid);
        Assert.Contains("c", val.OutOfBoundsSlotIds);
        Assert.Single(val.OverlappingPairs);
    }

    [Fact]
    public void SheetRenderer_And_Preflight_ShouldSupport270And180DegreeRotatedCustomSlots()
    {
        var sheet = new PrintSheet
        {
            PaperWidthMm = 152.4,
            PaperHeightMm = 101.6,
            Scenario = LayoutScenario.FreeGrid,
            Slots =
            [
                new LayoutSlot
                {
                    Id = "rot270", Index = 0, XMm = 10, YMm = 10, WidthMm = 50, HeightMm = 25,
                    SlotRotationDegrees = 270, BleedMm = 1.0, PhotoAssetId = "p1"
                },
                new LayoutSlot
                {
                    Id = "rot180", Index = 1, XMm = 70, YMm = 10, WidthMm = 40, HeightMm = 60,
                    SlotRotationDegrees = 180, PhotoAssetId = "p1",
                    PolaroidStyle = PolaroidSlotStyle.FromSpec(PolaroidSpec.BuiltInSpecs[0])
                }
            ]
        };

        // 竖图 600x1200 放入宽50高25但旋转270°的槽位，旋转后对齐为 25x50 => 约 609 DPI，不应误报低分辨率
        var asset = new PhotoAsset { Id = "p1", DisplayName = "tall.png", PixelWidth = 600, PixelHeight = 1200 };
        var assets = new Dictionary<string, PhotoAsset> { ["p1"] = asset };

        var report = PreflightChecker.InspectSheet(sheet, assets, new PrinterCalibrationProfile { DefaultBorderless = true });
        Assert.DoesNotContain(report.Issues, i => i.Category.Contains("分辨率"));

        using var srcBmp = new SKBitmap(600, 1200, SKColorType.Bgra8888, SKAlphaType.Premul);
        srcBmp.Erase(SKColors.CornflowerBlue);

        using var rendered = SheetRenderer.RenderSheetBitmap(
            sheet, assets, (_, _) => srcBmp.Copy(),
            new SheetRenderOptions { Dpi = 100, IsScreenPreview = false });

        Assert.True(rendered.Width > 500 && rendered.Height > 300);
    }
}
