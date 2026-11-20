using System.Text.Json;

namespace PhotoPrintStudio.Core.Imaging;

public sealed class ModelManifestDocument
{
    public string SchemaVersion { get; set; } = "1.0";
    public List<ModelManifestEntry> Models { get; set; } = [];
}

public sealed class ModelManifestEntry
{
    public string FileName { get; set; } = "";
    /// <summary>matting | face_detection | face_landmarks | relighting</summary>
    public string Role { get; set; } = "matting";
    public string DisplayName { get; set; } = "";
    public int Priority { get; set; } = 10;
    /// <summary>letterbox | stretch</summary>
    public string ResizeMode { get; set; } = "letterbox";
    /// <summary>minus1_to_1 | zero_to_1 | imagenet</summary>
    public string Normalization { get; set; } = "minus1_to_1";
    public bool OutputIsLogits { get; set; }
    public bool DefaultBundled { get; set; } = true;
    public string License { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public readonly record struct MattingPreprocessSpec(
    bool ResizeToSquare,
    (float R, float G, float B) RgbScale,
    (float R, float G, float B) RgbOffset,
    int Priority,
    bool OutputIsLogits);

/// <summary>
/// AI 模型清单元数据加载器：优先读取 portable_data\models\models.manifest.json，
/// 同时对用户手动放入的任意自定义 ONNX 模型提供向后兼容的启发式推断。
/// </summary>
public static class ModelManifestLoader
{
    public const string ManifestFileName = "models.manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyDictionary<string, ModelManifestEntry> LoadDirectoryManifest(string modelsDirectory)
    {
        var result = new Dictionary<string, ModelManifestEntry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(modelsDirectory)) return result;

        string manifestPath = Path.Combine(modelsDirectory, ManifestFileName);
        if (!File.Exists(manifestPath)) return result;

        try
        {
            string json = File.ReadAllText(manifestPath);
            var doc = JsonSerializer.Deserialize<ModelManifestDocument>(json, JsonOptions);
            if (doc?.Models == null) return result;

            foreach (var entry in doc.Models)
            {
                if (!string.IsNullOrWhiteSpace(entry.FileName))
                    result[entry.FileName.Trim()] = entry;
            }
        }
        catch
        {
            // 清单损坏时回退至启发式扫描，不阻断离线模型使用
        }

        return result;
    }

    public static bool IsRelightingModel(string modelPath, IReadOnlyDictionary<string, ModelManifestEntry> manifest)
    {
        string name = Path.GetFileName(modelPath);
        if (manifest.TryGetValue(name, out var entry))
            return string.Equals(entry.Role, "relighting", StringComparison.OrdinalIgnoreCase);

        return name.Contains("relight", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("dpr", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("zerodce", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("illumination", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMattingModel(string modelPath, IReadOnlyDictionary<string, ModelManifestEntry> manifest)
    {
        string name = Path.GetFileName(modelPath);
        if (manifest.TryGetValue(name, out var entry))
            return string.Equals(entry.Role, "matting", StringComparison.OrdinalIgnoreCase);

        if (IsRelightingModel(modelPath, manifest))
            return false;

        return !(name.Contains("yunet", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("face_detect", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("facedetect", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("face_landmark", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("scrfd", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("retinaface", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("landmark", StringComparison.OrdinalIgnoreCase));
    }

    public static MattingPreprocessSpec ResolveMattingSpec(
        string modelPath,
        IReadOnlyDictionary<string, ModelManifestEntry> manifest)
    {
        string name = Path.GetFileName(modelPath);
        if (manifest.TryGetValue(name, out var entry))
        {
            bool stretch = string.Equals(entry.ResizeMode, "stretch", StringComparison.OrdinalIgnoreCase);
            var (scale, offset) = entry.Normalization.ToLowerInvariant() switch
            {
                "imagenet" => (
                    (1f / (255f * 0.229f), 1f / (255f * 0.224f), 1f / (255f * 0.225f)),
                    (0.485f / 0.229f, 0.456f / 0.224f, 0.406f / 0.225f)),
                "zero_to_1" => (
                    (1f / 255f, 1f / 255f, 1f / 255f),
                    (0f, 0f, 0f)),
                _ => (
                    (1f / 127.5f, 1f / 127.5f, 1f / 127.5f),
                    (1f, 1f, 1f))
            };
            return new MattingPreprocessSpec(stretch, scale, offset, entry.Priority, entry.OutputIsLogits);
        }

        // 未登记的第三方 ONNX 模型启发式兼容
        bool isSalluNet = name.Contains("sallunet", StringComparison.OrdinalIgnoreCase);
        bool isBiRefNet = name.Contains("birefnet", StringComparison.OrdinalIgnoreCase);
        bool resizeToSquare = isSalluNet || isBiRefNet;
        float s = isSalluNet ? (1f / 255f) : (1f / 127.5f);
        float o = isSalluNet ? 0f : 1f;
        var rgbScale = isBiRefNet
            ? (1f / (255f * 0.229f), 1f / (255f * 0.224f), 1f / (255f * 0.225f))
            : (s, s, s);
        var rgbOffset = isBiRefNet
            ? (0.485f / 0.229f, 0.456f / 0.224f, 0.406f / 0.225f)
            : (o, o, o);

        int priority = 10;
        if (name.Equals(PortraitMattingService.PasPhotoMattingModelFile, StringComparison.OrdinalIgnoreCase)) priority = -2;
        else if (isSalluNet) priority = -1;
        else if (name.Contains("modnet", StringComparison.OrdinalIgnoreCase)) priority = 0;
        else if (isBiRefNet) priority = 1;
        else if (name.Contains("matting", StringComparison.OrdinalIgnoreCase)) priority = 2;
        else if (name.Contains("u2net", StringComparison.OrdinalIgnoreCase)) priority = 3;
        else if (name.Contains("rmbg", StringComparison.OrdinalIgnoreCase)) priority = 4;

        return new MattingPreprocessSpec(resizeToSquare, rgbScale, rgbOffset, priority, isBiRefNet);
    }
}
