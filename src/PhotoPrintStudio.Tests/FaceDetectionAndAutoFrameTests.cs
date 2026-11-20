using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Portable;
using PhotoPrintStudio.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace PhotoPrintStudio.Tests;

public class FaceDetectionAndAutoFrameTests
{
    [Fact]
    public void BundledYuNetAndModNetOnnx_ShouldCoexistAndDetectPortraitWithLandmarks()
    {
        string faceSrc = Path.Combine(AppContext.BaseDirectory, "assets", "models", "face_detection_yunet_2023mar.onnx");
        string matteSrc = Path.Combine(AppContext.BaseDirectory, "assets", "models", PortraitMattingService.ModNetModelFile);
        Assert.True(File.Exists(faceSrc), "Bundled YuNet face detection ONNX should exist");
        Assert.True(File.Exists(matteSrc), "Bundled MODNet matting ONNX should exist");

        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_face_{Guid.NewGuid():N}");
        try
        {
            var paths = new PortablePaths(tempRoot);
            paths.EnsureDirectoriesExist();
            File.Copy(faceSrc, Path.Combine(paths.ModelsDirectory, Path.GetFileName(faceSrc)), true);
            File.Copy(matteSrc, Path.Combine(paths.ModelsDirectory, Path.GetFileName(matteSrc)), true);

            var logger = new AppLogger(paths);
            using var matting = new PortraitMattingService(paths, logger);
            using var faceSvc = new FaceDetectionService(paths, logger);

            // 确认字母序靠前的 face_*.onnx 不会误加载到抠图服务，两者各自识别正确模型
            Assert.True(matting.HasModelLoaded);
            Assert.Equal(PortraitMattingService.ModNetModelFile, matting.ActiveModelName);
            Assert.True(faceSvc.HasModelLoaded);
            Assert.Equal("face_detection_yunet_2023mar.onnx", faceSvc.ActiveModelName);

            using var portrait = CreateStudioPortraitBitmap(900, 1200);
            var faces = faceSvc.DetectFaces(portrait, scoreThreshold: 0.45f);
            Assert.NotEmpty(faces);

            var f = faces[0];
            Assert.True(f.Confidence >= 0.55f);
            Assert.InRange(f.RightEye.X, 340f, 430f);
            Assert.InRange(f.LeftEye.X, 470f, 560f);
            Assert.InRange(f.EyeMidpoint.Y, 440f, 520f);
            Assert.True(f.MouthMidpoint.Y > f.EyeMidpoint.Y + 60f);

            // 验证逆时针偏转 90° 的图配合 QuarterTurns=1 (顺时针转正 90°) 也能正常检出并映射回原图坐标
            using var sideways = RotateClockwise90(portrait, steps: 3);
            var qtFaces = faceSvc.DetectFacesWithQuarterTurns(sideways, quarterTurns: 1, scoreThreshold: 0.45f);
            Assert.NotEmpty(qtFaces);
            Assert.True(qtFaces[0].Width > 100f && qtFaces[0].Height > 100f);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void AutoFrameIdPhoto_ShouldLevelEyesAndAlignHeadToOneInchStandard(int quarterTurns, bool flipH)
    {
        var oneInch = IdPhotoSpec.BuiltInSpecs.First(s => s.Id == "id_1inch"); // 25x35mm, head 19~23, eye 0.43

        // 在直立视图 (effW=1200, effH=1600) 中定义偏斜 6 度且偏心的人脸，再映射回 QuarterTurns 旋转前的原图坐标
        const int effW = 1200, effH = 1600;
        int rawW = (quarterTurns % 2 != 0) ? effH : effW;
        int rawH = (quarterTurns % 2 != 0) ? effW : effH;

        double tiltRad = 6.0 * Math.PI / 180.0;
        var eyeCenter = new SKPoint(690f, 640f);
        var halfEye = new SKPoint((float)(72 * Math.Cos(tiltRad)), (float)(72 * Math.Sin(tiltRad)));
        var uprightRe = new SKPoint(eyeCenter.X - halfEye.X, eyeCenter.Y - halfEye.Y);
        var uprightLe = new SKPoint(eyeCenter.X + halfEye.X, eyeCenter.Y + halfEye.Y);

        SKPoint ToRaw(SKPoint uprightPt)
        {
            double angle = -quarterTurns * Math.PI * 0.5;
            double dx = uprightPt.X - effW * 0.5;
            double dy = uprightPt.Y - effH * 0.5;
            return new SKPoint(
                (float)(rawW * 0.5 + dx * Math.Cos(angle) - dy * Math.Sin(angle)),
                (float)(rawH * 0.5 + dx * Math.Sin(angle) + dy * Math.Cos(angle)));
        }

        SKPoint[] boxCorners =
        [
            ToRaw(new SKPoint(540f, 460f)),
            ToRaw(new SKPoint(840f, 460f)),
            ToRaw(new SKPoint(540f, 820f)),
            ToRaw(new SKPoint(840f, 820f))
        ];
        float bx1 = boxCorners.Min(p => p.X), by1 = boxCorners.Min(p => p.Y);
        float bx2 = boxCorners.Max(p => p.X), by2 = boxCorners.Max(p => p.Y);

        var re = ToRaw(uprightRe);
        var le = ToRaw(uprightLe);

        var face = new DetectedFace(
            X: bx1,
            Y: by1,
            Width: bx2 - bx1,
            Height: by2 - by1,
            RightEye: re,
            LeftEye: le,
            NoseTip: ToRaw(new SKPoint(685f, 700f)),
            RightMouth: ToRaw(new SKPoint(640f, 760f)),
            LeftMouth: ToRaw(new SKPoint(730f, 768f)),
            Confidence: 0.92f);

        var edit = new PhotoEditState
        {
            QuarterTurns = quarterTurns,
            FlipHorizontal = flipH,
            FitMode = FitMode.Cover
        };

        var res = PortraitAutoFrameHelper.ComputeAutoFrame(
            rawW, rawH,
            [face],
            edit,
            oneInch.WidthMm,
            oneInch.HeightMm,
            slotBleedMm: 0.0,
            FitMode.Cover,
            oneInch,
            adjustZoomAndPan: true);

        Assert.True(res.Success);
        edit.RotationDegrees = res.RotationDegrees;
        edit.Zoom = res.Zoom;
        edit.PanX = res.PanX;
        edit.PanY = res.PanY;

        var destRect = new SKRect(0, 0, (float)oneInch.WidthMm, (float)oneInch.HeightMm);
        var pRe = PortraitAutoFrameHelper.TransformRawPointToDestRect(re, rawW, rawH, edit, destRect, FitMode.Cover);
        var pLe = PortraitAutoFrameHelper.TransformRawPointToDestRect(le, rawW, rawH, edit, destRect, FitMode.Cover);

        // 双眼投影后必须在屏幕同一水平线上
        Assert.True(Math.Abs(pRe.Y - pLe.Y) < 0.35f, $"Expected level eyes, got |dY|={Math.Abs(pRe.Y - pLe.Y)}");

        var metrics = PortraitAutoFrameHelper.MeasureCompliance(
            rawW, rawH,
            [face],
            edit,
            oneInch.WidthMm,
            oneInch.HeightMm,
            0.0,
            FitMode.Cover,
            oneInch);

        Assert.NotNull(metrics);
        Assert.True(Math.Abs(metrics!.EyeTiltDegrees) < 0.4, $"Tilt={metrics.EyeTiltDegrees}");
        Assert.InRange(metrics.EstimatedHeadHeightMm, oneInch.HeadHeightMinMm - 0.5, oneInch.HeadHeightMaxMm + 0.5);
        Assert.InRange(metrics.EyeLevelRatioFromTop, oneInch.EyeLineRatioFromTop - 0.04, oneInch.EyeLineRatioFromTop + 0.04);
        Assert.InRange(metrics.HorizontalOffsetMm, -1.5, 1.5);
    }

    [Fact]
    public void AutoFrame_WithEdgeNearFaceAndCoverMode_ShouldNotExposeBlankEdges()
    {
        var oneInch = IdPhotoSpec.BuiltInSpecs.First(s => s.Id == "id_1inch");
        const int rawW = 1000, rawH = 1400;

        // 人物靠右边缘且偏大，检验 Cover 模式安全边界收敛
        var edgeFace = new DetectedFace(
            X: 620f,
            Y: 220f,
            Width: 340f,
            Height: 420f,
            RightEye: new SKPoint(720f, 390f),
            LeftEye: new SKPoint(860f, 405f),
            NoseTip: new SKPoint(790f, 470f),
            RightMouth: new SKPoint(740f, 540f),
            LeftMouth: new SKPoint(840f, 548f),
            Confidence: 0.88f);

        var edit = new PhotoEditState { FitMode = FitMode.Cover };
        var res = PortraitAutoFrameHelper.ComputeAutoFrame(
            rawW, rawH,
            [edgeFace],
            edit,
            oneInch.WidthMm,
            oneInch.HeightMm,
            slotBleedMm: 0.0,
            FitMode.Cover,
            oneInch,
            adjustZoomAndPan: true);

        Assert.True(res.Success);
        Assert.InRange(res.Zoom, 1.0, 4.0);
        Assert.InRange(res.PanX, -1.0, 1.0);
        Assert.InRange(res.PanY, -1.0, 1.0);

        // 验证 destRect 四个角反算回原图均未出界
        double mmPerPx = Math.Max(oneInch.WidthMm / rawW, oneInch.HeightMm / rawH) * res.Zoom;
        double rad = res.RotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(-rad), sin = Math.Sin(-rad);
        double offX = res.PanX * oneInch.WidthMm * 0.5;
        double offY = res.PanY * oneInch.HeightMm * 0.5;

        foreach (double cx in new[] { -oneInch.WidthMm * 0.5, oneInch.WidthMm * 0.5 })
        foreach (double cy in new[] { -oneInch.HeightMm * 0.5, oneInch.HeightMm * 0.5 })
        {
            double px = (cx - offX) / mmPerPx;
            double py = (cy - offY) / mmPerPx;
            double rx = px * cos - py * sin;
            double ry = px * sin + py * cos;
            Assert.True(Math.Abs(rx) <= rawW * 0.5 + 1.5, $"X corner out of bounds: {rx}");
            Assert.True(Math.Abs(ry) <= rawH * 0.5 + 1.5, $"Y corner out of bounds: {ry}");
        }
    }

    private static SKBitmap RotateClockwise90(SKBitmap src, int steps)
    {
        int s = ((steps % 4) + 4) % 4;
        int rw = s % 2 == 0 ? src.Width : src.Height;
        int rh = s % 2 == 0 ? src.Height : src.Width;
        var dst = new SKBitmap(new SKImageInfo(rw, rh, SKColorType.Bgra8888, SKAlphaType.Premul, src.ColorSpace));
        using var canvas = new SKCanvas(dst);
        switch (s)
        {
            case 1: canvas.Translate(src.Height, 0); canvas.RotateDegrees(90); break;
            case 2: canvas.Translate(src.Width, src.Height); canvas.RotateDegrees(180); break;
            case 3: canvas.Translate(0, src.Width); canvas.RotateDegrees(270); break;
        }
        using var srcImg = SKImage.FromBitmap(src);
        canvas.DrawImage(srcImg, 0, 0, SKSamplingOptions.Default);
        return dst;
    }

    private static SKBitmap CreateStudioPortraitBitmap(int w, int h)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(225, 229, 236));

        using var suitPaint = new SKPaint { Color = new SKColor(34, 48, 72), IsAntialias = true };
        using (var pb = new SKPathBuilder())
        {
            pb.MoveTo(110, h);
            pb.CubicTo(120, 860, 280, 800, 450, 800);
            pb.CubicTo(620, 800, 780, 860, 790, h);
            pb.Close();
            using var path = pb.Detach();
            canvas.DrawPath(path, suitPaint);
        }

        using var neckPaint = new SKPaint { Color = new SKColor(225, 180, 152), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(390, 660, 510, 835), 25, 25, neckPaint);

        using var hairPaint = new SKPaint { Color = new SKColor(36, 32, 34), IsAntialias = true };
        canvas.DrawOval(new SKRect(265, 215, 635, 640), hairPaint);

        using var facePaint = new SKPaint { Color = new SKColor(238, 198, 170), IsAntialias = true };
        canvas.DrawOval(new SKRect(295, 280, 605, 695), facePaint);

        canvas.DrawArc(new SKRect(280, 225, 620, 495), 185, 170, true, hairPaint);

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
        return bmp;
    }

    [Fact]
    public void FormalSuitRenderer_ShouldComputeAnatomicalCollarPlacement_AndRenderOnSheet()
    {
        string faceSrc = Path.Combine(AppContext.BaseDirectory, "assets", "models", "face_detection_yunet_2023mar.onnx");
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pps_suit_{Guid.NewGuid():N}");
        try
        {
            var paths = new PortablePaths(tempRoot);
            paths.EnsureDirectoriesExist();
            File.Copy(faceSrc, Path.Combine(paths.ModelsDirectory, Path.GetFileName(faceSrc)), true);

            using var faceSvc = new FaceDetectionService(paths, new AppLogger(paths));
            using var portrait = CreateStudioPortraitBitmap(900, 1200);
            var faces = faceSvc.DetectFaces(portrait, scoreThreshold: 0.45f);
            Assert.NotEmpty(faces);

            var (cx, ny, scale) = FormalSuitRenderer.ComputeSuitPlacement(faces, 900, 1200);
            Assert.InRange(cx, 0.40, 0.60);
            Assert.InRange(ny, 0.50, 0.80);
            Assert.InRange(scale, 0.60, 1.60);

            var asset = new PhotoAsset
            {
                Id = "suit_portrait",
                PixelWidth = 900,
                PixelHeight = 1200,
                EditState = new PhotoEditState
                {
                    FormalSuit = FormalSuitStyle.ClassicWhiteShirt,
                    SuitCenterXRatio = cx,
                    SuitNeckYRatio = ny,
                    SuitScale = scale
                }
            };

            var sheet = new PrintSheet
            {
                PaperWidthMm = 25,
                PaperHeightMm = 35,
                CropMarkStyle = CropMarkStyle.None,
                Slots = [new LayoutSlot { Index = 0, XMm = 0, YMm = 0, WidthMm = 25, HeightMm = 35, PhotoAssetId = asset.Id }]
            };

            using var rendered = SheetRenderer.RenderSheetBitmap(
                sheet,
                new Dictionary<string, PhotoAsset> { [asset.Id] = asset },
                (_, _) => portrait.Copy(),
                new SheetRenderOptions { Dpi = 300, IsScreenPreview = false, ShowSafeMarginOverlay = false, ShowBleedOverlay = false, ShowIdGuidelinesOnSelected = false });

            // 原底图下半部分深色西装区域在叠加正装纯白衬衫后应变为亮白色，而人脸中心应完好保留肤色
            var lowerChestPixel = rendered.GetPixel(rendered.Width / 2, (int)(rendered.Height * 0.90));
            var foreheadPixel = rendered.GetPixel(rendered.Width / 2, (int)(rendered.Height * 0.35));
            Assert.True(lowerChestPixel.Red > 200 && lowerChestPixel.Green > 200 && lowerChestPixel.Blue > 200,
                $"Expected white formal shirt at lower chest, got {lowerChestPixel}");
            Assert.True(foreheadPixel.Red > foreheadPixel.Blue + 30,
                $"Expected intact skin tone at forehead, got {foreheadPixel}");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
