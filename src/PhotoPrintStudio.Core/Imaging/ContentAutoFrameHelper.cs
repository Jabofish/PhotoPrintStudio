using PhotoPrintStudio.Core.Models;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

public static unsafe class ContentAutoFrameHelper
{
    public static (double Zoom, double PanX, double PanY) Compute(
        int width, int height, PhotoEditState edit, double frameWidth, double frameHeight, FitMode fit,
        double slotZoom, double slotPanX, double slotPanY, IReadOnlyList<DetectedFace> faces, SKBitmap? preview)
    {
        bool swapped = edit.QuarterTurns % 2 != 0;
        double sx = frameWidth / (swapped ? height : width);
        double sy = frameHeight / (swapped ? width : height);
        double angle = (edit.QuarterTurns * 90 + edit.RotationDegrees) * Math.PI / 180;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double absCos = Math.Abs(cos), absSin = Math.Abs(sin);

        if (fit == FitMode.Contain)
        {
            double scaleToFit = Math.Min(frameWidth / (width * absCos + height * absSin),
                                         frameHeight / (width * absSin + height * absCos));
            return (Math.Clamp(scaleToFit / Math.Min(sx, sy) / slotZoom, 1, 4),
                    Math.Clamp(-slotPanX, -1, 1), Math.Clamp(-slotPanY, -1, 1));
        }

        // 半幅在原图坐标的投影限定裁切中心，保留用户转过的角度也不会因平移露边。
        double halfW = frameWidth / 2, halfH = frameHeight / 2;
        double projectedW = halfW * absCos + halfH * absSin;
        double projectedH = halfW * absSin + halfH * absCos;
        double baseScale = Math.Max(sx, sy);
        double minScale = Math.Max(2 * projectedW / width, 2 * projectedH / height);
        double zoom = Math.Clamp(Math.Max(1, minScale / baseScale) / slotZoom, 1, 4);
        double scale = baseScale * Math.Clamp(zoom * slotZoom, 0.5, 8);
        double roomX = Math.Max(0, width / 2.0 - projectedW / scale);
        double roomY = Math.Max(0, height / 2.0 - projectedH / scale);
        var centered = (Zoom: zoom, PanX: Math.Clamp(-slotPanX, -1, 1), PanY: Math.Clamp(-slotPanY, -1, 1));
        if (roomX < 0.01 && roomY < 0.01) return centered;

        var heads = faces.Select(face => ProjectHead(face)).ToArray();
        var samples = preview != null ? SampleDetail(preview, (x, y) => Project(x - width / 2.0, y - height / 2.0)) : [];
        double weight = heads.Length > 0 ? heads.Length : samples.Sum(p => p.Weight);
        var best = centered;
        double bestScore = double.NegativeInfinity;
        int stepsX = roomX >= 0.01 ? 6 : 0;
        int stepsY = roomY >= 0.01 ? 6 : 0;
        for (int y = -stepsY; y <= stepsY; y++)
        for (int x = -stepsX; x <= stepsX; x++)
        {
            var center = Project(roomX * x / 6, roomY * y / 6);
            double panX = -center.X / halfW - slotPanX;
            double panY = -center.Y / halfH - slotPanY;
            if (Math.Abs(panX) > 1.000001 || Math.Abs(panY) > 1.000001) continue;

            var window = new SKRect((float)(center.X - halfW), (float)(center.Y - halfH),
                                    (float)(center.X + halfW), (float)(center.Y + halfH));
            double score = -0.045 * (1 + weight) * (x * x + y * y) / 36;
            foreach (var head in heads)
            {
                var visible = SKRect.Intersect(head, window);
                double retained = visible.Width * visible.Height / Math.Max(0.001, head.Width * head.Height);
                score += retained * retained;
            }
            foreach (var sample in samples)
            {
                double edge = Math.Max(Math.Abs(sample.Point.X - center.X) / halfW, Math.Abs(sample.Point.Y - center.Y) / halfH);
                score += sample.Weight * Math.Clamp((1 - edge) * 8, 0, 1);
            }
            if (score <= bestScore) continue;
            bestScore = score;
            best = (zoom, Math.Clamp(panX, -1, 1), Math.Clamp(panY, -1, 1));
        }
        return best;

        SKPoint Project(double x, double y) => new(
            (float)((x * cos - y * sin) * scale * (edit.FlipHorizontal ? -1 : 1)),
            (float)((x * sin + y * cos) * scale * (edit.FlipVertical ? -1 : 1)));

        SKRect ProjectHead(DetectedFace face)
        {
            double left = Math.Max(0, face.X - face.Width * 0.20) - width / 2.0;
            double right = Math.Min(width, face.BoxRight + face.Width * 0.20) - width / 2.0;
            double top = Math.Max(0, face.Y - face.Height * 0.18) - height / 2.0;
            double bottom = Math.Min(height, face.BoxBottom + face.Height * 0.55) - height / 2.0;
            SKPoint[] corners = [Project(left, top), Project(right, top), Project(left, bottom), Project(right, bottom)];
            return new SKRect(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
        }
    }

    private static List<(SKPoint Point, double Weight)> SampleDetail(SKBitmap preview, Func<double, double, SKPoint> project)
    {
        int w = preview.Width, h = preview.Height;
        if (w <= 0 || h <= 0) return [];
        int step = Math.Max(1, Math.Max(w, h) / 40);
        int red = 0, green = 0, blue = 0, count = 0;

        bool fastBgra = preview.ColorType == SKColorType.Bgra8888 && preview.AlphaType != SKAlphaType.Premul;
        byte* basePtr = fastBgra ? (byte*)preview.GetPixels().ToPointer() : null;
        int rowBytes = preview.RowBytes;

        SKColor ReadPixel(int x, int y)
        {
            if (fastBgra && basePtr != null)
            {
                byte* p = basePtr + (long)y * rowBytes + x * 4;
                return new SKColor(p[2], p[1], p[0], p[3]);
            }
            return preview.GetPixel(x, y);
        }

        for (int y = 0; y < h; y += step)
        for (int x = 0; x < w; x += step)
        {
            var c = ReadPixel(x, y);
            red += c.Red;
            green += c.Green;
            blue += c.Blue;
            count++;
        }

        if (count == 0) return [];
        var average = new SKColor((byte)(red / count), (byte)(green / count), (byte)(blue / count));
        var samples = new List<(SKPoint Point, double Weight)>(count);
        for (int y = 0; y < h; y += step)
        for (int x = 0; x < w; x += step)
        {
            var c = ReadPixel(x, y);
            var right = ReadPixel(Math.Min(w - 1, x + step), y);
            var below = ReadPixel(x, Math.Min(h - 1, y + step));
            double contrast = (Distance(c, right) + Distance(c, below)) / 6.0;
            double weight = Math.Max(0, 0.65 * contrast + 0.35 * Distance(c, average) / 3.0 - 5) * c.Alpha / 255.0;
            if (weight > 0) samples.Add((project(x + 0.5, y + 0.5), weight));
        }
        GC.KeepAlive(preview);
        return samples;

        static int Distance(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
    }
}
