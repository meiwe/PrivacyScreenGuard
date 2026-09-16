using System;
using System.Windows;

// WinForms 隐式全局 using 会引入 System.Windows.Forms.Application，这里显式消歧
using Application = System.Windows.Application;
using Wpf.Ui.Appearance;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 主题管理器：明暗两套主题整体切换。
/// Fluent 控件配色交给 WPF-UI 官方 ApplicationThemeManager；
/// 自定义画刷（背景/强调色/状态色）通过替换 App.Resources.MergedDictionaries[0] 实现。
/// </summary>
public static class ThemeManager
{
    /// <summary>自定义画刷字典在 App.Resources.MergedDictionaries 中的索引（Light.xaml）。</summary>
    private const int PaletteIndex = 0;

    /// <summary>主题标识。</summary>
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>当前主题（应用启动时由 Apply 设置）。</summary>
    public static string Current { get; private set; } = Light;

    /// <summary>
    /// 应用指定主题："light" / "dark"（其他值回退为 light）。
    /// 必须在 UI 线程调用（App 启动时或主题切换按钮）。
    /// </summary>
    public static void Apply(string theme)
    {
        bool dark = string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase);
        Current = dark ? Dark : Light;

        // 1. WPF-UI 官方主题：Fluent 控件配色、窗口标题栏等整体切换
        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light);

        // 2. 自定义画刷（背景/卡片/强调色/状态色）：整体替换调色板字典，
        //    所有 DynamicResource 引用自动刷新，无需重建任何窗口
        var dicts = Application.Current.Resources.MergedDictionaries;
        var source = new Uri(dark
            ? "pack://application:,,,/Themes/Dark.xaml"
            : "pack://application:,,,/Themes/Light.xaml");

        var palette = new ResourceDictionary { Source = source };
        if (dicts.Count > PaletteIndex)
        {
            dicts[PaletteIndex] = palette; // 替换画刷字典：全部 DynamicResource 自动刷新
        }
        else
        {
            dicts.Insert(PaletteIndex, palette);
        }
    }

    /// <summary>切换到另一套主题并返回新主题标识（"light" ↔ "dark"）。</summary>
    public static string Toggle()
    {
        string next = Current == Dark ? Light : Dark;
        Apply(next);
        return next;
    }
}
