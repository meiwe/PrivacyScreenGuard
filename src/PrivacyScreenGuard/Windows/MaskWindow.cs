using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Forms;
// WinForms 的 HorizontalAlignment 与 WPF 同名类型冲突，遮罩 UI 全部使用 WPF 版本
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
// WinForms 与 WPF 的 Image 同名，遮罩 UI 使用 WPF 版本
using Image = System.Windows.Controls.Image;
using PrivacyScreenGuard.Native;

namespace PrivacyScreenGuard.Windows;

/// <summary>
/// 单个显示器上的隐私遮罩窗口：模糊背景（屏幕快照高斯模糊）+ 压暗层 + 深色胶囊（黑底白字提示）。
/// 特性：点击穿透、不抢焦点、不出现在任务栏与 Alt+Tab、始终置顶。
/// 模糊背景由管理器在遮罩显示【前】捕获（避免拍到遮罩自身），失败时自动回退为半透明纯黑。
/// 界面由纯 C# 构建（不使用 XAML）。
/// </summary>
public sealed class MaskWindow : Window
{
    /// <summary>所属显示器设备名（与 Screen.DeviceName 一致，供管理器做缓存匹配）。</summary>
    public string MonitorName { get; }

    /// <summary>创建窗口时传入的 DPI 缩放（96 DPI = 1.0），用于把物理像素换算成 WPF 的 DIP。</summary>
    private readonly double _dpiScale;

    /// <summary>窗口句柄；SourceInitialized 之后有效。Hide() 不销毁句柄，可反复复用。</summary>
    private IntPtr _hwnd;

    /// <summary>当前目标屏幕；ShowFor 时设置，SourceInitialized 中据此做物理定位。</summary>
    private Screen? _targetScreen;

    /// <summary>HwndSource 引用，用于挂接 WndProc 钩子。</summary>
    private HwndSource? _hwndSource;

    /// <summary>模糊背景图层（显示该屏快照的高斯模糊结果，铺满全屏）。</summary>
    private Image? _blurryImage;

    /// <summary>
    /// 构造遮罩窗口（初始隐藏在屏幕外，等待 ShowFor 定位并显示）。
    /// </summary>
    /// <param name="monitorName">显示器设备名（Screen.DeviceName）。</param>
    /// <param name="screenDpiScale">该屏 DPI 缩放（如 1.5 = 150%）。</param>
    public MaskWindow(string monitorName, double screenDpiScale)
    {
        MonitorName = monitorName;
        _dpiScale = screenDpiScale > 0 ? screenDpiScale : 1.0;

        // —— 窗口基本样式 ——
        Title = "PrivacyScreenGuard Mask";
        WindowStyle = WindowStyle.None;            // 无边框
        AllowsTransparency = true;                 // 支持半透明背景
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;                            // 始终置顶
        ShowInTaskbar = false;                     // 不进任务栏
        ShowActivated = false;                     // 显示时不激活
        Focusable = false;                         // 不接受键盘焦点
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -2000;                              // 初始藏到屏幕外，位置由 ShowFor / SetWindowPos 负责
        Top = -2000;
        // 约半透明的深黑色回退底色：模糊图尚未就绪（或捕获失败）时仍然保证内容不可读
        Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x10, 0x10, 0x14));
        Foreground = Brushes.White;

        SourceInitialized += OnSourceInitialized;

        BuildContent();
    }

    /// <summary>
    /// 构建 UI（三层）：模糊背景图（UniformToFill 铺满）→ 半透明压暗层 → 居中提示文字。
    /// </summary>
    private void BuildContent()
    {
        // 模糊背景图层：管理器在显示前捕获屏幕快照并高斯模糊后填入；
        // 未填入时该层透明，露出窗口的半透明黑底
        _blurryImage = new Image
        {
            Stretch = System.Windows.Media.Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        // 压暗层：在模糊图上再叠一层约 35% 的黑，进一步确保不可读
        var dimOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x59, 0x00, 0x00, 0x00)),
        };

        // 文字容器：深色实底胶囊（黑底），保证白字在任何模糊背景上对比恒定可读
        var capsule = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x10, 0x10, 0x14)), // 约 85% 深黑
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(56, 40, 56, 40),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var icon = new TextBlock
        {
            Text = "🛡",
            FontSize = 64,
            FontFamily = new FontFamily("Segoe UI Emoji"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var mainTip = new TextBlock
        {
            Text = "隐私保护中，屏幕已暂时隐藏",
            FontSize = 42,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White, // 黑底白字
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 14),
        };

        var subTip = new TextBlock
        {
            Text = "主人回到镜头前，画面会自动恢复",
            FontSize = 18,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)), // 70% 白
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        panel.Children.Add(icon);
        panel.Children.Add(mainTip);
        panel.Children.Add(subTip);
        capsule.Child = panel;

        var root = new Grid();
        root.Children.Add(_blurryImage);
        root.Children.Add(dimOverlay);
        root.Children.Add(capsule);
        Content = root;
    }

    /// <summary>
    /// 在指定屏幕上显示遮罩：先以物理像素定位到该屏，再显示，确保位置在显示前就绪（切换 ≤200ms）。
    /// 窗口句柄复用：第二次起不再重建窗口，仅重新定位 + 显示。
    /// 必须在 WPF UI 线程调用。
    /// </summary>
    /// <param name="screen">目标屏幕。</param>
    /// <param name="blurryBackground">
    /// 该屏快照的高斯模糊结果（已冻结、可跨线程）；null 时仅显示半透明黑底。
    /// </param>
    public void ShowFor(Screen screen, ImageSource? blurryBackground = null)
    {
        _targetScreen = screen;
        Topmost = true; // 显示时再次置顶，防止被其他 Topmost 窗口压住

        // 设置模糊背景（已冻结的 BitmapSource 跨线程赋值安全；null = 纯黑回退）
        _blurryImage!.Source = blurryBackground;

        if (_hwnd == IntPtr.Zero)
        {
            // 首次显示：先用 DPI 缩放把 WPF 的 DIP 位置/尺寸设为近似值，
            // 句柄创建后 SourceInitialized 中再用 SetWindowPos 以物理像素精确覆盖
            var b = screen.Bounds; // 物理像素
            Left = b.X / _dpiScale;
            Top = b.Y / _dpiScale;
            Width = b.Width / _dpiScale;
            Height = b.Height / _dpiScale;
            Show();
        }
        else
        {
            // 已有句柄（此前 Hide 过）：窗口处于隐藏状态，先物理定位再显示，避免跨屏移动闪烁
            ApplyPhysicalBounds(screen);
            Show();
        }
    }

    /// <summary>隐藏遮罩（句柄保留，供快速复用；同时清空模糊快照，下次显示前重新捕获）。</summary>
    public void HideMask()
    {
        // 清空模糊背景：旧快照对应的是隐藏前的屏幕内容，复用会显示过时画面，
        // 且尽早释放引用让 GC 回收位图内存（隐私考虑：快照不留存）
        _blurryImage!.Source = null;
        Hide();
    }

    /// <summary>
    /// 句柄创建完成的初始化：
    /// 1) 设置点击穿透等扩展样式；2) 挂钩 WndProc 拒绝鼠标激活；3) 以物理像素定位到目标屏。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // 扩展样式：点击穿透 + 分层窗口 + 工具窗口（不进 Alt+Tab）+ 不可激活
        long exStyle = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        exStyle |= Win32.WS_EX_TRANSPARENT
                 | Win32.WS_EX_LAYERED
                 | Win32.WS_EX_TOOLWINDOW
                 | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(exStyle));

        // WndProc 钩子：鼠标点击尝试激活本窗口时返回 MA_NOACTIVATE（双保险防抢焦点）
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProcHook);

        // 物理像素定位到目标屏（PerMonitorV2 下 WPF 的 Left/Top/Width/Height 是 DIP，
        // 跨不同 DPI 的屏幕直接用它们定位会有偏差，因此用 SetWindowPos 物理像素覆盖）
        if (_targetScreen != null)
        {
            ApplyPhysicalBounds(_targetScreen);
        }
    }

    /// <summary>以物理像素把窗口精确覆盖到目标屏（同时保证 Topmost、不激活）。</summary>
    private void ApplyPhysicalBounds(Screen screen)
    {
        var b = screen.Bounds; // Screen.Bounds 为物理像素
        Win32.SetWindowPos(
            _hwnd,
            Win32.HWND_TOPMOST,
            b.X, b.Y, b.Width, b.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    /// <summary>WndProc 钩子：拦截 WM_MOUSEACTIVATE，返回 MA_NOACTIVATE 拒绝被点击激活。</summary>
    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Win32.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }
}
