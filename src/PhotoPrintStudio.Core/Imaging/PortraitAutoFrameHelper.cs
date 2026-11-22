using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

public sealed record FaceAutoFrameResult(
    bool Success,
    string Message,
    double RotationDegrees,
    double Zoom,
    double PanX,
    double PanY,
    int FaceCount,
    double EstimatedHeadHeightMm,
    double EyeTiltBeforeDeg,
    bool WasZoomClampedMin,
    bool WasPanClamped);

public sealed record FaceComplianceMetrics(
    int FaceCount,
    double EyeTiltDegrees,
    double EyeLevelRatioFromTop,
    double EstimatedHeadHeightMm,
    double HorizontalOffsetMm,
    bool IsHeadHeightCompliant,
    bool IsLevelCompliant,
    bool IsCenteredCompliant);

/// <summary>
/// 人像与五官构图几何引擎：与 SheetRenderer.DrawFittedImage 共享精确一致的仿射投影矩阵，
/// 提供双眼自动水平校正、国标/签证证件照头高与眼位智能构图、普通人像黄金分割居中及实时合规检测。
/// </summary>
public static class PortraitAutoFrameHelper
{
    public static (double TrimWmm, double TrimHmm, double BleedMm) ResolveUprightSlotPhotoFrameMm(LayoutSlot slot)
    {
        bool swapped = slot.IsQuarterTurnRotated;
        double w = swapped ? slot.HeightMm : slot.WidthMm;
        double h = swapped ? slot.WidthMm : slot.HeightMm;

        if (slot.PolaroidStyle != null)
        {
            w = Math.Max(4.0, w - slot.PolaroidStyle.MarginLeftMm - slot.PolaroidStyle.MarginRightMm);
            h = Math.Max(4.0, h - slot.PolaroidStyle.MarginTopMm - slot.PolaroidStyle.MarginBottomMm);
            return (w, h, 0.0);
        }

        return (Math.Max(4.0, w), Math.Max(4.0, h), Math.Max(0.0, slot.BleedMm));
    }

    public static DetectedFace? SelectFramingTarget(
        IReadOnlyList<DetectedFace> faces,
        int imgW,
        int imgH,
        IdPhotoSpec? idSpec = null)
    {
        if (faces == null || faces.Count == 0 || imgW <= 0 || imgH <= 0)
            return null;

        // 结婚登记照等双人横版规范：检测到两张主脸时联合取外接构图
        if (idSpec?.Id == "id_marriage" && faces.Count >= 2)
        {
            var topTwo = faces
                .OrderByDescending(f => f.Width * f.Height * f.Confidence)
                .Take(2)
                .ToList();

            float a0 = topTwo[0].Width * topTwo[0].Height;
            float a1 = topTwo[1].Width * topTwo[1].Height;
            if (a1 >= a0 * 0.35f && topTwo[1].Confidence >= 0.40f)
            {
                var left = topTwo[0].EyeMidpoint.X <= topTwo[1].EyeMidpoint.X ? topTwo[0] : topTwo[1];
                var right = ReferenceEquals(left, topTwo[0]) ? topTwo[1] : topTwo[0];
                float x1 = Math.Min(left.X, right.X);
                float y1 = Math.Min(left.Y, right.Y);
                float x2 = Math.Max(left.BoxRight, right.BoxRight);
                float y2 = Math.Max(left.BoxBottom, right.BoxBottom);
                var eyeL = left.EyeMidpoint;
                var eyeR = right.EyeMidpoint;
                var mouthMid = new SKPoint(
                    (left.MouthMidpoint.X + right.MouthMidpoint.X) * 0.5f,
                    (left.MouthMidpoint.Y + right.MouthMidpoint.Y) * 0.5f);

                return new DetectedFace(
                    x1, y1, x2 - x1, y2 - y1,
                    eyeL,
                    eyeR,
                    new SKPoint((x1 + x2) * 0.5f, (eyeL.Y + eyeR.Y + mouthMid.Y) / 3f),
                    left.MouthMidpoint,
                    right.MouthMidpoint,
                    Math.Min(left.Confidence, right.Confidence));
            }
        }

        return faces
            .OrderByDescending(f =>
            {
                float cx = (f.X + f.Width * 0.5f) / imgW - 0.5f;
                float cy = (f.Y + f.Height * 0.5f) / imgH - 0.45f;
                float dist = MathF.Sqrt(cx * cx + cy * cy);
                float area = Math.Min(0.45f, (f.Width * f.Height) / Math.Max(1f, (float)imgW * imgH));
                return area * f.Confidence / (1.0f + dist * 0.65f);
            })
            .FirstOrDefault();
    }

    /// <summary>
    /// 根据左右眼坐标计算将双眼摆平所需的 RotationDegrees ([-45, 45])。
    /// 与 SheetRenderer.DrawFittedImage 的先 RotateDegrees(QuarterTurns*90 + RotationDegrees) 后 Flip 保持精确一致。
    /// </summary>
    public static double ComputeTwoEyeLevelRotationDegrees(
        SKPoint eyeA,
        SKPoint eyeB,
        int rawW,
        int rawH,
        int quarterTurns)
    {
        var center = new SKPoint(rawW * 0.5f, rawH * 0.5f);
        double qtRad = ((quarterTurns % 4 + 4) % 4) * Math.PI * 0.5;
        var aQt = RotatePoint(eyeA - center, qtRad);
        var bQt = RotatePoint(eyeB - center, qtRad);

        // 在 pre-flip 空间中选择 X 较小的为左、较大的为右，保证求解落在 (-90°, 90°) 正立区间
        var (leftEye, rightEye) = aQt.X <= bQt.X ? (aQt, bQt) : (bQt, aQt);
        double dx = rightEye.X - leftEye.X;
        double dy = rightEye.Y - leftEye.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < 2.0) return 0.0;

        double angleDeg = Math.Atan2(-dy, dx) * 180.0 / Math.PI;
        if (Math.Abs(angleDeg) < 0.25) return 0.0;
        return Math.Round(Math.Clamp(angleDeg, -45.0, 45.0), 2);
    }

    public static FaceAutoFrameResult ComputeAutoFrame(
        int rawW,
        int rawH,
        IReadOnlyList<DetectedFace> faces,
        PhotoEditState currentEdit,
        double slotTrimWmm,
        double slotTrimHmm,
        double slotBleedMm,
        FitMode effectiveFitMode,
        IdPhotoSpec? idSpec,
        bool adjustZoomAndPan = true,
        double slotZoom = 1.0,
        double slotPanX = 0.0,
        double slotPanY = 0.0)
    {
        if (rawW <= 0 || rawH <= 0 || faces == null || faces.Count == 0)
        {
            return new FaceAutoFrameResult(
                false,
                "未检测到清晰人脸与五官，请检查照片角度或使用手动微调。",
                currentEdit.RotationDegrees,
                currentEdit.Zoom,
                currentEdit.PanX,
                currentEdit.PanY,
                0,
                0,
                0,
                false,
                false);
        }

        var target = SelectFramingTarget(faces, rawW, rawH, idSpec)!;
        double destWmm = Math.Max(4.0, slotTrimWmm + slotBleedMm * 2.0);
        double destHmm = Math.Max(4.0, slotTrimHmm + slotBleedMm * 2.0);

        double eyeTiltBefore = ComputeRenderedEyeTiltDegrees(
            target.RightEye, target.LeftEye, rawW, rawH,
            currentEdit.QuarterTurns, currentEdit.RotationDegrees,
            currentEdit.FlipHorizontal, currentEdit.FlipVertical);

        double newRot = ComputeTwoEyeLevelRotationDegrees(
            target.RightEye, target.LeftEye, rawW, rawH, currentEdit.QuarterTurns);

        if (!adjustZoomAndPan)
        {
            return new FaceAutoFrameResult(
                true,
                $"已依据五官眼线水平校正 ({newRot:+0.0;-0.0;0.0}°)",
                newRot,
                currentEdit.Zoom,
                currentEdit.PanX,
                currentEdit.PanY,
                faces.Count,
                0,
                eyeTiltBefore,
                false,
                false);
        }

        // 将候选旋转代入前向几何，计算直立像素坐标系 (q) 中的眼部中心与估计头高
        var qEyeMid = ToQSpace(target.EyeMidpoint, rawW, rawH, currentEdit.QuarterTurns, newRot, currentEdit.FlipHorizontal, currentEdit.FlipVertical);
        var qCorners = new[]
        {
            ToQSpace(new SKPoint(target.X, target.Y), rawW, rawH, currentEdit.QuarterTurns, newRot, currentEdit.FlipHorizontal, currentEdit.FlipVertical),
            ToQSpace(new SKPoint(target.BoxRight, target.Y), rawW, rawH, currentEdit.QuarterTurns, newRot, currentEdit.FlipHorizontal, currentEdit.FlipVertical),
            ToQSpace(new SKPoint(target.X, target.BoxBottom), rawW, rawH, currentEdit.QuarterTurns, newRot, currentEdit.FlipHorizontal, currentEdit.FlipVertical),
            ToQSpace(new SKPoint(target.BoxRight, target.BoxBottom), rawW, rawH, currentEdit.QuarterTurns, newRot, currentEdit.FlipHorizontal, currentEdit.FlipVertical)
        };
        float qMinY = qCorners.Min(p => p.Y);
        float qMaxY = qCorners.Max(p => p.Y);
        float qBoxH = Math.Max(10f, qMaxY - qMinY);
        float eyeToChin = Math.Max(10f, qMaxY - qEyeMid.Y);

        // YuNet WIDER bbox 高度通常覆盖发际线至下巴，头顶含发冠全高约为 boxH * 1.18 或 2.02 * eyeToChin
        double estHeadHeightQPx = idSpec?.Id == "id_marriage" && faces.Count >= 2
            ? Math.Max(qBoxH * 1.12, eyeToChin * 1.95)
            : Math.Max(qBoxH * 1.18, eyeToChin * 2.02);

        bool quarterSwap = (currentEdit.QuarterTurns % 2) != 0;
        double effW = quarterSwap ? rawH : rawW;
        double effH = quarterSwap ? rawW : rawH;
        double baseScaleMm = effectiveFitMode == FitMode.Cover
            ? Math.Max(destWmm / effW, destHmm / effH)
            : Math.Min(destWmm / effW, destHmm / effH);

        double desiredTotalZoom;
        double targetEyeYFromDestCenterMm;
        bool isIdMode = idSpec != null && idSpec.HeadHeightMaxMm > 0;

        if (isIdMode)
        {
            double targetHeadMm = (idSpec!.HeadHeightMinMm + idSpec.HeadHeightMaxMm) * 0.5;
            if (idSpec.Id == "id_marriage")
                targetHeadMm = Math.Clamp(targetHeadMm, 18.0, 22.0);

            desiredTotalZoom = targetHeadMm / Math.Max(1e-5, estHeadHeightQPx * baseScaleMm);
            double eyeYFromTrimTopMm = idSpec.EyeLineRatioFromTop * slotTrimHmm;
            targetEyeYFromDestCenterMm = (slotBleedMm + eyeYFromTrimTopMm) - destHmm * 0.5;
        }
        else
        {
            // 非证件照模式（拍立得 / 二合一 / 自由网格）：微调保证人物大小合理，眼线置于黄金上 38% 位置
            double headRatioAt1x = (estHeadHeightQPx * baseScaleMm) / destHmm;
            desiredTotalZoom = headRatioAt1x < 0.18
                ? Math.Clamp(0.24 / Math.Max(0.05, headRatioAt1x), 1.0, 1.8)
                : Math.Clamp(currentEdit.Zoom * slotZoom, 1.0, 2.5);
            double eyeYFromTrimTopMm = 0.38 * slotTrimHmm;
            targetEyeYFromDestCenterMm = (slotBleedMm + eyeYFromTrimTopMm) - destHmm * 0.5;
        }

        double desiredEditZoom = desiredTotalZoom / Math.Max(0.5, slotZoom);
        bool wasZoomClampedMin = desiredEditZoom < 0.99;
        double finalEditZoom = Math.Clamp(desiredEditZoom, 1.0, 4.0);
        double finalTotalZoom = Math.Clamp(finalEditZoom * slotZoom, 0.5, 8.0);

        // Cover 模式下若微旋转角导致四角露白边，略微补足最小遮罩倍率
        if (effectiveFitMode == FitMode.Cover && Math.Abs(newRot) > 0.1)
        {
            double cornerCoverRatio = ComputeRequiredCoverZoomFactor(
                rawW, rawH, currentEdit.QuarterTurns, newRot,
                currentEdit.FlipHorizontal, currentEdit.FlipVertical,
                destWmm, destHmm, baseScaleMm * finalTotalZoom);
            if (cornerCoverRatio > 1.001)
            {
                finalTotalZoom = Math.Clamp(finalTotalZoom * cornerCoverRatio, 0.5, 8.0);
                finalEditZoom = Math.Clamp(finalTotalZoom / Math.Max(0.5, slotZoom), 1.0, 4.0);
            }
        }

        double mmPerPx = baseScaleMm * finalTotalZoom;
        double desiredTotalPanX = -(mmPerPx * qEyeMid.X) / (destWmm * 0.5);
        double desiredTotalPanY = (targetEyeYFromDestCenterMm - mmPerPx * qEyeMid.Y) / (destHmm * 0.5);

        double desiredEditPanX = Math.Clamp(desiredTotalPanX - slotPanX, -1.0, 1.0);
        double desiredEditPanY = Math.Clamp(desiredTotalPanY - slotPanY, -1.0, 1.0);
        bool wasPanClamped = Math.Abs((desiredTotalPanX - slotPanX) - desiredEditPanX) > 0.015 ||
                             Math.Abs((desiredTotalPanY - slotPanY) - desiredEditPanY) > 0.015;

        double finalEditPanX = desiredEditPanX;
        double finalEditPanY = desiredEditPanY;

        if (effectiveFitMode == FitMode.Cover)
        {
            double reqX = slotPanX + desiredEditPanX;
            double reqY = slotPanY + desiredEditPanY;
            var (safeTotalX, safeTotalY) = ClampCoverPanPerAxis(
                rawW, rawH,
                currentEdit.QuarterTurns, newRot,
                currentEdit.FlipHorizontal, currentEdit.FlipVertical,
                destWmm, destHmm, mmPerPx,
                reqX, reqY);

            if (Math.Abs(safeTotalX - reqX) > 1e-3 || Math.Abs(safeTotalY - reqY) > 1e-3)
            {
                wasPanClamped = true;
                finalEditPanX = Math.Clamp(safeTotalX - slotPanX, -1.0, 1.0);
                finalEditPanY = Math.Clamp(safeTotalY - slotPanY, -1.0, 1.0);
            }
        }

        double estHeadMm = Math.Round(estHeadHeightQPx * mmPerPx, 1);

        string msg = isIdMode
            ? $"已完成 AI 智能构图，预估头高 {estHeadMm:0.0} mm，水平校正 {newRot:+0.0;-0.0;0.0}°"
            : $"已完成 AI 智能人像构图，校正倾角 {newRot:+0.0;-0.0;0.0}°";
        if (wasZoomClampedMin)
            msg += "（原图人像偏大，已贴合最大可视构图）";

        return new FaceAutoFrameResult(
            true,
            msg,
            newRot,
            finalEditZoom,
            finalEditPanX,
            finalEditPanY,
            faces.Count,
            estHeadMm,
            eyeTiltBefore,
            wasZoomClampedMin,
            wasPanClamped);
    }

    public static FaceComplianceMetrics? MeasureCompliance(
        int rawW,
        int rawH,
        IReadOnlyList<DetectedFace> faces,
        PhotoEditState edit,
        double slotTrimWmm,
        double slotTrimHmm,
        double slotBleedMm,
        FitMode effectiveFitMode,
        IdPhotoSpec? idSpec,
        double slotZoom = 1.0,
        double slotPanX = 0.0,
        double slotPanY = 0.0)
    {
        if (rawW <= 0 || rawH <= 0 || faces == null || faces.Count == 0)
            return null;

        var target = SelectFramingTarget(faces, rawW, rawH, idSpec);
        if (target == null) return null;

        double destWmm = Math.Max(4.0, slotTrimWmm + slotBleedMm * 2.0);
        double destHmm = Math.Max(4.0, slotTrimHmm + slotBleedMm * 2.0);
        var destRectMm = new SKRect(
            (float)(-slotBleedMm),
            (float)(-slotBleedMm),
            (float)(slotTrimWmm + slotBleedMm),
            (float)(slotTrimHmm + slotBleedMm));

        var pEyeMid = TransformRawPointToDestRect(target.EyeMidpoint, rawW, rawH, edit, destRectMm, effectiveFitMode, slotZoom, slotPanX, slotPanY);
        double tiltDeg = ComputeRenderedEyeTiltDegrees(
            target.RightEye, target.LeftEye, rawW, rawH,
            edit.QuarterTurns, edit.RotationDegrees,
            edit.FlipHorizontal, edit.FlipVertical);

        var qEyeMid = ToQSpace(target.EyeMidpoint, rawW, rawH, edit.QuarterTurns, edit.RotationDegrees, edit.FlipHorizontal, edit.FlipVertical);
        var qCorners = new[]
        {
            ToQSpace(new SKPoint(target.X, target.Y), rawW, rawH, edit.QuarterTurns, edit.RotationDegrees, edit.FlipHorizontal, edit.FlipVertical),
            ToQSpace(new SKPoint(target.BoxRight, target.Y), rawW, rawH, edit.QuarterTurns, edit.RotationDegrees, edit.FlipHorizontal, edit.FlipVertical),
            ToQSpace(new SKPoint(target.X, target.BoxBottom), rawW, rawH, edit.QuarterTurns, edit.RotationDegrees, edit.FlipHorizontal, edit.FlipVertical),
            ToQSpace(new SKPoint(target.BoxRight, target.BoxBottom), rawW, rawH, edit.QuarterTurns, edit.RotationDegrees, edit.FlipHorizontal, edit.FlipVertical)
        };
        float qBoxH = Math.Max(10f, qCorners.Max(p => p.Y) - qCorners.Min(p => p.Y));
        float eyeToChin = Math.Max(10f, qCorners.Max(p => p.Y) - qEyeMid.Y);
        double estHeadQPx = idSpec?.Id == "id_marriage" && faces.Count >= 2
            ? Math.Max(qBoxH * 1.12, eyeToChin * 1.95)
            : Math.Max(qBoxH * 1.18, eyeToChin * 2.02);

        bool quarterSwap = (edit.QuarterTurns % 2) != 0;
        double effW = quarterSwap ? rawH : rawW;
        double effH = quarterSwap ? rawW : rawH;
        double baseScaleMm = effectiveFitMode == FitMode.Cover
            ? Math.Max(destWmm / effW, destHmm / effH)
            : Math.Min(destWmm / effW, destHmm / effH);
        double mmPerPx = baseScaleMm * Math.Clamp(edit.Zoom * slotZoom, 0.5, 8.0);
        double estHeadMm = estHeadQPx * mmPerPx;

        double eyeRatioFromTop = pEyeMid.Y / Math.Max(1.0, slotTrimHmm);
        double horizOffsetMm = pEyeMid.X - slotTrimWmm * 0.5;

        bool headOk = idSpec != null && idSpec.HeadHeightMaxMm > 0
            ? (estHeadMm >= idSpec.HeadHeightMinMm - 0.8 && estHeadMm <= idSpec.HeadHeightMaxMm + 0.8)
            : true;
        bool levelOk = Math.Abs(tiltDeg) <= 1.5;
        bool centerOk = Math.Abs(horizOffsetMm) <= Math.Max(1.8, slotTrimWmm * 0.06);

        return new FaceComplianceMetrics(
            faces.Count,
            Math.Round(tiltDeg, 1),
            Math.Round(eyeRatioFromTop, 3),
            Math.Round(estHeadMm, 1),
            Math.Round(horizOffsetMm, 1),
            headOk,
            levelOk,
            centerOk);
    }

    /// <summary>
    /// 与 SheetRenderer.DrawFittedImage 精确同步：将原图坐标点映射到目标绘制矩形坐标系。
    /// </summary>
    public static SKPoint TransformRawPointToDestRect(
        SKPoint rawPt,
        int rawW,
        int rawH,
        PhotoEditState edit,
        SKRect destRect,
        FitMode effectiveFitMode,
        double slotZoom = 1.0,
        double slotPanX = 0.0,
        double slotPanY = 0.0)
    {
        bool quarterSwap = (edit.QuarterTurns % 2) != 0;
        float effW = quarterSwap ? rawH : rawW;
        float effH = quarterSwap ? rawW : rawH;

        float baseScale = effectiveFitMode == FitMode.Cover
            ? Math.Max(destRect.Width / effW, destRect.Height / effH)
            : Math.Min(destRect.Width / effW, destRect.Height / effH);

        float zoom = (float)Math.Clamp(edit.Zoom * slotZoom, 0.5, 8.0);
        float scale = baseScale * zoom;

        float panX = (float)Math.Clamp(edit.PanX + slotPanX, -1.5, 1.5) * destRect.Width * 0.5f;
        float panY = (float)Math.Clamp(edit.PanY + slotPanY, -1.5, 1.5) * destRect.Height * 0.5f;

        var q = ToQSpace(rawPt, rawW, rawH, edit.QuarterTurns, edit.RotationDegrees, edit.FlipHorizontal, edit.FlipVertical);
        return new SKPoint(
            destRect.MidX + panX + q.X * scale,
            destRect.MidY + panY + q.Y * scale);
    }

    public static SKRect TransformRawBoxToDestRect(
        DetectedFace face,
        int rawW,
        int rawH,
        PhotoEditState edit,
        SKRect destRect,
        FitMode effectiveFitMode,
        double slotZoom = 1.0,
        double slotPanX = 0.0,
        double slotPanY = 0.0)
    {
        SKPoint[] pts =
        [
            TransformRawPointToDestRect(new SKPoint(face.X, face.Y), rawW, rawH, edit, destRect, effectiveFitMode, slotZoom, slotPanX, slotPanY),
            TransformRawPointToDestRect(new SKPoint(face.BoxRight, face.Y), rawW, rawH, edit, destRect, effectiveFitMode, slotZoom, slotPanX, slotPanY),
            TransformRawPointToDestRect(new SKPoint(face.X, face.BoxBottom), rawW, rawH, edit, destRect, effectiveFitMode, slotZoom, slotPanX, slotPanY),
            TransformRawPointToDestRect(new SKPoint(face.BoxRight, face.BoxBottom), rawW, rawH, edit, destRect, effectiveFitMode, slotZoom, slotPanX, slotPanY)
        ];

        return new SKRect(
            pts.Min(p => p.X),
            pts.Min(p => p.Y),
            pts.Max(p => p.X),
            pts.Max(p => p.Y));
    }

    private static double ComputeRenderedEyeTiltDegrees(
        SKPoint eyeA,
        SKPoint eyeB,
        int rawW,
        int rawH,
        int quarterTurns,
        double rotDeg,
        bool flipH,
        bool flipV)
    {
        var qA = ToQSpace(eyeA, rawW, rawH, quarterTurns, rotDeg, flipH, flipV);
        var qB = ToQSpace(eyeB, rawW, rawH, quarterTurns, rotDeg, flipH, flipV);
        var (left, right) = qA.X <= qB.X ? (qA, qB) : (qB, qA);
        double dx = right.X - left.X;
        double dy = right.Y - left.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < 1.0) return 0.0;
        return Math.Atan2(dy, dx) * 180.0 / Math.PI;
    }

    private static SKPoint ToQSpace(
        SKPoint rawPt,
        int rawW,
        int rawH,
        int quarterTurns,
        double rotDeg,
        bool flipH,
        bool flipV)
    {
        double angleRad = (quarterTurns * 90.0 + rotDeg) * Math.PI / 180.0;
        var r = RotatePoint(new SKPoint(rawPt.X - rawW * 0.5f, rawPt.Y - rawH * 0.5f), angleRad);
        return new SKPoint(flipH ? -r.X : r.X, flipV ? -r.Y : r.Y);
    }

    private static SKPoint RotatePoint(SKPoint p, double rad)
    {
        if (Math.Abs(rad) < 1e-8) return p;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        return new SKPoint((float)(p.X * cos - p.Y * sin), (float)(p.X * sin + p.Y * cos));
    }

    private static double ComputeRequiredCoverZoomFactor(
        int rawW,
        int rawH,
        int quarterTurns,
        double rotDeg,
        bool flipH,
        bool flipV,
        double destWmm,
        double destHmm,
        double mmPerPx)
    {
        double halfW = destWmm * 0.5;
        double halfH = destHmm * 0.5;
        double angleRad = (quarterTurns * 90.0 + rotDeg) * Math.PI / 180.0;
        float fx = flipH ? -1f : 1f;
        float fy = flipV ? -1f : 1f;

        SKPoint[] corners =
        [
            new((float)-halfW, (float)-halfH),
            new((float)halfW, (float)-halfH),
            new((float)-halfW, (float)halfH),
            new((float)halfW, (float)halfH)
        ];

        double reqFactor = 1.0;
        foreach (var c in corners)
        {
            var rPx = new SKPoint((float)(fx * c.X / mmPerPx), (float)(fy * c.Y / mmPerPx));
            var rawRel = RotatePoint(rPx, -angleRad);
            reqFactor = Math.Max(reqFactor, Math.Abs(rawRel.X) / (rawW * 0.5));
            reqFactor = Math.Max(reqFactor, Math.Abs(rawRel.Y) / (rawH * 0.5));
        }

        return reqFactor;
    }

    private static (double SafeX, double SafeY) ClampCoverPanPerAxis(
        int rawW,
        int rawH,
        int quarterTurns,
        double rotDeg,
        bool flipH,
        bool flipV,
        double destWmm,
        double destHmm,
        double mmPerPx,
        double panX,
        double panY)
    {
        if (AllCornersInsideRaw(panX, panY)) return (panX, panY);
        if (!AllCornersInsideRaw(0.0, 0.0)) return (0.0, 0.0);

        // 先独立约束 X 轴再独立约束 Y 轴，避免单轴满幅 (maxPanX ≈ 0) 连带锁死另一轴的构图余量
        double safeX = BinarySearchAxis(0.0, panX, x => AllCornersInsideRaw(x, 0.0));
        double safeY = BinarySearchAxis(0.0, panY, y => AllCornersInsideRaw(safeX, y));
        return (safeX, safeY);

        static double BinarySearchAxis(double start, double target, Func<double, bool> isValid)
        {
            if (isValid(target)) return target;
            double lo = start, hi = target;
            for (int i = 0; i < 14; i++)
            {
                double mid = (lo + hi) * 0.5;
                if (isValid(mid))
                    lo = mid;
                else
                    hi = mid;
            }
            return lo;
        }

        bool AllCornersInsideRaw(double px, double py)
        {
            double halfW = destWmm * 0.5;
            double halfH = destHmm * 0.5;
            double offX = Math.Clamp(px, -1.5, 1.5) * halfW;
            double offY = Math.Clamp(py, -1.5, 1.5) * halfH;
            double angleRad = (quarterTurns * 90.0 + rotDeg) * Math.PI / 180.0;
            float fx = flipH ? -1f : 1f;
            float fy = flipV ? -1f : 1f;

            SKPoint[] corners =
            [
                new((float)(-halfW - offX), (float)(-halfH - offY)),
                new((float)(halfW - offX), (float)(-halfH - offY)),
                new((float)(-halfW - offX), (float)(halfH - offY)),
                new((float)(halfW - offX), (float)(halfH - offY))
            ];

            const double tol = 0.5;
            foreach (var c in corners)
            {
                var rPx = new SKPoint((float)(fx * c.X / mmPerPx), (float)(fy * c.Y / mmPerPx));
                var rawRel = RotatePoint(rPx, -angleRad);
                if (Math.Abs(rawRel.X) > rawW * 0.5 + tol || Math.Abs(rawRel.Y) > rawH * 0.5 + tol)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 判断照片当前经过 QuarterTurns 旋转后的宽高朝向是否与目标格位方向垂直冲突（如横图配竖格）；
    /// 显著垂直冲突（高宽比偏离阈值 > 8%）时返回 true，便于一键旋正 90° 以 Cover 满幅填满。
    /// </summary>
    public static bool NeedsQuarterTurnForSlotAspect(
        int pixelWidth,
        int pixelHeight,
        int currentQuarterTurns,
        double targetWidthMm,
        double targetHeightMm)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || targetWidthMm <= 0.1 || targetHeightMm <= 0.1)
            return false;

        bool swapped = (currentQuarterTurns % 2) != 0;
        double effW = swapped ? pixelHeight : pixelWidth;
        double effH = swapped ? pixelWidth : pixelHeight;

        double photoAspect = effW / effH;
        double slotAspect = targetWidthMm / targetHeightMm;
        if (Math.Abs(photoAspect - 1.0) < 0.08 || Math.Abs(slotAspect - 1.0) < 0.08)
            return false;

        return (photoAspect > 1.08 && slotAspect < 0.92) || (photoAspect < 0.92 && slotAspect > 1.08);
    }
}
