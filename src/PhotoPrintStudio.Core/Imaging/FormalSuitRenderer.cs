using System.Text.Json;
using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

public sealed record FormalSuitPresetOption(FormalSuitStyle Style, string Name, string? CustomPngPath = null);

public sealed record CustomSuitMetadata(
    double NeckAnchorXRatio = 0.50,
    double NeckAnchorYRatio = 0.22,
    double WidthScaleFactor = 1.82,
    string? Name = null);

/// <summary>
/// 证件照矢量正装衣领高精度叠加引擎：根据人脸五官/下颌关键点自动贴合肩颈位置与肩宽比例，
/// 在预览与 300/600 DPI 导出画布上渲染带有立体光影折痕的商业正装。
/// </summary>
public static class FormalSuitRenderer
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<FormalSuitPresetOption> BuiltInPresets { get; } =
    [
        new(FormalSuitStyle.None, "无正装叠加（原图服装）"),
        new(FormalSuitStyle.MenNavySuitTie, "男士藏青西装配商务蓝领带"),
        new(FormalSuitStyle.MenBlackSuitTie, "男士经典黑西装配勃艮第领带"),
        new(FormalSuitStyle.WomenBlazerVNeck, "女士职业黑西装配气质 V 领白衫"),
        new(FormalSuitStyle.ClassicWhiteShirt, "正装纯白免烫翻领衬衫")
    ];

    /// <summary>
    /// 扫描内置正装 + portable_data\suits\ 下的自定义高清透明 PNG 正装贴图（支持同名 .json 锚点配置）。
    /// </summary>
    public static IReadOnlyList<FormalSuitPresetOption> DiscoverAllPresets(string? suitsDirectory)
    {
        var list = new List<FormalSuitPresetOption>(BuiltInPresets);
        if (string.IsNullOrWhiteSpace(suitsDirectory) || !Directory.Exists(suitsDirectory))
            return list;

        foreach (string png in Directory.GetFiles(suitsDirectory, "*.png").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string display = $"自定义贴图：{Path.GetFileNameWithoutExtension(png)}";
            string metaPath = Path.ChangeExtension(png, ".json");
            if (File.Exists(metaPath))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<CustomSuitMetadata>(File.ReadAllText(metaPath), JsonOpts);
                    if (!string.IsNullOrWhiteSpace(meta?.Name))
                        display = meta.Name.Trim();
                }
                catch { /* ignore malformed metadata */ }
            }
            list.Add(new FormalSuitPresetOption(FormalSuitStyle.CustomPng, display, png));
        }
        return list;
    }

    public static (double centerXRatio, double neckYRatio, double suitScale) ComputeSuitPlacement(
        IReadOnlyList<DetectedFace> faces,
        int imageWidth,
        int imageHeight)
    {
        if (faces.Count == 0 || imageWidth <= 0 || imageHeight <= 0)
            return (0.50, 0.60, 1.00);

        var face = faces.OrderByDescending(f => f.Width * f.Height).First();
        var eyeMid = face.EyeMidpoint;
        var mouthMid = face.MouthMidpoint;

        float vx = mouthMid.X - eyeMid.X;
        float vy = mouthMid.Y - eyeMid.Y;
        float vLen = MathF.Sqrt(vx * vx + vy * vy);
        float downX = vLen > 1f ? vx / vLen : 0f;
        float downY = vLen > 1f ? vy / vLen : 1f;

        float eyeSpan = MathF.Max(face.EyeDistance, face.Width * 0.36f);
        float faceHeight = MathF.Max(face.Height, eyeSpan * 2.25f);

        // 颈部衣领缺口锚点：嘴巴中心沿面部下轴向下约 0.56 倍脸高（处于下巴下方喉结/锁骨窝处）
        float anchorX = mouthMid.X + downX * (faceHeight * 0.56f);
        float anchorY = mouthMid.Y + downY * (faceHeight * 0.56f);

        double cxRatio = Math.Clamp(anchorX / imageWidth, 0.15, 0.85);
        double nyRatio = Math.Clamp(anchorY / imageHeight, 0.20, 0.90);

        double targetShoulderPx = Math.Max(face.Width * 2.60, eyeSpan * 5.30);
        double baseRefPx = Math.Min(imageWidth, imageHeight) * 0.68;
        double scale = baseRefPx > 1 ? Math.Clamp(targetShoulderPx / baseRefPx, 0.45, 1.85) : 1.0;

        return (Math.Round(cxRatio, 4), Math.Round(nyRatio, 4), Math.Round(scale, 3));
    }

    public static void DrawSuitOverlay(SKBitmap targetBitmap, PhotoEditState edit)
    {
        if (edit.FormalSuit == FormalSuitStyle.None) return;

        int w = targetBitmap.Width;
        int h = targetBitmap.Height;
        if (w < 16 || h < 16) return;

        using var canvas = new SKCanvas(targetBitmap);
        canvas.Save();
        canvas.Translate(w * 0.5f, h * 0.5f);
        DrawSuitInCenteredImageSpace(canvas, w, h, edit);
        canvas.Restore();
    }

    /// <summary>
    /// 在已应用完裁剪/旋转/镜像矩阵（原点位于图片几何中心、左上角为 (-iw/2, -ih/2)）的画布上叠加正装，
    /// 保证预览与 300/600 DPI 印刷导出完全一致，且在旋转校平或翻转后肩膀仍保持绝对水平。
    /// </summary>
    public static void DrawSuitInCenteredImageSpace(SKCanvas canvas, float iw, float ih, PhotoEditState edit)
    {
        if (edit.FormalSuit == FormalSuitStyle.None || iw < 16f || ih < 16f) return;

        canvas.Save();
        float cx = (float)(Math.Clamp(edit.SuitCenterXRatio, 0.05, 0.95) - 0.5) * iw;
        float ny = (float)(Math.Clamp(edit.SuitNeckYRatio, 0.10, 0.95) - 0.5) * ih;
        canvas.Translate(cx, ny);

        // DrawFittedImage 按 Flip * Rotate(totalAngle) 顺序作用，求逆必须先撤销旋转再撤销镜像，
        // 才能在翻转 + 倾角校正同时存在时保持衣领肩线绝对水平正向朝上
        float totalAngle = (float)(edit.QuarterTurns * 90.0 + edit.RotationDegrees);
        if (Math.Abs(totalAngle) > 0.01f)
            canvas.RotateDegrees(-totalAngle);
        if (edit.FlipVertical) canvas.Scale(1f, -1f);
        if (edit.FlipHorizontal) canvas.Scale(-1f, 1f);

        float s = (float)(Math.Min(iw, ih) * 0.68 * Math.Clamp(edit.SuitScale, 0.35, 2.2));

        switch (edit.FormalSuit)
        {
            case FormalSuitStyle.CustomPng:
                DrawCustomPngSuit(canvas, s, edit.CustomSuitAssetPath);
                break;
            case FormalSuitStyle.MenNavySuitTie:
                DrawMenSuitWithTie(canvas, s, new SKColor(24, 42, 78), new SKColor(15, 26, 52), new SKColor(32, 74, 138), new SKColor(20, 48, 96));
                break;
            case FormalSuitStyle.MenBlackSuitTie:
                DrawMenSuitWithTie(canvas, s, new SKColor(34, 36, 42), new SKColor(18, 19, 24), new SKColor(148, 28, 36), new SKColor(96, 16, 22));
                break;
            case FormalSuitStyle.WomenBlazerVNeck:
                DrawWomenBlazerVNeck(canvas, s);
                break;
            case FormalSuitStyle.ClassicWhiteShirt:
                DrawClassicWhiteShirtOnly(canvas, s);
                break;
        }

        canvas.Restore();
    }

    private static void DrawMenSuitWithTie(SKCanvas canvas, float s, SKColor coatTop, SKColor coatDark, SKColor tieLight, SKColor tieDark)
    {
        // 1. 柔和颈部投影（让衣领自然压在脖颈下方而非生硬贴纸）
        DrawNeckContactShadow(canvas, s, openWidth: 0.17f, dropY: 0.34f);

        // 2. 挺括白衬衫内衬与三角领口
        DrawWhiteShirtBase(canvas, s, openVHeight: 0.38f, halfNeckW: 0.165f);

        // 3. 商务领带（结 + 渐层绸面斜影 + 立领遮挡）
        DrawSilkTie(canvas, s, tieLight, tieDark, topY: 0.045f, bottomY: 1.35f);

        // 4. 衬衫左/右立领翻页（压在领带结上）
        DrawMensShirtCollars(canvas, s);

        // 5. 双侧西装大身、顺滑溜肩及翻驳头（延伸足够远以完全遮盖原衣服）
        DrawTailoredBlazer(canvas, s, coatTop, coatDark, notchY: 0.39f, neckSpread: 0.175f, openLapel: true);
    }

    private static void DrawWomenBlazerVNeck(SKCanvas canvas, float s)
    {
        DrawNeckContactShadow(canvas, s, openWidth: 0.19f, dropY: 0.42f);

        // 柔白丝绸V领内搭（保留颈锁骨优雅V线，无领带）
        DrawWhiteShirtBase(canvas, s, openVHeight: 0.48f, halfNeckW: 0.185f);

        // 精致女性立领边缘高光与翻领内阴影
        using (var innerShadow = new SKPaint
        {
            Color = new SKColor(203, 213, 225, 190),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.5f, 0.012f * s),
            IsAntialias = true
        })
        {
            using var pb = new SKPathBuilder();
            pb.MoveTo(-0.185f * s, -0.02f * s);
            pb.QuadTo(-0.12f * s, 0.25f * s, 0f, 0.48f * s);
            pb.QuadTo(0.12f * s, 0.25f * s, 0.185f * s, -0.02f * s);
            using var path = pb.Detach();
            canvas.DrawPath(path, innerShadow);
        }

        // 修身无扣女士黑色西装外套与长弧驳领
        DrawTailoredBlazer(canvas, s, new SKColor(36, 40, 48), new SKColor(18, 20, 26), notchY: 0.50f, neckSpread: 0.19f, openLapel: false);
    }

    private static void DrawClassicWhiteShirtOnly(SKCanvas canvas, float s)
    {
        DrawNeckContactShadow(canvas, s, openWidth: 0.175f, dropY: 0.24f);

        // 整片白衬衫肩胸大身（覆盖下端并略带自然褶皱立体光影）
        using var shirtShader = SKShader.CreateLinearGradient(
            new SKPoint(-0.55f * s, 0f),
            new SKPoint(0.55f * s, 0.9f * s),
            [new SKColor(252, 253, 255), new SKColor(240, 244, 249), new SKColor(224, 230, 239)],
            [0f, 0.55f, 1f],
            SKShaderTileMode.Clamp);
        using var shirtPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = shirtShader
        };

        using (var pb = new SKPathBuilder())
        {
            pb.MoveTo(-0.18f * s, -0.02f * s);
            pb.QuadTo(0f, 0.24f * s, 0.18f * s, -0.02f * s);
            pb.CubicTo(0.32f * s, 0.06f * s, 0.62f * s, 0.14f * s, 0.85f * s, 0.36f * s);
            pb.LineTo(0.95f * s, 1.55f * s);
            pb.LineTo(-0.95f * s, 1.55f * s);
            pb.LineTo(-0.85f * s, 0.36f * s);
            pb.CubicTo(-0.62f * s, 0.14f * s, -0.32f * s, 0.06f * s, -0.18f * s, -0.02f * s);
            pb.Close();
            using var path = pb.Detach();
            canvas.DrawPath(path, shirtPaint);
        }

        // 中线门襟与微弱纽扣
        using var placketPaint = new SKPaint
        {
            Color = new SKColor(203, 213, 225, 180),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.2f, 0.008f * s),
            IsAntialias = true
        };
        canvas.DrawLine(-0.015f * s, 0.22f * s, -0.015f * s, 1.45f * s, placketPaint);
        canvas.DrawLine(0.015f * s, 0.22f * s, 0.015f * s, 1.45f * s, placketPaint);

        using var btnPaint = new SKPaint { Color = new SKColor(226, 232, 240), IsAntialias = true };
        using var btnRim = new SKPaint { Color = new SKColor(148, 163, 184), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, 0.005f * s), IsAntialias = true };
        for (int i = 0; i < 3; i++)
        {
            float by = (0.31f + i * 0.21f) * s;
            canvas.DrawCircle(0f, by, 0.014f * s, btnPaint);
            canvas.DrawCircle(0f, by, 0.014f * s, btnRim);
        }

        DrawMensShirtCollars(canvas, s, openNoTie: true);
    }

    private static void DrawNeckContactShadow(SKCanvas canvas, float s, float openWidth, float dropY)
    {
        // 双层环境光遮蔽投影：外层柔和过渡 + 内层紧贴衣领压影，消除贴纸悬浮感
        using (var outerBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(3f, 0.036f * s)))
        using (var outerShadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(28, 18, 14, 48),
            MaskFilter = outerBlur
        })
        {
            using var pb = new SKPathBuilder();
            pb.MoveTo(-(openWidth + 0.03f) * s, -0.015f * s);
            pb.QuadTo(0f, (dropY + 0.05f) * s, (openWidth + 0.03f) * s, -0.015f * s);
            pb.LineTo((openWidth + 0.22f) * s, 0.20f * s);
            pb.LineTo(-(openWidth + 0.22f) * s, 0.20f * s);
            pb.Close();
            using var path = pb.Detach();
            canvas.DrawPath(path, outerShadow);
        }

        using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1.8f, 0.016f * s));
        using var shadowPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(22, 14, 12, 62),
            MaskFilter = blur
        };
        using var pbInner = new SKPathBuilder();
        pbInner.MoveTo(-openWidth * s, 0.002f * s);
        pbInner.QuadTo(0f, dropY * s, openWidth * s, 0.002f * s);
        pbInner.LineTo((openWidth + 0.16f) * s, 0.14f * s);
        pbInner.LineTo(-(openWidth + 0.16f) * s, 0.14f * s);
        pbInner.Close();
        using var innerPath = pbInner.Detach();
        canvas.DrawPath(innerPath, shadowPaint);
    }

    private static void DrawWhiteShirtBase(SKCanvas canvas, float s, float openVHeight, float halfNeckW)
    {
        using var shirtShader = SKShader.CreateLinearGradient(
            new SKPoint(0f, 0f),
            new SKPoint(0f, 0.7f * s),
            [new SKColor(252, 253, 255), new SKColor(238, 242, 247)],
            [0f, 1f],
            SKShaderTileMode.Clamp);
        using var shirtPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = shirtShader
        };

        using var pb = new SKPathBuilder();
        pb.MoveTo(-halfNeckW * s, -0.02f * s);
        pb.QuadTo(0f, (openVHeight * 0.45f) * s, halfNeckW * s, -0.02f * s);
        pb.LineTo((halfNeckW + 0.18f) * s, 0.30f * s);
        pb.LineTo(0.16f * s, (openVHeight + 0.55f) * s);
        pb.LineTo(-0.16f * s, (openVHeight + 0.55f) * s);
        pb.LineTo(-(halfNeckW + 0.18f) * s, 0.30f * s);
        pb.Close();
        using var path = pb.Detach();
        canvas.DrawPath(path, shirtPaint);
    }

    private static void DrawSilkTie(SKCanvas canvas, float s, SKColor tieLight, SKColor tieDark, float topY, float bottomY)
    {
        // 领带阴影
        using (var tieBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1.5f, 0.012f * s)))
        using (var tieShadow = new SKPaint
        {
            Color = new SKColor(15, 23, 42, 55),
            MaskFilter = tieBlur
        })
        {
            canvas.DrawRect(-0.042f * s, (topY + 0.08f) * s, 0.095f * s, 0.55f * s, tieShadow);
        }

        using var tieShader = SKShader.CreateLinearGradient(
            new SKPoint(-0.06f * s, topY * s),
            new SKPoint(0.06f * s, 0.6f * s),
            [tieDark, tieLight, tieDark],
            [0f, 0.48f, 1f],
            SKShaderTileMode.Clamp);
        using var tiePaint = new SKPaint
        {
            IsAntialias = true,
            Shader = tieShader
        };

        // 领带长身
        using (var pb = new SKPathBuilder())
        {
            pb.MoveTo(-0.032f * s, (topY + 0.07f) * s);
            pb.LineTo(0.032f * s, (topY + 0.07f) * s);
            pb.LineTo(0.058f * s, bottomY * s);
            pb.LineTo(-0.058f * s, bottomY * s);
            pb.Close();
            using var path = pb.Detach();
            canvas.DrawPath(path, tiePaint);
        }

        // 温莎领带结
        using (var pb = new SKPathBuilder())
        {
            pb.MoveTo(-0.048f * s, topY * s);
            pb.QuadTo(0f, (topY - 0.012f) * s, 0.048f * s, topY * s);
            pb.LineTo(0.030f * s, (topY + 0.088f) * s);
            pb.LineTo(-0.030f * s, (topY + 0.088f) * s);
            pb.Close();
            using var path = pb.Detach();
            canvas.DrawPath(path, tiePaint);
        }
    }

    private static void DrawMensShirtCollars(SKCanvas canvas, float s, bool openNoTie = false)
    {
        using var collarShader = SKShader.CreateLinearGradient(
            new SKPoint(0f, -0.03f * s),
            new SKPoint(0f, 0.22f * s),
            [new SKColor(255, 255, 255), new SKColor(235, 240, 246)],
            [0f, 1f],
            SKShaderTileMode.Clamp);
        using var collarFill = new SKPaint
        {
            IsAntialias = true,
            Shader = collarShader
        };
        using var collarEdge = new SKPaint
        {
            Color = new SKColor(195, 205, 218),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.2f, 0.008f * s),
            IsAntialias = true
        };

        foreach (int side in new[] { -1, 1 })
        {
            using var pb = new SKPathBuilder();
            pb.MoveTo(side * 0.165f * s, -0.025f * s);
            pb.QuadTo(side * (openNoTie ? 0.05f : 0.028f) * s, (openNoTie ? 0.08f : 0.045f) * s, side * (openNoTie ? 0.02f : 0.018f) * s, (openNoTie ? 0.20f : 0.095f) * s);
            pb.LineTo(side * 0.145f * s, (openNoTie ? 0.225f : 0.195f) * s);
            pb.LineTo(side * 0.20f * s, 0.065f * s);
            pb.Close();
            using var path = pb.Detach();
            canvas.DrawPath(path, collarFill);
            canvas.DrawPath(path, collarEdge);
        }
    }

    private static void DrawTailoredBlazer(SKCanvas canvas, float s, SKColor coatTop, SKColor coatDark, float notchY, float neckSpread, bool openLapel)
    {
        using var coatShader = SKShader.CreateRadialGradient(
            new SKPoint(0f, 0.18f * s),
            1.15f * s,
            [Lighten(coatTop, 10), coatTop, coatDark],
            [0f, 0.45f, 1f],
            SKShaderTileMode.Clamp);
        using var coatPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = coatShader
        };

        using var lapelShader = SKShader.CreateLinearGradient(
            new SKPoint(-0.3f * s, 0f),
            new SKPoint(0.3f * s, 0.55f * s),
            [Lighten(coatTop, 20), coatTop, coatDark],
            [0f, 0.5f, 1f],
            SKShaderTileMode.Clamp);
        using var lapelPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = lapelShader
        };

        using var lapelDropBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1.6f, 0.015f * s));
        using var lapelDropShadow = new SKPaint
        {
            Color = new SKColor(6, 8, 14, 72),
            IsAntialias = true,
            MaskFilter = lapelDropBlur
        };

        using var shoulderRimPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 26),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.4f, 0.014f * s),
            IsAntialias = true,
            MaskFilter = lapelDropBlur
        };

        using var seamStroke = new SKPaint
        {
            Color = new SKColor(10, 12, 18, 120),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.2f, 0.008f * s),
            IsAntialias = true
        };

        foreach (int side in new[] { -1, 1 })
        {
            // 西装主肩身：向上微贴领缘、向外延伸至完全覆盖原有肩膀、向下到画幅底部之外
            using var bodyBuilder = new SKPathBuilder();
            bodyBuilder.MoveTo(side * neckSpread * s, -0.025f * s);
            bodyBuilder.CubicTo(side * 0.34f * s, 0.05f * s, side * 0.62f * s, 0.13f * s, side * 0.90f * s, 0.34f * s);
            bodyBuilder.LineTo(side * 0.96f * s, 1.55f * s);
            bodyBuilder.LineTo(0f, 1.55f * s);
            bodyBuilder.LineTo(0f, notchY * s);
            bodyBuilder.QuadTo(side * (neckSpread * 0.72f) * s, (notchY * 0.45f) * s, side * neckSpread * s, -0.025f * s);
            bodyBuilder.Close();
            using var bodyPath = bodyBuilder.Detach();
            canvas.DrawPath(bodyPath, coatPaint);

            // 影棚顶光肩线柔和高光棱
            using (var rimBuilder = new SKPathBuilder())
            {
                rimBuilder.MoveTo(side * (neckSpread + 0.05f) * s, 0.01f * s);
                rimBuilder.CubicTo(side * 0.34f * s, 0.065f * s, side * 0.60f * s, 0.145f * s, side * 0.86f * s, 0.34f * s);
                using var rimPath = rimBuilder.Detach();
                canvas.DrawPath(rimPath, shoulderRimPaint);
            }

            // 精致西装翻驳领（带自然投影与立体高光边缘）
            using var lapelBuilder = new SKPathBuilder();
            lapelBuilder.MoveTo(side * neckSpread * s, -0.025f * s);
            lapelBuilder.LineTo(side * (neckSpread + 0.075f) * s, 0.03f * s);
            if (openLapel)
            {
                lapelBuilder.LineTo(side * 0.245f * s, 0.135f * s);
                lapelBuilder.LineTo(side * 0.205f * s, 0.155f * s);
                lapelBuilder.LineTo(side * 0.255f * s, 0.22f * s);
            }
            else
            {
                lapelBuilder.QuadTo(side * 0.265f * s, 0.18f * s, side * 0.22f * s, 0.30f * s);
            }
            lapelBuilder.LineTo(0f, notchY * s);
            lapelBuilder.QuadTo(side * (neckSpread * 0.70f) * s, (notchY * 0.45f) * s, side * neckSpread * s, -0.025f * s);
            lapelBuilder.Close();
            using var lapelPath = lapelBuilder.Detach();

            canvas.Save();
            canvas.Translate(side * 0.008f * s, 0.012f * s);
            canvas.DrawPath(lapelPath, lapelDropShadow);
            canvas.Restore();

            canvas.DrawPath(lapelPath, lapelPaint);
            canvas.DrawPath(lapelPath, seamStroke);
        }
    }

    private static SKColor Lighten(SKColor c, byte delta)
        => new(
            (byte)Math.Min(255, c.Red + delta),
            (byte)Math.Min(255, c.Green + delta),
            (byte)Math.Min(255, c.Blue + delta),
            c.Alpha);

    private static void DrawCustomPngSuit(SKCanvas canvas, float s, string path)
    {
        if (!File.Exists(path)) return;
        using var img = SKImage.FromEncodedData(path);
        if (img == null) return;

        var meta = new CustomSuitMetadata();
        string metadataPath = Path.ChangeExtension(path, ".json");
        if (File.Exists(metadataPath))
        {
            try
            {
                meta = JsonSerializer.Deserialize<CustomSuitMetadata>(File.ReadAllText(metadataPath), JsonOpts) ?? meta;
            }
            catch (JsonException) { }
        }

        DrawNeckContactShadow(canvas, s, openWidth: 0.17f, dropY: 0.30f);
        float drawW = (float)(s * Math.Clamp(meta.WidthScaleFactor, 1.0, 2.8));
        float drawH = drawW * (img.Height / (float)Math.Max(1, img.Width));
        float left = -(float)(Math.Clamp(meta.NeckAnchorXRatio, 0.2, 0.8) * drawW);
        float top = -(float)(Math.Clamp(meta.NeckAnchorYRatio, 0.02, 0.6) * drawH);
        canvas.DrawImage(img, new SKRect(left, top, left + drawW, top + drawH), new SKSamplingOptions(SKCubicResampler.CatmullRom));
    }
}
