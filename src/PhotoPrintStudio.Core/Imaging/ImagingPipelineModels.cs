using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

public sealed class HistogramData
{
    public int[] Red { get; } = new int[256];
    public int[] Green { get; } = new int[256];
    public int[] Blue { get; } = new int[256];
    public int[] Luminance { get; } = new int[256];
    public int TotalPixels { get; set; }
    public double HighlightClippedRatio { get; set; }
    public double ShadowClippedRatio { get; set; }
    public double BorderWhiteRatio { get; set; }
    public double InteriorHighlightClippedRatio { get; set; }
    public bool LooksLikeWhiteBackdrop { get; set; }
}

/// <summary>
/// 模型原始尺度下的紧凑人像透明度遮罩，可在任意输出分辨率上直接双线性采样，
/// 避免在 24MP 等高像素导出场景分配数十至上百 MB 的全图浮点遮罩数组。
/// </summary>
public sealed record CompactAlphaMatte(
    float[] Data,
    int Width,
    int Height,
    float CropX,
    float CropY,
    float CropW,
    float CropH);

/// <summary>
/// 紧凑低频重打光增益图（来自可选 ONNX 人像重打光模型，如 DPR / 轻量光照增强 ONNX），
/// 存储每个采样点的相对照度增益比（约 0.5 ~ 2.0，1.0 表示无变化），供全分辨率保细节融合。
/// </summary>
public sealed record CompactShadingMap(
    float[] GainData,
    int Width,
    int Height,
    float CropX,
    float CropY,
    float CropW,
    float CropH);

/// <summary>
/// 人像智能增强上下文：向 FloatImageProcessor 传递当前图像对应的人脸 478 点 3D 网格与可选 ONNX 重打光图。
/// 坐标系通过 FaceSourceWidth / FaceSourceHeight 自动缩放到当前处理位图尺寸，预览与高清导出完全一致。
/// </summary>
public sealed record PortraitProcessContext(
    DetectedFace? PrimaryFace,
    int FaceSourceWidth,
    int FaceSourceHeight,
    CompactShadingMap? RelightingShading = null)
{
    public DetectedFace? ResolveFaceForSize(int targetWidth, int targetHeight)
    {
        if (PrimaryFace == null || targetWidth <= 0 || targetHeight <= 0)
            return null;
        if (FaceSourceWidth <= 0 || FaceSourceHeight <= 0 ||
            (FaceSourceWidth == targetWidth && FaceSourceHeight == targetHeight))
            return PrimaryFace;
        return PrimaryFace.Scale(
            targetWidth / (float)FaceSourceWidth,
            targetHeight / (float)FaceSourceHeight);
    }
}

public sealed class ImageProcessingResult : IDisposable
{
    public required SKBitmap Bitmap { get; init; }
    public required HistogramData Histogram { get; init; }

    public void Dispose() => Bitmap.Dispose();
}

internal sealed class HistAccum
{
    public readonly int[] R = new int[256];
    public readonly int[] G = new int[256];
    public readonly int[] B = new int[256];
    public readonly int[] L = new int[256];
    public int Hi, Lo, BPx, BWh, IPx, IHi;
}
