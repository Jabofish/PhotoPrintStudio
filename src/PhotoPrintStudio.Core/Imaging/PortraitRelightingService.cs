using System.Buffers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoPrintStudio.Core.Portable;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

/// <summary>
/// 可插拔离线 ONNX 人像重打光服务：
/// 扫描 portable_data\models\（及 models\relight\）中 role="relighting" 或包含 relight/dpr/zerodce/illumination 的 ONNX 模型。
/// 支持两类主流架构：
/// 1) DPR 球谐光重打光模型（双输入：亮度图 [1,1,H,W] + 目标9维球谐光照 [1,9,1,1] -> 重打光亮度 [1,1,H,W]）；
/// 2) 单输入光照/重打光模型（RGB [1,3,H,W] -> 重打光 RGB [1,3,H,W]）。
/// 统一输出低频照度增益比 CompactShadingMap（~0.6..1.8），确保在任意 300/600 DPI 导出分辨率下 100% 保留原始皮肤与毛发细节；
/// 无外部重打光 ONNX 文件时优雅降级，由内置 478 点 3D Face Mesh 影棚重打光引擎无缝接管。
/// </summary>
public sealed unsafe class PortraitRelightingService : IDisposable
{
    // 影棚标准柔光正面蝴蝶光（Soft Frontal Butterfly Key + Gentle Fill）9维球谐系数
    private static readonly float[] StudioFrontalSh = [0.88f, 0.0f, 0.14f, 0.42f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f];

    private readonly PortablePaths _paths;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile InferenceSession? _session;
    private volatile string? _loadedModelPath;
    private volatile IReadOnlyList<string> _availableModels = [];
    private volatile bool _disposed;

    public PortraitRelightingService(PortablePaths paths, AppLogger logger, bool autoLoad = true)
    {
        _paths = paths;
        _logger = logger;
        if (autoLoad) RefreshAvailableModels();
    }

    public IReadOnlyList<string> AvailableModels => _availableModels;
    public string? ActiveModelName => _loadedModelPath is { } p ? Path.GetFileName(p) : null;
    public bool HasModelLoaded => _session != null;

    public void RefreshAvailableModels()
    {
        if (_disposed) return;
        _paths.EnsureDirectoriesExist();
        var manifest = ModelManifestLoader.LoadDirectoryManifest(_paths.ModelsDirectory);

        var candidates = new List<string>();
        if (Directory.Exists(_paths.ModelsDirectory))
            candidates.AddRange(Directory.GetFiles(_paths.ModelsDirectory, "*.onnx"));

        string relightDir = Path.Combine(_paths.ModelsDirectory, "relight");
        if (Directory.Exists(relightDir))
            candidates.AddRange(Directory.GetFiles(relightDir, "*.onnx"));

        IReadOnlyList<string> models = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => ModelManifestLoader.IsRelightingModel(p, manifest))
            .OrderBy(p => manifest.TryGetValue(Path.GetFileName(p), out var e) ? e.Priority : 10)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _availableModels = models;

        if (_loadedModelPath != null && !File.Exists(_loadedModelPath))
        {
            UnloadSession();
        }

        if (_session == null && models.Count > 0)
        {
            foreach (var modelPath in models)
            {
                if (TryLoadModel(modelPath))
                    break;
            }
        }
    }

    public bool TryLoadModel(string modelPath)
    {
        if (_disposed) return false;
        try { _gate.Wait(); }
        catch (ObjectDisposedException) { return false; }

        try
        {
            if (_disposed || !File.Exists(modelPath)) return false;
            using var options = new SessionOptions
            {
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6),
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            var candidate = new InferenceSession(modelPath, options);
            if (!IsValidRelightingSession(candidate))
            {
                candidate.Dispose();
                return false;
            }

            var old = _session;
            _session = candidate;
            _loadedModelPath = modelPath;
            old?.Dispose();
            _logger.Info($"已加载离线人像重打光模型: {Path.GetFileName(modelPath)}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"加载人像重打光 ONNX 模型失败: {modelPath}", ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void UnloadSession()
    {
        try { _gate.Wait(); }
        catch (ObjectDisposedException) { return; }
        try
        {
            _session?.Dispose();
            _session = null;
            _loadedModelPath = null;
        }
        finally { _gate.Release(); }
    }

    private static bool IsValidRelightingSession(InferenceSession session)
    {
        if (session.InputMetadata.Count == 0 || session.OutputMetadata.Count == 0)
            return false;
        var imgIn = session.InputMetadata.First().Value;
        var outMeta = session.OutputMetadata.First().Value;
        return imgIn.Dimensions.Length == 4 && outMeta.Dimensions.Length is 3 or 4;
    }

    public CompactShadingMap? ComputeStudioShadingMap(SKBitmap source, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || source == null || source.Width < 32 || source.Height < 32)
            return null;

        try { _gate.Wait(token); }
        catch (ObjectDisposedException) { return null; }

        try
        {
            token.ThrowIfCancellationRequested();
            var session = _session;
            if (_disposed || session == null) return null;

            return RunRelightInference(source, session, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.Error("ONNX 人像重打光推理异常，自动回退内置 3D 影棚重打光", ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static CompactShadingMap? RunRelightInference(SKBitmap source, InferenceSession session, CancellationToken token)
    {
        var inputList = session.InputMetadata.ToList();
        var imgInput = inputList[0];
        var dims = imgInput.Value.Dimensions;
        bool isNchw = dims.Length == 4 && (dims[1] is 1 or 3 || dims[3] is not (1 or 3));
        int inChannels = isNchw ? (dims[1] > 0 ? dims[1] : 3) : (dims[3] > 0 ? dims[3] : 3);
        int rawH = isNchw ? dims[2] : dims[1];
        int rawW = isNchw ? dims[3] : dims[2];

        int targetH = rawH > 0 ? rawH : 256;
        int targetW = rawW > 0 ? rawW : 256;

        double fitScale = Math.Min(targetW / (double)source.Width, targetH / (double)source.Height);
        int fitW = Math.Clamp((int)Math.Round(source.Width * fitScale), 1, targetW);
        int fitH = Math.Clamp((int)Math.Round(source.Height * fitScale), 1, targetH);
        int padX = (targetW - fitW) / 2;
        int padY = (targetH - fitH) / 2;

        using var fitted = source.Resize(
            new SKImageInfo(fitW, fitH, SKColorType.Bgra8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        if (fitted == null) return null;

        int planeElems = targetW * targetH;
        int tensorElems = planeElems * inChannels;
        float[] pooledInput = ArrayPool<float>.Shared.Rent(tensorElems);
        float[] origLuma = new float[planeElems];
        try
        {
            byte* fPtr = (byte*)fitted.GetPixels().ToPointer();
            int fRowBytes = fitted.RowBytes;
            const float inv255 = 1f / 255f;

            for (int y = 0; y < targetH; y++)
            {
                int sy = Math.Clamp(y - padY, 0, fitH - 1);
                byte* sRow = fPtr + sy * fRowBytes;
                int dstRow = y * targetW;
                for (int x = 0; x < targetW; x++)
                {
                    int sx = Math.Clamp(x - padX, 0, fitW - 1);
                    byte* px = sRow + sx * 4;
                    float r = px[2] * inv255;
                    float g = px[1] * inv255;
                    float b = px[0] * inv255;
                    float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    int idx = dstRow + x;
                    origLuma[idx] = luma;

                    if (inChannels == 1)
                    {
                        pooledInput[idx] = luma;
                    }
                    else if (isNchw)
                    {
                        pooledInput[idx] = r;
                        pooledInput[planeElems + idx] = g;
                        pooledInput[planeElems * 2 + idx] = b;
                    }
                    else
                    {
                        int o = idx * 3;
                        pooledInput[o] = r;
                        pooledInput[o + 1] = g;
                        pooledInput[o + 2] = b;
                    }
                }
            }
            GC.KeepAlive(fitted);

            int[] tensorDims = isNchw ? [1, inChannels, targetH, targetW] : [1, targetH, targetW, inChannels];
            var inputs = new List<NamedOnnxValue>(inputList.Count)
            {
                NamedOnnxValue.CreateFromTensor(imgInput.Key, new DenseTensor<float>(pooledInput.AsMemory(0, tensorElems), tensorDims))
            };

            // 若模型带球谐光向量第二输入（DPR 架构），自动馈入影棚柔光正面蝴蝶光 SH 系数
            if (inputList.Count >= 2)
            {
                var shInput = inputList[1];
                var shDims = shInput.Value.Dimensions.Select(d => d > 0 ? d : 1).ToArray();
                int shElems = 1;
                foreach (int d in shDims) shElems *= d;
                var shData = new float[shElems];
                int copy = Math.Min(shElems, StudioFrontalSh.Length);
                Array.Copy(StudioFrontalSh, shData, copy);
                inputs.Add(NamedOnnxValue.CreateFromTensor(shInput.Key, new DenseTensor<float>(shData, shDims)));
            }

            using var results = OnnxInference.Run(session, inputs, token);
            var outTensor = results.First().AsTensor<float>();
            var outDims = outTensor.Dimensions;
            bool outNchw = outDims.Length == 4 && (outDims[1] is 1 or 3 || outDims[3] is not (1 or 3));
            int outC = outDims.Length == 4 ? (outNchw ? outDims[1] : outDims[3]) : 1;
            int outH = outDims.Length == 4 ? (outNchw ? outDims[2] : outDims[1]) : outDims[1];
            int outW = outDims.Length == 4 ? (outNchw ? outDims[3] : outDims[2]) : outDims[2];
            if (outW <= 0 || outH <= 0) return null;

            ReadOnlySpan<float> outSpan = outTensor is DenseTensor<float> dt ? dt.Buffer.Span : outTensor.ToArray();
            int outPlane = outW * outH;
            var gainMap = new float[outPlane];

            for (int y = 0; y < outH; y++)
            {
                int oy = Math.Clamp((int)MathF.Round((y + 0.5f) * targetH / outH - 0.5f), 0, targetH - 1);
                for (int x = 0; x < outW; x++)
                {
                    int ox = Math.Clamp((int)MathF.Round((x + 0.5f) * targetW / outW - 0.5f), 0, targetW - 1);
                    int oIdx = y * outW + x;
                    float relitLuma;
                    if (outC == 1)
                    {
                        relitLuma = Math.Clamp(outSpan[oIdx], 0f, 1f);
                    }
                    else if (outNchw)
                    {
                        float rr = Math.Clamp(outSpan[oIdx], 0f, 1f);
                        float gg = Math.Clamp(outSpan[outPlane + oIdx], 0f, 1f);
                        float bb = Math.Clamp(outSpan[outPlane * 2 + oIdx], 0f, 1f);
                        relitLuma = 0.2126f * rr + 0.7152f * gg + 0.0722f * bb;
                    }
                    else
                    {
                        int p = oIdx * 3;
                        relitLuma = 0.2126f * Math.Clamp(outSpan[p], 0f, 1f) +
                                    0.7152f * Math.Clamp(outSpan[p + 1], 0f, 1f) +
                                    0.0722f * Math.Clamp(outSpan[p + 2], 0f, 1f);
                    }

                    float baseL = origLuma[oy * targetW + ox];
                    // 仅提取平滑安全的照度增益比，避免在暗部噪点处过度放大
                    float ratio = (relitLuma + 0.06f) / (baseL + 0.06f);
                    gainMap[oIdx] = Math.Clamp(ratio, 0.65f, 1.65f);
                }
            }

            float cropX = padX * (outW / (float)targetW);
            float cropY = padY * (outH / (float)targetH);
            float cropW = fitW * (outW / (float)targetW);
            float cropH = fitH * (outH / (float)targetH);
            return new CompactShadingMap(gainMap, outW, outH, cropX, cropY, cropW, cropH);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(pooledInput);
        }
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
        }
        finally { _gate.Release(); }
    }
}
