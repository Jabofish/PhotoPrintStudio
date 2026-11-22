using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.Core.Layout;

public enum ResizeHandle
{
    None,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left
}

public enum FrameAlignment
{
    Left,
    CenterHorizontal,
    Right,
    Top,
    CenterVertical,
    Bottom,
    CenterOnPage
}

public enum SnapGuideKind
{
    PaperEdge,
    PaperCenter,
    SafeMargin,
    SlotEdge,
    SlotCenter
}

public readonly record struct LayoutRectMm(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width * 0.5;
    public double CenterY => Y + Height * 0.5;
}

public readonly record struct SnapGuideLine(
    bool IsVertical,
    double PositionMm,
    double StartMm,
    double EndMm,
    SnapGuideKind Kind);

public sealed record MoveSnapResult(
    double DeltaXMm,
    double DeltaYMm,
    IReadOnlyList<SnapGuideLine> Guides);

public sealed record ResizeSnapResult(
    LayoutRectMm Rect,
    IReadOnlyList<SnapGuideLine> Guides);

public sealed record FreeformValidationResult(
    bool IsValid,
    IReadOnlyList<string> OutOfBoundsSlotIds,
    IReadOnlyList<string> InvalidSizeSlotIds,
    IReadOnlyList<(string FirstId, string SecondId)> OverlappingPairs);

public sealed record FrameSizePreset(
    string Name,
    double WidthMm,
    double HeightMm,
    string Category,
    string? IdPhotoSpecId = null,
    string? PolaroidSpecId = null)
{
    public override string ToString() => $"{Name} ({WidthMm:0.#}×{HeightMm:0.#} mm)";
}

/// <summary>
/// 自由自定义版式的纯几何计算引擎：磁吸对齐、8 向控制点缩放、对齐分布、智能新框定位、网格生成与越界/重叠校验。
/// </summary>
public static class FreeformLayoutMath
{
    public const double MinSlotSizeMm = 5.0;

    public static IReadOnlyList<FrameSizePreset> CommonSizePresets { get; } =
    [
        new("一寸证件照", 25.0, 35.0, "证件照", IdPhotoSpecId: "id_1inch"),
        new("小一寸证件照", 22.0, 32.0, "证件照", IdPhotoSpecId: "id_small1"),
        new("大一寸 / 护照", 33.0, 48.0, "证件照", IdPhotoSpecId: "id_big1"),
        new("二寸证件照", 35.0, 49.0, "证件照", IdPhotoSpecId: "id_2inch"),
        new("小二寸 / 签证", 35.0, 45.0, "证件照", IdPhotoSpecId: "id_small2"),
        new("美签方形 (2×2 寸)", 51.0, 51.0, "证件照", IdPhotoSpecId: "id_visa_us"),
        new("3 寸钱包照 (标准)", 63.0, 90.0, "常用照片"),
        new("Lomo 3 寸小卡", 63.5, 89.0, "常用照片"),
        new("4 寸照片 (76×102)", 76.2, 101.6, "常用照片"),
        new("5 寸 / 3R", 88.9, 127.0, "常用照片"),
        new("6 寸 / 4R", 101.6, 152.4, "常用照片"),
        new("7 寸 / 5R", 127.0, 177.8, "常用照片"),
        new("Instax Mini 拍立得", 54.0, 86.0, "拍立得", PolaroidSpecId: "pol_instax_mini"),
        new("Instax Square 方片", 72.0, 86.0, "拍立得", PolaroidSpecId: "pol_instax_square"),
        new("Instax Wide 宽幅", 108.0, 86.0, "拍立得", PolaroidSpecId: "pol_instax_wide"),
        new("宝丽来 600 / i-Type", 88.0, 107.0, "拍立得", PolaroidSpecId: "pol_600"),
        new("标准卡证 / 身份证", 85.6, 54.0, "证照卡证", IdPhotoSpecId: "id_std_card")
    ];

    public static LayoutRectMm GetBoundingBox(IEnumerable<LayoutRectMm> rects)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        bool any = false;
        foreach (var r in rects)
        {
            any = true;
            minX = Math.Min(minX, r.X);
            minY = Math.Min(minY, r.Y);
            maxX = Math.Max(maxX, r.Right);
            maxY = Math.Max(maxY, r.Bottom);
        }
        return any ? new LayoutRectMm(minX, minY, maxX - minX, maxY - minY) : new LayoutRectMm(0, 0, 0, 0);
    }

    public static MoveSnapResult CalculateMoveSnap(
        LayoutRectMm movingBounds,
        double proposedDxMm,
        double proposedDyMm,
        double paperWidthMm,
        double paperHeightMm,
        IReadOnlyList<LayoutRectMm> otherSlots,
        bool enableMagneticSnap,
        bool enableGridSnap,
        double gridStepMm,
        double safeMarginMm,
        double snapToleranceMm = 2.0,
        bool clampToPaper = true)
    {
        double dx = proposedDxMm;
        double dy = proposedDyMm;
        var guides = new List<SnapGuideLine>();

        if (clampToPaper)
        {
            double maxX = Math.Max(0, paperWidthMm - movingBounds.Width);
            double maxY = Math.Max(0, paperHeightMm - movingBounds.Height);
            double targetX = Math.Clamp(movingBounds.X + dx, 0, maxX);
            double targetY = Math.Clamp(movingBounds.Y + dy, 0, maxY);
            dx = targetX - movingBounds.X;
            dy = targetY - movingBounds.Y;
        }

        double curLeft = movingBounds.X + dx;
        double curRight = curLeft + movingBounds.Width;
        double curCenterX = curLeft + movingBounds.Width * 0.5;
        double curTop = movingBounds.Y + dy;
        double curBottom = curTop + movingBounds.Height;
        double curCenterY = curTop + movingBounds.Height * 0.5;

        bool snappedX = false;
        bool snappedY = false;

        if (enableMagneticSnap)
        {
            var xTargets = BuildAxisTargets(paperWidthMm, safeMarginMm, otherSlots, isX: true);
            var yTargets = BuildAxisTargets(paperHeightMm, safeMarginMm, otherSlots, isX: false);

            double bestAbsDx = snapToleranceMm + 1e-6;
            double bestAdjX = 0;
            SnapGuideLine? bestGuideX = null;

            double[] movingXs = [curLeft, curCenterX, curRight];
            foreach (double mx in movingXs)
            {
                foreach (var target in xTargets)
                {
                    double diff = target.Pos - mx;
                    double absDiff = Math.Abs(diff);
                    if (absDiff < bestAbsDx)
                    {
                        double candidateLeft = curLeft + diff;
                        if (clampToPaper && (candidateLeft < -0.001 || candidateLeft + movingBounds.Width > paperWidthMm + 0.001))
                            continue;
                        bestAbsDx = absDiff;
                        bestAdjX = diff;
                        bestGuideX = new SnapGuideLine(true, target.Pos, target.Start, target.End, target.Kind);
                    }
                }
            }

            if (bestGuideX is { } gx)
            {
                dx += bestAdjX;
                curLeft += bestAdjX;
                curRight += bestAdjX;
                snappedX = true;
                guides.Add(ExtendGuideToSpan(gx, curTop, curBottom, paperHeightMm));
            }

            double bestAbsDy = snapToleranceMm + 1e-6;
            double bestAdjY = 0;
            SnapGuideLine? bestGuideY = null;

            double[] movingYs = [curTop, curCenterY, curBottom];
            foreach (double my in movingYs)
            {
                foreach (var target in yTargets)
                {
                    double diff = target.Pos - my;
                    double absDiff = Math.Abs(diff);
                    if (absDiff < bestAbsDy)
                    {
                        double candidateTop = curTop + diff;
                        if (clampToPaper && (candidateTop < -0.001 || candidateTop + movingBounds.Height > paperHeightMm + 0.001))
                            continue;
                        bestAbsDy = absDiff;
                        bestAdjY = diff;
                        bestGuideY = new SnapGuideLine(false, target.Pos, target.Start, target.End, target.Kind);
                    }
                }
            }

            if (bestGuideY is { } gy)
            {
                dy += bestAdjY;
                curTop += bestAdjY;
                curBottom += bestAdjY;
                snappedY = true;
                guides.Add(ExtendGuideToSpan(gy, curLeft, curRight, paperWidthMm));
            }
        }

        if (enableGridSnap && gridStepMm > 0.05)
        {
            if (!snappedX)
            {
                double gridX = Math.Round((movingBounds.X + dx) / gridStepMm) * gridStepMm;
                if (clampToPaper) gridX = Math.Clamp(gridX, 0, Math.Max(0, paperWidthMm - movingBounds.Width));
                dx = gridX - movingBounds.X;
            }
            if (!snappedY)
            {
                double gridY = Math.Round((movingBounds.Y + dy) / gridStepMm) * gridStepMm;
                if (clampToPaper) gridY = Math.Clamp(gridY, 0, Math.Max(0, paperHeightMm - movingBounds.Height));
                dy = gridY - movingBounds.Y;
            }
        }

        return new MoveSnapResult(Math.Round(dx, 3), Math.Round(dy, 3), guides);
    }

    public static ResizeSnapResult CalculateResizeSnap(
        LayoutRectMm original,
        ResizeHandle handle,
        double pointerDeltaXMm,
        double pointerDeltaYMm,
        double paperWidthMm,
        double paperHeightMm,
        IReadOnlyList<LayoutRectMm> otherSlots,
        bool lockAspectRatio,
        bool enableMagneticSnap,
        bool enableGridSnap,
        double gridStepMm,
        double safeMarginMm,
        double snapToleranceMm = 2.0,
        bool clampToPaper = true)
    {
        if (handle == ResizeHandle.None)
            return new ResizeSnapResult(original, []);

        double minSize = Math.Min(MinSlotSizeMm, Math.Min(paperWidthMm, paperHeightMm));
        double left = original.X;
        double top = original.Y;
        double right = original.Right;
        double bottom = original.Bottom;

        bool moveLeft = handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft;
        bool moveRight = handle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight;
        bool moveTop = handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight;
        bool moveBottom = handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight;

        if (moveLeft) left += pointerDeltaXMm;
        if (moveRight) right += pointerDeltaXMm;
        if (moveTop) top += pointerDeltaYMm;
        if (moveBottom) bottom += pointerDeltaYMm;

        var guides = new List<SnapGuideLine>();
        bool snappedX = false;
        bool snappedY = false;

        // 自由边吸附（非锁定比例时分别吸附 X/Y；锁定比例时先按主轴吸附再推导另一轴）
        if (enableMagneticSnap)
        {
            var xTargets = BuildAxisTargets(paperWidthMm, safeMarginMm, otherSlots, isX: true);
            var yTargets = BuildAxisTargets(paperHeightMm, safeMarginMm, otherSlots, isX: false);

            if (moveLeft && TryFindBestSnap(left, xTargets, snapToleranceMm, isVertical: true, out double snappedL, out var gL))
            {
                left = snappedL;
                snappedX = true;
                guides.Add(ExtendGuideToSpan(gL, top, bottom, paperHeightMm));
            }
            else if (moveRight && TryFindBestSnap(right, xTargets, snapToleranceMm, isVertical: true, out double snappedR, out var gR))
            {
                right = snappedR;
                snappedX = true;
                guides.Add(ExtendGuideToSpan(gR, top, bottom, paperHeightMm));
            }

            if (!lockAspectRatio || (!moveLeft && !moveRight))
            {
                if (moveTop && TryFindBestSnap(top, yTargets, snapToleranceMm, isVertical: false, out double snappedT, out var gT))
                {
                    top = snappedT;
                    snappedY = true;
                    guides.Add(ExtendGuideToSpan(gT, left, right, paperWidthMm));
                }
                else if (moveBottom && TryFindBestSnap(bottom, yTargets, snapToleranceMm, isVertical: false, out double snappedB, out var gB))
                {
                    bottom = snappedB;
                    snappedY = true;
                    guides.Add(ExtendGuideToSpan(gB, left, right, paperWidthMm));
                }
            }
        }

        if (enableGridSnap && gridStepMm > 0.05)
        {
            if (!snappedX)
            {
                if (moveLeft) left = Math.Round(left / gridStepMm) * gridStepMm;
                if (moveRight) right = Math.Round(right / gridStepMm) * gridStepMm;
            }
            if (!snappedY && (!lockAspectRatio || (!moveLeft && !moveRight)))
            {
                if (moveTop) top = Math.Round(top / gridStepMm) * gridStepMm;
                if (moveBottom) bottom = Math.Round(bottom / gridStepMm) * gridStepMm;
            }
        }

        if (lockAspectRatio && original.Width > 0.01 && original.Height > 0.01)
        {
            double aspect = original.Width / original.Height;
            double minWidth = Math.Max(minSize, minSize * aspect);
            double maxWidth = clampToPaper ? Math.Min(paperWidthMm, paperHeightMm * aspect) : double.PositiveInfinity;
            if (minWidth <= maxWidth)
            {
                double w = right - left;
                double h = bottom - top;
                bool horizontal = moveLeft || moveRight;
                bool vertical = moveTop || moveBottom;
                bool widthDriven = horizontal && (!vertical ||
                    Math.Abs(w - original.Width) / original.Width >= Math.Abs(h - original.Height) / original.Height);

                if (clampToPaper)
                {
                    double anchorW = moveLeft ? right : moveRight ? paperWidthMm - left : paperWidthMm;
                    double anchorH = moveTop ? bottom : moveBottom ? paperHeightMm - top : paperHeightMm;
                    double anchoredWidth = Math.Min(anchorW, anchorH * aspect);
                    maxWidth = Math.Min(maxWidth, Math.Max(minWidth, anchoredWidth));
                }

                w = Math.Clamp(widthDriven ? w : h * aspect, minWidth, maxWidth);
                h = w / aspect;
                left = moveLeft ? right - w : moveRight ? left : original.CenterX - w / 2;
                top = moveTop ? bottom - h : moveBottom ? top : original.CenterY - h / 2;
                if (clampToPaper)
                {
                    left = Math.Clamp(left, 0, Math.Max(0, paperWidthMm - w));
                    top = Math.Clamp(top, 0, Math.Max(0, paperHeightMm - h));
                }
                return new ResizeSnapResult(new LayoutRectMm(Math.Round(left, 2), Math.Round(top, 2),
                    Math.Round(w, 2), Math.Round(h, 2)), guides);
            }
        }

        if (clampToPaper)
        {
            if (moveLeft)
            {
                right = Math.Clamp(right, minSize, paperWidthMm);
                left = Math.Clamp(left, 0, right - minSize);
            }
            else
            {
                left = Math.Clamp(left, 0, paperWidthMm - minSize);
                right = Math.Clamp(right, left + minSize, paperWidthMm);
            }

            if (moveTop)
            {
                bottom = Math.Clamp(bottom, minSize, paperHeightMm);
                top = Math.Clamp(top, 0, bottom - minSize);
            }
            else
            {
                top = Math.Clamp(top, 0, paperHeightMm - minSize);
                bottom = Math.Clamp(bottom, top + minSize, paperHeightMm);
            }
        }
        else
        {
            if (moveLeft && right - left < minSize) left = right - minSize;
            if (moveRight && right - left < minSize) right = left + minSize;
            if (moveTop && bottom - top < minSize) top = bottom - minSize;
            if (moveBottom && bottom - top < minSize) bottom = top + minSize;
        }

        var resultRect = new LayoutRectMm(
            Math.Round(left, 2),
            Math.Round(top, 2),
            Math.Round(Math.Max(minSize, right - left), 2),
            Math.Round(Math.Max(minSize, bottom - top), 2));

        return new ResizeSnapResult(resultRect, guides);
    }

    public static void AlignSlots(
        IReadOnlyList<CustomSlotDefinition> selectedSlots,
        FrameAlignment alignment,
        double paperWidthMm,
        double paperHeightMm)
    {
        if (selectedSlots.Count == 0) return;

        bool relativeToPaper = selectedSlots.Count == 1 || alignment == FrameAlignment.CenterOnPage;
        var refBox = relativeToPaper
            ? new LayoutRectMm(0, 0, paperWidthMm, paperHeightMm)
            : GetBoundingBox(selectedSlots.Select(s => new LayoutRectMm(s.XMm, s.YMm, s.WidthMm, s.HeightMm)));

        if (alignment == FrameAlignment.CenterOnPage && selectedSlots.Count > 1)
        {
            var groupBox = GetBoundingBox(selectedSlots.Select(s => new LayoutRectMm(s.XMm, s.YMm, s.WidthMm, s.HeightMm)));
            double dx = (paperWidthMm - groupBox.Width) * 0.5 - groupBox.X;
            double dy = (paperHeightMm - groupBox.Height) * 0.5 - groupBox.Y;
            foreach (var s in selectedSlots)
            {
                s.XMm = Math.Round(Math.Clamp(s.XMm + dx, 0, Math.Max(0, paperWidthMm - s.WidthMm)), 2);
                s.YMm = Math.Round(Math.Clamp(s.YMm + dy, 0, Math.Max(0, paperHeightMm - s.HeightMm)), 2);
            }
            return;
        }

        foreach (var s in selectedSlots)
        {
            switch (alignment)
            {
                case FrameAlignment.Left:
                    s.XMm = Math.Round(refBox.X, 2);
                    break;
                case FrameAlignment.CenterHorizontal:
                    s.XMm = Math.Round(refBox.CenterX - s.WidthMm * 0.5, 2);
                    break;
                case FrameAlignment.Right:
                    s.XMm = Math.Round(refBox.Right - s.WidthMm, 2);
                    break;
                case FrameAlignment.Top:
                    s.YMm = Math.Round(refBox.Y, 2);
                    break;
                case FrameAlignment.CenterVertical:
                    s.YMm = Math.Round(refBox.CenterY - s.HeightMm * 0.5, 2);
                    break;
                case FrameAlignment.Bottom:
                    s.YMm = Math.Round(refBox.Bottom - s.HeightMm, 2);
                    break;
                case FrameAlignment.CenterOnPage:
                    s.XMm = Math.Round((paperWidthMm - s.WidthMm) * 0.5, 2);
                    s.YMm = Math.Round((paperHeightMm - s.HeightMm) * 0.5, 2);
                    break;
            }
        }
    }

    public static void DistributeSlots(IReadOnlyList<CustomSlotDefinition> selectedSlots, bool horizontal)
    {
        if (selectedSlots.Count < 3) return;

        if (horizontal)
        {
            var ordered = selectedSlots.OrderBy(s => s.XMm).ThenBy(s => s.YMm).ToList();
            double start = ordered[0].XMm;
            double end = ordered[^1].XMm + ordered[^1].WidthMm;
            double totalWidth = ordered.Sum(s => s.WidthMm);
            double gap = (end - start - totalWidth) / (ordered.Count - 1);
            double cursor = start;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i > 0 && i < ordered.Count - 1)
                    ordered[i].XMm = Math.Round(cursor, 2);
                cursor += ordered[i].WidthMm + gap;
            }
        }
        else
        {
            var ordered = selectedSlots.OrderBy(s => s.YMm).ThenBy(s => s.XMm).ToList();
            double start = ordered[0].YMm;
            double end = ordered[^1].YMm + ordered[^1].HeightMm;
            double totalHeight = ordered.Sum(s => s.HeightMm);
            double gap = (end - start - totalHeight) / (ordered.Count - 1);
            double cursor = start;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i > 0 && i < ordered.Count - 1)
                    ordered[i].YMm = Math.Round(cursor, 2);
                cursor += ordered[i].HeightMm + gap;
            }
        }
    }

    public static void MatchSlotSize(
        IReadOnlyList<CustomSlotDefinition> selectedSlots,
        CustomSlotDefinition referenceSlot,
        bool matchWidth,
        bool matchHeight,
        double paperWidthMm,
        double paperHeightMm)
    {
        foreach (var s in selectedSlots)
        {
            if (ReferenceEquals(s, referenceSlot)) continue;
            if (matchWidth)
            {
                s.WidthMm = Math.Round(Math.Clamp(referenceSlot.WidthMm, MinSlotSizeMm, paperWidthMm), 2);
                if (s.XMm + s.WidthMm > paperWidthMm)
                    s.XMm = Math.Round(Math.Max(0, paperWidthMm - s.WidthMm), 2);
            }
            if (matchHeight)
            {
                s.HeightMm = Math.Round(Math.Clamp(referenceSlot.HeightMm, MinSlotSizeMm, paperHeightMm), 2);
                if (s.YMm + s.HeightMm > paperHeightMm)
                    s.YMm = Math.Round(Math.Max(0, paperHeightMm - s.HeightMm), 2);
            }
        }
    }

    public static LayoutRectMm ComputeNextSlotRect(
        double paperWidthMm,
        double paperHeightMm,
        IReadOnlyList<CustomSlotDefinition> existingSlots,
        CustomSlotDefinition? selectedSlot,
        double defaultGapMm = 4.0)
    {
        double w = selectedSlot?.WidthMm ?? Math.Min(63.0, Math.Max(MinSlotSizeMm, paperWidthMm * 0.42));
        double h = selectedSlot?.HeightMm ?? Math.Min(90.0, Math.Max(MinSlotSizeMm, paperHeightMm * 0.80));
        w = Math.Round(Math.Clamp(w, MinSlotSizeMm, paperWidthMm), 2);
        h = Math.Round(Math.Clamp(h, MinSlotSizeMm, paperHeightMm), 2);

        if (existingSlots.Count == 0)
        {
            return new LayoutRectMm(
                Math.Round((paperWidthMm - w) * 0.5, 2),
                Math.Round((paperHeightMm - h) * 0.5, 2),
                w, h);
        }

        var anchor = selectedSlot ?? existingSlots[^1];
        // 1. 尝试放在右侧
        double rightX = anchor.XMm + anchor.WidthMm + defaultGapMm;
        if (rightX + w <= paperWidthMm + 0.01 && !OverlapsAny(rightX, anchor.YMm, w, h, existingSlots))
            return new LayoutRectMm(Math.Round(rightX, 2), Math.Round(anchor.YMm, 2), w, h);

        // 2. 尝试放在下方（与首列或锚点左对齐）
        double belowY = anchor.YMm + anchor.HeightMm + defaultGapMm;
        double minX = existingSlots.Min(s => s.XMm);
        if (belowY + h <= paperHeightMm + 0.01 && !OverlapsAny(minX, belowY, w, h, existingSlots))
            return new LayoutRectMm(Math.Round(minX, 2), Math.Round(belowY, 2), w, h);
        if (belowY + h <= paperHeightMm + 0.01 && !OverlapsAny(anchor.XMm, belowY, w, h, existingSlots))
            return new LayoutRectMm(Math.Round(anchor.XMm, 2), Math.Round(belowY, 2), w, h);

        // 3. 尝试放在左侧
        double leftX = anchor.XMm - defaultGapMm - w;
        if (leftX >= -0.01 && !OverlapsAny(leftX, anchor.YMm, w, h, existingSlots))
            return new LayoutRectMm(Math.Round(Math.Max(0, leftX), 2), Math.Round(anchor.YMm, 2), w, h);

        // 4. 略微错位放置，保持可见且在纸内
        double cascadeX = Math.Clamp(anchor.XMm + 6.0, 0, Math.Max(0, paperWidthMm - w));
        double cascadeY = Math.Clamp(anchor.YMm + 6.0, 0, Math.Max(0, paperHeightMm - h));
        return new LayoutRectMm(Math.Round(cascadeX, 2), Math.Round(cascadeY, 2), w, h);
    }

    public static List<CustomSlotDefinition> GenerateGridSlots(
        double paperWidthMm,
        double paperHeightMm,
        int rows,
        int cols,
        double marginMm,
        double gapMm,
        double cornerRadiusMm = 0,
        double bleedMm = 0)
    {
        rows = Math.Clamp(rows, 1, 10);
        cols = Math.Clamp(cols, 1, 10);
        if (!double.IsFinite(marginMm) || !double.IsFinite(gapMm) || marginMm < 0 || gapMm < 0)
            return [];

        double cellW = (paperWidthMm - marginMm * 2 - (cols - 1) * gapMm) / cols;
        double cellH = (paperHeightMm - marginMm * 2 - (rows - 1) * gapMm) / rows;
        if (cellW < MinSlotSizeMm || cellH < MinSlotSizeMm)
            return [];

        var list = new List<CustomSlotDefinition>(rows * cols);
        int idx = 1;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                list.Add(new CustomSlotDefinition
                {
                    XMm = Math.Round(marginMm + c * (cellW + gapMm), 2),
                    YMm = Math.Round(marginMm + r * (cellH + gapMm), 2),
                    WidthMm = Math.Round(cellW, 2),
                    HeightMm = Math.Round(cellH, 2),
                    CornerRadiusMm = Math.Max(0, Math.Round(cornerRadiusMm, 2)),
                    BleedMm = Math.Max(0, Math.Round(bleedMm, 2)),
                    Label = $"图片框 {idx++}"
                });
            }
        }
        return list;
    }

    public static List<CustomSlotDefinition> GenerateFixedSizeGridSlots(
        double paperWidthMm,
        double paperHeightMm,
        double itemWidthMm,
        double itemHeightMm,
        double marginMm,
        double gapMm,
        bool allowRotate = true,
        bool centerOnPage = true,
        string? idPhotoSpecId = null,
        PolaroidSpec? polaroidSpec = null)
    {
        if (itemWidthMm < MinSlotSizeMm || itemHeightMm < MinSlotSizeMm ||
            !double.IsFinite(marginMm) || !double.IsFinite(gapMm) || marginMm < 0 || gapMm < 0)
            return [];
        var pack = LayoutEngine.CalculateBestPack(paperWidthMm, paperHeightMm, itemWidthMm, itemHeightMm, marginMm, gapMm, allowRotate);
        if (pack.TotalCount <= 0) return [];

        double totalW = pack.Columns * pack.SlotWidthMm + (pack.Columns - 1) * gapMm;
        double totalH = pack.Rows * pack.SlotHeightMm + (pack.Rows - 1) * gapMm;
        double startX = centerOnPage ? (paperWidthMm - totalW) * 0.5 : marginMm;
        double startY = centerOnPage ? (paperHeightMm - totalH) * 0.5 : marginMm;

        var list = new List<CustomSlotDefinition>(pack.TotalCount);
        int idx = 1;
        for (int r = 0; r < pack.Rows; r++)
        {
            for (int c = 0; c < pack.Columns; c++)
            {
                list.Add(new CustomSlotDefinition
                {
                    XMm = Math.Round(startX + c * (pack.SlotWidthMm + gapMm), 2),
                    YMm = Math.Round(startY + r * (pack.SlotHeightMm + gapMm), 2),
                    WidthMm = Math.Round(pack.SlotWidthMm, 2),
                    HeightMm = Math.Round(pack.SlotHeightMm, 2),
                    SlotRotationDegrees = pack.RotatedSlot ? 90 : 0,
                    CornerRadiusMm = polaroidSpec?.CornerRadiusMm ?? 0,
                    IdPhotoSpecId = idPhotoSpecId,
                    PolaroidStyle = polaroidSpec != null ? PolaroidSlotStyle.FromSpec(polaroidSpec) : null,
                    Label = $"图片框 {idx++}"
                });
            }
        }
        return list;
    }

    public static FreeformValidationResult ValidateSlots(
        IReadOnlyList<CustomSlotDefinition> slots,
        double paperWidthMm,
        double paperHeightMm)
    {
        var outOfBounds = new List<string>();
        var invalidSize = new List<string>();
        var overlaps = new List<(string, string)>();

        foreach (var s in slots)
        {
            if (!double.IsFinite(s.WidthMm) || !double.IsFinite(s.HeightMm) ||
                s.WidthMm < MinSlotSizeMm - 0.01 || s.HeightMm < MinSlotSizeMm - 0.01 ||
                (s.PolaroidStyle is { } style && !style.HasPhotoArea(s.WidthMm, s.HeightMm,
                    LayoutSlot.IsQuarterTurnRotation(s.SlotRotationDegrees))))
            {
                invalidSize.Add(s.Id);
                continue;
            }
            if (!FitsPaper(new(s.XMm, s.YMm, s.WidthMm, s.HeightMm), paperWidthMm, paperHeightMm))
            {
                outOfBounds.Add(s.Id);
            }
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var a = slots[i];
            for (int j = i + 1; j < slots.Count; j++)
            {
                var b = slots[j];
                bool isOverlap =
                    a.XMm < b.XMm + b.WidthMm - 0.05 &&
                    a.XMm + a.WidthMm > b.XMm + 0.05 &&
                    a.YMm < b.YMm + b.HeightMm - 0.05 &&
                    a.YMm + a.HeightMm > b.YMm + 0.05;
                if (isOverlap)
                    overlaps.Add((a.Id, b.Id));
            }
        }

        bool isValid = slots.Count > 0 && outOfBounds.Count == 0 && invalidSize.Count == 0;
        return new FreeformValidationResult(isValid, outOfBounds, invalidSize, overlaps);
    }

    public static bool FitsPaper(LayoutRectMm rect, double paperWidthMm, double paperHeightMm) =>
        double.IsFinite(paperWidthMm) && double.IsFinite(paperHeightMm) &&
        paperWidthMm > 0 && paperHeightMm > 0 &&
        rect.Width > 0 && rect.Height > 0 && rect.X >= -0.01 && rect.Y >= -0.01 &&
        rect.Right <= paperWidthMm + 0.01 && rect.Bottom <= paperHeightMm + 0.01;

    public static bool CanOutput(PrintSheet sheet) => sheet.Slots.Count > 0 &&
        sheet.Slots.All(slot => FitsPaper(new(slot.XMm, slot.YMm, slot.WidthMm, slot.HeightMm),
            sheet.PaperWidthMm, sheet.PaperHeightMm) && slot.HasValidPhotoArea);

    internal static void ValidateOutput(PrintSheet sheet)
    {
        if (!CanOutput(sheet))
            throw new InvalidOperationException($"{sheet.SheetName} 的照片框超出纸张或没有有效照片区域，请调整后再输出。");
    }

    private static bool OverlapsAny(double x, double y, double w, double h, IReadOnlyList<CustomSlotDefinition> existing)
    {
        foreach (var s in existing)
        {
            if (x < s.XMm + s.WidthMm - 0.05 &&
                x + w > s.XMm + 0.05 &&
                y < s.YMm + s.HeightMm - 0.05 &&
                y + h > s.YMm + 0.05)
                return true;
        }
        return false;
    }

    private readonly record struct AxisTarget(double Pos, double Start, double End, SnapGuideKind Kind);

    private static List<AxisTarget> BuildAxisTargets(
        double axisPaperLength,
        double safeMarginMm,
        IReadOnlyList<LayoutRectMm> otherSlots,
        bool isX)
    {
        var targets = new List<AxisTarget>
        {
            new(0, 0, 0, SnapGuideKind.PaperEdge),
            new(axisPaperLength * 0.5, 0, 0, SnapGuideKind.PaperCenter),
            new(axisPaperLength, 0, 0, SnapGuideKind.PaperEdge)
        };

        if (safeMarginMm > 0.2 && safeMarginMm * 2 < axisPaperLength)
        {
            targets.Add(new(safeMarginMm, 0, 0, SnapGuideKind.SafeMargin));
            targets.Add(new(axisPaperLength - safeMarginMm, 0, 0, SnapGuideKind.SafeMargin));
        }

        foreach (var o in otherSlots)
        {
            if (isX)
            {
                targets.Add(new(o.X, o.Y, o.Bottom, SnapGuideKind.SlotEdge));
                targets.Add(new(o.CenterX, o.Y, o.Bottom, SnapGuideKind.SlotCenter));
                targets.Add(new(o.Right, o.Y, o.Bottom, SnapGuideKind.SlotEdge));
            }
            else
            {
                targets.Add(new(o.Y, o.X, o.Right, SnapGuideKind.SlotEdge));
                targets.Add(new(o.CenterY, o.X, o.Right, SnapGuideKind.SlotCenter));
                targets.Add(new(o.Bottom, o.X, o.Right, SnapGuideKind.SlotEdge));
            }
        }
        return targets;
    }

    private static bool TryFindBestSnap(
        double value,
        List<AxisTarget> targets,
        double tolerance,
        bool isVertical,
        out double snappedValue,
        out SnapGuideLine guide)
    {
        double bestAbs = tolerance + 1e-6;
        snappedValue = value;
        guide = default;
        bool found = false;

        foreach (var t in targets)
        {
            double abs = Math.Abs(t.Pos - value);
            if (abs < bestAbs)
            {
                bestAbs = abs;
                snappedValue = t.Pos;
                guide = new SnapGuideLine(isVertical, t.Pos, t.Start, t.End, t.Kind);
                found = true;
            }
        }
        return found;
    }

    private static SnapGuideLine ExtendGuideToSpan(SnapGuideLine g, double minCross, double maxCross, double paperCrossLen)
    {
        if (g.Kind is SnapGuideKind.PaperEdge or SnapGuideKind.PaperCenter or SnapGuideKind.SafeMargin)
            return g with { StartMm = 0, EndMm = paperCrossLen };

        double start = Math.Min(g.StartMm, minCross);
        double end = Math.Max(g.EndMm, maxCross);
        return g with { StartMm = Math.Max(0, start - 3), EndMm = Math.Min(paperCrossLen, end + 3) };
    }
}
