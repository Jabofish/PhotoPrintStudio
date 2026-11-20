using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

public static unsafe partial class FloatImageProcessor
{
    // MediaPipe Face Mesh V2 (478点) 标准解剖区域索引
    private static readonly int[] FaceOvalIndices =
    [
        10, 338, 297, 332, 284, 251, 389, 356, 454, 323, 361, 288,
        397, 365, 379, 378, 400, 377, 152, 148, 176, 149, 150, 136,
        172, 58, 132, 93, 234, 127, 162, 21, 54, 103, 67, 109
    ];

    private static readonly int[] RightEyeIndices =
        [33, 7, 163, 144, 145, 153, 154, 155, 133, 173, 157, 158, 159, 160, 161, 246];

    private static readonly int[] LeftEyeIndices =
        [362, 382, 381, 380, 374, 373, 390, 249, 263, 466, 388, 387, 386, 385, 384, 398];

    private static readonly int[] RightBrowIndices =
        [70, 63, 105, 66, 107, 55, 65, 52, 53, 46];

    private static readonly int[] LeftBrowIndices =
        [300, 293, 334, 296, 336, 285, 295, 282, 283, 276];

    private static readonly int[] OuterLipIndices =
        [61, 185, 40, 39, 37, 0, 267, 269, 270, 409, 291, 375, 321, 405, 314, 17, 84, 181, 91, 146];

    private static readonly int[] RightUnderEyeIndices =
        [33, 7, 163, 144, 145, 153, 154, 155, 133, 243, 112, 26, 22, 23, 24, 110, 25];

    private static readonly int[] LeftUnderEyeIndices =
        [362, 382, 381, 380, 374, 373, 390, 249, 263, 255, 339, 254, 253, 252, 256, 341, 463];

    private static readonly int[] JawlineIndices =
        [172, 136, 150, 149, 176, 148, 152, 377, 400, 378, 379, 365, 397];

    internal readonly struct PortraitMaskSet(
        byte[] skinMask,
        byte[] featureMask,
        byte[] lipMask,
        byte[] underEyeMask,
        int width,
        int height) : IDisposable
    {
        public readonly byte[] SkinMask = skinMask;
        public readonly byte[] FeatureMask = featureMask;
        public readonly byte[] LipMask = lipMask;
        public readonly byte[] UnderEyeMask = underEyeMask;
        public readonly int Width = width;
        public readonly int Height = height;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float SampleMask(byte[] mask, int x, int y, int fullW, int fullH)
        {
            if (Width == fullW && Height == fullH)
                return mask[y * Width + x] * Inv255;

            float sx = (x + 0.5f) * (Width / (float)fullW) - 0.5f;
            float sy = (y + 0.5f) * (Height / (float)fullH) - 0.5f;
            int x0 = Math.Clamp((int)MathF.Floor(sx), 0, Width - 1);
            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, Height - 1);
            int x1 = x0 + 1 < Width ? x0 + 1 : x0;
            int y1 = y0 + 1 < Height ? y0 + 1 : y0;
            float fx = Math.Clamp(sx - x0, 0f, 1f);
            float fy = Math.Clamp(sy - y0, 0f, 1f);
            int r0 = y0 * Width, r1 = y1 * Width;
            float top = mask[r0 + x0] + (mask[r0 + x1] - mask[r0 + x0]) * fx;
            float bot = mask[r1 + x0] + (mask[r1 + x1] - mask[r1 + x0]) * fx;
            return (top + (bot - top) * fy) * Inv255;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(SkinMask);
            ArrayPool<byte>.Shared.Return(FeatureMask);
            ArrayPool<byte>.Shared.Return(LipMask);
            ArrayPool<byte>.Shared.Return(UnderEyeMask);
        }
    }

    internal readonly record struct WhiteWallWbSample(bool Valid, float GainR, float GainG, float GainB);

    /// <summary>
    /// 在换底/外延填充前，从原始背景墙（alpha &lt; 0.08 且低饱和度灰白墙面）采样灰卡白平衡参考。
    /// </summary>
    private static WhiteWallWbSample SampleOriginalWallWhiteBalance(
        float[] rBuf, float[] gBuf, float[] bBuf, float[]? alphaBuf, int w, int h)
    {
        if (alphaBuf == null || w < 16 || h < 16)
            return new WhiteWallWbSample(false, 1f, 1f, 1f);

        int step = Math.Max(1, Math.Min(w, h) / 220);
        double sumR = 0, sumG = 0, sumB = 0;
        int valid = 0, total = 0;

        for (int y = 0; y < h; y += step)
        {
            int row = y * w;
            for (int x = 0; x < w; x += step)
            {
                total++;
                int i = row + x;
                if (alphaBuf[i] > 0.06f) continue;

                float r = rBuf[i], g = gBuf[i], b = bBuf[i];
                float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                if (l < 0.45f || l > 0.96f) continue;

                float maxC = MathF.Max(r, MathF.Max(g, b));
                float minC = MathF.Min(r, MathF.Min(g, b));
                if (maxC - minC > 0.14f) continue; // 排除红墙/蓝墙，仅拿灰白墙做灰卡

                sumR += r;
                sumG += g;
                sumB += b;
                valid++;
            }
        }

        if (valid < Math.Max(24, total * 0.02))
            return new WhiteWallWbSample(false, 1f, 1f, 1f);

        float avgR = (float)(sumR / valid);
        float avgG = (float)(sumG / valid);
        float avgB = (float)(sumB / valid);
        float gray = (avgR + avgG + avgB) / 3f;
        if (gray < 0.1f)
            return new WhiteWallWbSample(false, 1f, 1f, 1f);

        // 温和阻尼（55%），既中和室内暖黄/节能灯偏绿，又不破坏轻微中性墙面氛围
        float gR = Math.Clamp(1f + (gray / MathF.Max(avgR, 0.05f) - 1f) * 0.55f, 0.90f, 1.12f);
        float gG = Math.Clamp(1f + (gray / MathF.Max(avgG, 0.05f) - 1f) * 0.55f, 0.90f, 1.12f);
        float gB = Math.Clamp(1f + (gray / MathF.Max(avgB, 0.05f) - 1f) * 0.55f, 0.90f, 1.12f);
        return new WhiteWallWbSample(true, gR, gG, gB);
    }

    /// <summary>
    /// 基于 478 点 Face Mesh（或 5 点回退）快速光栅化柔和抗锯齿人像分区掩码（工作尺度 ≤ 512px，耗时 &lt; 2ms）：
    /// - SkinMask：面部皮肤 + 柔和颈部（严格镂空眼睛/睫毛/眉毛/嘴唇/鼻孔）
    /// - FeatureMask：双眼 + 眉毛（五官提神）
    /// - LipMask：嘴唇（唇色微润）
    /// - UnderEyeMask：眼下泪沟阴影区（柔光箱轻柔提亮）
    /// </summary>
    internal static PortraitMaskSet? BuildPortraitMasks(DetectedFace? face, int w, int h)
    {
        if (face == null || w < 32 || h < 32) return null;

        int maxDim = Math.Max(w, h);
        float scale = maxDim > 512 ? 512f / maxDim : 1f;
        int mw = Math.Max(16, (int)MathF.Round(w * scale));
        int mh = Math.Max(16, (int)MathF.Round(h * scale));
        int count = mw * mh;

        byte[] skin = ArrayPool<byte>.Shared.Rent(count);
        byte[] feature = ArrayPool<byte>.Shared.Rent(count);
        byte[] lip = ArrayPool<byte>.Shared.Rent(count);
        byte[] underEye = ArrayPool<byte>.Shared.Rent(count);

        try
        {
            var scaledFace = scale < 0.999f ? face.Scale(scale, scale) : face;
            float fw = MathF.Max(12f, scaledFace.Width);
            float fh = MathF.Max(14f, scaledFace.Height);
            float blurSoft = MathF.Max(1.5f, fw * 0.032f);
            float blurCut = MathF.Max(1.0f, fw * 0.018f);

            // 1. 皮肤掩码（面部轮廓 + 颈部，严格镂空五官）
            RenderMaskInto(mw, mh, skin, canvas =>
            {
                using var addBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurSoft);
                using var fillPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, MaskFilter = addBlur };
                using var neckPaint = new SKPaint { Color = new SKColor(255, 255, 255, 195), IsAntialias = true, MaskFilter = addBlur };

                if (scaledFace.Mesh is { Points2D.Length: >= 468 } mesh)
                {
                    DrawClosedPolygon(canvas, mesh.Points2D, FaceOvalIndices, fillPaint);

                    // 沿下颌线向下延伸颈部皮肤区（后续叠加肤色判定防止误伤衣领）
                    using var neckPb = new SKPathBuilder();
                    var j0 = mesh.Points2D[JawlineIndices[0]];
                    neckPb.MoveTo(j0);
                    for (int k = 1; k < JawlineIndices.Length; k++)
                        neckPb.LineTo(mesh.Points2D[JawlineIndices[k]]);
                    var jLast = mesh.Points2D[JawlineIndices[^1]];
                    float neckDrop = fh * 0.45f;
                    neckPb.LineTo(jLast.X - fw * 0.04f, jLast.Y + neckDrop);
                    neckPb.LineTo(j0.X + fw * 0.04f, j0.Y + neckDrop);
                    neckPb.Close();
                    using var neckPath = neckPb.Detach();
                    canvas.DrawPath(neckPath, neckPaint);

                    // 严格镂空双眼、眉毛、嘴唇与鼻翼下缘，确保柔肤绝不糊掉五官与睫毛
                    using var cutBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurCut);
                    using var cutPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true, MaskFilter = cutBlur };
                    DrawExpandedPolygon(canvas, mesh.Points2D, RightEyeIndices, 1.32f, cutPaint);
                    DrawExpandedPolygon(canvas, mesh.Points2D, LeftEyeIndices, 1.32f, cutPaint);
                    DrawExpandedPolygon(canvas, mesh.Points2D, RightBrowIndices, 1.20f, cutPaint);
                    DrawExpandedPolygon(canvas, mesh.Points2D, LeftBrowIndices, 1.20f, cutPaint);
                    DrawExpandedPolygon(canvas, mesh.Points2D, OuterLipIndices, 1.16f, cutPaint);

                    // 鼻孔与鼻底边缘保护
                    if (mesh.Points2D.Length > 327)
                    {
                        float nostrilR = fw * 0.045f;
                        canvas.DrawCircle(mesh.Points2D[98], nostrilR, cutPaint);
                        canvas.DrawCircle(mesh.Points2D[327], nostrilR, cutPaint);
                    }
                }
                else
                {
                    float cx = scaledFace.X + fw * 0.5f;
                    float cy = scaledFace.Y + fh * 0.52f;
                    canvas.DrawOval(cx, cy, fw * 0.46f, fh * 0.50f, fillPaint);
                    canvas.DrawRect(cx - fw * 0.24f, cy + fh * 0.35f, fw * 0.48f, fh * 0.42f, neckPaint);

                    using var cutBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurCut);
                    using var cutPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true, MaskFilter = cutBlur };
                    float eyeR = MathF.Max(fw * 0.13f, scaledFace.EyeDistance * 0.30f);
                    canvas.DrawOval(scaledFace.RightEye.X, scaledFace.RightEye.Y, eyeR, eyeR * 0.75f, cutPaint);
                    canvas.DrawOval(scaledFace.LeftEye.X, scaledFace.LeftEye.Y, eyeR, eyeR * 0.75f, cutPaint);
                    var mm = scaledFace.MouthMidpoint;
                    float mwHalf = MathF.Max(fw * 0.18f, MathF.Abs(scaledFace.LeftMouth.X - scaledFace.RightMouth.X) * 0.65f);
                    canvas.DrawOval(mm.X, mm.Y, mwHalf, fh * 0.08f, cutPaint);
                }
            });

            // 2. 五官提神掩码（双眼 + 眉毛）
            RenderMaskInto(mw, mh, feature, canvas =>
            {
                using var fBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurCut);
                using var fPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, MaskFilter = fBlur };
                if (scaledFace.Mesh is { Points2D.Length: >= 468 } mesh)
                {
                    DrawExpandedPolygon(canvas, mesh.Points2D, RightEyeIndices, 1.24f, fPaint);
                    DrawExpandedPolygon(canvas, mesh.Points2D, LeftEyeIndices, 1.24f, fPaint);
                    DrawExpandedPolygon(canvas, mesh.Points2D, RightBrowIndices, 1.12f, fPaint);
                    DrawExpandedPolygon(canvas, mesh.Points2D, LeftBrowIndices, 1.12f, fPaint);
                }
                else
                {
                    float eyeR = MathF.Max(fw * 0.12f, scaledFace.EyeDistance * 0.26f);
                    canvas.DrawOval(scaledFace.RightEye.X, scaledFace.RightEye.Y, eyeR, eyeR * 0.65f, fPaint);
                    canvas.DrawOval(scaledFace.LeftEye.X, scaledFace.LeftEye.Y, eyeR, eyeR * 0.65f, fPaint);
                }
            });

            // 3. 嘴唇掩码
            RenderMaskInto(mw, mh, lip, canvas =>
            {
                using var lBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurCut * 0.8f);
                using var lPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, MaskFilter = lBlur };
                if (scaledFace.Mesh is { Points2D.Length: >= 468 } mesh)
                {
                    DrawClosedPolygon(canvas, mesh.Points2D, OuterLipIndices, lPaint);
                }
                else
                {
                    var mm = scaledFace.MouthMidpoint;
                    float mwHalf = MathF.Max(fw * 0.16f, MathF.Abs(scaledFace.LeftMouth.X - scaledFace.RightMouth.X) * 0.55f);
                    canvas.DrawOval(mm.X, mm.Y, mwHalf, fh * 0.055f, lPaint);
                }
            });

            // 4. 眼下黑眼圈/泪沟补光掩码
            RenderMaskInto(mw, mh, underEye, canvas =>
            {
                using var uBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurSoft * 1.15f);
                using var uPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, MaskFilter = uBlur };
                if (scaledFace.Mesh is { Points2D.Length: >= 468 } mesh)
                {
                    DrawClosedPolygon(canvas, mesh.Points2D, RightUnderEyeIndices, uPaint);
                    DrawClosedPolygon(canvas, mesh.Points2D, LeftUnderEyeIndices, uPaint);
                    // 扣除眼睛本体防止提亮眼白或睫毛
                    using var cutPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true, MaskFilter = uBlur };
                    DrawExpandedPolygon(canvas, mesh.Points2D, RightEyeIndices, 1.15f, cutPaint);
                    DrawExpandedPolygon(canvas, mesh.Points2D, LeftEyeIndices, 1.15f, cutPaint);
                }
                else
                {
                    float eyeR = MathF.Max(fw * 0.11f, scaledFace.EyeDistance * 0.25f);
                    canvas.DrawOval(scaledFace.RightEye.X, scaledFace.RightEye.Y + eyeR * 0.85f, eyeR * 0.95f, eyeR * 0.42f, uPaint);
                    canvas.DrawOval(scaledFace.LeftEye.X, scaledFace.LeftEye.Y + eyeR * 0.85f, eyeR * 0.95f, eyeR * 0.42f, uPaint);
                }
            });

            return new PortraitMaskSet(skin, feature, lip, underEye, mw, mh);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(skin);
            ArrayPool<byte>.Shared.Return(feature);
            ArrayPool<byte>.Shared.Return(lip);
            ArrayPool<byte>.Shared.Return(underEye);
            throw;
        }
    }

    private static void RenderMaskInto(int mw, int mh, byte[] target, Action<SKCanvas> drawAction)
    {
        using var bmp = new SKBitmap(new SKImageInfo(mw, mh, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Black);
            drawAction(canvas);
        }

        byte* ptr = (byte*)bmp.GetPixels().ToPointer();
        int rowBytes = bmp.RowBytes;
        for (int y = 0; y < mh; y++)
        {
            byte* row = ptr + y * rowBytes;
            int dstOff = y * mw;
            for (int x = 0; x < mw; x++)
                target[dstOff + x] = row[x * 4 + 2]; // Red 通道即灰度掩码 0..255
        }
        GC.KeepAlive(bmp);
    }

    private static void DrawClosedPolygon(SKCanvas canvas, SKPoint[] pts, int[] indices, SKPaint paint)
    {
        using var pb = new SKPathBuilder();
        pb.MoveTo(pts[indices[0]]);
        for (int i = 1; i < indices.Length; i++)
            pb.LineTo(pts[indices[i]]);
        pb.Close();
        using var path = pb.Detach();
        canvas.DrawPath(path, paint);
    }

    private static void DrawExpandedPolygon(SKCanvas canvas, SKPoint[] pts, int[] indices, float scale, SKPaint paint)
    {
        float cx = 0f, cy = 0f;
        for (int i = 0; i < indices.Length; i++)
        {
            cx += pts[indices[i]].X;
            cy += pts[indices[i]].Y;
        }
        cx /= indices.Length;
        cy /= indices.Length;

        using var pb = new SKPathBuilder();
        for (int i = 0; i < indices.Length; i++)
        {
            var p = pts[indices[i]];
            float px = cx + (p.X - cx) * scale;
            float py = cy + (p.Y - cy) * scale;
            if (i == 0) pb.MoveTo(px, py);
            else pb.LineTo(px, py);
        }
        pb.Close();
        using var path = pb.Detach();
        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// 一键影棚级人像自动优化管线（可自由开关与调节强度）：
    /// 1) 双锚点自动白平衡（原白墙灰卡 + 肤色健康度校准）；
    /// 2) 人脸自适应测光曝光 + 保护高光肩部滚降；
    /// 3) 3D 感知左右脸光照对称均衡（消除阴阳脸）+ 眼下蝶形柔光箱补光 + 可选 ONNX 重打光图融合；
    /// 4) 面部油光压制（消除额头/鼻梁/苹果肌油光发白，保留眼神高光）；
    /// 5) 色度降噪 + 478 点精准高低频分离肤质精修（均匀肤色祛暗沉同时保留真实皮肤微纹理）；
    /// 6) 五官局部提神（眼睛/眉毛微反差清晰化 + 嘴唇自然气色微润）。
    /// </summary>
    private static void ApplyStudioPortraitEnhancement(
        float[] rBuf, float[] gBuf, float[] bBuf,
        float[]? alphaBuf,
        int w, int h,
        DetectedFace? face,
        PortraitMaskSet? masks,
        in WhiteWallWbSample wallWb,
        CompactShadingMap? onnxRelight,
        float strength,
        StudioEnhanceMode mode)
    {
        float s = Math.Clamp(strength, 0f, 1f);
        if (s < 0.01f || w < 16 || h < 16) return;

        int pixelCount = w * h;
        int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));

        // === 1. 统计面部肤色亮度与色度基线 ===
        double skinSumR = 0, skinSumG = 0, skinSumB = 0, skinSumLinL = 0;
        int skinCount = 0;
        int step = Math.Max(1, Math.Min(w, h) / 240);

        for (int y = 0; y < h; y += step)
        {
            int row = y * w;
            for (int x = 0; x < w; x += step)
            {
                int i = row + x;
                if (alphaBuf != null && alphaBuf[i] < 0.85f) continue;

                float skinW = masks != null
                    ? masks.Value.SampleMask(masks.Value.SkinMask, x, y, w, h)
                    : 0f;
                float r = rBuf[i], g = gBuf[i], b = bBuf[i];
                bool isSkinColor = r > g * 0.92f && g > b * 0.82f && (r - b) > 0.025f;
                if (masks != null ? (skinW < 0.65f || !isSkinColor) : !isSkinColor) continue;

                float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                if (l < 0.14f || l > 0.92f) continue;

                skinSumR += r;
                skinSumG += g;
                skinSumB += b;
                skinSumLinL += 0.2126f * SrgbToLinear(r) + 0.7152f * SrgbToLinear(g) + 0.0722f * SrgbToLinear(b);
                skinCount++;
            }
        }

        float meanSkinR = skinCount > 20 ? (float)(skinSumR / skinCount) : 0.70f;
        float meanSkinG = skinCount > 20 ? (float)(skinSumG / skinCount) : 0.58f;
        float meanSkinB = skinCount > 20 ? (float)(skinSumB / skinCount) : 0.49f;
        float meanSkinLuma = 0.2126f * meanSkinR + 0.7152f * meanSkinG + 0.0722f * meanSkinB;
        float meanSkinLinL = skinCount > 20 ? (float)(skinSumLinL / skinCount) : SrgbToLinear(0.62f);

        // === 2. 计算双锚点白平衡增益 + 人脸自适应曝光增益 ===
        float wbR = wallWb.Valid ? wallWb.GainR : 1f;
        float wbG = wallWb.Valid ? wallWb.GainG : 1f;
        float wbB = wallWb.Valid ? wallWb.GainB : 1f;

        if (skinCount > 20)
        {
            // 在预估白墙校正后的肤色上做温和健康肤色锚定（祛荧光灯发绿/暗黄气）
            float sR = meanSkinR * wbR;
            float sG = meanSkinG * wbG;
            float sB = meanSkinB * wbB;
            float rbMid = 0.5f * (sR + sB);
            float greenExcess = sG - (rbMid + 0.025f);
            if (greenExcess > 0.005f)
            {
                // 压低轻微偏绿/蜡黄，让肤色更通透红润
                float fix = Math.Clamp(greenExcess * 0.45f, 0f, 0.055f);
                wbG *= (1f - fix);
                wbR *= (1f + fix * 0.45f);
            }
            // 若肤色显著偏冷发青（R-B 过小），给予轻微暖调提气色
            float warmth = sR - sB;
            if (warmth < 0.11f)
            {
                float warmNudge = Math.Clamp((0.11f - warmth) * 0.35f, 0f, 0.045f);
                wbR *= (1f + warmNudge);
                wbB *= (1f - warmNudge * 0.75f);
            }
        }

        // 按强度缩放白平衡并严格限幅
        wbR = Math.Clamp(1f + (wbR - 1f) * s, 0.90f, 1.13f);
        wbG = Math.Clamp(1f + (wbG - 1f) * s, 0.90f, 1.13f);
        wbB = Math.Clamp(1f + (wbB - 1f) * s, 0.90f, 1.13f);

        // 自适应曝光：将面部肤色引导向冲印最佳亮度区间（sRGB ~0.715，线性 ~0.47）
        var (targetSkin, exposureScale, maxExposureRatio, symmetryScale, smoothingScale) = mode switch
        {
            StudioEnhanceMode.Natural => (0.695f, 0.78f, 1.85f, 0.85f, 0.55f),
            StudioEnhanceMode.Rescue => (0.725f, 1.28f, 2.15f, 1.32f, 0.95f),
            _ => (0.715f, 1f, 1.85f, 1f, 1f)
        };
        float targetSkinLin = SrgbToLinear(targetSkin);
        float rawExposureRatio = targetSkinLin / MathF.Max(meanSkinLinL, 0.05f);
        // 偏暗时积极柔和提亮，已偏亮时仅极轻微回拉防死白
        float dampedRatio = rawExposureRatio >= 1f
            ? 1f + (Math.Clamp(rawExposureRatio, 1f, maxExposureRatio) - 1f) * 0.72f * s * exposureScale
            : 1f + (Math.Clamp(rawExposureRatio, 0.80f, 1f) - 1f) * 0.45f * s;

        // === 3. 构建低频光照场（用于去阴阳脸、油光压制与眼下补光）===
        int minDim = Math.Min(w, h);
        int lightDs = minDim > 260 ? Math.Clamp(minDim / 200, 2, 10) : 1;
        int lw = Math.Max(8, (w + lightDs - 1) / lightDs);
        int lh = Math.Max(8, (h + lightDs - 1) / lightDs);
        int lCount = lw * lh;

        float[] lowR = FloatPool.Rent(lCount);
        float[] lowG = FloatPool.Rent(lCount);
        float[] lowB = FloatPool.Rent(lCount);
        float[] tmpR = FloatPool.Rent(lCount);
        float[] tmpG = FloatPool.Rent(lCount);
        float[] tmpB = FloatPool.Rent(lCount);

        try
        {
            DownsampleBox3(rBuf, gBuf, bBuf, w, h, lowR, lowG, lowB, lw, lh, lightDs);
            float faceRefPx = face != null ? MathF.Max(face.Width, face.Height * 0.85f) : minDim * 0.38f;
            int blurRad = Math.Clamp((int)MathF.Round((faceRefPx / lightDs) * 0.14f), 3, 22);

            BoxBlurFloatInto(lowR, tmpR, lCount, lw, lh, blurRad);
            BoxBlurFloatInto(tmpR, lowR, lCount, lw, lh, blurRad);
            BoxBlurFloatInto(lowG, tmpG, lCount, lw, lh, blurRad);
            BoxBlurFloatInto(tmpG, lowG, lCount, lw, lh, blurRad);
            BoxBlurFloatInto(lowB, tmpB, lCount, lw, lh, blurRad);
            BoxBlurFloatInto(tmpB, lowB, lCount, lw, lh, blurRad);

            // 分析左右脸照度不对称度（阴阳脸检测）
            float faceCx = face != null ? (face.RightEye.X + face.LeftEye.X) * 0.5f : w * 0.5f;
            float faceCy = face != null ? (face.EyeMidpoint.Y + face.NoseTip.Y) * 0.5f : h * 0.42f;
            float eyeAngle = face != null
                ? MathF.Atan2(face.LeftEye.Y - face.RightEye.Y, face.LeftEye.X - face.RightEye.X)
                : 0f;
            float cosE = MathF.Cos(eyeAngle);
            float sinE = MathF.Sin(eyeAngle);
            float halfFaceW = MathF.Max(16f, face != null ? face.Width * 0.48f : w * 0.22f);
            float halfFaceH = MathF.Max(20f, face != null ? face.Height * 0.52f : h * 0.26f);

            float asymDarkerDir = 0f; // +1 表示左侧脸颊(u>0)暗，-1 表示右侧脸颊(u<0)暗
            float asymGain = 1f;
            float cheekBaselineL = meanSkinLuma;

            if (face != null)
            {
                double sumLeft = 0, sumRight = 0;
                int cntLeft = 0, cntRight = 0;
                float lScaleX = lw / (float)w;
                float lScaleY = lh / (float)h;

                for (int y = Math.Max(0, (int)(face.Y)); y < Math.Min(h, (int)(face.BoxBottom)); y += step)
                {
                    for (int x = Math.Max(0, (int)(face.X)); x < Math.Min(w, (int)(face.BoxRight)); x += step)
                    {
                        float sw = masks != null ? masks.Value.SampleMask(masks.Value.SkinMask, x, y, w, h) : 1f;
                        if (sw < 0.65f) continue;

                        float dx = x - faceCx;
                        float dy = y - faceCy;
                        float u = (dx * cosE + dy * sinE) / halfFaceW;
                        float v = (-dx * sinE + dy * cosE) / halfFaceH;
                        if (v < -0.15f || v > 0.72f) continue;

                        int lx = Math.Clamp((int)(x * lScaleX), 0, lw - 1);
                        int ly = Math.Clamp((int)(y * lScaleY), 0, lh - 1);
                        int li = ly * lw + lx;
                        float ll = 0.2126f * lowR[li] + 0.7152f * lowG[li] + 0.0722f * lowB[li];

                        if (u >= 0.18f && u <= 0.78f) { sumLeft += ll; cntLeft++; }
                        else if (u <= -0.18f && u >= -0.78f) { sumRight += ll; cntRight++; }
                    }
                }

                if (cntLeft >= 12 && cntRight >= 12)
                {
                    float lLeft = (float)(sumLeft / cntLeft);
                    float lRight = (float)(sumRight / cntRight);
                    cheekBaselineL = 0.5f * (lLeft + lRight);
                    float diff = MathF.Abs(lLeft - lRight);
                    if (diff > 0.018f)
                    {
                        asymDarkerDir = lLeft < lRight ? 1f : -1f;
                        float brightSide = MathF.Max(lLeft, lRight);
                        float darkSide = MathF.Max(0.10f, MathF.Min(lLeft, lRight));
                        // 在线性光域按比例补齐暗侧，保留 25% 立体感避免变平
                        float linRatio = SrgbToLinear(Math.Clamp(brightSide, 0.05f, 0.92f)) /
                                         MathF.Max(0.015f, SrgbToLinear(Math.Clamp(darkSide, 0.05f, 0.92f)));
                        asymGain = 1f + (Math.Clamp(linRatio, 1f, 1.85f) - 1f) * 0.72f * s * symmetryScale;
                    }
                }
            }

            // === 4. 应用白平衡 + 自适应曝光 + 3D去阴阳脸/蝶形柔光/ONNX重打光 + 油光压制 ===
            float scaleLx = lw / (float)w;
            float scaleLy = lh / (float)h;
            bool hasAsym = asymGain > 1.008f;
            bool hasOnnxRelight = onnxRelight != null;
            float relightScaleX = hasOnnxRelight ? onnxRelight!.CropW / Math.Max(1, w) : 1f;
            float relightScaleY = hasOnnxRelight ? onnxRelight!.CropH / Math.Max(1, h) : 1f;

            Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    float syL = (y + 0.5f) * scaleLy - 0.5f;
                    int ly0 = Math.Clamp((int)MathF.Floor(syL), 0, lh - 1);
                    int ly1 = ly0 + 1 < lh ? ly0 + 1 : ly0;
                    float fyL = Math.Clamp(syL - ly0, 0f, 1f);
                    int lr0 = ly0 * lw, lr1 = ly1 * lw;
                    int rowOffset = y * w;

                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOffset + x;
                        // 无换底时也可对全图生效；有换底时外区已做前景外延，全图处理保持边缘连贯
                        float r = rBuf[i], g = gBuf[i], b = bBuf[i];

                        float sxL = (x + 0.5f) * scaleLx - 0.5f;
                        int lx0 = Math.Clamp((int)MathF.Floor(sxL), 0, lw - 1);
                        int lx1 = lx0 + 1 < lw ? lx0 + 1 : lx0;
                        float fxL = Math.Clamp(sxL - lx0, 0f, 1f);

                        float locR = Bilerp(lowR, lr0, lr1, lx0, lx1, fxL, fyL);
                        float locG = Bilerp(lowG, lr0, lr1, lx0, lx1, fxL, fyL);
                        float locB = Bilerp(lowB, lr0, lr1, lx0, lx1, fxL, fyL);
                        float locL = 0.2126f * locR + 0.7152f * locG + 0.0722f * locB;

                        float skinW = masks != null ? masks.Value.SampleMask(masks.Value.SkinMask, x, y, w, h) : 0f;
                        float underEyeW = masks != null ? masks.Value.SampleMask(masks.Value.UnderEyeMask, x, y, w, h) : 0f;

                        // (a) 面部油光压制（在曝光提亮前压低闪光灯/顶光白斑，恢复周围自然肤色）
                        if (skinW > 0.15f)
                        {
                            float curL = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                            float excessL = curL - MathF.Max(locL + 0.020f, meanSkinLuma + 0.048f);
                            if (excessL > 0.004f)
                            {
                                float curChroma = MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));
                                float locChroma = MathF.Max(locR, MathF.Max(locG, locB)) - MathF.Min(locR, MathF.Min(locG, locB));
                                float desatFactor = 1f - Math.Clamp(curChroma / MathF.Max(locChroma, 0.05f), 0f, 1f);
                                float specW = SmoothStep(0.004f, 0.09f, excessL) * (0.48f + 0.52f * desatFactor) * skinW * s * 0.78f;
                                if (specW > 0.001f)
                                {
                                    float targetR = Math.Clamp(locR * 1.03f, 0f, 1f);
                                    float targetG = Math.Clamp(locG * 1.03f, 0f, 1f);
                                    float targetB = Math.Clamp(locB * 1.03f, 0f, 1f);
                                    r += (targetR - r) * specW;
                                    g += (targetG - g) * specW;
                                    b += (targetB - b) * specW;
                                }
                            }
                        }

                        // (b) 计算该像素的总光照乘性增益（自适应曝光 + 去阴阳脸 + 3D正面柔光 + 眼下补光 + ONNX重打光）
                        float totalLightGain = dampedRatio;

                        if (face != null)
                        {
                            float dx = x - faceCx;
                            float dy = y - faceCy;
                            float u = (dx * cosE + dy * sinE) / halfFaceW;
                            float v = (-dx * sinE + dy * cosE) / halfFaceH;

                            // 左右脸去阴阳脸平滑补光
                            if (hasAsym)
                            {
                                float env = MathF.Exp(-0.5f * ((u / 1.25f) * (u / 1.25f) + ((v - 0.25f) / 1.45f) * ((v - 0.25f) / 1.45f)));
                                if (env > 0.01f)
                                {
                                    float sideW = SmoothStep(-0.18f, 0.58f, asymDarkerDir * u);
                                    totalLightGain *= 1f + (asymGain - 1f) * sideW * env;
                                }
                            }

                            // 3D 影棚正面蝶形微雕光（柔和提亮面部正面中心区，消除平光沉闷感）
                            float centerBell = MathF.Exp(-0.5f * ((u / 0.85f) * (u / 0.85f) + ((v - 0.08f) / 0.95f) * ((v - 0.08f) / 0.95f)));
                            totalLightGain *= 1f + 0.055f * s * centerBell;

                            // 眼下黑眼圈/泪沟柔和反射板补光
                            if (underEyeW > 0.05f)
                            {
                                float shadowDip = MathF.Max(0f, cheekBaselineL - locL);
                                float fillBoost = (0.06f + Math.Clamp(shadowDip * 1.4f, 0f, 0.18f)) * underEyeW * s;
                                totalLightGain *= 1f + fillBoost;
                            }

                            // 颈部/下颌阴影轻柔提亮（仅作用于低亮肤色区域）
                            if (v > 0.65f && v < 1.85f && MathF.Abs(u) < 0.95f && r > g * 0.90f && g > b * 0.80f)
                            {
                                float neckShadow = MathF.Max(0f, cheekBaselineL * 0.88f - locL);
                                if (neckShadow > 0.01f)
                                {
                                    float neckEnv = SmoothStep(0.65f, 0.95f, v) * (1f - SmoothStep(1.45f, 1.85f, v));
                                    totalLightGain *= 1f + Math.Clamp(neckShadow * 1.1f, 0f, 0.16f) * neckEnv * s;
                                }
                            }
                        }

                        // 若加载了外部 ONNX 人像重打光模型，则融合其低频照度增益比
                        if (hasOnnxRelight)
                        {
                            float onnxGain = SampleCompactShading(onnxRelight!, x, y, relightScaleX, relightScaleY);
                            totalLightGain *= 1f + (onnxGain - 1f) * (0.55f * s);
                        }

                        // (c) 线性光应用白平衡与光照增益，并做高光肩部滚降防止白衣/额头死白过曝
                        float linR = SrgbToLinear(Math.Clamp(r, 0f, 1f));
                        float linG = SrgbToLinear(Math.Clamp(g, 0f, 1f));
                        float linB = SrgbToLinear(Math.Clamp(b, 0f, 1f));
                        float linL = 0.2126f * linR + 0.7152f * linG + 0.0722f * linB;

                        // 高光肩部保护：当原始亮度已接近高光时平滑衰减向上增益
                        float effectiveGain = totalLightGain;
                        if (effectiveGain > 1f)
                        {
                            float hiProtect = 1f - SmoothStep(0.62f, 0.96f, linL) * 0.78f;
                            effectiveGain = 1f + (effectiveGain - 1f) * hiProtect;
                        }

                        // 轻微暗部柔和抬升（保持黑发黑西装扎实不发灰）
                        float shadowLift = 1f + 0.06f * s * (1f - linL) * (1f - linL) * SmoothStep(0.015f, 0.12f, linL);
                        float gFinal = effectiveGain * shadowLift;

                        rBuf[i] = LinearToSrgb(Math.Clamp(linR * wbR * gFinal, 0f, 1f));
                        gBuf[i] = LinearToSrgb(Math.Clamp(linG * wbG * gFinal, 0f, 1f));
                        bBuf[i] = LinearToSrgb(Math.Clamp(linB * wbB * gFinal, 0f, 1f));
                    }
                }
            });
        }
        finally
        {
            FloatPool.Return(tmpB); FloatPool.Return(tmpG); FloatPool.Return(tmpR);
            FloatPool.Return(lowB); FloatPool.Return(lowG); FloatPool.Return(lowR);
        }

        // === 5. 色度降噪 + 478 点精准高低频分离肤质精修 + 五官局部提神 ===
        ApplyStudioFrequencyRetouchAndFeatures(rBuf, gBuf, bBuf, w, h, face, masks, s, smoothingScale);
    }

    private static void ApplyStudioFrequencyRetouchAndFeatures(
        float[] rBuf, float[] gBuf, float[] bBuf,
        int w, int h,
        DetectedFace? face,
        PortraitMaskSet? masks,
        float strength, float smoothingScale)
    {
        int pixelCount = w * h;
        int minDim = Math.Min(w, h);
        float faceW = face != null ? MathF.Max(24f, face.Width) : minDim * 0.35f;

        // 细尺度模糊（用于分离高频皮肤毛孔纹理 + 五官微反差提神）
        int fineRad = Math.Clamp((int)MathF.Round(faceW * 0.016f), 2, 10);
        // 宽尺度模糊（用于均匀肤色低频色斑/泛红/暗沉）
        int midRad = Math.Clamp((int)MathF.Round(faceW * 0.036f), 4, 18);

        float[] fineR = FloatPool.Rent(pixelCount);
        float[] fineG = FloatPool.Rent(pixelCount);
        float[] fineB = FloatPool.Rent(pixelCount);
        float[] midR = FloatPool.Rent(pixelCount);
        float[] midG = FloatPool.Rent(pixelCount);
        float[] midB = FloatPool.Rent(pixelCount);

        try
        {
            BoxBlurFloatInto(rBuf, fineR, pixelCount, w, h, fineRad);
            BoxBlurFloatInto(gBuf, fineG, pixelCount, w, h, fineRad);
            BoxBlurFloatInto(bBuf, fineB, pixelCount, w, h, fineRad);

            BoxBlurFloatInto(fineR, midR, pixelCount, w, h, midRad);
            BoxBlurFloatInto(fineG, midG, pixelCount, w, h, midRad);
            BoxBlurFloatInto(fineB, midB, pixelCount, w, h, midRad);

            int rowChunk = Math.Max(8, h / (PixelParallelism.MaxDegreeOfParallelism * 4));
            float chromaDenoise = 0.45f * strength;
            float skinSmoothAmt = 0.56f * strength * smoothingScale;
            float eyeCrispAmt = 0.28f * strength;
            float lipBoostAmt = 0.10f * strength;

            Parallel.ForEach(Partitioner.Create(0, h, rowChunk), PixelParallelism, range =>
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    int rowOffset = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOffset + x;
                        float r = rBuf[i], g = gBuf[i], b = bBuf[i];
                        float fr = fineR[i], fg = fineG[i], fb = fineB[i];
                        float mr = midR[i], mg = midG[i], mb = midB[i];

                        // (a) 轻微全人像色度降噪（仅平滑 Cb/Cr 彩噪，100% 保留亮度清晰度）
                        float origL = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                        float fineL = 0.2126f * fr + 0.7152f * fg + 0.0722f * fb;
                        float diffFine = (MathF.Abs(r - fr) + MathF.Abs(g - fg) + MathF.Abs(b - fb)) * 0.333f;
                        float flatKeep = 1f - SmoothStep(0.02f, 0.11f, diffFine);
                        float cMix = chromaDenoise * flatKeep;
                        if (cMix > 0.001f)
                        {
                            // 把色差 (R-L, G-L, B-L) 向 fine 的色差靠拢，保持亮度 origL 绝对不变
                            float cr = (r - origL) + ((fr - fineL) - (r - origL)) * cMix;
                            float cg = (g - origL) + ((fg - fineL) - (g - origL)) * cMix;
                            float cb = (b - origL) + ((fb - fineL) - (b - origL)) * cMix;
                            r = Math.Clamp(origL + cr, 0f, 1f);
                            g = Math.Clamp(origL + cg, 0f, 1f);
                            b = Math.Clamp(origL + cb, 0f, 1f);
                        }

                        // (b) 478 点精准高低频分离磨皮（仅作用于皮肤区，严格避开双眼/眉毛/嘴唇/头发）
                        float skinW = masks != null ? masks.Value.SampleMask(masks.Value.SkinMask, x, y, w, h) : 0f;
                        bool skinLike = r > g * 0.90f && g > b * 0.80f && (r - b) > 0.02f;
                        float effectiveSkin = masks != null
                            ? (skinLike ? skinW : skinW * 0.15f)
                            : (skinLike ? 0.55f : 0f);

                        if (effectiveSkin > 0.02f)
                        {
                            float diffMid = (MathF.Abs(r - mr) + MathF.Abs(g - mg) + MathF.Abs(b - mb)) * 0.333f;
                            // 强边缘保护（发际线/镜框/耳廓轮廓绝不糊）
                            float edgeKeep = 1f - SmoothStep(0.015f, 0.095f, diffMid);
                            float lowMix = skinSmoothAmt * effectiveSkin * edgeKeep;
                            if (lowMix > 0.001f)
                            {
                                // 高低频分离：用平滑低频取代暗沉色斑，同时按 70% 比例回填真实高频皮肤毛孔质感
                                float highL = origL - fineL;
                                float baseR = fr + (mr - fr) * lowMix;
                                float baseG = fg + (mg - fg) * lowMix;
                                float baseB = fb + (mb - fb) * lowMix;

                                float texKeep = 1f - lowMix * 0.30f; // 保留 70%~100% 皮肤微纹理，绝不塑料假面
                                float targetR = baseR + highL * texKeep;
                                float targetG = baseG + highL * texKeep;
                                float targetB = baseB + highL * texKeep;

                                r = Math.Clamp(r + (targetR - r) * lowMix, 0f, 1f);
                                g = Math.Clamp(g + (targetG - g) * lowMix, 0f, 1f);
                                b = Math.Clamp(b + (targetB - b) * lowMix, 0f, 1f);
                            }
                        }

                        // (c) 五官局部提神（眼睛/睫毛/眉毛微清晰化，提升证件照精气神）
                        if (masks != null)
                        {
                            float featW = masks.Value.SampleMask(masks.Value.FeatureMask, x, y, w, h);
                            if (featW > 0.04f)
                            {
                                float dl = origL - fineL;
                                if (MathF.Abs(dl) > 0.008f)
                                {
                                    float boost = dl * eyeCrispAmt * featW;
                                    r = Math.Clamp(r + boost, 0f, 1f);
                                    g = Math.Clamp(g + boost, 0f, 1f);
                                    b = Math.Clamp(b + boost, 0f, 1f);
                                }
                            }

                            // (d) 嘴唇自然气色微润（避免苍白无血色）
                            float lipW = masks.Value.SampleMask(masks.Value.LipMask, x, y, w, h);
                            if (lipW > 0.05f)
                            {
                                float ll = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                                float satBoost = 1f + lipBoostAmt * 1.4f * lipW;
                                float nr = ll + (r - ll) * satBoost + 0.012f * strength * lipW;
                                float ng = ll + (g - ll) * satBoost - 0.004f * strength * lipW;
                                float nb = ll + (b - ll) * satBoost;
                                r = Math.Clamp(nr, 0f, 1f);
                                g = Math.Clamp(ng, 0f, 1f);
                                b = Math.Clamp(nb, 0f, 1f);
                            }
                        }

                        rBuf[i] = r;
                        gBuf[i] = g;
                        bBuf[i] = b;
                    }
                }
            });
        }
        finally
        {
            FloatPool.Return(midB); FloatPool.Return(midG); FloatPool.Return(midR);
            FloatPool.Return(fineB); FloatPool.Return(fineG); FloatPool.Return(fineR);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleCompactShading(CompactShadingMap map, int x, int y, float scaleX, float scaleY)
    {
        float sx = map.CropX + (x + 0.5f) * scaleX - 0.5f;
        float sy = map.CropY + (y + 0.5f) * scaleY - 0.5f;
        int x0 = Math.Clamp((int)MathF.Floor(sx), 0, map.Width - 1);
        int y0 = Math.Clamp((int)MathF.Floor(sy), 0, map.Height - 1);
        int x1 = x0 + 1 < map.Width ? x0 + 1 : x0;
        int y1 = y0 + 1 < map.Height ? y0 + 1 : y0;
        float fx = Math.Clamp(sx - x0, 0f, 1f);
        float fy = Math.Clamp(sy - y0, 0f, 1f);
        int r0 = y0 * map.Width;
        int r1 = y1 * map.Width;
        return Bilerp(map.GainData, r0, r1, x0, x1, fx, fy);
    }
}
