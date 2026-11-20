using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.Core.Persistence;

/// <summary>
/// 便携工作区持久化抽象，隔离文件系统存储以便 ViewModel 与业务逻辑脱离真实磁盘进行单元测试。
/// </summary>
public interface IWorkspaceStore
{
    AppPortableConfig LoadConfig();
    void SaveConfig(AppPortableConfig config);

    bool HasSavedPrinterProfile(string printerName);
    PrinterCalibrationProfile LoadPrinterProfile(string printerName);
    void SavePrinterProfile(PrinterCalibrationProfile profile);

    List<ColorPresetItem> LoadAllPresets();
    void SaveCustomPreset(ColorPresetItem preset);
    bool DeleteCustomPreset(string presetId);

    List<LayoutTemplateDefinition> LoadCustomTemplates();
    void SaveCustomTemplate(LayoutTemplateDefinition template);
    bool DeleteCustomTemplate(string templateId);

    IReadOnlyList<string> ListIccProfiles();

    void SaveProject(string filePath, PrintProject project, bool copyAssetsBesideProject = false);
    PrintProject LoadProject(string filePath);

    void SaveAutoRecovery(PrintProject project);
    PrintProject? TryLoadAutoRecovery();
    void ClearAutoRecovery();

    void SaveTemporaryWorkspace(PrintProject project);
    PrintProject? LoadTemporaryWorkspace();
    void ClearTemporaryWorkspace();
}
