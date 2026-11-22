using System.Windows;
using System.Windows.Controls;
using PhotoPrintStudio.App.Themes;

namespace PhotoPrintStudio.App.Views;

public partial class ShortcutsDialog : Window
{
    private static readonly (string Group, (string Keys, string Action)[] Items)[] Groups =
    [
        ("工程与输出",
        [
            ("Ctrl+N", "新建空白工程"),
            ("Ctrl+O", "导入照片"),
            ("Ctrl+Shift+O", "打开工程"),
            ("Ctrl+S / Ctrl+Shift+S", "保存 / 另存为"),
            ("Ctrl+Z / Ctrl+Y", "撤销 / 重做（也可按 Ctrl+Shift+Z）"),
            ("Ctrl+E / Ctrl+Shift+E", "导出当前页 JPG / 选择导出格式"),
            ("Ctrl+P", "打印当前页"),
        ]),
        ("工作区与设置",
        [
            ("Alt+1 / 2 / 3", "调色修图 / 卡片装饰 / 打印导出"),
            ("Alt+Shift+1 / 2 / 3", "光影与色彩 / 曲线与分色 / 裁剪与人像"),
            ("Alt+4 / 5", "聚焦模板列表 / 画布"),
            ("Alt+6 / 7", "打开照片素材 / 打印页面"),
            ("Ctrl+, / Ctrl+Shift+,", "设置 / 预览设置"),
            ("Alt+F / F1", "文件菜单 / 键盘快捷键"),
        ]),
        ("页面导航与管理",
        [
            ("Page Up / Page Down", "上一页 / 下一页"),
            ("方向键 / Home / End", "翻页 / 首页 / 末页（画布）"),
            ("Ctrl+G", "跳转到指定页"),
            ("Ctrl+Shift+N", "新增空白页"),
            ("Ctrl+Shift+D", "复制当前页"),
            ("Alt+← / Alt+→", "前移 / 后移当前页（打印页面）"),
            ("Delete", "删除所选页（打印页面）"),
        ]),
        ("画布与照片",
        [
            ("G / R / Y", "排版总览 / 单图裁剪 / 分屏对比（画布）"),
            ("F6", "切换排版总览与单图裁剪"),
            ("Ctrl+= / Ctrl+- / Ctrl+0", "放大 / 缩小 / 适应窗口"),
            ("Ctrl+[ / Ctrl+]", "逆时针 / 顺时针旋转 90°"),
            ("J / Shift+T", "溢出提示 / 两点拉直（画布）"),
            ("按住空格", "查看原图（画布）"),
            ("Esc / Delete", "退出工具 / 清空当前格（画布）"),
            ("Shift+滚轮", "缩放格内照片（排版总览）"),
            ("Alt+拖动格位", "互换或移动两格照片（排版总览）"),
            ("中键拖动", "平移已放大的画布"),
        ]),
        ("照片素材与选择",
        [
            ("双击照片", "快速放入空位或替换当前格"),
            ("Ctrl+A", "全选（照片素材或打印页面）"),
            ("Ctrl+Enter", "打开所选照片的排版与移除菜单"),
            ("Delete", "从工程移除所选照片（照片素材）"),
            ("右键菜单", "画布、照片与页面均支持快捷操作"),
            ("Alt+拖动 / Ctrl+拖动", "从照片卡片开始框选 / 追加选择"),
        ]),
        ("调色与名称",
        [
            ("Ctrl+C / Ctrl+V", "复制 / 粘贴照片调色参数"),
            ("方向键 / Shift+方向键", "滑块微调 / 每次调整十步"),
            ("Enter / F2", "输入滑块数值，Esc 取消"),
            ("Alt+Backspace", "恢复当前滑块默认值"),
            ("F2", "重命名当前自定义模板或调色预设"),
        ]),
    ];

    public ShortcutsDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBarTheme(this, ThemeManager.IsDarkTheme);
        foreach (var (group, items) in Groups)
            GroupsHost.Children.Add(BuildGroup(group, items));
    }

    private static FrameworkElement BuildGroup(string title, (string Keys, string Action)[] items)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 20, 8) };
        var heading = new TextBlock { Text = title };
        heading.SetResourceReference(StyleProperty, "DialogHeading");
        panel.Children.Add(heading);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < items.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new Border
            {
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 2, 10, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = items[i].Keys, FontSize = 11 }
            };
            keys.SetResourceReference(Border.BackgroundProperty, "BrushBgInput");
            keys.SetResourceReference(Border.BorderBrushProperty, "BrushBorderStrong");
            Grid.SetRow(keys, i);
            grid.Children.Add(keys);

            var action = new TextBlock { Text = items[i].Action, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            action.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);
        }
        panel.Children.Add(grid);
        return panel;
    }
}
