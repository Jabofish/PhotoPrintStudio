using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace PhotoPrintStudio.App.Themes;

public static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static bool IsDarkTheme { get; private set; } = true;

    public static bool SystemUsesDarkTheme() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int light && light == 0;

    public static void ApplyTheme(bool isDark)
    {
        IsDarkTheme = isDark;
        var app = Application.Current;
        if (app == null) return;

        string targetName = isDark ? "DarkTheme.xaml" : "LightTheme.xaml";
        var merged = app.Resources.MergedDictionaries;
        int existingIdx = -1;
        bool alreadyTarget = false;
        for (int i = 0; i < merged.Count; i++)
        {
            var src = merged[i].Source?.OriginalString ?? "";
            if (src.Contains("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("LightTheme.xaml", StringComparison.OrdinalIgnoreCase))
            {
                existingIdx = i;
                alreadyTarget = src.Contains(targetName, StringComparison.OrdinalIgnoreCase);
                break;
            }
        }

        if (alreadyTarget) return;

        var uri = new Uri(isDark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml", UriKind.Relative);
        var newDict = new ResourceDictionary { Source = uri };

        if (existingIdx >= 0)
            merged[existingIdx] = newDict;
        else
            merged.Insert(0, newDict);

        foreach (Window win in app.Windows)
        {
            ApplyWindowTitleBarTheme(win, isDark);
        }
    }

    public static void ApplyWindowTitleBarTheme(Window window, bool isDark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch
        {
            // 非 Win10/11 环境下静默忽略
        }
    }
}
