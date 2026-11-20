using System.Collections.Concurrent;
using PhotoPrintStudio.Core.Imaging;
using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Rendering;

public sealed class SheetRenderOptions
{
    public double Dpi { get; set; } = 150.0;
    public bool IsScreenPreview { get; set; } = true;
    public bool IsInteractiveDrag { get; set; } = false;
    public string? SelectedSlotId { get; set; }
    public bool ShowSafeMarginOverlay { get; set; } = true;
    public bool ShowBleedOverlay { get; set; } = true;
    public bool ShowIdGuidelinesOnSelected { get; set; } = true;
    public bool ShowFaceLandmarksOnSelected { get; set; } = false;
    public bool ShowClippingOverlay { get; set; } = false;
    public IReadOnlyList<DetectedFace>? SingleViewDetectedFaces { get; set; }
    public int SingleViewSourceWidth { get; set; }
    public int SingleViewSourceHeight { get; set; }
    public double SafeMarginMm { get; set; } = 4.0;
    public PrinterCalibrationProfile? PrinterCalibration { get; set; }
    public bool ApplyPrinterCalibrationTransform { get; set; } = false;
    public bool ApplyPrinterColorCompensation { get; set; } = false;
    public bool DuplexMirrorBackSide { get; set; } = false;
    public byte[]? SoftProofIccBytes { get; set; }
    public SoftProofOptions SoftProofOptions { get; set; } = SoftProofOptions.Default;
    public double OutputSharpeningAmount { get; set; } = 0.0;
}

public static class SheetRenderer
{
    private static readonly SKSamplingOptions HighQualitySampling = new(SKCubicResampler.CatmullRom);
    private static readonly SKSamplingOptions PreviewSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private static readonly SKSamplingOptions FastDragSampling = new(SKFilterMode.Linear, SKMipmapMode.None);
    private static readonly ConcurrentDictionary<string, SKTypeface> TypefaceCache = new(StringComparer.OrdinalIgnoreCase);

    public static SKTypeface GetCachedTypeface(string familyName) =>
        TypefaceCache.GetOrAdd(familyName ?? "", static f =>
            string.IsNullOrWhiteSpace(f)
                ? SKTypeface.Default
                : (SKTypeface.FromFamilyName(f) ?? SKTypeface.Default));

    /// <summary>
    /// 根据相纸物理尺寸与当前视口/缩放目标物理长边像素数，动态计算屏幕预览渲染 DPI，
    /// 保证在高分屏及放大视图下仍保持清晰锐利。
    /// </summary>
    public static double CalculatePreviewDpi(double paperWidthMm, double paperHeightMm, int targetLongEdgePx = 1920, int minClampPx = 1600)
    {
        double longEdgeInches = Math.Max(paperWidthMm, paperHeightMm) / UnitConverter.MmPerInch;
        if (longEdgeInches <= 0.1) return 240.0;
        int clampedPx = Math.Clamp(targetLongEdgePx, minClampPx, 3200);
        return Math.Min(clampedPx / longEdgeInches, 600.0);
    }

    public static SKBitmap RenderSheetBitmap(
        PrintSheet sheet,
        IReadOnlyDictionary<string, PhotoAsset> assetsById,
        Func<PhotoAsset, PhotoEditState, SKBitmap> resolveProcessedBitmap,
        SheetRenderOptions options)
    {
        int widthPx = UnitConverter.MmToPixelsRounded(sheet.PaperWidthMm, options.Dpi);
        int heightPx = UnitConverter.MmToPixelsRounded(sheet.PaperHeightMm, options.Dpi);

        var info = new SKImageInfo(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var bitmap = new SKBitmap(info);

        try
        {
            using var canvas = new SKCanvas(bitmap);
            RenderToCanvas(canvas, sheet, assetsById, resolveProcessedBitmap, options);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }

        if (!options.IsScreenPreview && options.OutputSharpeningAmount > 0.5)
        {
            FloatImageProcessor.ApplyOutputSharpeningInPlace(bitmap, options.OutputSharpeningAmount, (int)Math.Round(options.Dpi));
        }

        if (options.IsScreenPreview && options.SoftProofIccBytes is { Length: > 0 })
        {
            using var original = bitmap;
            bitmap = ExifAndColorHelper.ApplySoftProofIcc(original, options.SoftProofIccBytes, fastPreviewLut: true, options.SoftProofOptions);
        }

        return bitmap;
    }

    public static void RenderToCanvas(
        SKCanvas canvas,
        PrintSheet sheet,
        IReadOnlyDictionary<string, PhotoAsset> assetsById,
        Func<PhotoAsset, PhotoEditState, SKBitmap> resolveProcessedBitmap,
        SheetRenderOptions options)
    {
        float pxPerMm = (float)(options.Dpi / UnitConverter.MmPerInch);
        float paperW = (float)sheet.PaperWidthMm * pxPerMm;
        float paperH = (float)sheet.PaperHeightMm * pxPerMm;

        var bgColor = SKColor.TryParse(sheet.CanvasBackgroundHex, out var bg) ? bg : SKColors.White;
        canvas.Clear(bgColor);

        canvas.Save();

        // 应用物理打印校准变换（偏移 + 缩放 + 热升华过扫描补偿 + 双面翻页对齐）
        if (options.ApplyPrinterCalibrationTransform && options.PrinterCalibration != null)
        {
            var cal = options.PrinterCalibration;
            double overscan = Math.Clamp(cal.OverscanCompensationPercent / 100.0, 0.85, 1.15);
            float sx = (float)(cal.ScaleXPercent / 100.0 * overscan);
            float sy = (float)(cal.ScaleYPercent / 100.0 * overscan);
            float dxMm = (float)(cal.OffsetXMm + (options.DuplexMirrorBackSide ? cal.DuplexBackOffsetXMm : 0));
            float dyMm = (float)(cal.OffsetYMm + (options.DuplexMirrorBackSide ? cal.DuplexBackOffsetYMm : 0));

            canvas.Translate(dxMm * pxPerMm, dyMm * pxPerMm);
            canvas.Translate(paperW * 0.5f, paperH * 0.5f);
            canvas.Scale(sx, sy);
            canvas.Translate(-paperW * 0.5f, -paperH * 0.5f);
        }

        var sampling = options.IsScreenPreview
            ? (options.IsInteractiveDrag ? FastDragSampling : PreviewSampling)
            : HighQualitySampling;

        // 1. 绘制各个照片格位（同页内复用同一 PhotoAsset 的位图及单一 SKImage 实例，避免多图混排重复处理与每格重建图像包装）
        var resolvedCache = new Dictionary<PhotoAsset, (SKBitmap Bmp, SKImage Img)>(ReferenceEqualityComparer.Instance);
        try
        {
            foreach (var slot in sheet.Slots)
            {
                PhotoAsset? asset = null;
                if (!string.IsNullOrEmpty(slot.PhotoAssetId))
                    assetsById.TryGetValue(slot.PhotoAssetId, out asset);

                SKImage? processedImg = null;
                if (asset != null)
                {
                    if (!resolvedCache.TryGetValue(asset, out var entry))
                    {
                        var bmp = resolveProcessedBitmap(asset, BuildEffectiveEdit(asset.EditState, options));
                        bmp.SetImmutable();
                        var img = SKImage.FromBitmap(bmp);
                        entry = (bmp, img);
                        resolvedCache[asset] = entry;
                    }
                    processedImg = entry.Img;
                }
                DrawSlot(canvas, sheet, slot, asset, processedImg, options, pxPerMm, sampling);
            }
        }
        finally
        {
            foreach (var entry in resolvedCache.Values)
            {
                entry.Img.Dispose();
                entry.Bmp.Dispose();
            }
        }

        // 2. 绘制裁切辅助线 / 外置四角标 / 中线对折标
        DrawCropMarks(canvas, sheet, assetsById, pxPerMm);

        // 3. 绘制印前页脚标记 (Slug)
        if (sheet.ShowSlugFooter)
        {
            DrawSlugFooter(canvas, sheet, options, pxPerMm, paperW, paperH);
        }

        // 4. 屏幕预览辅助叠加层（安全边距、出血虚线、选中框）
        if (options.IsScreenPreview)
        {
            DrawPreviewOverlays(canvas, sheet, options, pxPerMm, paperW, paperH);
        }

        canvas.Restore();
    }

    private static void DrawSlot(
        SKCanvas canvas,
        PrintSheet sheet,
        LayoutSlot slot,
        PhotoAsset? asset,
        SKImage? processedImg,
        SheetRenderOptions options,
        float pxPerMm,
        SKSamplingOptions sampling)
    {
        float x = (float)slot.XMm * pxPerMm;
        float y = (float)slot.YMm * pxPerMm;
        float w = (float)slot.WidthMm * pxPerMm;
        float h = (float)slot.HeightMm * pxPerMm;
        float bleed = (float)slot.BleedMm * pxPerMm;
        float cornerR = (float)slot.CornerRadiusMm * pxPerMm;

        if (slot.PolaroidStyle != null)
        {
            DrawPolaroidSlot(canvas, slot, asset, processedImg, options, pxPerMm, sampling);
            return;
        }

        var drawRect = new SKRect(x - bleed, y - bleed, x + w + bleed, y + h + bleed);

        canvas.Save();
        if (cornerR > 0.5f)
        {
            using var rrect = new SKRoundRect(drawRect, cornerR);
            canvas.ClipRoundRect(rrect, SKClipOperation.Intersect, true);
        }
        else
        {
            canvas.ClipRect(drawRect);
        }

        if (bleed > 0)
        {
            foreach (var other in sheet.Slots.Where(s => !ReferenceEquals(s, slot)))
            {
                canvas.ClipRect(new SKRect((float)other.XMm * pxPerMm, (float)other.YMm * pxPerMm,
                    (float)(other.XMm + other.WidthMm) * pxPerMm, (float)(other.YMm + other.HeightMm) * pxPerMm), SKClipOperation.Difference);
            }
        }

        if (asset != null)
        {
            var effectiveEdit = BuildEffectiveEdit(asset.EditState, options);

            canvas.Save();
            if (Math.Abs(slot.SlotRotationDegrees) > 0.1)
            {
                canvas.RotateDegrees((float)slot.SlotRotationDegrees, x + w * 0.5f, y + h * 0.5f);
            }

            // 若格位旋转 90 度，则局部可用矩形宽高互换
            bool slotSwapped = Math.Abs(Math.Abs(slot.SlotRotationDegrees) - 90.0) < 1.0;
            float localW = slotSwapped ? h + bleed * 2 : w + bleed * 2;
            float localH = slotSwapped ? w + bleed * 2 : h + bleed * 2;
            var localRect = new SKRect(
                x + w * 0.5f - localW * 0.5f,
                y + h * 0.5f - localH * 0.5f,
                x + w * 0.5f + localW * 0.5f,
                y + h * 0.5f + localH * 0.5f);

            DrawFittedImage(canvas, processedImg!, localRect, effectiveEdit, slot, sampling, options.IsInteractiveDrag);

            if (options.IsScreenPreview && options.ShowFaceLandmarksOnSelected &&
                options.SelectedSlotId == slot.Id &&
                options.SingleViewDetectedFaces is { Count: > 0 } faces &&
                options.SingleViewSourceWidth > 0 && options.SingleViewSourceHeight > 0)
            {
                DrawDetectedFaceLandmarks(
                    canvas,
                    localRect,
                    effectiveEdit,
                    slot,
                    faces,
                    options.SingleViewSourceWidth,
                    options.SingleViewSourceHeight,
                    pxPerMm);
            }

            canvas.Restore();

            // 证件照构图参考线（仅预览模式选中格位且开启辅助线时显示）
            if (options.IsScreenPreview && options.ShowIdGuidelinesOnSelected &&
                options.SelectedSlotId == slot.Id && !string.IsNullOrEmpty(slot.IdPhotoSpecId))
            {
                var idSpec = IdPhotoSpec.BuiltInSpecs.FirstOrDefault(s => s.Id == slot.IdPhotoSpecId);
                if (idSpec != null && idSpec.HeadHeightMaxMm > 0)
                {
                    DrawIdPhotoGuidelines(canvas, new SKRect(x, y, x + w, y + h), idSpec, slot.SlotRotationDegrees, pxPerMm);
                }
            }
        }
        else if (options.IsScreenPreview)
        {
            DrawEmptySlotPlaceholder(canvas, new SKRect(x, y, x + w, y + h), slot.Label, pxPerMm);
        }

        // Before/After 对比角标或自定义标签
        if (!string.IsNullOrWhiteSpace(slot.CustomCaption))
        {
            DrawSlotCaptionPill(canvas, new SKRect(x, y, x + w, y + h), slot.CustomCaption!, pxPerMm);
        }

        canvas.Restore();
    }

    private static void DrawPolaroidSlot(
        SKCanvas canvas,
        LayoutSlot slot,
        PhotoAsset? asset,
        SKImage? processedImg,
        SheetRenderOptions options,
        float pxPerMm,
        SKSamplingOptions sampling)
    {
        var pStyle = slot.PolaroidStyle!;
        float x = (float)slot.XMm * pxPerMm;
        float y = (float)slot.YMm * pxPerMm;
        float slotW = (float)slot.WidthMm * pxPerMm;
        float slotH = (float)slot.HeightMm * pxPerMm;

        bool rotated = Math.Abs(Math.Abs(slot.SlotRotationDegrees) - 90.0) < 1.0;
        float cardW = rotated ? slotH : slotW;
        float cardH = rotated ? slotW : slotH;
        float cx = x + slotW * 0.5f;
        float cy = y + slotH * 0.5f;
        var cardRect = new SKRect(cx - cardW * 0.5f, cy - cardH * 0.5f, cx + cardW * 0.5f, cy + cardH * 0.5f);
        float cornerR = (float)slot.CornerRadiusMm * pxPerMm;

        canvas.Save();
        if (rotated)
            canvas.RotateDegrees((float)slot.SlotRotationDegrees, cx, cy);

        // 外卡圆角裁剪
        if (cornerR > 0.5f)
        {
            using var rrect = new SKRoundRect(cardRect, cornerR);
            canvas.ClipRoundRect(rrect, SKClipOperation.Intersect, true);
        }
        else
            canvas.ClipRect(cardRect);

        // 1. 相纸边框背景
        var frameColor = SKColor.TryParse(pStyle.FrameColorHex, out var fc) ? fc : SKColors.White;
        using (var framePaint = new SKPaint { Color = frameColor, Style = SKPaintStyle.Fill })
        {
            canvas.DrawRect(cardRect, framePaint);
        }

        // 2. 可选轻微纸质纹理（模拟细腻相纸颗粒感）
        if (pStyle.ShowPaperGrain)
        {
            DrawSubtlePaperGrain(canvas, cardRect, frameColor);
        }

        // 3. 画面内框窗口
        float mTop = (float)pStyle.MarginTopMm * pxPerMm;
        float mLeft = (float)pStyle.MarginLeftMm * pxPerMm;
        float mRight = (float)pStyle.MarginRightMm * pxPerMm;
        float mBottom = (float)pStyle.MarginBottomMm * pxPerMm;
        var photoRect = new SKRect(
            cardRect.Left + mLeft,
            cardRect.Top + mTop,
            cardRect.Right - mRight,
            cardRect.Bottom - mBottom);

        canvas.Save();
        canvas.ClipRect(photoRect);
        if (asset != null)
        {
            var effectiveEdit = BuildEffectiveEdit(asset.EditState, options);
            DrawFittedImage(canvas, processedImg!, photoRect, effectiveEdit, slot, sampling, options.IsInteractiveDrag);
        }
        else if (options.IsScreenPreview)
        {
            DrawEmptySlotPlaceholder(canvas, photoRect, slot.Label, pxPerMm);
        }
        canvas.Restore();

        // 4. 模拟实体拍立得相纸内嵌微阴影压痕
        if (pStyle.ShowInnerShadow)
        {
            using var shadowBorder = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 38),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1f, 0.25f * pxPerMm),
                IsAntialias = true
            };
            canvas.DrawRect(photoRect, shadowBorder);
        }

        // 5. 宽下巴文字 / 日期签名区
        string caption = ResolvePolaroidCaption(slot, asset, pStyle);
        if (!string.IsNullOrWhiteSpace(caption))
        {
            float chinTop = photoRect.Bottom;
            float chinHeight = cardRect.Bottom - chinTop;
            if (chinHeight > 2f)
            {
                var textColor = SKColor.TryParse(pStyle.TextColorHex, out var tc) ? tc : new SKColor(55, 65, 81);
                float fontSizePx = (float)(pStyle.FontSizePt / UnitConverter.PointsPerInch * options.Dpi);
                var typeface = GetCachedTypeface(pStyle.FontFamily);
                using var font = new SKFont(typeface, fontSizePx, 1f, 0f);
                using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };

                var lines = caption.Split(["\r\n", "\n"], StringSplitOptions.None);
                float lineHeight = fontSizePx * 1.28f;
                float totalTextH = lines.Length * lineHeight;
                float startY = chinTop + (chinHeight - totalTextH) * 0.5f + fontSizePx * 0.85f;

                var align = pStyle.TextAlignment switch
                {
                    PolaroidTextAlign.Left => SKTextAlign.Left,
                    PolaroidTextAlign.Right => SKTextAlign.Right,
                    _ => SKTextAlign.Center
                };
                float anchorX = pStyle.TextAlignment switch
                {
                    PolaroidTextAlign.Left => photoRect.Left + 1.5f * pxPerMm,
                    PolaroidTextAlign.Right => photoRect.Right - 1.5f * pxPerMm,
                    _ => cardRect.MidX
                };

                for (int i = 0; i < lines.Length; i++)
                {
                    canvas.DrawText(lines[i], anchorX, startY + i * lineHeight, align, font, textPaint);
                }
            }
        }

        // 屏幕预览下为了防止白底拍立得和白画布融为一体，额外画极浅预览外边线
        if (options.IsScreenPreview)
        {
            using var previewEdge = new SKPaint
            {
                Color = new SKColor(120, 130, 145, 95),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1f, 0.12f * pxPerMm)
            };
            canvas.DrawRect(cardRect, previewEdge);
        }

        canvas.Restore();
    }

    public static string ResolvePolaroidCaption(LayoutSlot slot, PhotoAsset? asset, PolaroidSlotStyle style)
    {
        string raw = slot.CustomCaption ?? style.CaptionTemplate ?? "";
        if (string.IsNullOrWhiteSpace(raw)) return "";

        string dateStr = asset?.DateTaken?.ToString("yyyy.MM.dd") ?? DateTime.Now.ToString("yyyy.MM.dd");
        string cameraStr = string.IsNullOrWhiteSpace(asset?.CameraModel) ? "PhotoPrintStudio" : asset.CameraModel;
        string fileStr = asset?.DisplayName ?? $"Photo #{slot.Index + 1}";

        return raw
            .Replace("{Date}", dateStr, StringComparison.OrdinalIgnoreCase)
            .Replace("{Camera}", cameraStr, StringComparison.OrdinalIgnoreCase)
            .Replace("{Filename}", fileStr, StringComparison.OrdinalIgnoreCase)
            .Replace("{Index}", (slot.Index + 1).ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static void DrawFittedImage(
        SKCanvas canvas,
        SKImage skImg,
        SKRect destRect,
        PhotoEditState edit,
        LayoutSlot slot,
        SKSamplingOptions sampling,
        bool isInteractiveDrag)
    {
        float iw = skImg.Width;
        float ih = skImg.Height;
        bool quarterSwap = (edit.QuarterTurns % 2) != 0;
        float effW = quarterSwap ? ih : iw;
        float effH = quarterSwap ? iw : ih;

        var fitMode = slot.FitModeOverride ?? edit.FitMode;
        float baseScale = fitMode == FitMode.Cover
            ? Math.Max(destRect.Width / effW, destRect.Height / effH)
            : Math.Min(destRect.Width / effW, destRect.Height / effH);

        float zoom = (float)Math.Clamp(edit.Zoom * slot.SlotZoom, 0.5, 8.0);
        float scale = baseScale * zoom;

        float panX = (float)Math.Clamp(edit.PanX + slot.SlotPanX, -1.5, 1.5) * destRect.Width * 0.5f;
        float panY = (float)Math.Clamp(edit.PanY + slot.SlotPanY, -1.5, 1.5) * destRect.Height * 0.5f;

        canvas.Save();
        canvas.Translate(destRect.MidX + panX, destRect.MidY + panY);
        canvas.Scale(scale);
        if (edit.FlipHorizontal) canvas.Scale(-1, 1);
        if (edit.FlipVertical) canvas.Scale(1, -1);
        float totalAngle = (float)(edit.QuarterTurns * 90.0 + edit.RotationDegrees);
        if (Math.Abs(totalAngle) > 0.01f)
            canvas.RotateDegrees(totalAngle);

        // 交互拖动帧保持 FastDragSampling；静置高精度预览放大/轻度缩小时使用 CatmullRom 保持锐利
        var drawSampling = isInteractiveDrag
            ? FastDragSampling
            : (sampling.Equals(PreviewSampling) && scale >= 0.45f ? HighQualitySampling : sampling);
        canvas.DrawImage(skImg, -iw * 0.5f, -ih * 0.5f, drawSampling);
        if (edit.FormalSuit != FormalSuitStyle.None)
            FormalSuitRenderer.DrawSuitInCenteredImageSpace(canvas, iw, ih, edit);
        canvas.Restore();
    }

    private static void DrawCropMarks(SKCanvas canvas, PrintSheet sheet,
        IReadOnlyDictionary<string, PhotoAsset> assetsById, float pxPerMm)
    {
        if (sheet.CropMarkStyle == CropMarkStyle.None && !sheet.ShowCenterFoldTick) return;

        var lineColor = SKColor.TryParse(sheet.CropMarkColorHex, out var cc) ? cc : new SKColor(136, 136, 136);
        float strokeW = Math.Max(1f, (float)sheet.CropMarkThicknessMm * pxPerMm);
        using var paint = new SKPaint
        {
            Color = lineColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeW,
            IsAntialias = true
        };

        using var dashEffect = sheet.CropMarkStyle == CropMarkStyle.DashedBorder
            ? SKPathEffect.CreateDash([2.0f * pxPerMm, 1.8f * pxPerMm], 0)
            : null;
        if (dashEffect != null)
        {
            paint.PathEffect = dashEffect;
        }

        if (sheet.CropMarkStyle is CropMarkStyle.DashedBorder or CropMarkStyle.SolidThinBorder)
        {
            foreach (var s in sheet.Slots)
            {
                var r = new SKRect((float)s.XMm * pxPerMm, (float)s.YMm * pxPerMm, (float)(s.XMm + s.WidthMm) * pxPerMm, (float)(s.YMm + s.HeightMm) * pxPerMm);
                if (s.CornerRadiusMm > 0.5)
                    canvas.DrawRoundRect(r, (float)s.CornerRadiusMm * pxPerMm, (float)s.CornerRadiusMm * pxPerMm, paint);
                else
                    canvas.DrawRect(r, paint);
            }
        }
        else if (sheet.CropMarkStyle is CropMarkStyle.OuterCornerTicks or CropMarkStyle.WhiteBackgroundGuide)
        {
            bool whiteBackground = sheet.CropMarkStyle == CropMarkStyle.WhiteBackgroundGuide;
            double gapMm = whiteBackground ? 0.65 : 0.6;
            double tickMm = whiteBackground ? 1.5 : 2.6;
            using var whiteBackgroundDash = whiteBackground
                ? SKPathEffect.CreateDash([1.5f * pxPerMm, 1.0f * pxPerMm], 0)
                : null;
            using var whiteBackgroundPaint = whiteBackground ? new SKPaint
            {
                Color = new SKColor(142, 161, 181),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.25f * pxPerMm,
                IsAntialias = true,
                PathEffect = whiteBackgroundDash
            } : null;
            var tickPaint = whiteBackgroundPaint ?? paint;
            var drawnTicks = new HashSet<(bool isHoriz, int axisKey, int aKey, int bKey)>();

            foreach (var s in sheet.Slots)
            {
                if (whiteBackground && (s.PhotoAssetId is not string assetId || !assetsById.ContainsKey(assetId)))
                    continue;

                double l = s.XMm;
                double t = s.YMm;
                double r = s.XMm + s.WidthMm;
                double b = s.YMm + s.HeightMm;

                // Top-left
                TryDrawClearAxisTick(canvas, sheet, pxPerMm, tickPaint, drawnTicks, isHoriz: true, fixedMm: t, edgeMm: l, dir: -1, gapMm, tickMm, whiteBackground);
                TryDrawClearAxisTick(canvas, sheet, pxPerMm, tickPaint, drawnTicks, isHoriz: false, fixedMm: l, edgeMm: t, dir: -1, gapMm, tickMm, whiteBackground);
                // Top-right
                TryDrawClearAxisTick(canvas, sheet, pxPerMm, tickPaint, drawnTicks, isHoriz: true, fixedMm: t, edgeMm: r, dir: 1, gapMm, tickMm, whiteBackground);
                TryDrawClearAxisTick(canvas, sheet, pxPerMm, tickPaint, drawnTicks, isHoriz: false, fixedMm: r, edgeMm: t, dir: -1, gapMm, tickMm, whiteBackground);
                // Bottom-left
                TryDrawClearAxisTick(canvas, sheet, pxPerMm, tickPaint, drawnTicks, isHoriz: true, fixedMm: b, edgeMm: l, dir: -1, gapMm, tickMm, whiteBackground);
                TryDrawClearAxisTick(canvas, sheet, pxPerMm, tickPaint, drawnTicks, isHoriz: false, fixedMm: l, edgeMm: b, dir: 1, gapMm, tickMm, whiteBackground);
                // Bottom-right
                TryDrawClearAxisTick(canvas, sheet, pxPerMm, tickPaint, drawnTicks, isHoriz: true, fixedMm: b, edgeMm: r, dir: 1, gapMm, tickMm, whiteBackground);
                TryDrawClearAxisTick(canvas, sheet, pxPerMm, tickPaint, drawnTicks, isHoriz: false, fixedMm: r, edgeMm: b, dir: 1, gapMm, tickMm, whiteBackground);
            }
        }

        if (sheet.CropMarkStyle == CropMarkStyle.CenterFoldTicks || sheet.ShowCenterFoldTick)
        {
            float paperW = (float)sheet.PaperWidthMm * pxPerMm;
            float paperH = (float)sheet.PaperHeightMm * pxPerMm;
            bool horizSplit = sheet.PaperWidthMm >= sheet.PaperHeightMm;
            float tickLen = 4.0f * pxPerMm;

            using var centerDash = SKPathEffect.CreateDash([1.6f * pxPerMm, 2.2f * pxPerMm], 0);
            using var centerPaint = new SKPaint
            {
                Color = new SKColor(lineColor.Red, lineColor.Green, lineColor.Blue, 180),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeW,
                PathEffect = centerDash
            };

            if (horizSplit)
            {
                float mx = paperW * 0.5f;
                canvas.DrawLine(mx, 0, mx, tickLen, paint);
                canvas.DrawLine(mx, paperH - tickLen, mx, paperH, paint);
                canvas.DrawLine(mx, tickLen, mx, paperH - tickLen, centerPaint);
            }
            else
            {
                float my = paperH * 0.5f;
                canvas.DrawLine(0, my, tickLen, my, paint);
                canvas.DrawLine(paperW - tickLen, my, paperW, my, paint);
                canvas.DrawLine(tickLen, my, paperW - tickLen, my, centerPaint);
            }
        }
    }

    private static void TryDrawClearAxisTick(
        SKCanvas canvas,
        PrintSheet sheet,
        float pxPerMm,
        SKPaint paint,
        HashSet<(bool isHoriz, int axisKey, int aKey, int bKey)> drawnTicks,
        bool isHoriz,
        double fixedMm,
        double edgeMm,
        int dir,
        double gapMm,
        double tickMm,
        bool extendAtOpenEnd)
    {
        const double pad = 0.25;
        const double edgeTol = 0.05;
        const double minVisibleMm = 0.8;
        const double extendedTickMm = 12.0;

        double paperLimit = isHoriz ? sheet.PaperWidthMm : sheet.PaperHeightMm;
        double fixedLimit = isHoriz ? sheet.PaperHeightMm : sheet.PaperWidthMm;
        if (fixedMm < edgeTol || fixedMm > fixedLimit - edgeTol) return;

        double startMm = edgeMm + dir * gapMm;
        double maxLengthMm = extendAtOpenEnd ? extendedTickMm : tickMm;
        double endMm = dir > 0
            ? Math.Min(paperLimit, startMm + maxLengthMm)
            : Math.Max(0.0, startMm - maxLengthMm);

        if (dir > 0 && endMm <= startMm + 0.01) return;
        if (dir < 0 && endMm >= startMm - 0.01) return;

        double firstPhotoMm = paperLimit;
        double lastPhotoMm = 0;
        // 防止裁切线侵入相邻格位的图像区域（如紧贴无间距排布时）
        foreach (var other in sheet.Slots)
        {
            double oFixMin = (isHoriz ? other.YMm : other.XMm) - pad;
            double oFixMax = (isHoriz ? other.YMm + other.HeightMm : other.XMm + other.WidthMm) + pad;
            if (fixedMm < oFixMin || fixedMm > oFixMax) continue;

            double oAxMin = (isHoriz ? other.XMm : other.YMm) - pad;
            double oAxMax = (isHoriz ? other.XMm + other.WidthMm : other.YMm + other.HeightMm) + pad;
            firstPhotoMm = Math.Min(firstPhotoMm, oAxMin + pad);
            lastPhotoMm = Math.Max(lastPhotoMm, oAxMax - pad);

            if (startMm >= oAxMin && startMm <= oAxMax) return;

            if (dir > 0 && oAxMin > startMm && oAxMin < endMm)
                endMm = oAxMin;
            else if (dir < 0 && oAxMax < startMm && oAxMax > endMm)
                endMm = oAxMax;
        }

        if (extendAtOpenEnd)
        {
            bool outside = dir > 0 ? edgeMm >= lastPhotoMm - edgeTol : edgeMm <= firstPhotoMm + edgeTol;
            double oppositeMarginMm = dir > 0 ? firstPhotoMm : paperLimit - lastPhotoMm;
            if (!outside || oppositeMarginMm >= gapMm + minVisibleMm)
                endMm = dir > 0 ? Math.Min(endMm, startMm + tickMm) : Math.Max(endMm, startMm - tickMm);
        }

        if (Math.Abs(endMm - startMm) < minVisibleMm) return;

        double segMin = Math.Min(startMm, endMm);
        double segMax = Math.Max(startMm, endMm);
        var key = (isHoriz, (int)Math.Round(fixedMm * 20), (int)Math.Round(segMin * 20), (int)Math.Round(segMax * 20));
        if (!drawnTicks.Add(key)) return;

        // 虚线的第一段始终从靠近照片的一端开始，纸边截短时也保留切点。
        float startPx = (float)startMm * pxPerMm;
        float endPx = (float)endMm * pxPerMm;
        if (isHoriz)
            canvas.DrawLine(startPx, (float)fixedMm * pxPerMm, endPx, (float)fixedMm * pxPerMm, paint);
        else
            canvas.DrawLine((float)fixedMm * pxPerMm, startPx, (float)fixedMm * pxPerMm, endPx, paint);
    }

    private static void DrawSlugFooter(SKCanvas canvas, PrintSheet sheet, SheetRenderOptions options, float pxPerMm, float paperW, float paperH)
    {
        float barH = 3.2f * pxPerMm;
        float fontSize = 1.9f * pxPerMm;
        var typeface = GetCachedTypeface("Microsoft YaHei UI");
        using var font = new SKFont(typeface, fontSize, 1f, 0f);
        using var textPaint = new SKPaint { Color = new SKColor(95, 100, 110), IsAntialias = true };

        string text = string.IsNullOrWhiteSpace(sheet.SlugCustomText)
            ? $"{sheet.SheetName} | {sheet.PaperWidthMm:0.#}×{sheet.PaperHeightMm:0.#}mm | {(int)options.Dpi} DPI | {DateTime.Now:yyyy-MM-dd HH:mm}"
            : sheet.SlugCustomText;

        canvas.DrawText(text, 2.5f * pxPerMm, paperH - 0.8f * pxPerMm, SKTextAlign.Left, font, textPaint);

        // 右下角 11 级微缩灰阶测试条
        float stepW = 2.4f * pxPerMm;
        float stepH = 1.8f * pxPerMm;
        float startX = paperW - 2.5f * pxPerMm - 11 * stepW;
        float y = paperH - barH + 0.5f * pxPerMm;
        using var patchPaint = new SKPaint { Style = SKPaintStyle.Fill };
        for (int i = 0; i <= 10; i++)
        {
            byte v = (byte)Math.Round(i * 25.5);
            patchPaint.Color = new SKColor(v, v, v);
            canvas.DrawRect(startX + i * stepW, y, stepW, stepH, patchPaint);
        }
        using var borderPaint = new SKPaint { Color = new SKColor(150, 150, 150), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(0.8f, 0.1f * pxPerMm) };
        canvas.DrawRect(startX, y, 11 * stepW, stepH, borderPaint);
    }

    private static void DrawPreviewOverlays(SKCanvas canvas, PrintSheet sheet, SheetRenderOptions options, float pxPerMm, float paperW, float paperH)
    {
        // 打印机硬件安全边距提示框
        if (options.ShowSafeMarginOverlay && options.SafeMarginMm > 0.2)
        {
            float m = (float)options.SafeMarginMm * pxPerMm;
            using var safeDash = SKPathEffect.CreateDash([2.5f * pxPerMm, 2.0f * pxPerMm], 0);
            using var safePaint = new SKPaint
            {
                Color = new SKColor(239, 68, 68, 125),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1f, 0.18f * pxPerMm),
                PathEffect = safeDash
            };
            canvas.DrawRect(m, m, Math.Max(1, paperW - m * 2), Math.Max(1, paperH - m * 2), safePaint);
        }

        // 出血区域蓝框与选中格位高亮
        using var bleedDash = options.ShowBleedOverlay ? SKPathEffect.CreateDash([1.8f * pxPerMm, 1.5f * pxPerMm], 0) : null;
        foreach (var s in sheet.Slots)
        {
            float x = (float)s.XMm * pxPerMm;
            float y = (float)s.YMm * pxPerMm;
            float w = (float)s.WidthMm * pxPerMm;
            float h = (float)s.HeightMm * pxPerMm;

            if (options.ShowBleedOverlay && s.BleedMm > 0.05)
            {
                float b = (float)s.BleedMm * pxPerMm;
                using var bleedPaint = new SKPaint
                {
                    Color = new SKColor(59, 130, 246, 130),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max(1f, 0.15f * pxPerMm),
                    PathEffect = bleedDash
                };
                canvas.DrawRect(x - b, y - b, w + b * 2, h + b * 2, bleedPaint);
            }

            if (options.SelectedSlotId == s.Id)
            {
                using var selPaint = new SKPaint
                {
                    Color = new SKColor(59, 130, 246, 245),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max(2.2f, 0.45f * pxPerMm)
                };
                canvas.DrawRect(x, y, w, h, selPaint);

                // 绘制商业软件风格的四角选中控制块
                float handleSize = Math.Clamp(1.6f * pxPerMm, 5f, 11f);
                float halfH = handleSize * 0.5f;
                using var handleFill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
                using var handleStroke = new SKPaint { Color = new SKColor(37, 99, 235), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };

                SKPoint[] corners = [new(x, y), new(x + w, y), new(x, y + h), new(x + w, y + h)];
                foreach (var c in corners)
                {
                    var hr = new SKRect(c.X - halfH, c.Y - halfH, c.X + halfH, c.Y + halfH);
                    canvas.DrawRect(hr, handleFill);
                    canvas.DrawRect(hr, handleStroke);
                }
            }

            // 格位编号角标（多格排版时方便定位，如 #1, #2）
            if (sheet.Slots.Count > 1)
            {
                bool isSel = options.SelectedSlotId == s.Id;
                float badgeSize = Math.Clamp(3.6f * pxPerMm, 14f, 24f);
                float fontSize = badgeSize * 0.54f;
                string idxText = $"{s.Index + 1}";
                using var badgeBg = new SKPaint
                {
                    Color = isSel ? new SKColor(37, 99, 235, 225) : new SKColor(15, 23, 42, 135),
                    IsAntialias = true
                };
                var typeface = GetCachedTypeface("Segoe UI");
                using var font = new SKFont(typeface, fontSize, 1f, 0f);
                using var fg = new SKPaint { Color = SKColors.White, IsAntialias = true };

                var br = new SKRect(x + 1.0f * pxPerMm, y + 1.0f * pxPerMm,
                    x + 1.0f * pxPerMm + badgeSize, y + 1.0f * pxPerMm + badgeSize * 0.8f);
                canvas.DrawRoundRect(br, 3f, 3f, badgeBg);
                canvas.DrawText(idxText, br.MidX, br.MidY + fontSize * 0.36f, SKTextAlign.Center, font, fg);
            }
        }
    }

    private static void DrawIdPhotoGuidelines(SKCanvas canvas, SKRect rect, IdPhotoSpec spec, double slotRotDeg, float pxPerMm)
    {
        canvas.Save();
        if (Math.Abs(slotRotDeg) > 0.1)
            canvas.RotateDegrees((float)slotRotDeg, rect.MidX, rect.MidY);

        bool swapped = Math.Abs(Math.Abs(slotRotDeg) - 90.0) < 1.0;
        float w = swapped ? rect.Height : rect.Width;
        float h = swapped ? rect.Width : rect.Height;
        var upright = new SKRect(rect.MidX - w * 0.5f, rect.MidY - h * 0.5f, rect.MidX + w * 0.5f, rect.MidY + h * 0.5f);

        float topMargin = (float)spec.TopMarginMm * pxPerMm;
        float eyeY = upright.Top + (float)(spec.EyeLineRatioFromTop * upright.Height);
        float chinY = upright.Top + (float)(spec.ChinLineRatioFromTop * upright.Height);

        using var guideDash = SKPathEffect.CreateDash([1.6f * pxPerMm, 1.4f * pxPerMm], 0);
        using var guideGreen = new SKPaint
        {
            Color = new SKColor(16, 185, 129, 200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, 0.2f * pxPerMm),
            PathEffect = guideDash
        };
        using var axisDash = SKPathEffect.CreateDash([2f * pxPerMm, 2f * pxPerMm], 0);
        using var axisBlue = new SKPaint
        {
            Color = new SKColor(56, 189, 248, 175),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, 0.16f * pxPerMm),
            PathEffect = axisDash
        };

        // 头顶参考线、双眼线、下巴线、中心对称线
        canvas.DrawLine(upright.Left, upright.Top + topMargin, upright.Right, upright.Top + topMargin, guideGreen);
        canvas.DrawLine(upright.Left, eyeY, upright.Right, eyeY, guideGreen);
        canvas.DrawLine(upright.Left, chinY, upright.Right, chinY, guideGreen);
        canvas.DrawLine(upright.MidX, upright.Top, upright.MidX, upright.Bottom, axisBlue);

        // 椭圆人头参考圈
        float headW = (float)((spec.HeadHeightMinMm + spec.HeadHeightMaxMm) * 0.34) * pxPerMm;
        float headTop = upright.Top + topMargin;
        float headBot = chinY;
        var headOval = new SKRect(upright.MidX - headW * 0.5f, headTop, upright.MidX + headW * 0.5f, headBot);
        canvas.DrawOval(headOval, guideGreen);

        canvas.Restore();
    }

    private static void DrawDetectedFaceLandmarks(
        SKCanvas canvas,
        SKRect localRect,
        PhotoEditState edit,
        LayoutSlot slot,
        IReadOnlyList<DetectedFace> faces,
        int srcW,
        int srcH,
        float pxPerMm)
    {
        var fitMode = slot.FitModeOverride ?? edit.FitMode;
        using var boxDash = SKPathEffect.CreateDash([1.4f * pxPerMm, 1.2f * pxPerMm], 0);
        using var boxPaint = new SKPaint
        {
            Color = new SKColor(56, 189, 248, 180),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.2f, 0.14f * pxPerMm),
            PathEffect = boxDash,
            IsAntialias = true
        };
        using var eyeLinePaint = new SKPaint
        {
            Color = new SKColor(250, 204, 21, 185),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.1f, 0.12f * pxPerMm),
            IsAntialias = true
        };
        using var eyeFill = new SKPaint { Color = new SKColor(250, 204, 21, 235), IsAntialias = true };
        using var keyFill = new SKPaint { Color = new SKColor(56, 189, 248, 225), IsAntialias = true };
        using var haloStroke = new SKPaint { Color = new SKColor(15, 23, 42, 200), Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, IsAntialias = true };

        float eyeRadius = Math.Clamp(0.45f * pxPerMm, 3.0f, 6.0f);
        float keyRadius = Math.Clamp(0.35f * pxPerMm, 2.4f, 4.8f);

        foreach (var f in faces)
        {
            var box = PortraitAutoFrameHelper.TransformRawBoxToDestRect(
                f, srcW, srcH, edit, localRect, fitMode, slot.SlotZoom, slot.SlotPanX, slot.SlotPanY);
            canvas.DrawRect(box, boxPaint);

            var re = PortraitAutoFrameHelper.TransformRawPointToDestRect(f.RightEye, srcW, srcH, edit, localRect, fitMode, slot.SlotZoom, slot.SlotPanX, slot.SlotPanY);
            var le = PortraitAutoFrameHelper.TransformRawPointToDestRect(f.LeftEye, srcW, srcH, edit, localRect, fitMode, slot.SlotZoom, slot.SlotPanX, slot.SlotPanY);
            var nose = PortraitAutoFrameHelper.TransformRawPointToDestRect(f.NoseTip, srcW, srcH, edit, localRect, fitMode, slot.SlotZoom, slot.SlotPanX, slot.SlotPanY);
            var rm = PortraitAutoFrameHelper.TransformRawPointToDestRect(f.RightMouth, srcW, srcH, edit, localRect, fitMode, slot.SlotZoom, slot.SlotPanX, slot.SlotPanY);
            var lm = PortraitAutoFrameHelper.TransformRawPointToDestRect(f.LeftMouth, srcW, srcH, edit, localRect, fitMode, slot.SlotZoom, slot.SlotPanX, slot.SlotPanY);

            canvas.DrawLine(re, le, eyeLinePaint);
            foreach (var ep in new[] { re, le })
            {
                canvas.DrawCircle(ep, eyeRadius, eyeFill);
                canvas.DrawCircle(ep, eyeRadius, haloStroke);
            }
            foreach (var kp in new[] { nose, rm, lm })
            {
                canvas.DrawCircle(kp, keyRadius, keyFill);
                canvas.DrawCircle(kp, keyRadius, haloStroke);
            }
        }
    }

    private static void DrawEmptySlotPlaceholder(SKCanvas canvas, SKRect rect, string label, float pxPerMm)
    {
        using var fill = new SKPaint { Color = new SKColor(241, 245, 249), Style = SKPaintStyle.Fill };
        canvas.DrawRect(rect, fill);

        using var emptyDash = SKPathEffect.CreateDash([2f * pxPerMm, 2f * pxPerMm], 0);
        using var stroke = new SKPaint
        {
            Color = new SKColor(148, 163, 184),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, 0.2f * pxPerMm),
            PathEffect = emptyDash
        };
        canvas.DrawRect(rect, stroke);

        var typeface = GetCachedTypeface("Microsoft YaHei UI");
        float fontSize = Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.12f, 9f, 18f);
        using var font = new SKFont(typeface, fontSize, 1f, 0f);
        using var txtPaint = new SKPaint { Color = new SKColor(100, 116, 139), IsAntialias = true };
        string txt = string.IsNullOrWhiteSpace(label) ? "拖入照片" : label;
        canvas.DrawText(txt, rect.MidX, rect.MidY + fontSize * 0.35f, SKTextAlign.Center, font, txtPaint);
    }

    private static void DrawSlotCaptionPill(SKCanvas canvas, SKRect rect, string text, float pxPerMm)
    {
        float fontSize = Math.Clamp(2.6f * pxPerMm, 8f, 36f);
        var typeface = GetCachedTypeface("Microsoft YaHei UI");
        using var font = new SKFont(typeface, fontSize, 1f, 0f);
        float textW = font.MeasureText(text);
        float padX = 2.0f * pxPerMm;
        float padY = 0.8f * pxPerMm;
        var pill = new SKRect(
            rect.MidX - textW * 0.5f - padX,
            rect.Bottom - fontSize - padY * 2 - 2.5f * pxPerMm,
            rect.MidX + textW * 0.5f + padX,
            rect.Bottom - 2.5f * pxPerMm);

        using var bg = new SKPaint { Color = new SKColor(15, 23, 42, 185), IsAntialias = true };
        canvas.DrawRoundRect(pill, 1.2f * pxPerMm, 1.2f * pxPerMm, bg);
        using var fg = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText(text, pill.MidX, pill.Bottom - padY - fontSize * 0.15f, SKTextAlign.Center, font, fg);
    }

    private static void DrawSubtlePaperGrain(SKCanvas canvas, SKRect cardRect, SKColor baseColor)
    {
        bool darkBg = (baseColor.Red * 0.299 + baseColor.Green * 0.587 + baseColor.Blue * 0.114) < 128;
        byte alpha = 14;
        var speckColor = darkBg ? new SKColor(255, 255, 255, alpha) : new SKColor(120, 110, 95, alpha);
        using var p = new SKPaint { Color = speckColor, StrokeWidth = 1f };

        int step = Math.Max(4, (int)(Math.Min(cardRect.Width, cardRect.Height) / 45f));
        int cols = Math.Max(1, ((int)cardRect.Right - (int)cardRect.Left + step - 1) / step);
        int rows = Math.Max(1, ((int)cardRect.Bottom - (int)cardRect.Top + step - 1) / step);
        var pts = new List<SKPoint>((cols * rows) / 3);
        uint seed = 2166136261;
        for (int y = (int)cardRect.Top; y < (int)cardRect.Bottom; y += step)
        {
            for (int x = (int)cardRect.Left; x < (int)cardRect.Right; x += step)
            {
                seed = seed * 16777619 ^ (uint)(x * 73856093 ^ y * 19349663);
                if ((seed & 3) == 0)
                {
                    pts.Add(new SKPoint(x + (seed % (uint)step), y + ((seed >> 4) % (uint)step)));
                }
            }
        }
        if (pts.Count > 0)
            canvas.DrawPoints(SKPointMode.Points, [.. pts], p);
    }

    private static PhotoEditState BuildEffectiveEdit(PhotoEditState original, SheetRenderOptions options)
    {
        if (!options.ApplyPrinterColorCompensation || options.PrinterCalibration == null)
            return original;

        var cal = options.PrinterCalibration;
        if (Math.Abs(cal.BrightnessCompensation) < 0.01 &&
            Math.Abs(cal.GammaCompensation - 1.0) < 0.005 &&
            Math.Abs(cal.TemperatureCompensation) < 0.01)
            return original;

        var copy = original.Clone();
        copy.Brightness = Math.Clamp(copy.Brightness + cal.BrightnessCompensation, -100, 100);
        copy.Gamma = Math.Clamp(copy.Gamma * cal.GammaCompensation, 0.4, 2.2);
        copy.Temperature = Math.Clamp(copy.Temperature + cal.TemperatureCompensation, -100, 100);
        return copy;
    }
}
