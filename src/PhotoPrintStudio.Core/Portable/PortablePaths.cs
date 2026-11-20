using System.Text.Json;

namespace PhotoPrintStudio.Core.Portable;

/// <summary>
/// 严格隔离在程序同级目录的绿色便携路径管理器（零注册表、零 AppData 写入）。
/// </summary>
public sealed class PortablePaths
{
    public string BaseDirectory { get; }
    public string DataDirectory { get; }
    public string ConfigFilePath { get; }
    public string PrintersDirectory { get; }
    public string PresetsDirectory { get; }
    public string TemplatesDirectory { get; }
    public string IccDirectory { get; }
    public string ModelsDirectory { get; }
    public string SuitsDirectory { get; }
    public string ProjectsDirectory { get; }
    public string RecoveryDirectory { get; }
    public string LogsDirectory { get; }

    public PortablePaths(string? baseDirectoryOverride = null)
    {
        BaseDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(baseDirectoryOverride)
                ? AppContext.BaseDirectory
                : baseDirectoryOverride);

        DataDirectory = Path.Combine(BaseDirectory, "portable_data");
        ConfigFilePath = Path.Combine(DataDirectory, "config.json");
        PrintersDirectory = Path.Combine(DataDirectory, "printers");
        PresetsDirectory = Path.Combine(DataDirectory, "presets");
        TemplatesDirectory = Path.Combine(DataDirectory, "templates");
        IccDirectory = Path.Combine(DataDirectory, "icc");
        ModelsDirectory = Path.Combine(DataDirectory, "models");
        SuitsDirectory = Path.Combine(DataDirectory, "suits");
        ProjectsDirectory = Path.Combine(DataDirectory, "projects");
        RecoveryDirectory = Path.Combine(DataDirectory, "recovery");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(PrintersDirectory);
        Directory.CreateDirectory(PresetsDirectory);
        Directory.CreateDirectory(TemplatesDirectory);
        Directory.CreateDirectory(IccDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(SuitsDirectory);
        Directory.CreateDirectory(ProjectsDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true
    };
}
