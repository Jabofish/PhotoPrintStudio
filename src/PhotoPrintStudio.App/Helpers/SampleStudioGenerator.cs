using System.IO;
using PhotoPrintStudio.Core.Portable;
using SkiaSharp;

namespace PhotoPrintStudio.App.Helpers;

/// <summary>
/// 生成用于快速体验全部排版与调色功能的本地示例照片（不占用程序安装包体积）。
/// </summary>
public static class SampleStudioGenerator
{
    public static List<string> EnsureSampleImages(PortablePaths paths)
    {
        string dir = Path.Combine(paths.DataDirectory, "samples");
        Directory.CreateDirectory(dir);

        string idSample = Path.Combine(dir, "sample_id_portrait.png");
        string scenicSample = Path.Combine(dir, "sample_sunset_polaroid.png");
        string coupleSample = Path.Combine(dir, "sample_citywalk_3r.png");

        if (!File.Exists(idSample)) GenerateIdPortraitSample(idSample);
        if (!File.Exists(scenicSample)) GeneratePolaroidScenicSample(scenicSample);
        if (!File.Exists(coupleSample)) GenerateCityWalkSample(coupleSample);

        return [idSample, scenicSample, coupleSample];
    }

    private static void GenerateIdPortraitSample(string path)
    {
        const int w = 900, h = 1200;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(bmp);

        // 略微偏灰的“在家白墙”背景，方便测试白墙提纯 & 调色功能
        using var bgShader = SKShader.CreateRadialGradient(
            new SKPoint(w * 0.5f, h * 0.38f),
            w * 0.85f,
            [new SKColor(238, 241, 246), new SKColor(206, 212, 222)],
            SKShaderTileMode.Clamp);
        using (var p = new SKPaint { Shader = bgShader })
            canvas.DrawRect(0, 0, w, h, p);

        // 商务深蓝西装肩膀
        using (var suitPaint = new SKPaint { Color = new SKColor(34, 48, 72), IsAntialias = true })
        {
            using var pb = new SKPathBuilder();
            pb.MoveTo(110, h);
            pb.CubicTo(120, 860, 280, 800, 450, 800);
            pb.CubicTo(620, 800, 780, 860, 790, h);
            pb.Close();
            using var suitPath = pb.Detach();
            canvas.DrawPath(suitPath, suitPaint);
        }

        // 白衬衫领口
        using (var shirtPaint = new SKPaint { Color = new SKColor(246, 248, 252), IsAntialias = true })
        {
            using var pb = new SKPathBuilder();
            pb.MoveTo(355, 802);
            pb.LineTo(450, 965);
            pb.LineTo(545, 802);
            pb.Close();
            using var shirtPath = pb.Detach();
            canvas.DrawPath(shirtPath, shirtPaint);
        }

        // 颈部与头部皮肤
        using (var neckPaint = new SKPaint { Color = new SKColor(225, 180, 152), IsAntialias = true })
        {
            canvas.DrawRoundRect(new SKRect(390, 660, 510, 835), 25, 25, neckPaint);
        }

        // 头发后轮廓
        using (var hairPaint = new SKPaint { Color = new SKColor(36, 32, 34), IsAntialias = true })
        {
            canvas.DrawOval(new SKRect(265, 215, 635, 640), hairPaint);
        }

        // 脸庞
        using var faceShader = SKShader.CreateRadialGradient(
            new SKPoint(430, 445),
            210,
            [new SKColor(244, 206, 178), new SKColor(220, 174, 146)],
            SKShaderTileMode.Clamp);
        using (var facePaint = new SKPaint { Shader = faceShader, IsAntialias = true })
        {
            canvas.DrawOval(new SKRect(295, 280, 605, 695), facePaint);
        }

        // 刘海与头发轮廓
        using (var hairPaint = new SKPaint { Color = new SKColor(34, 30, 32), IsAntialias = true })
        {
            canvas.DrawArc(new SKRect(280, 225, 620, 495), 185, 170, true, hairPaint);
            canvas.DrawOval(new SKRect(268, 445, 304, 545), new SKPaint { Color = new SKColor(232, 188, 160), IsAntialias = true });
            canvas.DrawOval(new SKRect(596, 445, 632, 545), new SKPaint { Color = new SKColor(232, 188, 160), IsAntialias = true });
        }

        // 眉毛、眼睛、鼻影、微笑
        using var browPaint = new SKPaint { Color = new SKColor(42, 36, 36), StrokeWidth = 11, StrokeCap = SKStrokeCap.Round, Style = SKPaintStyle.Stroke, IsAntialias = true };
        canvas.DrawLine(348, 430, 412, 424, browPaint);
        canvas.DrawLine(488, 424, 552, 430, browPaint);

        using var eyeWhite = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var irisPaint = new SKPaint { Color = new SKColor(48, 36, 32), IsAntialias = true };
        canvas.DrawOval(new SKRect(352, 462, 410, 488), eyeWhite);
        canvas.DrawOval(new SKRect(490, 462, 548, 488), eyeWhite);
        canvas.DrawCircle(381, 475, 11, irisPaint);
        canvas.DrawCircle(519, 475, 11, irisPaint);

        using var lipPaint = new SKPaint { Color = new SKColor(192, 114, 108), StrokeWidth = 12, StrokeCap = SKStrokeCap.Round, Style = SKPaintStyle.Stroke, IsAntialias = true };
        canvas.DrawArc(new SKRect(402, 565, 498, 608), 15, 150, false, lipPaint);

        SavePng(bmp, path);
    }

    private static void GeneratePolaroidScenicSample(string path)
    {
        const int w = 1200, h = 1500;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(bmp);

        using var sky = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, h * 0.65f),
            [new SKColor(38, 70, 128), new SKColor(235, 134, 96), new SKColor(251, 206, 132)],
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, w, h, new SKPaint { Shader = sky });

        // 夕阳
        canvas.DrawCircle(w * 0.62f, h * 0.44f, 110, new SKPaint { Color = new SKColor(255, 238, 175), IsAntialias = true });

        // 远山剪影
        using var pb1 = new SKPathBuilder();
        pb1.MoveTo(0, h * 0.68f);
        pb1.LineTo(280, h * 0.50f);
        pb1.LineTo(580, h * 0.62f);
        pb1.LineTo(890, h * 0.47f);
        pb1.LineTo(w, h * 0.60f);
        pb1.LineTo(w, h);
        pb1.LineTo(0, h);
        pb1.Close();
        using (var path1 = pb1.Detach())
            canvas.DrawPath(path1, new SKPaint { Color = new SKColor(46, 56, 86), IsAntialias = true });

        // 湖面倒影
        using var water = SKShader.CreateLinearGradient(
            new SKPoint(0, h * 0.65f), new SKPoint(0, h),
            [new SKColor(72, 86, 126), new SKColor(28, 38, 64)],
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, h * 0.66f, w, h * 0.34f, new SKPaint { Shader = water });

        using var reflect = new SKPaint { Color = new SKColor(255, 212, 140, 130), StrokeWidth = 8, StrokeCap = SKStrokeCap.Round };
        for (int i = 0; i < 9; i++)
        {
            float y = h * 0.69f + i * 36;
            float rw = 160 - i * 12;
            canvas.DrawLine(w * 0.62f - rw, y, w * 0.62f + rw, y, reflect);
        }

        SavePng(bmp, path);
    }

    private static void GenerateCityWalkSample(string path)
    {
        const int w = 1200, h = 1600;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(bmp);

        using var sky = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, h),
            [new SKColor(56, 142, 214), new SKColor(168, 214, 242), new SKColor(246, 224, 198)],
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, w, h, new SKPaint { Shader = sky });

        // 复古建筑轮廓与暖色街灯
        canvas.DrawRect(0, h * 0.56f, 440, h * 0.44f, new SKPaint { Color = new SKColor(196, 128, 96) });
        canvas.DrawRect(460, h * 0.46f, 380, h * 0.54f, new SKPaint { Color = new SKColor(218, 168, 122) });
        canvas.DrawRect(860, h * 0.60f, 340, h * 0.40f, new SKPaint { Color = new SKColor(92, 136, 152) });

        // 地面与阴影
        canvas.DrawRect(0, h * 0.82f, w, h * 0.18f, new SKPaint { Color = new SKColor(72, 76, 86) });
        canvas.DrawCircle(w * 0.72f, h * 0.22f, 80, new SKPaint { Color = new SKColor(255, 250, 232, 220), IsAntialias = true });

        SavePng(bmp, path);
    }

    private static void SavePng(SKBitmap bmp, string path)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }
}
