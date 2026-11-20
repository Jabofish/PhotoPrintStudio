namespace PhotoPrintStudio.Core.Models;

public enum FormalSuitStyle
{
    None = 0,
    MenNavySuitTie = 1,
    MenBlackSuitTie = 2,
    WomenBlazerVNeck = 3,
    ClassicWhiteShirt = 4,
    CustomPng = 100
}

public enum StudioEnhanceMode
{
    Natural = 0,
    Studio = 1,
    Rescue = 2
}

public sealed class CurvePoint
{
    public double X { get; set; }
    public double Y { get; set; }

    public CurvePoint() { }

    public CurvePoint(double x, double y)
    {
        X = Math.Clamp(x, 0.0, 1.0);
        Y = Math.Clamp(y, 0.0, 1.0);
    }

    public CurvePoint Clone() => new(X, Y);
}

public sealed class ToneCurveData
{
    public List<CurvePoint> Master { get; set; } = DefaultLinear();
    public List<CurvePoint> Red { get; set; } = DefaultLinear();
    public List<CurvePoint> Green { get; set; } = DefaultLinear();
    public List<CurvePoint> Blue { get; set; } = DefaultLinear();

    public static List<CurvePoint> DefaultLinear() => [new(0.0, 0.0), new(1.0, 1.0)];

    public ToneCurveData Clone() => new()
    {
        Master = Master.Select(p => p.Clone()).ToList(),
        Red = Red.Select(p => p.Clone()).ToList(),
        Green = Green.Select(p => p.Clone()).ToList(),
        Blue = Blue.Select(p => p.Clone()).ToList()
    };

    public bool IsIdentity =>
        IsLinear(Master) && IsLinear(Red) && IsLinear(Green) && IsLinear(Blue);

    private static bool IsLinear(List<CurvePoint> pts) =>
        pts.Count == 2
        && Math.Abs(pts[0].X) < 1e-4 && Math.Abs(pts[0].Y) < 1e-4
        && Math.Abs(pts[1].X - 1.0) < 1e-4 && Math.Abs(pts[1].Y - 1.0) < 1e-4;

    /// <summary>
    /// 使用 Fritsch-Carlson 单调分段三次插值构建 0..1 查表，避免曲线控制点剧烈波动产生反常色阶。
    /// </summary>
    public static float[] BuildLut(List<CurvePoint> rawPoints, int size = 4096)
    {
        var lut = new float[size];
        var pts = rawPoints
            .Select(p => new CurvePoint(Math.Clamp(p.X, 0.0, 1.0), Math.Clamp(p.Y, 0.0, 1.0)))
            .OrderBy(p => p.X)
            .ToList();

        if (pts.Count == 0)
        {
            pts = DefaultLinear();
        }
        if (pts[0].X > 0.0) pts.Insert(0, new CurvePoint(0.0, pts[0].Y));
        if (pts[^1].X < 1.0) pts.Add(new CurvePoint(1.0, pts[^1].Y));

        // 合并极近 X 坐标防止除零
        var clean = new List<CurvePoint> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            if (pts[i].X - clean[^1].X >= 1e-4)
                clean.Add(pts[i]);
            else
                clean[^1] = pts[i];
        }

        int n = clean.Count;
        if (n == 1)
        {
            Array.Fill(lut, (float)clean[0].Y);
            return lut;
        }

        var d = new double[n - 1];
        var m = new double[n];
        for (int i = 0; i < n - 1; i++)
        {
            d[i] = (clean[i + 1].Y - clean[i].Y) / Math.Max(1e-6, clean[i + 1].X - clean[i].X);
        }

        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            if (d[i - 1] * d[i] <= 0)
                m[i] = 0;
            else
                m[i] = (d[i - 1] + d[i]) * 0.5;
        }

        for (int i = 0; i < n - 1; i++)
        {
            if (Math.Abs(d[i]) < 1e-6)
            {
                m[i] = 0;
                m[i + 1] = 0;
            }
            else
            {
                double a = m[i] / d[i];
                double b = m[i + 1] / d[i];
                double s = a * a + b * b;
                if (s > 9.0)
                {
                    double t = 3.0 / Math.Sqrt(s);
                    m[i] = t * a * d[i];
                    m[i + 1] = t * b * d[i];
                }
            }
        }

        int seg = 0;
        for (int i = 0; i < size; i++)
        {
            double x = i / (double)(size - 1);
            while (seg < n - 2 && x > clean[seg + 1].X) seg++;

            double h = Math.Max(1e-6, clean[seg + 1].X - clean[seg].X);
            double t = Math.Clamp((x - clean[seg].X) / h, 0.0, 1.0);
            double t2 = t * t;
            double t3 = t2 * t;
            double h00 = 2 * t3 - 3 * t2 + 1;
            double h10 = t3 - 2 * t2 + t;
            double h01 = -2 * t3 + 3 * t2;
            double h11 = t3 - t2;

            double y = h00 * clean[seg].Y + h10 * h * m[seg] + h01 * clean[seg + 1].Y + h11 * h * m[seg + 1];
            lut[i] = (float)Math.Clamp(y, 0.0, 1.0);
        }

        return lut;
    }
}

public sealed class HslChannelAdjustment
{
    public double Hue { get; set; }         // -100 .. +100 (maps to +-30 deg)
    public double Saturation { get; set; }  // -100 .. +100
    public double Lightness { get; set; }   // -100 .. +100

    public bool IsZero =>
        Math.Abs(Hue) < 1e-4 && Math.Abs(Saturation) < 1e-4 && Math.Abs(Lightness) < 1e-4;

    public HslChannelAdjustment Clone() => new()
    {
        Hue = Hue,
        Saturation = Saturation,
        Lightness = Lightness
    };
}

public sealed class HslAdjustmentSet
{
    // 顺序对应色相环: 红(0), 橙(30), 黄(60), 绿(120), 青(180), 蓝(240), 紫(275), 品红(315)
    public HslChannelAdjustment Red { get; set; } = new();
    public HslChannelAdjustment Orange { get; set; } = new();
    public HslChannelAdjustment Yellow { get; set; } = new();
    public HslChannelAdjustment Green { get; set; } = new();
    public HslChannelAdjustment Cyan { get; set; } = new();
    public HslChannelAdjustment Blue { get; set; } = new();
    public HslChannelAdjustment Purple { get; set; } = new();
    public HslChannelAdjustment Magenta { get; set; } = new();

    public HslChannelAdjustment GetByIndex(int idx) => idx switch
    {
        0 => Red,
        1 => Orange,
        2 => Yellow,
        3 => Green,
        4 => Cyan,
        5 => Blue,
        6 => Purple,
        7 => Magenta,
        _ => Red
    };

    public bool IsZero =>
        Red.IsZero && Orange.IsZero && Yellow.IsZero && Green.IsZero &&
        Cyan.IsZero && Blue.IsZero && Purple.IsZero && Magenta.IsZero;

    public HslAdjustmentSet Clone() => new()
    {
        Red = Red.Clone(),
        Orange = Orange.Clone(),
        Yellow = Yellow.Clone(),
        Green = Green.Clone(),
        Cyan = Cyan.Clone(),
        Blue = Blue.Clone(),
        Purple = Purple.Clone(),
        Magenta = Magenta.Clone()
    };
}

public sealed class PhotoEditState
{
    // 构图与几何变换
    public double RotationDegrees { get; set; }      // -45 .. +45
    public int QuarterTurns { get; set; }            // 0..3 (x 90 deg)
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public double Zoom { get; set; } = 1.0;          // 1.0 .. 5.0
    public double PanX { get; set; }                 // -1.0 .. +1.0 (normalized offset)
    public double PanY { get; set; }                 // -1.0 .. +1.0
    public FitMode FitMode { get; set; } = FitMode.Cover;

    // 光影与曝光
    public double Exposure { get; set; }             // -2.0 .. +2.0 EV
    public double Brightness { get; set; }           // -100 .. +100
    public double Contrast { get; set; }             // -100 .. +100
    public double Highlights { get; set; }           // -100 .. +100
    public double Shadows { get; set; }              // -100 .. +100
    public double Whites { get; set; }               // -100 .. +100
    public double Blacks { get; set; }               // -100 .. +100
    public double Gamma { get; set; } = 1.0;         // 0.50 .. 1.80

    // 色阶 (Levels)
    public double InputBlack { get; set; } = 0.0;    // 0 .. 200
    public double InputWhite { get; set; } = 255.0;  // 55 .. 255
    public double OutputBlack { get; set; } = 0.0;   // 0 .. 200
    public double OutputWhite { get; set; } = 255.0; // 55 .. 255

    // 白平衡与色彩
    public double Temperature { get; set; }          // -100 .. +100
    public double Tint { get; set; }                 // -100 .. +100
    public double Vibrance { get; set; }             // -100 .. +100
    public double Saturation { get; set; }           // -100 .. +100

    // 曲线与 HSL
    public ToneCurveData ToneCurves { get; set; } = new();
    public HslAdjustmentSet Hsl { get; set; } = new();

    // 清晰度、锐化、人像柔肤
    public double Clarity { get; set; }              // -100 .. +100
    public double Sharpening { get; set; }           // 0 .. 100
    public double SkinSoftening { get; set; }        // 0 .. 100

    // 证件照背景处理
    public double WhiteWallCleanup { get; set; }     // 0 .. 100（无需AI的白墙阴影提纯）
    public bool EnableAiBackgroundReplace { get; set; }
    public string ReplacementBackgroundHex { get; set; } = "#FFFFFF";
    public bool UseGradientBackground { get; set; }
    public string ReplacementBackgroundSecondaryHex { get; set; } = "#93C5FD";
    public double MattingFeather { get; set; } = 0.5;
    public double SpillSuppression { get; set; } = 60.0;
    public double MattingEdgeShift { get; set; } = 0.0;   // -2.0 .. +1.0 px（负值内缩收边，正值微扩保细发）

    // 一键影棚人像自动优化（智能白平衡 / 自适应曝光 / 左右脸补光 / 去油光 / 人像精修）
    public bool EnableStudioPortraitEnhance { get; set; }
    public double StudioEnhanceStrength { get; set; } = 70.0; // 0 .. 100
    public StudioEnhanceMode StudioEnhanceMode { get; set; } = StudioEnhanceMode.Studio;
    public bool IsStudioEnhancementActive() => EnableStudioPortraitEnhance && StudioEnhanceStrength >= 1.0;

    // 证件照矢量/高清贴图正装叠加
    public FormalSuitStyle FormalSuit { get; set; } = FormalSuitStyle.None;
    public string CustomSuitAssetPath { get; set; } = "";
    public double SuitCenterXRatio { get; set; } = 0.50;
    public double SuitNeckYRatio { get; set; } = 0.60;
    public double SuitScale { get; set; } = 1.00;

    // 风格滤镜
    public string FilterPresetId { get; set; } = "none";
    public double FilterIntensity { get; set; } = 100.0; // 0 .. 100

    public PhotoEditState Clone() => new()
    {
        RotationDegrees = RotationDegrees,
        QuarterTurns = QuarterTurns,
        FlipHorizontal = FlipHorizontal,
        FlipVertical = FlipVertical,
        Zoom = Zoom,
        PanX = PanX,
        PanY = PanY,
        FitMode = FitMode,
        Exposure = Exposure,
        Brightness = Brightness,
        Contrast = Contrast,
        Highlights = Highlights,
        Shadows = Shadows,
        Whites = Whites,
        Blacks = Blacks,
        Gamma = Gamma,
        InputBlack = InputBlack,
        InputWhite = InputWhite,
        OutputBlack = OutputBlack,
        OutputWhite = OutputWhite,
        Temperature = Temperature,
        Tint = Tint,
        Vibrance = Vibrance,
        Saturation = Saturation,
        ToneCurves = ToneCurves.Clone(),
        Hsl = Hsl.Clone(),
        Clarity = Clarity,
        Sharpening = Sharpening,
        SkinSoftening = SkinSoftening,
        WhiteWallCleanup = WhiteWallCleanup,
        EnableAiBackgroundReplace = EnableAiBackgroundReplace,
        ReplacementBackgroundHex = ReplacementBackgroundHex,
        UseGradientBackground = UseGradientBackground,
        ReplacementBackgroundSecondaryHex = ReplacementBackgroundSecondaryHex,
        MattingFeather = MattingFeather,
        SpillSuppression = SpillSuppression,
        MattingEdgeShift = MattingEdgeShift,
        EnableStudioPortraitEnhance = EnableStudioPortraitEnhance,
        StudioEnhanceStrength = StudioEnhanceStrength,
        StudioEnhanceMode = StudioEnhanceMode,
        FormalSuit = FormalSuit,
        CustomSuitAssetPath = CustomSuitAssetPath,
        SuitCenterXRatio = SuitCenterXRatio,
        SuitNeckYRatio = SuitNeckYRatio,
        SuitScale = SuitScale,
        FilterPresetId = FilterPresetId,
        FilterIntensity = FilterIntensity
    };

    public PhotoEditState CloneColorOnlyInto(PhotoEditState target, bool includePortraitEffects = true)
    {
        var copy = Clone();
        copy.RotationDegrees = target.RotationDegrees;
        copy.QuarterTurns = target.QuarterTurns;
        copy.FlipHorizontal = target.FlipHorizontal;
        copy.FlipVertical = target.FlipVertical;
        copy.Zoom = target.Zoom;
        copy.PanX = target.PanX;
        copy.PanY = target.PanY;
        copy.FitMode = target.FitMode;
        if (!includePortraitEffects)
        {
            copy.WhiteWallCleanup = target.WhiteWallCleanup;
            copy.EnableAiBackgroundReplace = target.EnableAiBackgroundReplace;
            copy.ReplacementBackgroundHex = target.ReplacementBackgroundHex;
            copy.UseGradientBackground = target.UseGradientBackground;
            copy.ReplacementBackgroundSecondaryHex = target.ReplacementBackgroundSecondaryHex;
            copy.MattingFeather = target.MattingFeather;
            copy.SpillSuppression = target.SpillSuppression;
            copy.MattingEdgeShift = target.MattingEdgeShift;
            copy.EnableStudioPortraitEnhance = target.EnableStudioPortraitEnhance;
            copy.StudioEnhanceStrength = target.StudioEnhanceStrength;
            copy.StudioEnhanceMode = target.StudioEnhanceMode;
            copy.FormalSuit = target.FormalSuit;
            copy.CustomSuitAssetPath = target.CustomSuitAssetPath;
            copy.SuitCenterXRatio = target.SuitCenterXRatio;
            copy.SuitNeckYRatio = target.SuitNeckYRatio;
            copy.SuitScale = target.SuitScale;
        }
        return copy;
    }

    public bool ColorEquals(PhotoEditState? other)
    {
        if (other == null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Exposure == other.Exposure &&
               Brightness == other.Brightness &&
               Contrast == other.Contrast &&
               Highlights == other.Highlights &&
               Shadows == other.Shadows &&
               Whites == other.Whites &&
               Blacks == other.Blacks &&
               Gamma == other.Gamma &&
               InputBlack == other.InputBlack &&
               InputWhite == other.InputWhite &&
               OutputBlack == other.OutputBlack &&
               OutputWhite == other.OutputWhite &&
               Temperature == other.Temperature &&
               Tint == other.Tint &&
               Vibrance == other.Vibrance &&
               Saturation == other.Saturation &&
               Clarity == other.Clarity &&
               Sharpening == other.Sharpening &&
               SkinSoftening == other.SkinSoftening &&
               WhiteWallCleanup == other.WhiteWallCleanup &&
               EnableAiBackgroundReplace == other.EnableAiBackgroundReplace &&
               UseGradientBackground == other.UseGradientBackground &&
               MattingFeather == other.MattingFeather &&
               SpillSuppression == other.SpillSuppression &&
               MattingEdgeShift == other.MattingEdgeShift &&
               EnableStudioPortraitEnhance == other.EnableStudioPortraitEnhance &&
               StudioEnhanceStrength == other.StudioEnhanceStrength &&
               StudioEnhanceMode == other.StudioEnhanceMode &&
               FilterIntensity == other.FilterIntensity &&
               string.Equals(ReplacementBackgroundHex, other.ReplacementBackgroundHex, StringComparison.Ordinal) &&
               string.Equals(ReplacementBackgroundSecondaryHex, other.ReplacementBackgroundSecondaryHex, StringComparison.Ordinal) &&
               string.Equals(FilterPresetId, other.FilterPresetId, StringComparison.Ordinal) &&
               HslEqual(Hsl, other.Hsl) &&
               CurvesEqual(ToneCurves, other.ToneCurves);
    }

    private static bool HslEqual(HslAdjustmentSet a, HslAdjustmentSet b)
    {
        for (int i = 0; i < 8; i++)
        {
            var ca = a.GetByIndex(i);
            var cb = b.GetByIndex(i);
            if (ca.Hue != cb.Hue || ca.Saturation != cb.Saturation || ca.Lightness != cb.Lightness)
                return false;
        }
        return true;
    }

    private static bool CurvesEqual(ToneCurveData a, ToneCurveData b) =>
        CurvePointsEqual(a.Master, b.Master) &&
        CurvePointsEqual(a.Red, b.Red) &&
        CurvePointsEqual(a.Green, b.Green) &&
        CurvePointsEqual(a.Blue, b.Blue);

    private static bool CurvePointsEqual(List<CurvePoint> a, List<CurvePoint> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].X != b[i].X || a[i].Y != b[i].Y)
                return false;
        }
        return true;
    }

    public void ResetColor()
    {
        Exposure = 0;
        Brightness = 0;
        Contrast = 0;
        Highlights = 0;
        Shadows = 0;
        Whites = 0;
        Blacks = 0;
        Gamma = 1.0;
        InputBlack = 0;
        InputWhite = 255;
        OutputBlack = 0;
        OutputWhite = 255;
        Temperature = 0;
        Tint = 0;
        Vibrance = 0;
        Saturation = 0;
        ToneCurves = new ToneCurveData();
        Hsl = new HslAdjustmentSet();
        Clarity = 0;
        Sharpening = 0;
        SkinSoftening = 0;
        WhiteWallCleanup = 0;
        EnableAiBackgroundReplace = false;
        UseGradientBackground = false;
        MattingEdgeShift = 0.0;
        EnableStudioPortraitEnhance = false;
        StudioEnhanceStrength = 70.0;
        StudioEnhanceMode = StudioEnhanceMode.Studio;
        FormalSuit = FormalSuitStyle.None;
        CustomSuitAssetPath = "";
        SuitCenterXRatio = 0.50;
        SuitNeckYRatio = 0.60;
        SuitScale = 1.00;
        FilterPresetId = "none";
        FilterIntensity = 100.0;
    }

    public void ResetAll()
    {
        ResetColor();
        RotationDegrees = 0;
        QuarterTurns = 0;
        FlipHorizontal = false;
        FlipVertical = false;
        Zoom = 1.0;
        PanX = 0;
        PanY = 0;
        FitMode = FitMode.Cover;
    }
}

public sealed class ColorPresetItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "内置";
    public string Description { get; set; } = "";
    public PhotoEditState Parameters { get; set; } = new();
    public bool IsBuiltIn { get; set; }

    public override string ToString() => Name;

    public static IReadOnlyList<ColorPresetItem> BuiltInPresets { get; } =
    [
        new()
        {
            Id = "preset_original",
            Name = "原色还原（重置调色）",
            Category = "冲印基础",
            Description = "全部光影与色彩归零",
            IsBuiltIn = true,
            Parameters = new PhotoEditState()
        },
        new()
        {
            Id = "preset_inkjet_boost",
            Name = "喷墨相纸通用提亮（推荐）",
            Category = "冲印基础",
            Description = "补偿家用相纸打印偏暗问题，温和提亮暗部并微增锐度",
            IsBuiltIn = true,
            Parameters = new PhotoEditState
            {
                Exposure = 0.18,
                Shadows = 22,
                Contrast = 8,
                Gamma = 1.08,
                Vibrance = 10,
                Sharpening = 20
            }
        },
        new()
        {
            Id = "preset_sublimation",
            Name = "热升华自然防过饱和",
            Category = "冲印基础",
            Description = "适配佳能 SELPHY 等热升华相纸，控红防溢出",
            IsBuiltIn = true,
            Parameters = new PhotoEditState
            {
                Exposure = 0.12,
                Highlights = -10,
                Shadows = 16,
                Contrast = 6,
                Saturation = -6,
                Vibrance = 8,
                Sharpening = 18
            }
        },
        new()
        {
            Id = "preset_id_fresh",
            Name = "证件照通透提亮柔肤",
            Category = "证件照",
            Description = "提亮肤色与暗部阴影，适度柔肤与锐化五官",
            IsBuiltIn = true,
            Parameters = new PhotoEditState
            {
                Exposure = 0.22,
                Shadows = 25,
                Highlights = -8,
                Gamma = 1.06,
                SkinSoftening = 22,
                Sharpening = 18,
                Hsl = new HslAdjustmentSet
                {
                    Orange = new HslChannelAdjustment { Lightness = 12, Saturation = -5 }
                }
            }
        },
        new()
        {
            Id = "preset_fuji_instax",
            Name = "富士日系清透拍立得",
            Category = "拍立得风格",
            Description = "清透蓝青调高光 + 柔和肤色",
            IsBuiltIn = true,
            Parameters = new PhotoEditState
            {
                Exposure = 0.15,
                Shadows = 18,
                Contrast = 6,
                Temperature = -8,
                Tint = -4,
                Vibrance = 16,
                FilterPresetId = "fuji_fresh",
                FilterIntensity = 75
            }
        },
        new()
        {
            Id = "preset_kodak_warm",
            Name = "柯达复古暖调胶片",
            Category = "拍立得风格",
            Description = "微暖高光与怀旧微抬黑场",
            IsBuiltIn = true,
            Parameters = new PhotoEditState
            {
                Exposure = 0.10,
                Contrast = 12,
                Temperature = 16,
                Tint = 4,
                Saturation = 8,
                OutputBlack = 10,
                FilterPresetId = "kodak_warm",
                FilterIntensity = 80
            }
        },
        new()
        {
            Id = "preset_bw_silver",
            Name = "银盐高反差黑白",
            Category = "艺术黑白",
            Description = "经典高反差暗房黑白质感",
            IsBuiltIn = true,
            Parameters = new PhotoEditState
            {
                Contrast = 24,
                Clarity = 18,
                Sharpening = 25,
                FilterPresetId = "bw_silver",
                FilterIntensity = 100
            }
        }
    ];
}
