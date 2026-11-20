using System.Buffers;
using System.Runtime.CompilerServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoPrintStudio.Core.Portable;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

/// <summary>
/// 可插拔本地离线 AI 人像抠图服务：扫描 portable_data\models\ 中的任意 ONNX 分割模型（如 ModNet / U2Net / BiRefNet）。
/// 无模型文件时优雅降级，不影响软件任何核心功能。
/// </summary>
public sealed unsafe class PortraitMattingService : IDisposable
{
    private readonly PortablePaths _paths;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile InferenceSession? _session;
    private volatile string? _loadedModelPath;
    private volatile IReadOnlyList<string> _availableModels = [];
    private volatile IReadOnlyDictionary<string, ModelManifestEntry> _manifest = new Dictionary<string, ModelManifestEntry>(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;
    public const string ModNetModelFile = "hivision_modnet.onnx";
    public const string PasPhotoMattingModelFile = "pas-photo-matting-v1.onnx";
    private InferenceSession? _portraitFallbackSession;

    public PortraitMattingService(PortablePaths paths, AppLogger logger, bool autoLoad = true)
    {
        _paths = paths;
        _logger = logger;
        if (autoLoad) RefreshAvailableModels();
    }

    public IReadOnlyList<string> AvailableModels => _availableModels;
    public IReadOnlyList<string> AvailableModelNames => _availableModels.Select(Path.GetFileName).Where(x => x != null).Cast<string>().ToList();
    public string? ActiveModelName => _loadedModelPath is { } p ? Path.GetFileName(p) : null;
    public bool HasModelLoaded => _session != null;
    public IReadOnlyDictionary<string, ModelManifestEntry> ModelManifest => _manifest;

    public void RefreshAvailableModels(string? preferredModelFileName = null)
    {
        if (_disposed) return;
        _paths.EnsureDirectoriesExist();
        var manifest = ModelManifestLoader.LoadDirectoryManifest(_paths.ModelsDirectory);
        _manifest = manifest;

        IReadOnlyList<string> models = Directory.Exists(_paths.ModelsDirectory)
            ? Directory.GetFiles(_paths.ModelsDirectory, "*.onnx")
                .Where(p => ModelManifestLoader.IsMattingModel(p, manifest))
                .OrderBy(p => ModelManifestLoader.ResolveMattingSpec(p, manifest).Priority)
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        _availableModels = models;

        if (models.Count == 0) return;

        if (!string.IsNullOrWhiteSpace(preferredModelFileName))
        {
            var match = models.FirstOrDefault(p =>
                string.Equals(Path.GetFileName(p), preferredModelFileName, StringComparison.OrdinalIgnoreCase));
            if (match != null && !string.Equals(match, _loadedModelPath, StringComparison.OrdinalIgnoreCase))
            {
                if (TryLoadModel(match)) return;
            }
        }

        string? loadedPath = _loadedModelPath;
        if (_session == null || (loadedPath != null && !File.Exists(loadedPath)))
        {
            foreach (var modelPath in models)
            {
                if (TryLoadModel(modelPath))
                    break;
            }
        }
    }

    public bool SelectModel(string modelNameOrPath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(modelNameOrPath)) return false;
        string? match = _availableModels.FirstOrDefault(p =>
            string.Equals(p, modelNameOrPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(p), modelNameOrPath, StringComparison.OrdinalIgnoreCase));

        if (match == null && File.Exists(modelNameOrPath) && ModelManifestLoader.IsMattingModel(modelNameOrPath, _manifest))
            match = modelNameOrPath;

        if (match == null) return false;
        if (string.Equals(match, _loadedModelPath, StringComparison.OrdinalIgnoreCase) && _session != null)
            return true;

        return TryLoadModel(match);
    }

    public bool TryLoadModel(string modelPath)
    {
        if (_disposed) return false;
        try
        {
            _gate.Wait();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        try
        {
            if (_disposed || !File.Exists(modelPath)) return false;
            var candidate = OpenSession(modelPath);
            if (candidate == null) return false;

            var old = _session;
            _session = candidate;
            _loadedModelPath = modelPath;
            old?.Dispose();
            _logger.Info($"已加载离线人像抠图模型: {Path.GetFileName(modelPath)}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"加载 ONNX 模型失败: {modelPath}", ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static InferenceSession? OpenSession(string modelPath)
    {
        using var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 4, 8),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        var session = new InferenceSession(modelPath, options);
        if (IsValidMattingSession(session)) return session;
        session.Dispose();
        return null;
    }

    private static bool IsValidMattingSession(InferenceSession session)
    {
        if (session.InputMetadata.Count == 0 || session.OutputMetadata.Count == 0)
            return false;

        bool hasDetectionHeads = session.OutputMetadata.Keys.Any(k =>
            k.StartsWith("cls_", StringComparison.OrdinalIgnoreCase) ||
            k.StartsWith("bbox_", StringComparison.OrdinalIgnoreCase) ||
            k.StartsWith("kps_", StringComparison.OrdinalIgnoreCase));
        if (hasDetectionHeads) return false;

        var firstOut = session.OutputMetadata.First().Value;
        return firstOut.Dimensions.Length is 3 or 4;
    }

    /// <summary>
    /// 对输入图执行人像 Alpha 遮罩预测，返回模型尺度下的紧凑遮罩（含 Letterbox 裁切矩形），
    /// 供 FloatImageProcessor 直接双线性采样，避免高分辨率导出时分配上百 MB 全图浮数组。
    /// </summary>
    public CompactAlphaMatte? ComputeCompactAlphaMatte(SKBitmap source) => ComputeCompactAlphaMatte(source, []);

    public CompactAlphaMatte? ComputeCompactAlphaMatte(SKBitmap source, IReadOnlyList<DetectedFace> faces) =>
        ComputeCompactAlphaMatte(source, faces, CancellationToken.None);

    public CompactAlphaMatte? ComputeCompactAlphaMatte(SKBitmap source, IReadOnlyList<DetectedFace> faces, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed) return null;
        try
        {
            _gate.Wait(token);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        try
        {
            token.ThrowIfCancellationRequested();
            var session = _session;
            string? loadedPath = _loadedModelPath;
            if (_disposed || session == null || loadedPath == null) return null;
            var matte = PredictMatte(source, session, loadedPath, token);
            if (matte == null) return null;

            if (loadedPath.Contains("sallunet", StringComparison.OrdinalIgnoreCase) &&
                faces.Any(face => face.Confidence >= 0.65f))
            {
                float coverage = MeasureFaceCoverage(matte, faces, source.Width, source.Height);
                string fallbackPath = Path.Combine(_paths.ModelsDirectory, ModNetModelFile);
                if (coverage < 0.85f && File.Exists(fallbackPath))
                {
                    try
                    {
                        _portraitFallbackSession ??= OpenSession(fallbackPath);
                        if (_portraitFallbackSession != null && PredictMatte(source, _portraitFallbackSession, fallbackPath, token) is { } candidate &&
                            MeasureFaceCoverage(candidate, faces, source.Width, source.Height) >= 0.90f)
                            matte = candidate;
                    }
                    catch (OnnxRuntimeException ex)
                    {
                        _logger.Error("加载备用人像模型失败", ex);
                    }
                }
            }

            ProtectFaceInterior(matte, faces, source.Width, source.Height);
            return matte;
        }
        finally
        {
            _gate.Release();
        }
    }

    private CompactAlphaMatte? PredictMatte(SKBitmap source, InferenceSession session, string modelPath, CancellationToken token)
    {
        float[]? pooledTensor = null;
        try
        {
            var inputMeta = session.InputMetadata.First();
            string inputName = inputMeta.Key;
            int[] dims = inputMeta.Value.Dimensions;

            bool isNchw = dims.Length == 4 && (dims[1] == 3 || dims[3] != 3);
            int rawH = isNchw ? dims[2] : dims[1];
            int rawW = isNchw ? dims[3] : dims[2];

            int targetW, targetH;
            if (rawW <= 0 && rawH <= 0)
            {
                double aspect = source.Width / (double)Math.Max(1, source.Height);
                if (aspect >= 1.0)
                {
                    targetH = 512;
                    targetW = Math.Clamp(((int)Math.Round((512 * aspect) / 32.0)) * 32, 256, 800);
                }
                else
                {
                    targetW = 512;
                    targetH = Math.Clamp(((int)Math.Round((512 / aspect) / 32.0)) * 32, 256, 800);
                }
            }
            else
            {
                targetH = rawH > 0 ? rawH : 512;
                targetW = rawW > 0 ? rawW : 512;
            }

            var spec = ModelManifestLoader.ResolveMattingSpec(modelPath, _manifest);
            bool resizeToSquare = spec.ResizeToSquare;
            double fitScale = Math.Min(targetW / (double)source.Width, targetH / (double)source.Height);
            int fitW = resizeToSquare ? targetW : Math.Clamp((int)Math.Round(source.Width * fitScale), 1, targetW);
            int fitH = resizeToSquare ? targetH : Math.Clamp((int)Math.Round(source.Height * fitScale), 1, targetH);
            int padX = (targetW - fitW) / 2;
            int padY = (targetH - fitH) / 2;

            using var fitted = source.Resize(
                new SKImageInfo(fitW, fitH, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (fitted == null) return null;

            int planeElems = targetW * targetH;
            int tensorElems = planeElems * 3;
            pooledTensor = ArrayPool<float>.Shared.Rent(tensorElems);
            var tensorDims = isNchw
                ? new[] { 1, 3, targetH, targetW }
                : new[] { 1, targetH, targetW, 3 };
            var tensor = new DenseTensor<float>(pooledTensor.AsMemory(0, tensorElems), tensorDims);

            var rgbScale = spec.RgbScale;
            var rgbOffset = spec.RgbOffset;

            byte* fPtr = (byte*)fitted.GetPixels().ToPointer();
            int fRowBytes = fitted.RowBytes;
            fixed (float* tPtr = pooledTensor)
            {
                FillClampedLetterboxTensor(
                    fPtr, fitW, fitH, fRowBytes,
                    tPtr, targetW, targetH, padX, padY,
                    isNchw, planeElems, rgbScale, rgbOffset);
            }
            GC.KeepAlive(fitted);

            var inputs = new List<NamedOnnxValue>(1) { NamedOnnxValue.CreateFromTensor(inputName, tensor) };
            using var results = OnnxInference.Run(session, inputs, token);
            var output = results.First().AsTensor<float>();
            var outDims = output.Dimensions;

            int outH = outDims.Length == 4 ? outDims[2] : (outDims.Length == 3 ? outDims[1] : targetH);
            int outW = outDims.Length == 4 ? outDims[3] : (outDims.Length == 3 ? outDims[2] : targetW);
            if (outDims.Length == 4 && outDims[3] == 1 && outDims[1] > 1)
            {
                outH = outDims[1];
                outW = outDims[2];
            }

            int count = outW * outH;
            var smallMask = new float[count];
            float minVal = float.MaxValue, maxVal = float.MinValue;

            if (output is DenseTensor<float> denseOut)
            {
                var span = denseOut.Buffer.Span;
                int copyLen = Math.Min(span.Length, count);
                for (int i = 0; i < copyLen; i++)
                {
                    float v = span[i];
                    smallMask[i] = v;
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }
            }
            else
            {
                var flat = output.ToArray();
                int copyLen = Math.Min(flat.Length, count);
                for (int i = 0; i < copyLen; i++)
                {
                    float v = flat[i];
                    smallMask[i] = v;
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }
            }

            bool looksLikeLogits = spec.OutputIsLogits || minVal < -0.1f || maxVal > 1.1f;
            for (int i = 0; i < count; i++)
            {
                float v = smallMask[i];
                if (looksLikeLogits)
                    v = 1.0f / (1.0f + MathF.Exp(-v));
                smallMask[i] = Math.Clamp(v, 0f, 1f);
            }

            float cropX = padX * (outW / (float)targetW);
            float cropY = padY * (outH / (float)targetH);
            float cropW = fitW * (outW / (float)targetW);
            float cropH = fitH * (outH / (float)targetH);
            return new CompactAlphaMatte(smallMask, outW, outH, cropX, cropY, cropW, cropH);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.Error("AI 抠图推理异常", ex);
            return null;
        }
        finally
        {
            if (pooledTensor != null)
                ArrayPool<float>.Shared.Return(pooledTensor);
        }
    }

    private static float MeasureFaceCoverage(CompactAlphaMatte matte, IReadOnlyList<DetectedFace> faces, int imageWidth, int imageHeight)
    {
        float coverage = 1;
        foreach (var face in faces)
        {
            if (face.Confidence < 0.65f) continue;
            float sum = 0;
            for (int row = 1; row <= 3; row++)
            for (int col = 1; col <= 3; col++)
            {
                int x = Math.Clamp((int)(matte.CropX + (face.X + face.Width * col / 4) * matte.CropW / imageWidth), 0, matte.Width - 1);
                int y = Math.Clamp((int)(matte.CropY + (face.Y + face.Height * row / 4) * matte.CropH / imageHeight), 0, matte.Height - 1);
                sum += matte.Data[y * matte.Width + x];
            }
            coverage = Math.Min(coverage, sum / 9);
        }
        return coverage;
    }

    private static void ProtectFaceInterior(CompactAlphaMatte matte, IReadOnlyList<DetectedFace> faces, int imageWidth, int imageHeight)
    {
        float scaleX = matte.CropW / imageWidth, scaleY = matte.CropH / imageHeight;
        foreach (var face in faces)
        {
            if (face.Confidence < 0.65f) continue;

            float cx = matte.CropX + (face.X + face.Width * 0.5f) * scaleX;
            float cy = matte.CropY + (face.Y + face.Height * 0.5f) * scaleY;
            float rx = face.Width * 0.46f * scaleX;
            float ry = face.Height * 0.48f * scaleY;
            int left = Math.Max(0, (int)MathF.Floor(cx - rx));
            int right = Math.Min(matte.Width, (int)MathF.Ceiling(cx + rx));
            int top = Math.Max(0, (int)MathF.Floor(cy - ry));
            int bottom = Math.Min(matte.Height, (int)MathF.Ceiling(cy + ry));
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                float u = (x + 0.5f - cx) / rx;
                float v = (y + 0.5f - cy) / ry;
                float weight = Math.Clamp((1 - u * u - v * v) / 0.30f, 0, 1);
                int index = y * matte.Width + x;
                matte.Data[index] = Math.Max(matte.Data[index], weight * weight * (3 - 2 * weight));
            }
        }
    }

    /// <summary>
    /// 对输入图执行人像 Alpha 遮罩预测，同时输出紧凑原始遮罩与指定目标尺寸的全分辨率数组（供预览直方图复用与导出免二次推理）。
    /// </summary>
    public (float[] Full, CompactAlphaMatte Compact)? ComputeAlphaMatteWithCompact(SKBitmap source)
    {
        var compact = ComputeCompactAlphaMatte(source);
        if (compact == null) return null;
        float[] full = BilinearUpsampleSubRectMask(
            compact.Data, compact.Width, compact.Height,
            compact.CropX, compact.CropY, compact.CropW, compact.CropH,
            source.Width, source.Height);
        return (full, compact);
    }

    /// <summary>
    /// 对输入图执行人像 Alpha 遮罩预测，返回与原图同尺寸的 0..1 透明度数组。
    /// 若无模型或推理失败返回 null。
    /// </summary>
    public float[]? ComputeAlphaMatte(SKBitmap source)
    {
        var compact = ComputeCompactAlphaMatte(source);
        if (compact == null) return null;
        return BilinearUpsampleSubRectMask(
            compact.Data, compact.Width, compact.Height,
            compact.CropX, compact.CropY, compact.CropW, compact.CropH,
            source.Width, source.Height);
    }

    private static void FillClampedLetterboxTensor(
        byte* srcPtr, int fw, int fh, int srcRowBytes,
        float* tPtr, int targetW, int targetH, int padX, int padY,
        bool isNchw, int planeElems, (float R, float G, float B) scale, (float R, float G, float B) offset)
    {
        float* pR = tPtr;
        float* pG = tPtr + planeElems;
        float* pB = tPtr + planeElems * 2;

        for (int y = 0; y < targetH; y++)
        {
            int sy = Math.Clamp(y - padY, 0, fh - 1);
            byte* sRow = srcPtr + sy * srcRowBytes;
            int dstRow = y * targetW;

            if (isNchw)
            {
                for (int x = 0; x < targetW; x++)
                {
                    int sx = Math.Clamp(x - padX, 0, fw - 1);
                    byte* px = sRow + sx * 4;
                    int dIdx = dstRow + x;
                    pB[dIdx] = px[0] * scale.B - offset.B;
                    pG[dIdx] = px[1] * scale.G - offset.G;
                    pR[dIdx] = px[2] * scale.R - offset.R;
                }
            }
            else
            {
                for (int x = 0; x < targetW; x++)
                {
                    int sx = Math.Clamp(x - padX, 0, fw - 1);
                    byte* px = sRow + sx * 4;
                    int dIdx = (dstRow + x) * 3;
                    tPtr[dIdx] = px[2] * scale.R - offset.R;
                    tPtr[dIdx + 1] = px[1] * scale.G - offset.G;
                    tPtr[dIdx + 2] = px[0] * scale.B - offset.B;
                }
            }
        }
    }

    private static float[] BilinearUpsampleSubRectMask(
        float[] src, int sw, int sh,
        float cropX, float cropY, float cropW, float cropH,
        int dw, int dh)
    {
        var dst = new float[dw * dh];
        float scaleX = cropW / dw;
        float scaleY = cropH / dh;

        Parallel.For(0, dh, y =>
        {
            float sy = cropY + (y + 0.5f) * scaleY - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, sh - 1);
            int y1 = Math.Clamp(y0 + 1, 0, sh - 1);
            float fy = Math.Clamp(sy - y0, 0f, 1f);
            int r0 = y0 * sw;
            int r1 = y1 * sw;
            int dstRow = y * dw;

            for (int x = 0; x < dw; x++)
            {
                float sx = cropX + (x + 0.5f) * scaleX - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(sx), 0, sw - 1);
                int x1 = Math.Clamp(x0 + 1, 0, sw - 1);
                float fx = Math.Clamp(sx - x0, 0f, 1f);

                float v00 = src[r0 + x0];
                float v10 = src[r0 + x1];
                float v01 = src[r1 + x0];
                float v11 = src[r1 + x1];

                float top = v00 + (v10 - v00) * fx;
                float bot = v01 + (v11 - v01) * fx;
                dst[dstRow + x] = top + (bot - top) * fy;
            }
        });

        return dst;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Wait();
        try
        {
            _session?.Dispose();
            _session = null;
            _loadedModelPath = null;
            _portraitFallbackSession?.Dispose();
            _portraitFallbackSession = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
