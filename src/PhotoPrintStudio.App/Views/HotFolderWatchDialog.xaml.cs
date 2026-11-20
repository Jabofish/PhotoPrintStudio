using System.Windows;
using PhotoPrintStudio.App.Themes;
using PhotoPrintStudio.App.ViewModels;

namespace PhotoPrintStudio.App.Views;

public partial class HotFolderWatchDialog : Window
{
    public HotFolderWatchDialog(MainViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, vm.IsDarkTheme);
        Closed += (_, _) => vm.SaveConfig();
    }
}
