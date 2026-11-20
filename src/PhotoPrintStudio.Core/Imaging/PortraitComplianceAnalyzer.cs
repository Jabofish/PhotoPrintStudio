using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

/// <summary>
/// 证件照人像深度合规体检结果：涵盖姿态偏转、闭眼/张嘴、高低肩、衣着与底色顺色冲突及面部光照失衡检测。
/// </summary>
public sealed record PortraitQualityDiagnostics(
    float YawRatio,
    bool IsHeadTurned,
    float EyeAspectRatio,
    bool AreEyesClosed,
    float MouthAspectRatio,
    bool IsMouthOpen,
    float ShoulderTiltDegrees,
    bool AreShouldersUneven,
    float ClothingBgColorDistance,
    bool HasClothingBackgroundConflict,
    float FaceLightingAsymmetry,
    bool HasUnevenLighting,
    float MeanFaceLuminance,
    bool IsFaceUnderexposed);

public static unsafe class PortraitComplianceAnalyzer
{
    public static PortraitQualityDiagnostics? Analyze(
        SKBitmap source,
        DetectedFace? face,
        CompactAlphaMatte? matte,
        PhotoEditState edit)
    {
        if (source == null || source.Width < 32 || source.Height < 32 || face == null)
            return null;

        int sourceWidth = source.Width;
        int sourceHeight = source.Height;
        int turns = ((edit.QuarterTurns % 4) + 4) % 4;
        using var rotated = turns == 0 ? null : ExifAndColorHelper.RotateQuarterTurnsFast(source, turns);
        var upright = rotated ?? source;
        int w = upright.Width;
        int h = upright.Height;
        if (turns != 0)
            face = face.TransformPoints(pt => FaceDetectionService.InverseRotatePoint(pt, 4 - turns, w, h));

        float SampleAlpha(CompactAlphaMatte alpha, int x, int y)
        {
            var raw = FaceDetectionService.InverseRotatePoint(new SKPoint(x + 0.5f, y + 0.5f), turns, sourceWidth, sourceHeight);
            return SampleCompactAlpha(alpha, sourceWidth, sourceHeight, raw.X, raw.Y);
        }

        // 1. 头部左右偏转（Yaw）、闭眼（EAR）、张嘴（MAR）检测
        float yawRatio = 0f;
        float ear = 0.28f;
        float mar = 0.05f;

        if (face.Mesh is { Points2D.Length: >= 468 } mesh)
        {
            var pts = mesh.Points2D;
            float leftCheekDist = MathF.Abs(pts[454].X - pts[1].X);
            float rightCheekDist = MathF.Abs(pts[1].X - pts[234].X);
            float denom = MathF.Max(1f, leftCheekDist + rightCheekDist);
            yawRatio = MathF.Abs(leftCheekDist - rightCheekDist) / denom;

            float rEyeW = MathF.Max(1f, Dist(pts[33], pts[133]));
            float rEyeH = Dist(pts[159], pts[145]);
            float lEyeW = MathF.Max(1f, Dist(pts[362], pts[263]));
            float lEyeH = Dist(pts[386], pts[374]);
            ear = 0.5f * (rEyeH / rEyeW + lEyeH / lEyeW);

            float mouthW = MathF.Max(1f, Dist(pts[61], pts[291]));
            float mouthH = Dist(pts[13], pts[14]);
            mar = mouthH / mouthW;
        }
        else
        {
            float dLeft = MathF.Abs(face.LeftEye.X - face.NoseTip.X);
            float dRight = MathF.Abs(face.NoseTip.X - face.RightEye.X);
            yawRatio = MathF.Abs(dLeft - dRight) / MathF.Max(1f, dLeft + dRight);
        }

        bool isHeadTurned = yawRatio > 0.20f;
        bool areEyesClosed = face.Mesh != null && ear < 0.145f;
        bool isMouthOpen = face.Mesh != null && mar > 0.22f;

        // 2. 高低肩检测（利用紧凑 Alpha 遮罩扫描下巴下方左右肩线上沿高度）
        float shoulderTiltDeg = 0f;
        bool unevenShoulders = false;
        if (edit.FormalSuit == FormalSuitStyle.None && matte != null && matte.Width >= 16 && matte.Height >= 16)
        {
            float faceCx = face.X + face.Width * 0.5f;
            float chinY = face.BoxBottom;
            float shoulderOffset = face.Width * 0.85f;
            float leftX = Math.Clamp(faceCx + shoulderOffset, w * 0.08f, w * 0.92f);
            float rightX = Math.Clamp(faceCx - shoulderOffset, w * 0.08f, w * 0.92f);

            int scanStartY = Math.Clamp((int)(chinY + face.Height * 0.05f), 0, h - 1);
            int scanEndY = Math.Clamp((int)(chinY + face.Height * 1.15f), scanStartY + 1, h);

            float? leftShoulderY = FindShoulderTopY(matte, (int)leftX, scanStartY, scanEndY);
            float? rightShoulderY = FindShoulderTopY(matte, (int)rightX, scanStartY, scanEndY);
            if (leftShoulderY.HasValue && rightShoulderY.HasValue && leftX - rightX > 16f)
            {
                float dy = leftShoulderY.Value - rightShoulderY.Value;
                shoulderTiltDeg = MathF.Atan2(dy, leftX - rightX) * (180f / MathF.PI);
                unevenShoulders = MathF.Abs(shoulderTiltDeg) > 6.0f && MathF.Abs(dy) > face.Height * 0.08f;
            }
        }

        // 3. 左右脸光照均衡度 + 人脸曝光 + 衣着与新底色顺色冲突检测
        float lightingAsym = 0f;
        float meanFaceLuma = 0.65f;
        float clothBgDist = 1f;
        bool clothConflict = false;

        if (upright.ColorType == SKColorType.Bgra8888)
        {
            byte* ptr = (byte*)upright.GetPixels().ToPointer();
            int stride = upright.RowBytes;

            float fCx = (face.RightEye.X + face.LeftEye.X) * 0.5f;
            int y0 = Math.Clamp((int)(face.Y + face.Height * 0.30f), 0, h - 1);
            int y1 = Math.Clamp((int)(face.Y + face.Height * 0.78f), y0 + 1, h);
            int x0 = Math.Clamp((int)(face.X + face.Width * 0.14f), 0, w - 1);
            int x1 = Math.Clamp((int)(face.X + face.Width * 0.86f), x0 + 1, w);
            int step = Math.Max(1, (int)(face.Width / 40f));

            double sumL = 0, sumR = 0, sumAll = 0;
            int cntL = 0, cntR = 0, cntAll = 0;

            for (int y = y0; y < y1; y += step)
            {
                byte* row = ptr + y * stride;
                for (int x = x0; x < x1; x += step)
                {
                    int o = x * 4;
                    float r = row[o + 2] / 255f;
                    float g = row[o + 1] / 255f;
                    float b = row[o] / 255f;
                    if (!(r > g * 0.90f && g > b * 0.80f && (r - b) > 0.02f)) continue;
                    float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    sumAll += l; cntAll++;
                    if (x > fCx + face.Width * 0.10f) { sumL += l; cntL++; }
                    else if (x < fCx - face.Width * 0.10f) { sumR += l; cntR++; }
                }
            }

            if (cntAll > 10) meanFaceLuma = (float)(sumAll / cntAll);
            if (cntL >= 8 && cntR >= 8)
            {
                float lAvg = (float)(sumL / cntL);
                float rAvg = (float)(sumR / cntR);
                lightingAsym = MathF.Abs(lAvg - rAvg) / MathF.Max(0.15f, MathF.Max(lAvg, rAvg));
            }

            // 衣着与替换底色冲突检测（仅在启用 AI 换底且未叠加正装时检查，防白底穿白衣/蓝底穿蓝衣）
            if (matte != null && edit.EnableAiBackgroundReplace && edit.FormalSuit == FormalSuitStyle.None &&
                SKColor.TryParse(edit.ReplacementBackgroundHex, out var bgColor))
            {
                float bgR = bgColor.Red / 255f, bgG = bgColor.Green / 255f, bgB = bgColor.Blue / 255f;
                int cy0 = Math.Clamp((int)(face.BoxBottom + face.Height * 0.42f), 0, h - 1);
                int cy1 = Math.Clamp((int)(face.BoxBottom + face.Height * 0.95f), cy0 + 1, h);
                int cx0 = Math.Clamp((int)(fCx - face.Width * 1.05f), 0, w - 1);
                int cx1 = Math.Clamp((int)(fCx + face.Width * 1.05f), cx0 + 1, w);
                int cStep = Math.Max(2, (cx1 - cx0) / 28);

                double cR = 0, cG = 0, cB = 0;
                int cCnt = 0;
                for (int y = cy0; y < cy1; y += cStep)
                {
                    byte* row = ptr + y * stride;
                    for (int x = cx0; x < cx1; x += cStep)
                    {
                        if (SampleAlpha(matte, x, y) < 0.88f)
                            continue;
                        int o = x * 4;
                        float r = row[o + 2] / 255f, g = row[o + 1] / 255f, b = row[o] / 255f;
                        // 排除皮肤像素，聚焦衣物区域
                        if (r > g * 1.05f && g > b * 1.05f && (r - b) > 0.09f) continue;
                        cR += r; cG += g; cB += b; cCnt++;
                    }
                }

                if (cCnt >= 10)
                {
                    float avgCr = (float)(cR / cCnt);
                    float avgCg = (float)(cG / cCnt);
                    float avgCb = (float)(cB / cCnt);
                    float dr = avgCr - bgR, dg = avgCg - bgG, db = avgCb - bgB;
                    clothBgDist = MathF.Sqrt(dr * dr + dg * dg + db * db);
                    clothConflict = clothBgDist < 0.24f;
                }
            }

            GC.KeepAlive(upright);
        }

        bool studioActive = edit.IsStudioEnhancementActive();
        bool hasUnevenLighting = !studioActive && lightingAsym > 0.095f;
        bool isUnderexposed = !studioActive && meanFaceLuma < 0.48f;

        return new PortraitQualityDiagnostics(
            yawRatio,
            isHeadTurned,
            ear,
            areEyesClosed,
            mar,
            isMouthOpen,
            shoulderTiltDeg,
            unevenShoulders,
            clothBgDist,
            clothConflict,
            lightingAsym,
            hasUnevenLighting,
            meanFaceLuma,
            isUnderexposed);

        float? FindShoulderTopY(CompactAlphaMatte alpha, int x, int startY, int endY)
        {
            for (int y = startY; y < endY; y += 2)
            {
                if (SampleAlpha(alpha, x, y) >= 0.65f)
                    return y;
            }
            return null;
        }
    }

    private static float SampleCompactAlpha(CompactAlphaMatte matte, int imgW, int imgH, float pixelCenterX, float pixelCenterY)
    {
        float sx = matte.CropX + pixelCenterX * matte.CropW / imgW - 0.5f;
        float sy = matte.CropY + pixelCenterY * matte.CropH / imgH - 0.5f;
        int ix = Math.Clamp((int)MathF.Round(sx), 0, matte.Width - 1);
        int iy = Math.Clamp((int)MathF.Round(sy), 0, matte.Height - 1);
        return matte.Data[iy * matte.Width + ix];
    }

    private static float Dist(SKPoint a, SKPoint b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
