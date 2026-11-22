using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoPrintStudio.Core.Models;
using PhotoPrintStudio.Core.Portable;

namespace PhotoPrintStudio.Core.Persistence;

public sealed class PortableDataStore : IWorkspaceStore
{
    public const string CurrentProjectSchemaVersion = "2.1";

    private readonly PortablePaths _paths;
    private readonly AppLogger _logger;

    public PortableDataStore(PortablePaths paths, AppLogger logger)
    {
        _paths = paths;
        _logger = logger;
        _paths.EnsureDirectoriesExist();
    }

    public AppPortableConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_paths.ConfigFilePath))
            {
                var json = File.ReadAllText(_paths.ConfigFilePath);
                return JsonSerializer.Deserialize<AppPortableConfig>(json, PortablePaths.JsonOptions)
                    ?? new AppPortableConfig();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("读取 config.json 失败，使用默认配置", ex);
        }
        return new AppPortableConfig();
    }

    public void SaveConfig(AppPortableConfig config)
    {
        _paths.EnsureDirectoriesExist();
        var json = JsonSerializer.Serialize(config, PortablePaths.JsonOptions);
        WriteAllTextAtomic(_paths.ConfigFilePath, json);
    }

    public bool HasSavedPrinterProfile(string printerName)
    {
        string key = GetSafeKey(printerName);
        return File.Exists(Path.Combine(_paths.PrintersDirectory, $"{key}.json"));
    }

    public PrinterCalibrationProfile LoadPrinterProfile(string printerName)
    {
        string key = GetSafeKey(printerName);
        string path = Path.Combine(_paths.PrintersDirectory, $"{key}.json");
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var profile = JsonSerializer.Deserialize<PrinterCalibrationProfile>(json, PortablePaths.JsonOptions);
                if (profile != null) return profile;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"读取打印机校准配置失败: {printerName}", ex);
        }

        return new PrinterCalibrationProfile
        {
            PrinterName = printerName,
            PrinterKey = key
        };
    }

    public void SavePrinterProfile(PrinterCalibrationProfile profile)
    {
        _paths.EnsureDirectoriesExist();
        profile.PrinterKey = GetSafeKey(profile.PrinterName);
        string path = Path.Combine(_paths.PrintersDirectory, $"{profile.PrinterKey}.json");
        var json = JsonSerializer.Serialize(profile, PortablePaths.JsonOptions);
        WriteAllTextAtomic(path, json);
    }

    public List<ColorPresetItem> LoadAllPresets()
    {
        var list = new List<ColorPresetItem>(ColorPresetItem.BuiltInPresets);
        if (!Directory.Exists(_paths.PresetsDirectory)) return list;

        foreach (var file in Directory.GetFiles(_paths.PresetsDirectory, "*.json").OrderBy(x => x))
        {
            try
            {
                var json = File.ReadAllText(file);
                var item = JsonSerializer.Deserialize<ColorPresetItem>(json, PortablePaths.JsonOptions);
                if (item != null)
                {
                    item.IsBuiltIn = false;
                    list.Add(item);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"读取自定义调色预设失败: {file}", ex);
            }
        }
        return list;
    }

    public void SaveCustomPreset(ColorPresetItem preset)
    {
        _paths.EnsureDirectoriesExist();
        preset.IsBuiltIn = false;
        if (string.IsNullOrWhiteSpace(preset.Id))
            preset.Id = $"user_{Guid.NewGuid():N}";
        string file = Path.Combine(_paths.PresetsDirectory, $"{GetSafeKey(preset.Id)}.json");
        WriteAllTextAtomic(file, JsonSerializer.Serialize(preset, PortablePaths.JsonOptions));
    }

    public bool DeleteCustomPreset(string presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId)) return false;
        string file = Path.Combine(_paths.PresetsDirectory, $"{GetSafeKey(presetId)}.json");
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    public List<LayoutTemplateDefinition> LoadCustomTemplates()
    {
        var list = new List<LayoutTemplateDefinition>();
        if (!Directory.Exists(_paths.TemplatesDirectory)) return list;

        foreach (var file in Directory.GetFiles(_paths.TemplatesDirectory, "*.json").OrderBy(x => x))
        {
            try
            {
                var json = File.ReadAllText(file);
                var tpl = JsonSerializer.Deserialize<LayoutTemplateDefinition>(json, PortablePaths.JsonOptions);
                if (tpl != null)
                {
                    tpl.IsBuiltIn = false;
                    list.Add(tpl);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"读取自定义排版模板失败: {file}", ex);
            }
        }
        return list;
    }

    public void SaveCustomTemplate(LayoutTemplateDefinition template)
    {
        _paths.EnsureDirectoriesExist();
        template.IsBuiltIn = false;
        if (string.IsNullOrWhiteSpace(template.Id))
            template.Id = $"tpl_user_{Guid.NewGuid():N}";
        string file = Path.Combine(_paths.TemplatesDirectory, $"{GetSafeKey(template.Id)}.json");
        WriteAllTextAtomic(file, JsonSerializer.Serialize(template, PortablePaths.JsonOptions));
    }

    public bool DeleteCustomTemplate(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return false;
        string file = Path.Combine(_paths.TemplatesDirectory, $"{GetSafeKey(templateId)}.json");
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    public IReadOnlyList<string> ListIccProfiles()
    {
        if (!Directory.Exists(_paths.IccDirectory)) return [];
        return Directory.GetFiles(_paths.IccDirectory, "*.*")
            .Where(f => f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToList();
    }

    public void SaveProject(string filePath, PrintProject project, bool copyAssetsBesideProject = false)
    {
        project.SavedAt = DateTime.Now;
        var toSave = new PrintProject
        {
            Version = project.Version,
            ProjectName = project.ProjectName,
            SavedAt = project.SavedAt,
            ActiveSheetIndex = project.ActiveSheetIndex,
            Assets = project.Assets.Select(a => a.CloneMetadataOnly()).ToList(),
            Sheets = project.Sheets.Select(s => s.Clone(preserveId: true)).ToList()
        };

        foreach (var asset in toSave.Assets)
        {
            var edit = asset.EditState;
            if (edit.FormalSuit == FormalSuitStyle.CustomPng &&
                string.Equals(Path.GetDirectoryName(edit.CustomSuitAssetPath), _paths.SuitsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                edit.CustomSuitAssetPath = Path.GetFileName(edit.CustomSuitAssetPath);
            }
        }

        if (copyAssetsBesideProject)
        {
            string projDir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
            string assetsSub = Path.Combine(projDir, Path.GetFileNameWithoutExtension(filePath) + "_assets");
            Directory.CreateDirectory(assetsSub);
            foreach (var asset in toSave.Assets)
            {
                string source = Path.GetFullPath(asset.FilePath);
                string name = Path.GetFileName(source);
                string prefix = $"{asset.Id}_";
                string dst = Path.Combine(assetsSub,
                    name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name : prefix + name);
                if (!string.Equals(source, dst, StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
                    File.Copy(source, dst, true);
                asset.FilePath = Path.GetRelativePath(projDir, dst);
            }
        }
        else
        {
            string projDir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
            string dataRoot = Path.TrimEndingDirectorySeparator(_paths.DataDirectory) + Path.DirectorySeparatorChar;
            bool projectInPortableData = (projDir + Path.DirectorySeparatorChar).StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase);
            foreach (var asset in toSave.Assets)
            {
                string source = Path.GetFullPath(asset.FilePath);
                string relative = Path.GetRelativePath(projDir, source);
                bool sourceInPortableData = source.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase);
                if ((projectInPortableData && sourceInPortableData) ||
                    (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}")))
                    asset.FilePath = relative;
            }
        }

        WriteAllTextAtomic(filePath, JsonSerializer.Serialize(toSave, PortablePaths.JsonOptions));
    }

    public PrintProject LoadProject(string filePath)
    {
        string json = File.ReadAllText(filePath, Encoding.UTF8);
        var project = JsonSerializer.Deserialize<PrintProject>(json, PortablePaths.JsonOptions)
            ?? throw new InvalidDataException("无效的工程文件格式。");

        ValidateAndMigrateProjectSchema(project, filePath);
        MigrateRetiredComparisonTemplate(project);

        string projDir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        foreach (var asset in project.Assets)
        {
            var edit = asset.EditState;
            if (edit.FormalSuit == FormalSuitStyle.CustomPng &&
                !string.IsNullOrEmpty(edit.CustomSuitAssetPath) &&
                Path.GetFileName(edit.CustomSuitAssetPath) == edit.CustomSuitAssetPath)
            {
                edit.CustomSuitAssetPath = Path.Combine(_paths.SuitsDirectory, edit.CustomSuitAssetPath);
            }

            if (!Path.IsPathRooted(asset.FilePath))
            {
                asset.FilePath = Path.GetFullPath(Path.Combine(projDir, asset.FilePath));
            }
        }
        return project;
    }

    private static void MigrateRetiredComparisonTemplate(PrintProject project)
    {
        foreach (var sheet in project.Sheets)
        {
            if (sheet.TemplateId == "tpl_6in_2in1_compare")
            {
                sheet.TemplateId = "tpl_6in_2in1_border";
                if (sheet.Slots is [var left, var right])
                {
                    sheet.LayoutSettings ??= new(left.XMm, Math.Round(right.XMm - left.XMm - left.WidthMm, 3),
                        left.BleedMm, left.CornerRadiusMm, 2, 2, null, null, false);
                }
                foreach (var slot in sheet.Slots)
                {
                    slot.Label = $"二合一 #{slot.Index + 1}";
                    if (slot.CustomCaption is "BEFORE" or "AFTER")
                        slot.CustomCaption = null;
                }
            }
            else if (project.Version == "2.0" && sheet.TemplateId == "tpl_6in_2in1_left" &&
                     sheet.Slots is [var first, var second] &&
                     (first.CustomCaption, second.CustomCaption) is ("BEFORE", "AFTER") or ("AFTER", "BEFORE"))
            {
                first.CustomCaption = second.CustomCaption = null;
            }
        }

        if (project.Version == "2.0")
            project.Version = CurrentProjectSchemaVersion;
    }

    private void ValidateAndMigrateProjectSchema(PrintProject project, string filePath)
    {
        string rawVersion = (project.Version ?? "").Trim();
        if (string.IsNullOrEmpty(rawVersion))
        {
            _logger.Warn($"工程文件缺少版本号，按兼容模式升级为 {CurrentProjectSchemaVersion}: {filePath}");
            project.Version = CurrentProjectSchemaVersion;
            return;
        }

        if (Version.TryParse(rawVersion, out var parsed))
        {
            if (parsed.Major < 2)
            {
                _logger.Info($"已自动迁移旧版工程 ({rawVersion} -> {CurrentProjectSchemaVersion}): {filePath}");
                project.Version = CurrentProjectSchemaVersion;
            }
            else if (parsed.Major > 2)
            {
                throw new InvalidDataException($"工程格式为 {rawVersion}，请使用支持该格式的新版软件打开。");
            }
        }
        else
        {
            _logger.Warn($"工程文件版本号格式异常 ({rawVersion})，尝试兼容加载: {filePath}");
            project.Version = CurrentProjectSchemaVersion;
        }
    }

    public void SaveAutoRecovery(PrintProject project)
    {
        try
        {
            _paths.EnsureDirectoriesExist();
            string path = Path.Combine(_paths.RecoveryDirectory, "autosave.ppsproj");
            SaveProject(path, project, copyAssetsBesideProject: false);
        }
        catch (Exception ex)
        {
            _logger.Error("保存自动恢复快照失败", ex);
        }
    }

    public PrintProject? TryLoadAutoRecovery()
    {
        string path = Path.Combine(_paths.RecoveryDirectory, "autosave.ppsproj");
        if (!File.Exists(path)) return null;
        try
        {
            return LoadProject(path);
        }
        catch
        {
            return null;
        }
    }

    public void ClearAutoRecovery()
    {
        string path = Path.Combine(_paths.RecoveryDirectory, "autosave.ppsproj");
        if (File.Exists(path)) File.Delete(path);
    }

    private string TemporaryWorkspacePath => Path.Combine(_paths.RecoveryDirectory, "workspace.ppsproj");

    public void SaveTemporaryWorkspace(PrintProject project)
    {
        _paths.EnsureDirectoriesExist();
        SaveProject(TemporaryWorkspacePath, project);
    }

    public PrintProject? LoadTemporaryWorkspace() =>
        File.Exists(TemporaryWorkspacePath) ? LoadProject(TemporaryWorkspacePath) : null;

    public void ClearTemporaryWorkspace()
    {
        if (File.Exists(TemporaryWorkspacePath)) File.Delete(TemporaryWorkspacePath);
    }

    private static void WriteAllTextAtomic(string targetPath, string content)
    {
        string fullTarget = Path.GetFullPath(targetPath);
        string dir = Path.GetDirectoryName(fullTarget)!;
        Directory.CreateDirectory(dir);
        string pending = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(pending, fullTarget, overwrite: true);
        }
        finally
        {
            if (File.Exists(pending))
            {
                try { File.Delete(pending); } catch { }
            }
        }
    }

    private static string GetSafeKey(string name)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        string shortHash = Convert.ToHexString(hash[..6]).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
        }
        string prefix = sb.Length > 24 ? sb.ToString(0, 24) : sb.ToString();
        return string.IsNullOrEmpty(prefix) ? shortHash : $"{prefix}_{shortHash}";
    }
}
