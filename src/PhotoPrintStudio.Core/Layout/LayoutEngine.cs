using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.Core.Layout;

public sealed record AutoPackResult(
    int Columns,
    int Rows,
    int TotalCount,
    bool RotatedSlot,
    double SlotWidthMm,
    double SlotHeightMm,
    double UtilizationPercent);

public static class LayoutEngine
{
    public static IReadOnlyList<LayoutTemplateDefinition> BuiltInTemplates { get; } =
    [
        // === 1. 证件照模板 ===
        new()
        {
            Id = "tpl_id_6in_1in_8",
            Name = "6寸 × 8张 一寸 (经典正排 4×2)",
            Subtitle = "25×35 mm 直立整齐排布，一刀切到底",
            Scenario = LayoutScenario.IdPhoto,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 6.0,
            DefaultGapMm = 4.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            IdPhotoSpecId = "id_1inch",
            TemplateKind = "id_upright_grid"
        },
        new()
        {
            Id = "tpl_id_6in_1in_9",
            Name = "6寸 × 9张 一寸 (自动最优装箱 3×3)",
            Subtitle = "25×35 mm 自动旋转最大化利用率",
            Scenario = LayoutScenario.IdPhoto,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 6.0,
            DefaultGapMm = 4.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            IdPhotoSpecId = "id_1inch",
            TemplateKind = "auto_grid"
        },
        new()
        {
            Id = "tpl_id_6in_2in_4",
            Name = "6寸 × 4张 二寸 (35×49 mm)",
            Subtitle = "舒适间距，方便裁切",
            Scenario = LayoutScenario.IdPhoto,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 6.0,
            DefaultGapMm = 5.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            IdPhotoSpecId = "id_2inch",
            TemplateKind = "auto_grid"
        },
        new()
        {
            Id = "tpl_id_6in_small2_6",
            Name = "6寸 × 6张 小二寸 / 签证 (35×45 mm)",
            Subtitle = "高利用率 3×2 横排",
            Scenario = LayoutScenario.IdPhoto,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 3.5,
            DefaultGapMm = 3.5,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            IdPhotoSpecId = "id_small2",
            TemplateKind = "auto_grid"
        },
        new()
        {
            Id = "tpl_id_6in_big1_4",
            Name = "6寸 × 4张 大一寸 / 护照 (33×48 mm)",
            Subtitle = "中国护照/通行证",
            Scenario = LayoutScenario.IdPhoto,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 6.0,
            DefaultGapMm = 5.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            IdPhotoSpecId = "id_big1",
            TemplateKind = "auto_grid"
        },
        new()
        {
            Id = "tpl_id_6in_us_visa_2",
            Name = "6寸 × 2张 美签/印签 (51×51 mm)",
            Subtitle = "2×2 英寸正方形",
            Scenario = LayoutScenario.IdPhoto,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 6.0,
            DefaultGapMm = 8.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            IdPhotoSpecId = "id_visa_us",
            TemplateKind = "auto_grid"
        },
        new()
        {
            Id = "tpl_id_6in_mixed_1_2",
            Name = "6寸混排：4张一寸 + 2张二寸",
            Subtitle = "一纸搞定两种尺寸，可放同一人或不同人",
            Scenario = LayoutScenario.IdPhoto,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 6.0,
            DefaultGapMm = 4.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            IdPhotoSpecId = "id_1inch",
            TemplateKind = "id_mixed_1_2"
        },
        new()
        {
            Id = "tpl_id_6in_mixed_split",
            Name = "6寸双人一寸：左4张 + 右4张",
            Subtitle = "左右分开填充，两人拼印省相纸",
            Scenario = LayoutScenario.IdPhoto,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 6.0,
            DefaultGapMm = 4.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            IdPhotoSpecId = "id_1inch",
            TemplateKind = "id_mixed_split"
        },
        new()
        {
            Id = "tpl_id_a4_1in_max",
            Name = "A4 满版一寸 (最多 30 张)",
            Subtitle = "团体/班集体批量打印",
            Scenario = LayoutScenario.IdPhoto,
            DefaultPaperId = "paper_a4",
            DefaultLandscape = false,
            DefaultMarginMm = 8.0,
            DefaultGapMm = 4.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            IdPhotoSpecId = "id_1inch",
            TemplateKind = "auto_grid"
        },

        // === 2. 六寸打印二合一模板 ===
        new()
        {
            Id = "tpl_6in_2in1_border",
            Name = "六寸横向二合一（精致留白 + 裁剪虚线）",
            Subtitle = "四周留白防打印机吃边，裁开自带小白框",
            Scenario = LayoutScenario.SixInchTwoInOne,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 4.5,
            DefaultGapMm = 6.0,
            DefaultBleedMm = 0.0,
            DefaultCropMarkStyle = CropMarkStyle.CenterFoldTicks,
            TemplateKind = "six_2in1_border"
        },
        new()
        {
            Id = "tpl_6in_2in1_left",
            Name = "六寸并排（左对齐）",
            Subtitle = "63 × 90 mm，右侧留白",
            Scenario = LayoutScenario.SixInchTwoInOne,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 4.0,
            DefaultGapMm = 8.0,
            DefaultCropMarkStyle = CropMarkStyle.None,
            TemplateKind = "six_2in1_left"
        },
        new()
        {
            Id = "tpl_6in_2in1_full",
            Name = "六寸横向二合一（满幅双3寸，带出血）",
            Subtitle = "左右各 76.2×101.6 mm，含中线裁切标",
            Scenario = LayoutScenario.SixInchTwoInOne,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 0.0,
            DefaultGapMm = 0.0,
            DefaultBleedMm = 1.5,
            DefaultCropMarkStyle = CropMarkStyle.CenterFoldTicks,
            TemplateKind = "six_2in1_full"
        },
        new()
        {
            Id = "tpl_6in_2in1_vert",
            Name = "六寸纵向二合一（上下两张横图）",
            Subtitle = "适合风景照/横向双人合影二合一",
            Scenario = LayoutScenario.SixInchTwoInOne,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = false,
            DefaultMarginMm = 4.5,
            DefaultGapMm = 6.0,
            DefaultBleedMm = 0.0,
            DefaultCropMarkStyle = CropMarkStyle.CenterFoldTicks,
            TemplateKind = "six_2in1_vert"
        },
        new()
        {
            Id = "tpl_6in_2in1_card_duplex",
            Name = "六寸身份证/卡证 1:1 正反二合一",
            Subtitle = "严格 85.6×54 mm（R3.18圆角）同页双面",
            Scenario = LayoutScenario.SixInchTwoInOne,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = false,
            DefaultMarginMm = 6.0,
            DefaultGapMm = 12.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            IdPhotoSpecId = "id_std_card",
            TemplateKind = "six_2in1_card_duplex"
        },
        // === 3. 拍立得尺寸排版模板 ===
        new()
        {
            Id = "tpl_pol_6in_mini_2",
            Name = "6寸 × 2张 Instax Mini (1:1 原尺寸)",
            Subtitle = "实体 54×86 mm，四边留白充足不吃边",
            Scenario = LayoutScenario.Polaroid,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 5.0,
            DefaultGapMm = 6.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            PolaroidSpecId = "pol_instax_mini",
            TemplateKind = "polaroid_grid"
        },
        new()
        {
            Id = "tpl_pol_6in_square_2",
            Name = "6寸 × 2张 Instax Square (1:1 原尺寸)",
            Subtitle = "实体 72×86 mm 方片，2.4mm 中缝",
            Scenario = LayoutScenario.Polaroid,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 2.5,
            DefaultGapMm = 2.4,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            PolaroidSpecId = "pol_instax_square",
            TemplateKind = "polaroid_grid"
        },
        new()
        {
            Id = "tpl_pol_6in_square_2_safe",
            Name = "6寸 × 2张 Instax Square (安全防切边版)",
            Subtitle = "约 93% 等比缩放留出 5mm 安全边距",
            Scenario = LayoutScenario.Polaroid,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 5.0,
            DefaultGapMm = 5.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            PolaroidSpecId = "pol_instax_square",
            PolaroidSafeFit = true,
            CustomRows = 1,
            CustomCols = 2,
            TemplateKind = "polaroid_2in1_fit"
        },
        new()
        {
            Id = "tpl_pol_6in_wide_1",
            Name = "6寸 × 1张 Instax Wide (1:1 原尺寸)",
            Subtitle = "实体 108×86 mm 宽幅居中",
            Scenario = LayoutScenario.Polaroid,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 5.0,
            DefaultGapMm = 5.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            PolaroidSpecId = "pol_instax_wide",
            TemplateKind = "polaroid_grid"
        },
        new()
        {
            Id = "tpl_pol_6in_600_1",
            Name = "6寸 × 1张 宝丽来 600 / i-Type (1:1 原尺寸)",
            Subtitle = "实体 88×107 mm 经典大方框",
            Scenario = LayoutScenario.Polaroid,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = false,
            DefaultMarginMm = 5.0,
            DefaultGapMm = 5.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            PolaroidSpecId = "pol_600",
            TemplateKind = "polaroid_grid"
        },
        new()
        {
            Id = "tpl_pol_6in_600_2_fit",
            Name = "6寸 × 2张 宝丽来 600 等比二合一",
            Subtitle = "按 88:107 原比例等比微缩排两张",
            Scenario = LayoutScenario.Polaroid,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 5.0,
            DefaultGapMm = 6.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            PolaroidSpecId = "pol_600",
            PolaroidSafeFit = true,
            CustomCols = 2,
            CustomRows = 1,
            TemplateKind = "polaroid_2in1_fit"
        },
        new()
        {
            Id = "tpl_pol_6in_go_2",
            Name = "6寸 × 2张 宝丽来 Go (1:1 原尺寸)",
            Subtitle = "实体 53.9×66.6 mm 口袋方卡",
            Scenario = LayoutScenario.Polaroid,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 5.0,
            DefaultGapMm = 8.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            PolaroidSpecId = "pol_go",
            TemplateKind = "polaroid_grid"
        },
        new()
        {
            Id = "tpl_pol_6in_lomo_2",
            Name = "6寸 × 2张 Lomo 3寸 / 追星小卡 (1:1)",
            Subtitle = "实体 63.5×89 mm 宽边小卡",
            Scenario = LayoutScenario.Polaroid,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 5.0,
            DefaultGapMm = 6.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            PolaroidSpecId = "pol_lomo_3in",
            TemplateKind = "polaroid_grid"
        },
        new()
        {
            Id = "tpl_pol_a4_mini_9",
            Name = "A4 × 9张 Instax Mini (1:1 原尺寸)",
            Subtitle = "3×3 满排不浪费相纸",
            Scenario = LayoutScenario.Polaroid,
            DefaultPaperId = "paper_a4",
            DefaultLandscape = false,
            DefaultMarginMm = 8.0,
            DefaultGapMm = 6.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            PolaroidSpecId = "pol_instax_mini",
            TemplateKind = "polaroid_grid"
        },
        new()
        {
            Id = "tpl_pol_a4_600_4",
            Name = "A4 × 4张 宝丽来 600 (1:1 原尺寸)",
            Subtitle = "2×2 满排 88×107 mm",
            Scenario = LayoutScenario.Polaroid,
            DefaultPaperId = "paper_a4",
            DefaultLandscape = false,
            DefaultMarginMm = 8.0,
            DefaultGapMm = 8.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            PolaroidSpecId = "pol_600",
            TemplateKind = "polaroid_grid"
        },

        // === 4. 自由网格模板 ===
        new()
        {
            Id = "tpl_grid_6in_2x2",
            Name = "6寸 2×2 四宫格拼图",
            Subtitle = "等分四图，直角留白",
            Scenario = LayoutScenario.FreeGrid,
            DefaultPaperId = "paper_6inch",
            DefaultLandscape = true,
            DefaultMarginMm = 4.5,
            DefaultGapMm = 4.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            CustomRows = 2,
            CustomCols = 2,
            TemplateKind = "free_grid"
        },
        new()
        {
            Id = "tpl_grid_a4_3x3",
            Name = "A4 3×3 九宫格照片墙",
            Subtitle = "等分 9 格可裁切贴墙",
            Scenario = LayoutScenario.FreeGrid,
            DefaultPaperId = "paper_a4",
            DefaultLandscape = false,
            DefaultMarginMm = 10.0,
            DefaultGapMm = 5.0,
            DefaultCropMarkStyle = CropMarkStyle.OuterCornerTicks,
            CustomRows = 3,
            CustomCols = 3,
            TemplateKind = "free_grid"
        },
        .. CreateSinglePhotoTemplates()
    ];

    private static IEnumerable<LayoutTemplateDefinition> CreateSinglePhotoTemplates()
    {
        // 六寸是最常用的入口默认尺寸，其余按相纸和标准纸型排列。
        (string PaperId, string Name)[] formats =
        [
            ("paper_6inch", "6寸 / 4R"),
            ("paper_5inch", "5寸 / 3R"),
            ("paper_big6", "大6寸"),
            ("paper_7inch", "7寸 / 5R"),
            ("paper_8inch", "8寸 / 6R"),
            ("paper_9inch", "9寸"),
            ("paper_10inch", "10寸 / 8R"),
            ("paper_square_5inch", "5寸方形"),
            ("paper_square_6inch", "6寸方形"),
            ("paper_postcard", "明信片 / 佳能 P纸"),
            ("paper_a6", "A6"),
            ("paper_a5", "A5"),
            ("paper_b5_iso", "B5 (ISO)"),
            ("paper_b5_jis", "B5 (JIS)"),
            ("paper_a4", "A4")
        ];

        foreach (var (paperId, name) in formats)
        {
            var paper = PaperSizeSpec.BuiltInPapers.First(p => p.Id == paperId);
            yield return new LayoutTemplateDefinition
            {
                Id = $"tpl_single_{paperId["paper_".Length..]}",
                Name = $"{name} 单张",
                Subtitle = $"{paper.WidthMm:0.#} × {paper.HeightMm:0.#} mm",
                Scenario = LayoutScenario.SinglePhoto,
                DefaultPaperId = paperId,
                DefaultLandscape = false,
                DefaultMarginMm = 5.0,
                DefaultGapMm = 0.0,
                DefaultCropMarkStyle = CropMarkStyle.None,
                CustomRows = 1,
                CustomCols = 1,
                TemplateKind = "single_photo"
            };
        }
    }

    public static AutoPackResult CalculateBestPack(
        double paperWidthMm,
        double paperHeightMm,
        double itemWidthMm,
        double itemHeightMm,
        double marginMm,
        double gapMm,
        bool allowRotate = true)
    {
        double availW = Math.Max(1, paperWidthMm - marginMm * 2);
        double availH = Math.Max(1, paperHeightMm - marginMm * 2);

        (int cols0, int rows0) = FitGrid(availW, availH, itemWidthMm, itemHeightMm, gapMm);
        int count0 = cols0 * rows0;

        int cols90 = 0, rows90 = 0, count90 = 0;
        if (allowRotate && Math.Abs(itemWidthMm - itemHeightMm) > 0.01)
        {
            (cols90, rows90) = FitGrid(availW, availH, itemHeightMm, itemWidthMm, gapMm);
            count90 = cols90 * rows90;
        }

        // 若张数相同，优先选择旋转后每边余量更均衡者
        bool useRot = count90 > count0;
        int bestCols = useRot ? cols90 : cols0;
        int bestRows = useRot ? rows90 : rows0;
        int bestCount = bestCols * bestRows;
        double sw = useRot ? itemHeightMm : itemWidthMm;
        double sh = useRot ? itemWidthMm : itemHeightMm;
        double util = paperWidthMm * paperHeightMm > 0
            ? (bestCount * sw * sh) / (paperWidthMm * paperHeightMm) * 100.0
            : 0.0;

        return new AutoPackResult(bestCols, bestRows, bestCount, useRot, sw, sh, Math.Round(util, 1));
    }

    private static (int cols, int rows) FitGrid(double availW, double availH, double w, double h, double gap)
    {
        int cols = w <= availW + 1e-4 ? (int)Math.Floor((availW + gap + 1e-4) / (w + gap)) : 0;
        int rows = h <= availH + 1e-4 ? (int)Math.Floor((availH + gap + 1e-4) / (h + gap)) : 0;
        return (Math.Max(0, cols), Math.Max(0, rows));
    }

    public static PrintSheet BuildSheet(
        LayoutTemplateDefinition template,
        PaperSizeSpec paper,
        bool? isLandscapeOverride = null,
        double? marginOverride = null,
        double? gapOverride = null,
        double? bleedOverride = null,
        IdPhotoSpec? idSpecOverride = null,
        PolaroidSpec? polaroidSpecOverride = null,
        PolaroidSlotStyle? basePolaroidStyle = null,
        int? customRowsOverride = null,
        int? customColsOverride = null,
        CropMarkStyle? cropMarkOverride = null,
        bool? polaroidSafeFitOverride = null,
        bool flushToEdge = false)
    {
        bool landscape = isLandscapeOverride ?? template.DefaultLandscape;
        double longSide = Math.Max(paper.WidthMm, paper.HeightMm);
        double shortSide = Math.Min(paper.WidthMm, paper.HeightMm);
        double paperW = landscape ? longSide : shortSide;
        double paperH = landscape ? shortSide : longSide;

        double margin = marginOverride ?? template.DefaultMarginMm;
        double gap = gapOverride ?? template.DefaultGapMm;
        double bleed = bleedOverride ?? template.DefaultBleedMm;
        bool safeFit = polaroidSafeFitOverride ?? template.PolaroidSafeFit;

        var sheet = new PrintSheet
        {
            PaperId = paper.Id,
            PaperWidthMm = paperW,
            PaperHeightMm = paperH,
            IsLandscape = landscape,
            Scenario = template.Scenario,
            TemplateId = template.Id,
            IsCustomFreeform = template.IsCustomFreeform,
            CropMarkStyle = cropMarkOverride ?? template.DefaultCropMarkStyle,
            ShowCenterFoldTick = template.ShowCenterFoldTickByDefault,
            AlignFlushToEdge = flushToEdge
        };

        switch (template.TemplateKind)
        {
            case "id_mixed_1_2":
                BuildMixedOneAndTwoInch(sheet, margin, gap, flushToEdge);
                break;

            case "id_mixed_split":
                BuildSplitDualPersonOneInch(sheet, margin, gap, flushToEdge);
                break;

            case "six_2in1_full":
                BuildTwoInOneHorizontal(sheet, margin, gap, bleed);
                break;

            case "six_2in1_border":
                BuildTwoInOneHorizontal(sheet, margin, gap, 0.0);
                break;

            case "six_2in1_left":
                BuildTwoInOneHorizontal(sheet, margin, gap, 0.0, maxPhotoSize: (63, 90));
                break;

            case "six_2in1_vert":
                BuildTwoInOneVertical(sheet, margin, gap, bleed);
                break;

            case "six_2in1_card_duplex":
                BuildCardDuplexTwoInOne(sheet, margin, gap, flushToEdge);
                break;

            case "polaroid_grid":
            {
                var pSpec = polaroidSpecOverride
                    ?? PolaroidSpec.BuiltInSpecs.FirstOrDefault(p => p.Id == template.PolaroidSpecId)
                    ?? PolaroidSpec.BuiltInSpecs[0];
                BuildPolaroidGrid(sheet, pSpec, margin, gap, safeFit, basePolaroidStyle, flushToEdge);
                break;
            }

            case "polaroid_2in1_fit":
            case "polaroid_fixed_grid":
            {
                var pSpec = polaroidSpecOverride
                    ?? PolaroidSpec.BuiltInSpecs.FirstOrDefault(p => p.Id == template.PolaroidSpecId)
                    ?? PolaroidSpec.BuiltInSpecs[3];
                BuildPolaroidFixedFit(sheet, pSpec,
                    template.TemplateKind == "polaroid_fixed_grid" ? customColsOverride ?? template.CustomCols : template.CustomCols,
                    template.TemplateKind == "polaroid_fixed_grid" ? customRowsOverride ?? template.CustomRows : template.CustomRows,
                    margin, gap, basePolaroidStyle, flushToEdge);
                break;
            }

            case "single_photo":
                BuildFreeGrid(sheet, 1, 1, margin, 0, bleed, template.CustomCornerRadiusMm);
                break;

            case LayoutTemplateDefinition.KindCustomFreeform:
                BuildCustomFreeform(sheet, template);
                break;

            case "free_grid":
            {
                int rows = customRowsOverride ?? template.CustomRows;
                int cols = customColsOverride ?? template.CustomCols;
                BuildFreeGrid(sheet, rows, cols, margin, gap, bleed, template.CustomCornerRadiusMm);
                break;
            }

            case "id_upright_grid":
            {
                var idSpec = idSpecOverride
                    ?? IdPhotoSpec.BuiltInSpecs.FirstOrDefault(x => x.Id == template.IdPhotoSpecId)
                    ?? IdPhotoSpec.BuiltInSpecs[0];
                BuildAutoIdGrid(sheet, idSpec, margin, gap, bleed, allowRotate: false, flushToEdge: flushToEdge);
                break;
            }

            default:
            {
                var idSpec = idSpecOverride
                    ?? IdPhotoSpec.BuiltInSpecs.FirstOrDefault(x => x.Id == template.IdPhotoSpecId)
                    ?? IdPhotoSpec.BuiltInSpecs[0];
                BuildAutoIdGrid(sheet, idSpec, margin, gap, bleed, allowRotate: true, flushToEdge: flushToEdge);
                break;
            }
        }

        return sheet;
    }

    /// <summary>
    /// 估算整张相纸按直线裁切（断头台裁刀）所需的总裁切刀数；紧贴纸张边缘或共享切线可大幅降刀数。
    /// </summary>
    public static int EstimateCutCount(PrintSheet sheet)
    {
        if (sheet.Slots.Count == 0) return 0;

        const double eps = 0.08;
        var xCuts = new List<double>();
        var yCuts = new List<double>();

        static void AddIfInterior(List<double> list, double coord, double limit)
        {
            if (coord <= eps || coord >= limit - eps) return;
            foreach (double existing in list)
            {
                if (Math.Abs(existing - coord) <= eps) return;
            }
            list.Add(coord);
        }

        foreach (var s in sheet.Slots)
        {
            AddIfInterior(xCuts, s.XMm, sheet.PaperWidthMm);
            AddIfInterior(xCuts, s.XMm + s.WidthMm, sheet.PaperWidthMm);
            AddIfInterior(yCuts, s.YMm, sheet.PaperHeightMm);
            AddIfInterior(yCuts, s.YMm + s.HeightMm, sheet.PaperHeightMm);
        }

        return xCuts.Count + yCuts.Count;
    }

    private static void BuildAutoIdGrid(PrintSheet sheet, IdPhotoSpec spec, double margin, double gap, double bleed, bool allowRotate = true, bool flushToEdge = false)
    {
        var pack = CalculateBestPack(sheet.PaperWidthMm, sheet.PaperHeightMm, spec.WidthMm, spec.HeightMm, margin, gap, allowRotate);
        if (pack.TotalCount == 0)
        {
            // 如果常规边距排不下1张，回退到最小 1mm 边距重试
            margin = 1.0;
            gap = 1.0;
            pack = CalculateBestPack(sheet.PaperWidthMm, sheet.PaperHeightMm, spec.WidthMm, spec.HeightMm, margin, gap, allowRotate);
        }
        if (pack.TotalCount == 0) return;

        double totalW = pack.Columns * pack.SlotWidthMm + (pack.Columns - 1) * gap;
        double totalH = pack.Rows * pack.SlotHeightMm + (pack.Rows - 1) * gap;
        double startX = flushToEdge ? margin : (sheet.PaperWidthMm - totalW) / 2.0;
        double startY = flushToEdge ? margin : (sheet.PaperHeightMm - totalH) / 2.0;

        int idx = 0;
        for (int r = 0; r < pack.Rows; r++)
        {
            for (int c = 0; c < pack.Columns; c++)
            {
                sheet.Slots.Add(new LayoutSlot
                {
                    Index = idx++,
                    XMm = Math.Round(startX + c * (pack.SlotWidthMm + gap), 3),
                    YMm = Math.Round(startY + r * (pack.SlotHeightMm + gap), 3),
                    WidthMm = pack.SlotWidthMm,
                    HeightMm = pack.SlotHeightMm,
                    BleedMm = bleed,
                    SlotRotationDegrees = pack.RotatedSlot ? 90 : 0,
                    IdPhotoSpecId = spec.Id,
                    Label = $"{spec.Name.Split(' ')[0]} #{idx}"
                });
            }
        }
    }

    private static void BuildMixedOneAndTwoInch(PrintSheet sheet, double margin, double gap, bool flushToEdge)
    {
        // 左侧 2x2 一寸 (25x35), 右侧 1x2 旋转二寸 (49x35)
        double leftW = 25 * 2 + gap;
        double rightW = 49;
        double blockGap = flushToEdge ? gap : Math.Max(6.0, gap * 1.5);
        double totalW = leftW + blockGap + rightW;
        double totalH = 35 * 2 + gap;
        if (totalW > sheet.PaperWidthMm + 0.01 || totalH > sheet.PaperHeightMm + 0.01) return;

        double startX = flushToEdge ? Math.Min(margin, Math.Max(0, sheet.PaperWidthMm - totalW)) : (sheet.PaperWidthMm - totalW) / 2.0;
        double startY = flushToEdge ? Math.Min(margin, Math.Max(0, sheet.PaperHeightMm - totalH)) : (sheet.PaperHeightMm - totalH) / 2.0;

        int idx = 0;
        for (int r = 0; r < 2; r++)
        {
            for (int c = 0; c < 2; c++)
            {
                sheet.Slots.Add(new LayoutSlot
                {
                    Index = idx++,
                    XMm = Math.Round(startX + c * (25 + gap), 3),
                    YMm = Math.Round(startY + r * (35 + gap), 3),
                    WidthMm = 25,
                    HeightMm = 35,
                    IdPhotoSpecId = "id_1inch",
                    Label = $"一寸 #{idx}"
                });
            }
        }

        double rx = startX + leftW + blockGap;
        for (int r = 0; r < 2; r++)
        {
            sheet.Slots.Add(new LayoutSlot
            {
                Index = idx++,
                XMm = Math.Round(rx, 3),
                YMm = Math.Round(startY + r * (35 + gap), 3),
                WidthMm = 49,
                HeightMm = 35,
                SlotRotationDegrees = 90,
                IdPhotoSpecId = "id_2inch",
                Label = $"二寸 #{r + 1}"
            });
        }
    }

    private static void BuildSplitDualPersonOneInch(PrintSheet sheet, double margin, double gap, bool flushToEdge)
    {
        double groupW = 25 * 2 + gap;
        double centerGap = flushToEdge ? gap : Math.Max(8.0, gap * 2);
        double totalW = groupW * 2 + centerGap;
        double totalH = 35 * 2 + gap;
        if (totalW > sheet.PaperWidthMm + 0.01 || totalH > sheet.PaperHeightMm + 0.01) return;

        double startX = flushToEdge ? Math.Min(margin, Math.Max(0, sheet.PaperWidthMm - totalW)) : (sheet.PaperWidthMm - totalW) / 2.0;
        double startY = flushToEdge ? Math.Min(margin, Math.Max(0, sheet.PaperHeightMm - totalH)) : (sheet.PaperHeightMm - totalH) / 2.0;

        int idx = 0;
        for (int g = 0; g < 2; g++)
        {
            double gx = startX + g * (groupW + centerGap);
            for (int r = 0; r < 2; r++)
            {
                for (int c = 0; c < 2; c++)
                {
                    sheet.Slots.Add(new LayoutSlot
                    {
                        Index = idx++,
                        XMm = Math.Round(gx + c * (25 + gap), 3),
                        YMm = Math.Round(startY + r * (35 + gap), 3),
                        WidthMm = 25,
                        HeightMm = 35,
                        IdPhotoSpecId = "id_1inch",
                        Label = g == 0 ? $"A组 #{r * 2 + c + 1}" : $"B组 #{r * 2 + c + 1}"
                    });
                }
            }
        }
    }

    private static void BuildTwoInOneHorizontal(PrintSheet sheet, double margin, double gap, double bleed,
        (double Width, double Height)? maxPhotoSize = null)
    {
        double slotW = (sheet.PaperWidthMm - margin * 2 - gap) / 2.0;
        double slotH = sheet.PaperHeightMm - margin * 2;
        if (slotW <= 1 || slotH <= 1) return;
        double y = margin;
        if (maxPhotoSize is { } size)
        {
            double scale = Math.Min(1.0, Math.Min(slotW / size.Width, slotH / size.Height));
            slotW = size.Width * scale;
            slotH = size.Height * scale;
            if (!sheet.AlignFlushToEdge)
                y = (sheet.PaperHeightMm - slotH) / 2.0;
        }
        for (int i = 0; i < 2; i++)
        {
            sheet.Slots.Add(new LayoutSlot
            {
                Index = i,
                XMm = Math.Round(margin + i * (slotW + gap), 3),
                YMm = Math.Round(y, 3),
                WidthMm = Math.Round(slotW, 3),
                HeightMm = Math.Round(slotH, 3),
                BleedMm = bleed,
                Label = $"二合一 #{i + 1}"
            });
        }
    }

    private static void BuildTwoInOneVertical(PrintSheet sheet, double margin, double gap, double bleed)
    {
        double slotW = sheet.PaperWidthMm - margin * 2;
        double slotH = (sheet.PaperHeightMm - margin * 2 - gap) / 2.0;
        if (slotW <= 1 || slotH <= 1) return;
        for (int i = 0; i < 2; i++)
        {
            sheet.Slots.Add(new LayoutSlot
            {
                Index = i,
                XMm = Math.Round(margin, 3),
                YMm = Math.Round(margin + i * (slotH + gap), 3),
                WidthMm = Math.Round(slotW, 3),
                HeightMm = Math.Round(slotH, 3),
                BleedMm = bleed,
                Label = i == 0 ? "上图 #1" : "下图 #2"
            });
        }
    }

    private static void BuildCardDuplexTwoInOne(PrintSheet sheet, double margin, double gap, bool flushToEdge)
    {
        const double cardW = 85.6;
        const double cardH = 54.0;
        const double cornerR = 3.18;

        bool sideBySide = sheet.PaperWidthMm >= cardW * 2 + gap + (flushToEdge ? 0 : 4);
        if (sideBySide)
        {
            double totalW = cardW * 2 + gap;
            if (totalW > sheet.PaperWidthMm + 0.01 || cardH > sheet.PaperHeightMm + 0.01) return;
            double sx = flushToEdge ? Math.Min(margin, Math.Max(0, sheet.PaperWidthMm - totalW)) : (sheet.PaperWidthMm - totalW) / 2.0;
            double sy = flushToEdge ? Math.Min(margin, Math.Max(0, sheet.PaperHeightMm - cardH)) : (sheet.PaperHeightMm - cardH) / 2.0;
            for (int i = 0; i < 2; i++)
            {
                sheet.Slots.Add(new LayoutSlot
                {
                    Index = i,
                    XMm = Math.Round(sx + i * (cardW + gap), 3),
                    YMm = Math.Round(sy, 3),
                    WidthMm = cardW,
                    HeightMm = cardH,
                    CornerRadiusMm = cornerR,
                    IdPhotoSpecId = "id_std_card",
                    Label = i == 0 ? "卡证正面 (85.6×54)" : "卡证反面 (85.6×54)"
                });
            }
        }
        else
        {
            double totalH = cardH * 2 + gap;
            if (cardW > sheet.PaperWidthMm + 0.01 || totalH > sheet.PaperHeightMm + 0.01) return;
            double sx = flushToEdge ? Math.Min(margin, Math.Max(0, sheet.PaperWidthMm - cardW)) : (sheet.PaperWidthMm - cardW) / 2.0;
            double sy = flushToEdge ? Math.Min(margin, Math.Max(0, sheet.PaperHeightMm - totalH)) : (sheet.PaperHeightMm - totalH) / 2.0;
            for (int i = 0; i < 2; i++)
            {
                sheet.Slots.Add(new LayoutSlot
                {
                    Index = i,
                    XMm = Math.Round(sx, 3),
                    YMm = Math.Round(sy + i * (cardH + gap), 3),
                    WidthMm = cardW,
                    HeightMm = cardH,
                    CornerRadiusMm = cornerR,
                    IdPhotoSpecId = "id_std_card",
                    Label = i == 0 ? "卡证正面 (85.6×54)" : "卡证反面 (85.6×54)"
                });
            }
        }
    }

    private static void BuildPolaroidGrid(
        PrintSheet sheet,
        PolaroidSpec spec,
        double margin,
        double gap,
        bool safeFit,
        PolaroidSlotStyle? baseStyle,
        bool flushToEdge)
    {
        var pack = CalculateBestPack(sheet.PaperWidthMm, sheet.PaperHeightMm, spec.CardWidthMm, spec.CardHeightMm, margin, gap);
        int cols = pack.Columns;
        int rows = pack.Rows;

        double scale = 1.0;
        if (pack.TotalCount == 0)
        {
            if (!safeFit) return;
            cols = 1;
            rows = 1;
            double availW = (sheet.PaperWidthMm - margin * 2 - (cols - 1) * gap) / cols;
            double availH = sheet.PaperHeightMm - margin * 2;
            if (availW <= 0 || availH <= 0) return;
            scale = Math.Min(1.0, Math.Min(availW / spec.CardWidthMm, availH / spec.CardHeightMm));
        }

        bool rotateSlot = pack.RotatedSlot;
        if (pack.TotalCount > 0)
        {
            var mixed = FindMixedPolaroidPack(sheet, spec, margin, gap);
            if (mixed.Count > pack.TotalCount)
            {
                double totalMixedW = mixed.Max(s => s.X + s.Width);
                double totalMixedH = mixed.Max(s => s.Y + s.Height);
                double offsetX = flushToEdge ? margin : (sheet.PaperWidthMm - totalMixedW) / 2;
                double offsetY = flushToEdge ? margin : (sheet.PaperHeightMm - totalMixedH) / 2;
                foreach (var cell in mixed.OrderBy(s => s.Y).ThenBy(s => s.X))
                    AddPolaroidSlot(sheet, spec, baseStyle, offsetX + cell.X, offsetY + cell.Y, cell.Width, cell.Height, 1, cell.Rotated);
                return;
            }
        }
        double baseW = rotateSlot ? spec.CardHeightMm : spec.CardWidthMm;
        double baseH = rotateSlot ? spec.CardWidthMm : spec.CardHeightMm;
        double cardW = Math.Round(baseW * scale, 3);
        double cardH = Math.Round(baseH * scale, 3);
        double totalW = cols * cardW + (cols - 1) * gap;
        double totalH = rows * cardH + (rows - 1) * gap;
        double startX = flushToEdge ? margin : (sheet.PaperWidthMm - totalW) / 2.0;
        double startY = flushToEdge ? margin : (sheet.PaperHeightMm - totalH) / 2.0;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                AddPolaroidSlot(sheet, spec, baseStyle, startX + c * (cardW + gap), startY + r * (cardH + gap),
                    cardW, cardH, scale, rotateSlot);
            }
        }
    }

    private static List<(double X, double Y, double Width, double Height, bool Rotated)> FindMixedPolaroidPack(
        PrintSheet sheet, PolaroidSpec spec, double margin, double gap)
    {
        double width = sheet.PaperWidthMm - 2 * margin;
        double height = sheet.PaperHeightMm - 2 * margin;
        double cardW = spec.CardWidthMm, cardH = spec.CardHeightMm;
        List<(double X, double Y, double Width, double Height, bool Rotated)> best = [];
        var (maxCols, maxRows) = FitGrid(width, height, cardW, cardH, gap);
        for (int cols = 1; cols <= maxCols; cols++)
            Consider(cols, maxRows, cols * (cardW + gap), 0);
        for (int rows = 1; rows <= maxRows; rows++)
            Consider(maxCols, rows, 0, rows * (cardH + gap));
        return best;

        void Consider(int cols, int rows, double offsetX, double offsetY)
        {
            if (cols == 0 || rows == 0) return;
            var (rotatedCols, rotatedRows) = FitGrid(width - offsetX, height - offsetY, cardH, cardW, gap);
            if (rotatedCols == 0 || rotatedRows == 0 || cols * rows + rotatedCols * rotatedRows <= best.Count) return;
            best.Clear();
            for (int row = 0; row < rows; row++)
                for (int col = 0; col < cols; col++)
                    best.Add((col * (cardW + gap), row * (cardH + gap), cardW, cardH, false));
            for (int row = 0; row < rotatedRows; row++)
                for (int col = 0; col < rotatedCols; col++)
                    best.Add((offsetX + col * (cardH + gap), offsetY + row * (cardW + gap), cardH, cardW, true));
        }
    }

    private static void AddPolaroidSlot(PrintSheet sheet, PolaroidSpec spec, PolaroidSlotStyle? baseStyle,
        double x, double y, double width, double height, double scale, bool rotated = false)
    {
        var style = baseStyle?.Clone() ?? PolaroidSlotStyle.FromSpec(spec);
        style.PolaroidSpecId = spec.Id;
        style.MarginTopMm = Math.Round(spec.MarginTopMm * scale, 2);
        style.MarginLeftMm = Math.Round(spec.MarginLeftMm * scale, 2);
        style.MarginRightMm = Math.Round(spec.MarginRightMm * scale, 2);
        style.MarginBottomMm = Math.Round(spec.MarginBottomMm * scale, 2);
        sheet.Slots.Add(new LayoutSlot
        {
            Index = sheet.Slots.Count,
            XMm = Math.Round(x, 3),
            YMm = Math.Round(y, 3),
            WidthMm = width,
            HeightMm = height,
            CornerRadiusMm = Math.Round(spec.CornerRadiusMm * scale, 2),
            SlotRotationDegrees = rotated ? 90 : 0,
            PolaroidStyle = style,
            Label = $"拍立得 #{sheet.Slots.Count + 1}"
        });
    }

    private static void BuildPolaroidFixedFit(
        PrintSheet sheet,
        PolaroidSpec spec,
        int cols,
        int rows,
        double margin,
        double gap,
        PolaroidSlotStyle? baseStyle,
        bool flushToEdge)
    {
        double availW = (sheet.PaperWidthMm - margin * 2 - (cols - 1) * gap) / cols;
        double availH = (sheet.PaperHeightMm - margin * 2 - (rows - 1) * gap) / rows;
        if (availW <= 0 || availH <= 0) return;
        double scale = Math.Min(1.0, Math.Min(availW / spec.CardWidthMm, availH / spec.CardHeightMm));

        double cardW = Math.Round(spec.CardWidthMm * scale, 3);
        double cardH = Math.Round(spec.CardHeightMm * scale, 3);
        double totalW = cols * cardW + (cols - 1) * gap;
        double totalH = rows * cardH + (rows - 1) * gap;
        double startX = flushToEdge ? margin : (sheet.PaperWidthMm - totalW) / 2.0;
        double startY = flushToEdge ? margin : (sheet.PaperHeightMm - totalH) / 2.0;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                AddPolaroidSlot(sheet, spec, baseStyle, startX + c * (cardW + gap), startY + r * (cardH + gap),
                    cardW, cardH, scale);
            }
        }
    }

    private static void BuildCustomFreeform(PrintSheet sheet, LayoutTemplateDefinition template)
    {
        if (!string.IsNullOrWhiteSpace(template.CustomCanvasBackgroundHex))
            sheet.CanvasBackgroundHex = template.CustomCanvasBackgroundHex;

        if (template.CustomSlots == null || template.CustomSlots.Count == 0)
            return;

        int idx = 0;
        foreach (var def in template.CustomSlots)
        {
            if (!double.IsFinite(def.WidthMm) || !double.IsFinite(def.HeightMm) ||
                def.WidthMm <= 0.1 || def.HeightMm <= 0.1)
                continue;

            double rot = ((Math.Round(def.SlotRotationDegrees / 90.0) * 90.0) % 360.0 + 360.0) % 360.0;
            sheet.Slots.Add(new LayoutSlot
            {
                Id = string.IsNullOrWhiteSpace(def.Id) ? Guid.NewGuid().ToString("N") : def.Id,
                Index = idx,
                XMm = Math.Round(def.XMm, 3),
                YMm = Math.Round(def.YMm, 3),
                WidthMm = Math.Round(def.WidthMm, 3),
                HeightMm = Math.Round(def.HeightMm, 3),
                BleedMm = Math.Max(0, Math.Round(def.BleedMm, 3)),
                CornerRadiusMm = Math.Max(0, Math.Round(def.CornerRadiusMm, 3)),
                SlotRotationDegrees = rot,
                Label = string.IsNullOrWhiteSpace(def.Label) ? $"图片框 {idx + 1}" : def.Label,
                TemplateFitMode = def.FitModeOverride,
                IdPhotoSpecId = def.IdPhotoSpecId,
                PolaroidStyle = def.PolaroidStyle?.Clone()
            });
            idx++;
        }
    }

    private static void BuildFreeGrid(
        PrintSheet sheet,
        int rows,
        int cols,
        double margin,
        double gap,
        double bleed,
        double cornerRadiusMm)
    {
        rows = Math.Clamp(rows, 1, 10);
        cols = Math.Clamp(cols, 1, 10);
        double cellW = (sheet.PaperWidthMm - margin * 2 - (cols - 1) * gap) / cols;
        double cellH = (sheet.PaperHeightMm - margin * 2 - (rows - 1) * gap) / rows;
        if (cellW <= 1 || cellH <= 1) return;

        int idx = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                sheet.Slots.Add(new LayoutSlot
                {
                    Index = idx++,
                    XMm = Math.Round(margin + c * (cellW + gap), 3),
                    YMm = Math.Round(margin + r * (cellH + gap), 3),
                    WidthMm = Math.Round(cellW, 3),
                    HeightMm = Math.Round(cellH, 3),
                    BleedMm = bleed,
                    CornerRadiusMm = cornerRadiusMm,
                    Label = $"网格 #{idx}"
                });
            }
        }
    }

    public static IEnumerable<LayoutSlot> OrderSlots(IEnumerable<LayoutSlot> slots, SlotFillOrder order) => order switch
    {
        SlotFillOrder.RowFirst => slots.OrderBy(s => s.YMm).ThenBy(s => s.XMm),
        SlotFillOrder.ColumnFirst => slots.OrderBy(s => s.XMm).ThenBy(s => s.YMm),
        _ => slots
    };

    public static List<PrintSheet> BuildPaginatedSheets(
        PrintSheet prototypeSheet,
        IReadOnlyList<PhotoAsset> assets,
        SlotFillOrder order = SlotFillOrder.Template,
        FitMode? fit = null,
        bool orientSinglePhotoPages = false)
    {
        if (prototypeSheet.Slots.Count == 0 || assets.Count == 0)
            return [prototypeSheet.Clone()];

        int perPage = prototypeSheet.Slots.Count;
        int pageCount = (int)Math.Ceiling(assets.Count / (double)perPage);
        var result = new List<PrintSheet>(pageCount);

        for (int p = 0; p < pageCount; p++)
        {
            var sheet = prototypeSheet.Clone();
            sheet.SheetName = $"第 {p + 1} 页";
            int assetIdx = p * perPage;
            if (orientSinglePhotoPages && !sheet.IsCustomFreeform && sheet.Scenario == LayoutScenario.SinglePhoto && sheet.Slots.Count == 1)
                OrientSinglePhotoPage(sheet, assets[assetIdx]);
            foreach (var slot in OrderSlots(sheet.Slots, order))
            {
                slot.SlotZoom = 1.0;
                slot.SlotPanX = 0.0;
                slot.SlotPanY = 0.0;
                slot.FitModeOverride = null;
                if (assetIdx < assets.Count)
                {
                    slot.PhotoAssetId = assets[assetIdx++].Id;
                    slot.FitModeOverride = fit;
                }
                else
                {
                    slot.PhotoAssetId = null;
                }
            }
            result.Add(sheet);
        }

        return result;
    }

    private static void OrientSinglePhotoPage(PrintSheet sheet, PhotoAsset photo)
    {
        if (photo.PixelWidth == photo.PixelHeight || sheet.PaperWidthMm == sheet.PaperHeightMm) return;
        bool swapped = photo.EditState.QuarterTurns % 2 != 0;
        bool landscape = (photo.PixelWidth > photo.PixelHeight) != swapped;
        if (landscape == sheet.IsLandscape) return;

        sheet.IsLandscape = landscape;
        (sheet.PaperWidthMm, sheet.PaperHeightMm) = (sheet.PaperHeightMm, sheet.PaperWidthMm);
        var slot = sheet.Slots[0];
        (slot.XMm, slot.YMm) = (slot.YMm, slot.XMm);
        (slot.WidthMm, slot.HeightMm) = (slot.HeightMm, slot.WidthMm);
    }
}
