namespace PhotoPrintStudio.Core.Models;

public static class UnitConverter
{
    public const double MmPerInch = 25.4;
    public const double PointsPerInch = 72.0;

    public static double MmToPixels(double mm, double dpi) => mm / MmPerInch * dpi;

    public static int MmToPixelsRounded(double mm, double dpi) =>
        Math.Max(1, (int)Math.Round(MmToPixels(mm, dpi), MidpointRounding.AwayFromZero));

    public static double PixelsToMm(double pixels, double dpi) => pixels / dpi * MmPerInch;

    public static float MmToPoints(double mm) => (float)(mm / MmPerInch * PointsPerInch);

    public static double PointsToMm(double points) => points / PointsPerInch * MmPerInch;
}

public enum LayoutScenario
{
    IdPhoto,
    SixInchTwoInOne,
    Polaroid,
    FreeGrid,
    SinglePhoto
}

public enum FitMode
{
    Cover,
    Contain
}

public enum SlotFillOrder
{
    Template,
    RowFirst,
    ColumnFirst
}

public enum CropMarkStyle
{
    None,
    OuterCornerTicks,
    DashedBorder,
    SolidThinBorder,
    CenterFoldTicks,
    WhiteBackgroundGuide
}

public enum ExportImageFormat
{
    Jpeg,
    Png,
    Pdf
}

public sealed record PaperSizeSpec(
    string Id,
    string Name,
    double WidthMm,
    double HeightMm,
    string Category,
    string Description = "",
    bool IsBuiltIn = true)
{
    public override string ToString() => Name;

    public static IReadOnlyList<PaperSizeSpec> BuiltInPapers { get; } =
    [
        new("paper_6inch", "6寸 / 4R (101.6×152.4 mm)", 101.6, 152.4, "相纸", "最常用相纸，4×6 英寸"),
        new("paper_big6", "大6寸 (114.3×152.4 mm)", 114.3, 152.4, "相纸", "4.5×6 英寸"),
        new("paper_5inch", "5寸 / 3R (88.9×127.0 mm)", 88.9, 127.0, "相纸", "3.5×5 英寸"),
        new("paper_7inch", "7寸 / 5R (127.0×177.8 mm)", 127.0, 177.8, "相纸", "5×7 英寸"),
        new("paper_8inch", "8寸 / 6R (152.4×203.2 mm)", 152.4, 203.2, "相纸", "6×8 英寸"),
        new("paper_9inch", "9寸 (152.4×228.6 mm)", 152.4, 228.6, "相纸", "6×9 英寸"),
        new("paper_10inch", "10寸 / 8R (203.2×254 mm)", 203.2, 254.0, "相纸", "8×10 英寸"),
        new("paper_square_5inch", "5寸方形 (127×127 mm)", 127.0, 127.0, "相纸", "5×5 英寸"),
        new("paper_square_6inch", "6寸方形 (152.4×152.4 mm)", 152.4, 152.4, "相纸", "6×6 英寸"),
        new("paper_a4", "A4 (210×297 mm)", 210.0, 297.0, "办公纸张", "标准 A4 幅面"),
        new("paper_a5", "A5 (148×210 mm)", 148.0, 210.0, "办公纸张", "A4 对半幅面"),
        new("paper_a6", "A6 (105×148 mm)", 105.0, 148.0, "办公纸张", "明信片尺寸"),
        new("paper_b5_iso", "B5 (176×250 mm)", 176.0, 250.0, "办公纸张", "ISO B5 幅面"),
        new("paper_b5_jis", "JIS B5 (182×257 mm)", 182.0, 257.0, "办公纸张", "日规 B5 幅面"),
        new("paper_postcard", "明信片 / 佳能 P纸 (100×148 mm)", 100.0, 148.0, "热升华", "4×6 佳能 SELPHY / 富士明信片物理区"),
        new("paper_6inch_tab", "热升华 6寸带撕边全长 (101.6×178.0 mm)", 101.6, 178.0, "热升华", "含两端各 12.8mm 走纸撕边预留")
    ];
}

public sealed record IdPhotoSpec(
    string Id,
    string Name,
    double WidthMm,
    double HeightMm,
    string Category,
    double HeadHeightMinMm,
    double HeadHeightMaxMm,
    double TopMarginMm,
    double EyeLineRatioFromTop,
    double ChinLineRatioFromTop,
    string Notes = "",
    bool IsBuiltIn = true)
{
    public override string ToString() => Name;

    public static IReadOnlyList<IdPhotoSpec> BuiltInSpecs { get; } =
    [
        new("id_1inch", "一寸 (25×35 mm)", 25, 35, "国内通用", 19, 23, 3.0, 0.43, 0.77, "简历/证书/常用一寸"),
        new("id_small1", "小一寸 (22×32 mm)", 22, 32, "国内通用", 17, 21, 2.5, 0.43, 0.77, "驾驶证/部分体检表"),
        new("id_big1", "大一寸 / 护照 (33×48 mm)", 33, 48, "国内通用", 28, 33, 3.5, 0.42, 0.76, "中国护照/港澳台通行证"),
        new("id_2inch", "二寸 (35×49 mm)", 35, 49, "国内通用", 28, 33, 3.5, 0.42, 0.76, "标准二寸/资格证"),
        new("id_small2", "小二寸 (35×45 mm)", 35, 45, "国内通用", 26, 32, 3.5, 0.43, 0.77, "公考/四六级/护照小二寸"),
        new("id_big2", "大二寸 (35×53 mm)", 35, 53, "国内通用", 29, 35, 4.0, 0.42, 0.76, "部分职称与学历认证"),
        new("id_cn_idcard", "身份证 / 社保卡 (26×32 mm)", 26, 32, "证照卡证", 18, 22, 2.5, 0.44, 0.78, "二代居民身份证规范"),
        new("id_marriage", "结婚登记照 (53×35 mm)", 53, 35, "证照卡证", 18, 23, 3.0, 0.42, 0.78, "双人红底横版"),
        new("id_visa_us", "美国/印度签证 (51×51 mm)", 51, 51, "国际签证", 25, 35, 5.0, 0.42, 0.78, "2×2 英寸正方形"),
        new("id_visa_schengen", "申根/英/日/澳/韩签证 (35×45 mm)", 35, 45, "国际签证", 27, 34, 3.5, 0.42, 0.78, "各国常规签证标准"),
        new("id_visa_jp_card", "日本在留卡 (30×40 mm)", 30, 40, "国际签证", 23, 29, 3.5, 0.42, 0.77, "日本在留资格"),
        new("id_std_card", "标准卡证 / 身份证正反 (85.6×54 mm)", 85.6, 54, "证照卡证", 0, 0, 0, 0.4, 0.8, "ISO/IEC 7810 ID-1 卡尺寸")
    ];
}

public sealed record PolaroidSpec(
    string Id,
    string Name,
    double CardWidthMm,
    double CardHeightMm,
    double PhotoWidthMm,
    double PhotoHeightMm,
    double MarginTopMm,
    double MarginLeftMm,
    double MarginRightMm,
    double MarginBottomMm,
    double CornerRadiusMm = 0.8,
    string Description = "",
    bool IsBuiltIn = true)
{
    public override string ToString() => Name;

    public static IReadOnlyList<PolaroidSpec> BuiltInSpecs { get; } =
    [
        new("pol_instax_mini", "富士 Instax Mini (54×86 mm)", 54.0, 86.0, 46.0, 62.0, 4.0, 4.0, 4.0, 20.0, 1.0, "经典竖款迷你拍立得，6寸纸可1:1横排2张"),
        new("pol_instax_square", "富士 Instax Square (72×86 mm)", 72.0, 86.0, 62.0, 62.0, 5.0, 5.0, 5.0, 19.0, 1.0, "经典1:1方画幅，6寸纸可1:1横排2张"),
        new("pol_instax_wide", "富士 Instax Wide (108×86 mm)", 108.0, 86.0, 99.0, 62.0, 4.5, 4.5, 4.5, 19.5, 1.0, "宽幅风景/合照拍立得"),
        new("pol_600", "宝丽来 600 / i-Type (88×107 mm)", 88.0, 107.0, 79.0, 79.0, 4.5, 4.5, 4.5, 23.5, 1.2, "大画幅复古宝丽来方片"),
        new("pol_go", "宝丽来 Go (53.9×66.6 mm)", 53.9, 66.6, 46.0, 47.0, 4.0, 3.95, 3.95, 15.6, 1.0, "超小口袋方形拍立得"),
        new("pol_lomo_3in", "Lomo 3寸宽边小卡 (63.5×89 mm)", 63.5, 89.0, 55.5, 74.0, 4.0, 4.0, 4.0, 11.0, 1.5, "手账/照片墙常用3寸白边卡"),
        new("pol_kpop_card", "追星/收藏小卡 (54×85 mm)", 54.0, 85.0, 48.0, 72.0, 3.0, 3.0, 3.0, 10.0, 2.0, "标准钱包卡/小卡尺寸")
    ];
}
