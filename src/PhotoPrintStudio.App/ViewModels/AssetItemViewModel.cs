using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoPrintStudio.Core.Models;

namespace PhotoPrintStudio.App.ViewModels;

public partial class AssetItemViewModel : ObservableObject
{
    public PhotoAsset Asset { get; }

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaced))]
    [NotifyPropertyChangedFor(nameof(UsageBadgeText))]
    private int _usageCount;

    public bool IsPlaced => UsageCount > 0;
    public string UsageBadgeText => UsageCount > 0 ? $"×{UsageCount}" : "未排";

    [ObservableProperty]
    private bool _isSelected;

    public string Id => Asset.Id;
    public string DisplayName => Asset.DisplayName;
    public string ResolutionText => $"{Asset.PixelWidth}×{Asset.PixelHeight}";
    public string MetaText => $"{Asset.DateTaken?.ToString("yyyy.MM.dd") ?? ""}  {Asset.ColorProfileName}".Trim();

    public AssetItemViewModel(PhotoAsset asset, BitmapSource? thumbnail)
    {
        Asset = asset;
        _thumbnail = thumbnail;
    }
}
