namespace PhotoPrintStudio.Core.Models;

/// <summary>
/// 联机拍摄热文件夹新增照片后的入库与排版策略。
/// 默认采用「替换当前页」：所有历史样张均保留在素材库，但仅当前满意成片保留在打印页上，避免样张/废片被误打。
/// </summary>
public enum HotFolderPlacementStrategy
{
    /// <summary>保留所有样张在素材库，新片自动替换到当前打印页（推荐，杜绝连拍废片堆叠多页误打）。</summary>
    ReplaceCurrentSheet = 0,

    /// <summary>仅加入素材库并自动选中预览，绝不修改当前打印页（适合先连拍挑片再手动拖入上版）。</summary>
    PoolOnlySelectLatest = 1,

    /// <summary>按顺序填入当前页空置格位，满页后仅入素材库不再自动新增打印页。</summary>
    FillEmptySlotsOnly = 2,

    /// <summary>每张新片自动追加一页（适合活动现场流水拍摄；顶部「打印」按钮仍默认仅打印当前页）。</summary>
    AppendNewPage = 3
}

/// <summary>
/// 联机新片到达时自动套用的调色/处理参数来源。
/// </summary>
public enum HotFolderEditTemplateMode
{
    /// <summary>不套用额外调色模板（保持相机直出原图，仍可单独叠加下方快捷开关）。</summary>
    None = 0,

    /// <summary>自动套用指定的调色预设（支持内置与自定义预设）。</summary>
    ApplyColorPreset = 1,

    /// <summary>复刻当前选中照片的完整调色、AI 换底、影棚优化与电子正装设置（影楼试拍定版神器）。</summary>
    CloneCurrentPhotoStyle = 2
}

/// <summary>
/// 联机拍摄热文件夹监控的持久化配置与自动预处理策略。
/// 注意：安全设计上永不自动触发物理打印，所有样张必须经人工确认/微调后手动点击打印。
/// </summary>
public sealed class HotFolderWatchSettings
{
    public string WatchDirectory { get; set; } = "";
    public bool IncludeSubdirectories { get; set; } = false;
    public bool IgnoreExistingOnStart { get; set; } = true;

    public HotFolderPlacementStrategy PlacementStrategy { get; set; } = HotFolderPlacementStrategy.ReplaceCurrentSheet;
    public HotFolderEditTemplateMode EditTemplateMode { get; set; } = HotFolderEditTemplateMode.CloneCurrentPhotoStyle;
    public string ColorPresetId { get; set; } = "";

    public bool AutoStudioEnhance { get; set; } = false;
    public double StudioEnhanceStrength { get; set; } = 70.0;
    public StudioEnhanceMode StudioEnhanceMode { get; set; } = StudioEnhanceMode.Studio;

    public bool AutoBackgroundReplace { get; set; } = false;
    public string ReplacementBackgroundHex { get; set; } = "#438EDB";

    public bool AutoFaceFrame { get; set; } = false;
    public bool AutoSelectLatest { get; set; } = true;

    public HotFolderWatchSettings Clone() => new()
    {
        WatchDirectory = WatchDirectory,
        IncludeSubdirectories = IncludeSubdirectories,
        IgnoreExistingOnStart = IgnoreExistingOnStart,
        PlacementStrategy = PlacementStrategy,
        EditTemplateMode = EditTemplateMode,
        ColorPresetId = ColorPresetId,
        AutoStudioEnhance = AutoStudioEnhance,
        StudioEnhanceStrength = StudioEnhanceStrength,
        StudioEnhanceMode = StudioEnhanceMode,
        AutoBackgroundReplace = AutoBackgroundReplace,
        ReplacementBackgroundHex = ReplacementBackgroundHex,
        AutoFaceFrame = AutoFaceFrame,
        AutoSelectLatest = AutoSelectLatest
    };
}
