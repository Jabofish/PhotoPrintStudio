using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoPrintStudio.Core.Portable;
using SkiaSharp;

namespace PhotoPrintStudio.Core.Imaging;

public sealed record FaceMeshLandmarks(
    SKPoint[] Points2D,
    float[] PointsZ)
{
    public FaceMeshLandmarks Scale(float scaleX, float scaleY)
    {
        var scaled = new SKPoint[Points2D.Length];
        for (int i = 0; i < Points2D.Length; i++)
            scaled[i] = new SKPoint(Points2D[i].X * scaleX, Points2D[i].Y * scaleY);
        return new FaceMeshLandmarks(scaled, PointsZ);
    }

    public FaceMeshLandmarks TransformPoints(Func<SKPoint, SKPoint> map)
    {
        var mapped = new SKPoint[Points2D.Length];
        for (int i = 0; i < Points2D.Length; i++)
            mapped[i] = map(Points2D[i]);
        return new FaceMeshLandmarks(mapped, PointsZ);
    }
}

public sealed record DetectedFace(
    float X,
    float Y,
    float Width,
    float Height,
    SKPoint RightEye,
    SKPoint LeftEye,
    SKPoint NoseTip,
    SKPoint RightMouth,
    SKPoint LeftMouth,
    float Confidence,
    FaceMeshLandmarks? Mesh = null)
{
    public SKPoint EyeMidpoint => new((RightEye.X + LeftEye.X) * 0.5f, (RightEye.Y + LeftEye.Y) * 0.5f);
    public SKPoint MouthMidpoint => new((RightMouth.X + LeftMouth.X) * 0.5f, (RightMouth.Y + LeftMouth.Y) * 0.5f);
    public float BoxRight => X + Width;
    public float BoxBottom => Y + Height;
    public float EyeDistance
    {
        get
        {
            float dx = LeftEye.X - RightEye.X;
            float dy = LeftEye.Y - RightEye.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }
    }

    public DetectedFace Scale(float scaleX, float scaleY) => new(
        X * scaleX,
        Y * scaleY,
        Width * scaleX,
        Height * scaleY,
        new SKPoint(RightEye.X * scaleX, RightEye.Y * scaleY),
        new SKPoint(LeftEye.X * scaleX, LeftEye.Y * scaleY),
        new SKPoint(NoseTip.X * scaleX, NoseTip.Y * scaleY),
        new SKPoint(RightMouth.X * scaleX, RightMouth.Y * scaleY),
        new SKPoint(LeftMouth.X * scaleX, LeftMouth.Y * scaleY),
        Confidence,
        Mesh?.Scale(scaleX, scaleY));

    internal DetectedFace TransformPoints(Func<SKPoint, SKPoint> map)
    {
        var cornerA = map(new SKPoint(X, Y));
        var cornerB = map(new SKPoint(BoxRight, BoxBottom));
        return new DetectedFace(
            Math.Min(cornerA.X, cornerB.X), Math.Min(cornerA.Y, cornerB.Y),
            Math.Max(1f, Math.Abs(cornerB.X - cornerA.X)), Math.Max(1f, Math.Abs(cornerB.Y - cornerA.Y)),
            map(RightEye), map(LeftEye), map(NoseTip), map(RightMouth), map(LeftMouth),
            Confidence, Mesh?.TransformPoints(map));
    }
}

/// <summary>
/// 离线 YuNet 人脸检测与 Face Mesh V2 五官关键点精修服务。
/// 扫描 portable_data\models\ 与 portable_data\models\face\ 中的检测模型；无模型时优雅降级。
/// </summary>
public sealed unsafe class FaceDetectionService : IDisposable
{
    private static readonly int[] YuNetStrides = [8, 16, 32];

    private readonly PortablePaths _paths;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile InferenceSession? _session;
    private volatile string? _loadedModelPath;
    private volatile InferenceSession? _landmarkSession;
    private volatile string? _loadedLandmarkModelPath;
    private volatile IReadOnlyList<string> _availableModels = [];
    private volatile bool _disposed;

    public FaceDetectionService(PortablePaths paths, AppLogger logger, bool autoLoad = true)
    {
        _paths = paths;
        _logger = logger;
        if (autoLoad) RefreshAvailableModels();
    }

    public IReadOnlyList<string> AvailableModels => _availableModels;
    public string? ActiveModelName => _loadedModelPath is { } p ? Path.GetFileName(p) : null;
    public string? ActiveLandmarkModelName => _loadedLandmarkModelPath is { } p ? Path.GetFileName(p) : null;
    public bool HasModelLoaded => _session != null;

    public void RefreshAvailableModels()
    {
        if (_disposed) return;
        _paths.EnsureDirectoriesExist();
        try
        {
            _gate.Wait();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (_disposed) return;
            if (_loadedModelPath != null && !File.Exists(_loadedModelPath))
            {
                _session?.Dispose();
                _session = null;
                _loadedModelPath = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        var candidates = new List<string>();
        if (Directory.Exists(_paths.ModelsDirectory))
            candidates.AddRange(Directory.GetFiles(_paths.ModelsDirectory, "*.onnx"));

        string faceSubDir = Path.Combine(_paths.ModelsDirectory, "face");
        if (Directory.Exists(faceSubDir))
            candidates.AddRange(Directory.GetFiles(faceSubDir, "*.onnx"));

        IReadOnlyList<string> models = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(IsFaceCandidateFile)
            .OrderBy(GetFacePriority)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _availableModels = models;

        if (models.Count > 0 && _session == null)
        {
            foreach (var modelPath in models)
            {
                if (TryLoadModel(modelPath))
                    break;
            }
        }

        string? landmarkPath = candidates.FirstOrDefault(p =>
            Path.GetFileName(p).Equals("face_landmarks.onnx", StringComparison.OrdinalIgnoreCase));
        if (_disposed) return;
        try
        {
            _gate.Wait();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (_disposed) return;
            if (!string.Equals(landmarkPath, _loadedLandmarkModelPath, StringComparison.OrdinalIgnoreCase))
            {
                _landmarkSession?.Dispose();
                _landmarkSession = null;
                _loadedLandmarkModelPath = null;
                if (landmarkPath != null)
                {
                    try
                    {
                        using var opts = new SessionOptions { IntraOpNumThreads = Math.Min(4, Environment.ProcessorCount) };
                        opts.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                        _landmarkSession = new InferenceSession(landmarkPath, opts);
                        _loadedLandmarkModelPath = landmarkPath;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"加载五官关键点 ONNX 模型失败: {landmarkPath}", ex);
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
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
            using var opts = new SessionOptions { IntraOpNumThreads = Math.Min(4, Environment.ProcessorCount) };
            opts.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            var candidate = new InferenceSession(modelPath, opts);
            if (!IsValidYuNetSession(candidate))
            {
                candidate.Dispose();
                return false;
            }

            var old = _session;
            _session = candidate;
            _loadedModelPath = modelPath;
            old?.Dispose();
            _logger.Info($"已加载离线人脸与五官检测模型: {Path.GetFileName(modelPath)}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"加载人脸检测 ONNX 模型失败: {modelPath}", ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 检测输入图像中的人脸边界框及 5 个五官关键点（右眼、左眼、鼻尖、右嘴角、左嘴角）。
    /// 返回坐标位于 source 像素坐标系内，已按置信度降序排列。
    /// </summary>
    public IReadOnlyList<DetectedFace> DetectFaces(
        SKBitmap source,
        float scoreThreshold = 0.45f,
        float nmsThreshold = 0.30f,
        int maxFaces = 20) => DetectFaces(source, scoreThreshold, nmsThreshold, maxFaces, CancellationToken.None);

    private IReadOnlyList<DetectedFace> DetectFaces(
        SKBitmap source, float scoreThreshold, float nmsThreshold, int maxFaces, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || source == null || source.Width < 16 || source.Height < 16)
            return [];

        try
        {
            _gate.Wait(token);
        }
        catch (ObjectDisposedException)
        {
            return [];
        }
        try
        {
            token.ThrowIfCancellationRequested();
            var session = _session;
            if (_disposed || session == null) return [];

            float[]? pooledTensor = null;
            try
            {
                var inputMeta = session.InputMetadata.First();
                string inputName = inputMeta.Key;
                int[] dims = inputMeta.Value.Dimensions;
                int targetH = dims.Length >= 4 && dims[2] > 0 ? dims[2] : 640;
                int targetW = dims.Length >= 4 && dims[3] > 0 ? dims[3] : 640;

                float letterScale = Math.Min((float)targetW / source.Width, (float)targetH / source.Height);
                int nw = Math.Clamp((int)MathF.Round(source.Width * letterScale), 1, targetW);
                int nh = Math.Clamp((int)MathF.Round(source.Height * letterScale), 1, targetH);
                int ox = (targetW - nw) / 2;
                int oy = (targetH - nh) / 2;
                float scaleX = (float)nw / source.Width;
                float scaleY = (float)nh / source.Height;

                using var canvasBmp = new SKBitmap(new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
                using (var canvas = new SKCanvas(canvasBmp))
                using (var srcImg = SKImage.FromBitmap(source))
                {
                    canvas.Clear(new SKColor(128, 128, 128, 255));
                    canvas.DrawImage(
                        srcImg,
                        SKRect.Create(ox, oy, nw, nh),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                }

                int planeSize = targetW * targetH;
                int tensorElems = planeSize * 3;
                pooledTensor = ArrayPool<float>.Shared.Rent(tensorElems);
                var tensor = new DenseTensor<float>(pooledTensor.AsMemory(0, tensorElems), [1, 3, targetH, targetW]);

                byte* pCanvas = (byte*)canvasBmp.GetPixels().ToPointer();
                int cRowBytes = canvasBmp.RowBytes;
                fixed (float* tPtr = pooledTensor)
                {
                    float* p0 = tPtr;
                    float* p1 = tPtr + planeSize;
                    float* p2 = tPtr + planeSize * 2;
                    for (int y = 0; y < targetH; y++)
                    {
                        byte* sRow = pCanvas + y * cRowBytes;
                        int rowT = y * targetW;
                        for (int x = 0; x < targetW; x++)
                        {
                            byte* px = sRow + x * 4;
                            int tIdx = rowT + x;
                            p0[tIdx] = px[0];
                            p1[tIdx] = px[1];
                            p2[tIdx] = px[2];
                        }
                    }
                }
                GC.KeepAlive(canvasBmp);

                var inputs = new List<NamedOnnxValue>(1) { NamedOnnxValue.CreateFromTensor(inputName, tensor) };
                using var results = OnnxInference.Run(session, inputs, token);
                var outputs = new Dictionary<string, DenseTensor<float>>(results.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var r in results)
                {
                    if (r.AsTensor<float>() is DenseTensor<float> dt)
                        outputs[r.Name] = dt;
                }

                var candidates = DecodeYuNetCandidates(outputs, targetW, targetH, ox, oy, scaleX, scaleY, source.Width, source.Height, scoreThreshold);
                var faces = ApplyNms(candidates, nmsThreshold, maxFaces);
                var landmarkSession = _landmarkSession;
                if (landmarkSession == null) return faces;

                var refined = new List<DetectedFace>(faces.Count);
                foreach (var f in faces)
                {
                    try
                    {
                        refined.Add(RefineLandmarks(source, f, landmarkSession, token));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"五官关键点精修降级回退 YuNet 坐标: {ex.Message}");
                        refined.Add(f);
                    }
                }
                return refined;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.Error("人脸与五官 ONNX 推理异常", ex);
                return [];
            }
            finally
            {
                if (pooledTensor != null)
                    ArrayPool<float>.Shared.Return(pooledTensor);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private DetectedFace RefineLandmarks(SKBitmap source, DetectedFace face, InferenceSession landmarkSession, CancellationToken token)
    {
        static float Angle(SKPoint from, SKPoint to) => MathF.Atan2(to.Y - from.Y, to.X - from.X);
        static bool Differ(float a, float b, float degrees) =>
            MathF.Abs(MathF.IEEERemainder(a - b, MathF.Tau)) >= degrees * MathF.PI / 180f;

        float eyeAngle = Angle(face.RightEye, face.LeftEye);
        var refined = RefineLandmarksAtAngle(source, face, landmarkSession, eyeAngle, token, out _);
        if (refined.Mesh != null) return refined;

        // 粗检在倾斜的人脸上可能把一只眼定位到眉毛或头发，导致首轮裁剪方向偏斜。
        float mouthAngle = Angle(face.RightMouth, face.LeftMouth);
        if (!Differ(mouthAngle, eyeAngle, 10f)) return face;
        var retry = RefineLandmarksAtAngle(source, face, landmarkSession, mouthAngle, token, out float retryScore);
        if (retry.Mesh == null) return face;

        float correctedAngle = Angle(retry.RightEye, retry.LeftEye);
        if (Differ(correctedAngle, mouthAngle, 5f))
        {
            var aligned = RefineLandmarksAtAngle(source, face, landmarkSession, correctedAngle, token, out float alignedScore);
            if (aligned.Mesh != null && alignedScore >= retryScore)
                retry = aligned;
        }

        // 重试后的鼻尖和嘴角分别与独立粗检核对，允许约三分之一瞳距的修正。
        float tolerance = retry.EyeDistance / 3f;
        bool NearCoarsePoint(SKPoint precise, SKPoint coarse)
        {
            float dx = precise.X - coarse.X, dy = precise.Y - coarse.Y;
            return dx * dx + dy * dy <= tolerance * tolerance;
        }

        bool keepNose = NearCoarsePoint(retry.NoseTip, face.NoseTip);
        bool keepRightMouth = NearCoarsePoint(retry.RightMouth, face.RightMouth);
        bool keepLeftMouth = NearCoarsePoint(retry.LeftMouth, face.LeftMouth);
        if (keepNose && keepRightMouth && keepLeftMouth) return retry;

        return retry with
        {
            NoseTip = keepNose ? retry.NoseTip : face.NoseTip,
            RightMouth = keepRightMouth ? retry.RightMouth : face.RightMouth,
            LeftMouth = keepLeftMouth ? retry.LeftMouth : face.LeftMouth,
            Mesh = null
        };
    }

    private static DetectedFace RefineLandmarksAtAngle(
        SKBitmap source, DetectedFace face, InferenceSession landmarkSession, float angle,
        CancellationToken token, out float presenceScore)
    {
        token.ThrowIfCancellationRequested();
        const int size = 256;
        float side = Math.Max(face.Width, face.Height) * 1.5f;
        float cx = face.X + face.Width * 0.5f;
        float cy = face.Y + face.Height * 0.5f;
        using var crop = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        using (var canvas = new SKCanvas(crop))
        {
            canvas.Clear(SKColors.Black);
            canvas.Translate(size * 0.5f, size * 0.5f);
            canvas.Scale(size / side);
            canvas.RotateDegrees(-angle * 180f / MathF.PI);
            canvas.Translate(-cx, -cy);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        }

        const int elemCount = size * size * 3;
        float[] pooledTensor = ArrayPool<float>.Shared.Rent(elemCount);
        try
        {
            var tensor = new DenseTensor<float>(pooledTensor.AsMemory(0, elemCount), [1, size, size, 3]);
            const float inv255 = 1f / 255f;
            byte* cPtr = (byte*)crop.GetPixels().ToPointer();
            int cRowBytes = crop.RowBytes;
            fixed (float* tPtr = pooledTensor)
            {
                for (int y = 0; y < size; y++)
                {
                    byte* sRow = cPtr + y * cRowBytes;
                    float* dRow = tPtr + y * size * 3;
                    for (int x = 0; x < size; x++)
                    {
                        byte* px = sRow + x * 4;
                        float* dst = dRow + x * 3;
                        dst[0] = px[2] * inv255;
                        dst[1] = px[1] * inv255;
                        dst[2] = px[0] * inv255;
                    }
                }
            }
            GC.KeepAlive(crop);

            using var results = OnnxInference.Run(landmarkSession, [
                NamedOnnxValue.CreateFromTensor(landmarkSession.InputMetadata.First().Key, tensor)], token);
            // Identity_1 是人脸存在性的 logit，0 对应 0.5 概率。
            var presenceOut = results.FirstOrDefault(r => r.Name == "Identity_1");
            var pointsOut = results.FirstOrDefault(r => r.Name == "Identity");
            presenceScore = float.NegativeInfinity;
            if (presenceOut == null || pointsOut == null) return face;
            presenceScore = presenceOut.AsTensor<float>().First();
            if (presenceScore < 0f) return face;

            var idTensor = pointsOut.AsTensor<float>();
            ReadOnlySpan<float> points = idTensor is DenseTensor<float> denseId
                ? denseId.Buffer.Span
                : idTensor.ToArray();
            if (points.Length < 474 * 3) return face;

            static SKPoint ProjectPoint(ReadOnlySpan<float> pts, int index, int sz, float sd, float cX, float cY, float cosA, float sinA)
            {
                float x = (pts[index * 3] / sz - 0.5f) * sd;
                float y = (pts[index * 3 + 1] / sz - 0.5f) * sd;
                return new SKPoint(cX + x * cosA - y * sinA, cY + x * sinA + y * cosA);
            }

            float cosA = MathF.Cos(angle);
            float sinA = MathF.Sin(angle);

            int landmarkCount = Math.Min(478, points.Length / 3);
            var pts2D = new SKPoint[landmarkCount];
            var ptsZ = new float[landmarkCount];
            for (int i = 0; i < landmarkCount; i++)
            {
                pts2D[i] = ProjectPoint(points, i, size, side, cx, cy, cosA, sinA);
                ptsZ[i] = points[i * 3 + 2] / size;
            }

            // 478 点结果中的双眼虹膜中心、鼻尖与两侧嘴角，同时保留完整 3D 网格供影棚重打光、精准磨皮及合规检测使用。
            return face with
            {
                RightEye = pts2D[468],
                LeftEye = pts2D[473],
                NoseTip = pts2D[1],
                RightMouth = pts2D[61],
                LeftMouth = pts2D[291],
                Mesh = new FaceMeshLandmarks(pts2D, ptsZ)
            };
        }
        finally
        {
            ArrayPool<float>.Shared.Return(pooledTensor);
        }
    }

    /// <summary>
    /// 按 QuarterTurns 先旋正图像执行人脸检测，再把检出的边界框与五官坐标映射回原图坐标系。
    /// </summary>
    public IReadOnlyList<DetectedFace> DetectFacesWithQuarterTurns(
        SKBitmap rawSource,
        int quarterTurns,
        float scoreThreshold = 0.45f) => DetectFacesWithQuarterTurns(rawSource, quarterTurns, CancellationToken.None, scoreThreshold);

    public IReadOnlyList<DetectedFace> DetectFacesWithQuarterTurns(
        SKBitmap rawSource, int quarterTurns, CancellationToken token, float scoreThreshold = 0.45f)
    {
        token.ThrowIfCancellationRequested();
        int qt = ((quarterTurns % 4) + 4) % 4;
        if (qt == 0)
            return DetectFaces(rawSource, scoreThreshold, 0.30f, 20, token);

        int w = rawSource.Width;
        int h = rawSource.Height;
        using var rotated = ExifAndColorHelper.RotateQuarterTurnsFast(rawSource, qt);

        var rotFaces = DetectFaces(rotated, scoreThreshold, 0.30f, 20, token);
        if (rotFaces.Count == 0) return rotFaces;

        var mapped = new List<DetectedFace>(rotFaces.Count);
        foreach (var f in rotFaces)
            mapped.Add(f.TransformPoints(pt => InverseRotatePoint(pt, qt, w, h)));

        return mapped;
    }

    internal static SKPoint InverseRotatePoint(SKPoint pt, int qt, int rawW, int rawH) => qt switch
    {
        1 => new SKPoint(pt.Y, rawH - pt.X),
        2 => new SKPoint(rawW - pt.X, rawH - pt.Y),
        3 => new SKPoint(rawW - pt.Y, pt.X),
        _ => pt
    };

    private static List<DetectedFace> DecodeYuNetCandidates(
        Dictionary<string, DenseTensor<float>> outputs,
        int targetW,
        int targetH,
        int ox,
        int oy,
        float scaleX,
        float scaleY,
        int srcW,
        int srcH,
        float scoreThreshold)
    {
        var list = new List<DetectedFace>();

        foreach (int s in YuNetStrides)
        {
            if (!outputs.TryGetValue($"cls_{s}", out var clsTensor) ||
                !outputs.TryGetValue($"obj_{s}", out var objTensor) ||
                !outputs.TryGetValue($"bbox_{s}", out var bboxTensor) ||
                !outputs.TryGetValue($"kps_{s}", out var kpsTensor))
            {
                continue;
            }

            ReadOnlySpan<float> cls = clsTensor.Buffer.Span;
            ReadOnlySpan<float> obj = objTensor.Buffer.Span;
            ReadOnlySpan<float> bbox = bboxTensor.Buffer.Span;
            ReadOnlySpan<float> kps = kpsTensor.Buffer.Span;

            int featW = targetW / s;
            int featH = targetH / s;
            int anchorCount = Math.Min(featW * featH, Math.Min(cls.Length, obj.Length));
            if (anchorCount <= 0 || bbox.Length < anchorCount * 4 || kps.Length < anchorCount * 10)
                continue;

            bool clsNeedSigmoid = NeedsSigmoid(cls, anchorCount);
            bool objNeedSigmoid = NeedsSigmoid(obj, anchorCount);

            for (int idx = 0; idx < anchorCount; idx++)
            {
                float c = clsNeedSigmoid ? Sigmoid(cls[idx]) : Math.Clamp(cls[idx], 0f, 1f);
                float o = objNeedSigmoid ? Sigmoid(obj[idx]) : Math.Clamp(obj[idx], 0f, 1f);
                float score = MathF.Sqrt(Math.Clamp(c * o, 0f, 1f));
                if (score < scoreThreshold) continue;

                int gx = idx % featW;
                int gy = idx / featW;

                int bOff = idx * 4;
                float dx = bbox[bOff];
                float dy = bbox[bOff + 1];
                float dw = bbox[bOff + 2];
                float dh = bbox[bOff + 3];

                float cx = (gx + dx) * s;
                float cy = (gy + dy) * s;
                float bw = MathF.Exp(Math.Clamp(dw, -10f, 10f)) * s;
                float bh = MathF.Exp(Math.Clamp(dh, -10f, 10f)) * s;

                float rx = ((cx - bw * 0.5f) - ox) / scaleX;
                float ry = ((cy - bh * 0.5f) - oy) / scaleY;
                float rw = bw / scaleX;
                float rh = bh / scaleY;

                // 裁切边界框至有效原图范围内，过滤掉落在灰边 padding 里的无效框
                float x1 = Math.Clamp(rx, 0f, srcW);
                float y1 = Math.Clamp(ry, 0f, srcH);
                float x2 = Math.Clamp(rx + rw, 0f, srcW);
                float y2 = Math.Clamp(ry + rh, 0f, srcH);
                if (x2 - x1 < 8f || y2 - y1 < 8f) continue;

                int kOff = idx * 10;
                static SKPoint Kp(ReadOnlySpan<float> spanKps, int offset, int kIdx, int gridX, int gridY, int stride, float oX, float oY, float scX, float scY) => new(
                    ((gridX + spanKps[offset + kIdx * 2]) * stride - oX) / scX,
                    ((gridY + spanKps[offset + kIdx * 2 + 1]) * stride - oY) / scY);

                list.Add(new DetectedFace(
                    x1,
                    y1,
                    x2 - x1,
                    y2 - y1,
                    Kp(kps, kOff, 0, gx, gy, s, ox, oy, scaleX, scaleY),
                    Kp(kps, kOff, 1, gx, gy, s, ox, oy, scaleX, scaleY),
                    Kp(kps, kOff, 2, gx, gy, s, ox, oy, scaleX, scaleY),
                    Kp(kps, kOff, 3, gx, gy, s, ox, oy, scaleX, scaleY),
                    Kp(kps, kOff, 4, gx, gy, s, ox, oy, scaleX, scaleY),
                    score));
            }
        }

        return list;
    }

    private static List<DetectedFace> ApplyNms(List<DetectedFace> candidates, float iouThreshold, int maxFaces)
    {
        if (candidates.Count == 0) return candidates;
        var sorted = candidates.OrderByDescending(f => f.Confidence).ToList();
        var kept = new List<DetectedFace>(Math.Min(maxFaces, sorted.Count));

        foreach (var cand in sorted)
        {
            bool suppressed = false;
            foreach (var prev in kept)
            {
                if (ComputeIoU(cand, prev) > iouThreshold)
                {
                    suppressed = true;
                    break;
                }
            }

            if (!suppressed)
            {
                kept.Add(cand);
                if (kept.Count >= maxFaces) break;
            }
        }

        return kept;
    }

    private static float ComputeIoU(DetectedFace a, DetectedFace b)
    {
        float x1 = Math.Max(a.X, b.X);
        float y1 = Math.Max(a.Y, b.Y);
        float x2 = Math.Min(a.BoxRight, b.BoxRight);
        float y2 = Math.Min(a.BoxBottom, b.BoxBottom);
        float interW = Math.Max(0f, x2 - x1);
        float interH = Math.Max(0f, y2 - y1);
        float inter = interW * interH;
        float union = a.Width * a.Height + b.Width * b.Height - inter;
        return union > 1e-5f ? inter / union : 0f;
    }

    private static bool NeedsSigmoid(ReadOnlySpan<float> arr, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float v = arr[i];
            if (v < -0.05f || v > 1.05f) return true;
        }
        return false;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    private static bool IsFaceCandidateFile(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Contains("modnet", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("landmark", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("matting", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("u2net", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("birefnet", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("rmbg", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("relight", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("dpr", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("zerodce", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("illumination", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return name.Contains("yunet", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("face", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("scrfd", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("det", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetFacePriority(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Contains("yunet", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("face", StringComparison.OrdinalIgnoreCase)) return 1;
        return 5;
    }

    private static bool IsValidYuNetSession(InferenceSession session)
    {
        if (session.InputMetadata.Count == 0 || session.OutputMetadata.Count < 4)
            return false;

        return YuNetStrides.Any(s =>
            session.OutputMetadata.ContainsKey($"cls_{s}") &&
            session.OutputMetadata.ContainsKey($"obj_{s}") &&
            session.OutputMetadata.ContainsKey($"bbox_{s}") &&
            session.OutputMetadata.ContainsKey($"kps_{s}"));
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
            _landmarkSession?.Dispose();
            _landmarkSession = null;
            _loadedLandmarkModelPath = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
