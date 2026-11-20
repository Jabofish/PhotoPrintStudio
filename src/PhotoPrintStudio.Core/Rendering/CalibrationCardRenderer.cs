using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Rendering;

/// <summary>
/// 生成三合一工业打印物理校准测试卡：
/// 1) 10mm 精确网格与 50mm/100mm 直尺刻度条（反算打印机 X/Y 缩放偏差）；
/// 2) 四边 1..10mm 边缘吃边标尺（实测无边距过扫描或硬边距裁剪量）；
/// 3) 21 级灰阶与标准肤色/原色测试靶 + 正中十字校准靶。
/// </summary>
public static class CalibrationCardRenderer
{
    public static SKBitmap RenderCalibrationCard(
        double paperWidthMm,
        double paperHeightMm,
        double dpi = 300.0,
        PrinterCalibrationProfile? calibration = null)
    {
        int wPx = UnitConverter.MmToPixelsRounded(paperWidthMm, dpi);
        int hPx = UnitConverter.MmToPixelsRounded(paperHeightMm, dpi);

        var bmp = new SKBitmap(new SKImageInfo(wPx, hPx, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);

        float pxPerMm = (float)(dpi / UnitConverter.MmPerInch);
        float w = (float)paperWidthMm * pxPerMm;
        float h = (float)paperHeightMm * pxPerMm;

        if (calibration != null)
        {
            double overscan = Math.Clamp(calibration.OverscanCompensationPercent / 100.0, 0.85, 1.15);
            float sx = (float)(calibration.ScaleXPercent / 100.0 * overscan);
            float sy = (float)(calibration.ScaleYPercent / 100.0 * overscan);
            canvas.Translate((float)calibration.OffsetXMm * pxPerMm, (float)calibration.OffsetYMm * pxPerMm);
            canvas.Translate(w * 0.5f, h * 0.5f);
            canvas.Scale(sx, sy);
            canvas.Translate(-w * 0.5f, -h * 0.5f);
        }

        var typeface = SheetRenderer.GetCachedTypeface("Microsoft YaHei UI");
        using var smallFont = new SKFont(typeface, 2.2f * pxPerMm, 1f, 0f);
        using var titleFont = new SKFont(typeface, 3.2f * pxPerMm, 1f, 0f);
        using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        // 1. 10mm 网格线
        using var gridPaint = new SKPaint
        {
            Color = new SKColor(215, 222, 232),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, 0.15f * pxPerMm)
        };
        for (double xm = 10; xm < paperWidthMm; xm += 10)
        {
            float x = (float)xm * pxPerMm;
            canvas.DrawLine(x, 0, x, h, gridPaint);
        }
        for (double ym = 10; ym < paperHeightMm; ym += 10)
        {
            float y = (float)ym * pxPerMm;
            canvas.DrawLine(0, y, w, y, gridPaint);
        }

        // 2. 四边 1..10mm 吃边刻度尺
        using var edgePaint = new SKPaint
        {
            Color = new SKColor(220, 38, 38),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, 0.25f * pxPerMm)
        };
        for (int mm = 1; mm <= 10; mm++)
        {
            float d = mm * pxPerMm;
            float tick = (mm % 5 == 0 ? 6f : 3.5f) * pxPerMm;
            // Top & Bottom center edge ticks
            canvas.DrawLine(w * 0.5f - tick * 0.5f, d, w * 0.5f + tick * 0.5f, d, edgePaint);
            canvas.DrawLine(w * 0.5f - tick * 0.5f, h - d, w * 0.5f + tick * 0.5f, h - d, edgePaint);
            // Left & Right center edge ticks
            canvas.DrawLine(d, h * 0.5f - tick * 0.5f, d, h * 0.5f + tick * 0.5f, edgePaint);
            canvas.DrawLine(w - d, h * 0.5f - tick * 0.5f, w - d, h * 0.5f + tick * 0.5f, edgePaint);
        }

        // 3. 精确 50mm/100mm 物理长度校准线
        var (ruleLenMm, ruleVertMm) = GetRuleLengths(paperWidthMm, paperHeightMm);
        float rx0 = w * 0.5f - (ruleLenMm * 0.5f) * pxPerMm;
        float rx1 = w * 0.5f + (ruleLenMm * 0.5f) * pxPerMm;
        float ry = h * 0.30f;

        using var blackStroke = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, 0.35f * pxPerMm) };
        canvas.DrawLine(rx0, ry, rx1, ry, blackStroke);
        canvas.DrawLine(rx0, ry - 3 * pxPerMm, rx0, ry + 3 * pxPerMm, blackStroke);
        canvas.DrawLine(rx1, ry - 3 * pxPerMm, rx1, ry + 3 * pxPerMm, blackStroke);
        canvas.DrawText($"水平基准标尺: {ruleLenMm:0} mm (直尺实测此两竖线距离)", w * 0.5f, ry - 4.2f * pxPerMm, SKTextAlign.Center, smallFont, textPaint);

        float vx = w * 0.22f;
        float vy0 = h * 0.5f - (ruleVertMm * 0.5f) * pxPerMm;
        float vy1 = h * 0.5f + (ruleVertMm * 0.5f) * pxPerMm;
        canvas.DrawLine(vx, vy0, vx, vy1, blackStroke);
        canvas.DrawLine(vx - 3 * pxPerMm, vy0, vx + 3 * pxPerMm, vy0, blackStroke);
        canvas.DrawLine(vx - 3 * pxPerMm, vy1, vx + 3 * pxPerMm, vy1, blackStroke);
        canvas.DrawText($"纵向 {ruleVertMm:0} mm", vx + 4 * pxPerMm, h * 0.5f, SKTextAlign.Left, smallFont, textPaint);

        // 4. 中心十字校准靶
        float cx = w * 0.5f;
        float cy = h * 0.5f;
        canvas.DrawCircle(cx, cy, 4 * pxPerMm, edgePaint);
        canvas.DrawLine(cx - 8 * pxPerMm, cy, cx + 8 * pxPerMm, cy, blackStroke);
        canvas.DrawLine(cx, cy - 8 * pxPerMm, cx, cy + 8 * pxPerMm, blackStroke);

        // 5. 标题
        canvas.DrawText("PhotoPrintStudio 物理尺寸与色彩校准测试卡", cx, 14f * pxPerMm, SKTextAlign.Center, titleFont, textPaint);
        canvas.DrawText($"纸张: {paperWidthMm:0.#}×{paperHeightMm:0.#} mm | DPI: {(int)dpi}", cx, 18.5f * pxPerMm, SKTextAlign.Center, smallFont, textPaint);

        // 6. 21 级灰阶块
        float grayTotalW = Math.Min(w * 0.72f, 110f * pxPerMm);
        float patchW = grayTotalW / 21f;
        float patchH = 6.5f * pxPerMm;
        float gx0 = cx - grayTotalW * 0.5f;
        float gy0 = h * 0.68f;
        using var fillPaint = new SKPaint { Style = SKPaintStyle.Fill };
        for (int i = 0; i < 21; i++)
        {
            byte v = (byte)Math.Round(i / 20.0 * 255.0);
            fillPaint.Color = new SKColor(v, v, v);
            canvas.DrawRect(gx0 + i * patchW, gy0, patchW, patchH, fillPaint);
        }
        canvas.DrawRect(gx0, gy0, grayTotalW, patchH, blackStroke);

        // 7. 标准色靶（RGBCMY + 肤色参考色块）
        SKColor[] swatches =
        [
            new(239, 68, 68), new(34, 197, 94), new(59, 130, 246),
            new(6, 182, 212), new(217, 70, 239), new(234, 179, 8),
            new(241, 194, 165), new(224, 172, 138), new(67, 142, 219), new(217, 0, 27)
        ];
        float swW = grayTotalW / swatches.Length;
        float sy0 = gy0 + patchH + 3.0f * pxPerMm;
        for (int i = 0; i < swatches.Length; i++)
        {
            fillPaint.Color = swatches[i];
            canvas.DrawRect(gx0 + i * swW, sy0, swW, 5.5f * pxPerMm, fillPaint);
        }
        canvas.DrawRect(gx0, sy0, grayTotalW, 5.5f * pxPerMm, blackStroke);

        return bmp;
    }

    public static (float HorizontalMm, float VerticalMm) GetRuleLengths(double paperWidthMm, double paperHeightMm) =>
        (paperWidthMm >= 140 ? 100f : 50f, paperHeightMm >= 90 ? 50f : 30f);

    public static PrinterCalibrationProfile ComputeCalibrationFromMeasured(
        PrinterCalibrationProfile current,
        double expectedHorizontalMm,
        double measuredHorizontalMm,
        double expectedVerticalMm,
        double measuredVerticalMm)
    {
        var updated = current.Clone();
        updated.ScaleXPercent = measuredHorizontalMm > 1
            ? Math.Round(Math.Clamp(current.ScaleXPercent * (expectedHorizontalMm / measuredHorizontalMm), 90.0, 110.0), 2)
            : current.ScaleXPercent;
        updated.ScaleYPercent = measuredVerticalMm > 1
            ? Math.Round(Math.Clamp(current.ScaleYPercent * (expectedVerticalMm / measuredVerticalMm), 90.0, 110.0), 2)
            : current.ScaleYPercent;
        return updated;
    }
}
