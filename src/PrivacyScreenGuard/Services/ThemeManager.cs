using System;
using System.Windows;

// WinForms 隐式全局 using 会引入 System.Windows.Forms.Application，这里显式消歧
using Application = System.Windows.Application;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 主题管理器：在明亮/深色两套画刷字典之间整体切换（Controls.xaml 控件样式常驻，
/// 其中的 DynamicResource 引用会自动跟随画刷变化，无需重建任何窗口）。
/// </summary>
public static class ThemeManager
{
    /// <summary>画刷字典在 App.Resources.MergedDictionaries 中的索引（Controls.xaml 之前）。</summary>
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
