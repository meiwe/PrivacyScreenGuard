using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Windows.Forms;
// WinForms 与 WPF 同名类型冲突，横幅 UI 全部使用 WPF 版本
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using PrivacyScreenGuard.Native;

namespace PrivacyScreenGuard.Windows;

/// <summary>
/// 应用自绘的健康提醒轻量横幅：主屏右下角滑入（图标 + 标题 + 文案），停留约 4 秒后滑出淡出。
/// 特性：
/// - 不依赖 Windows 系统通知设置（托盘气泡被关时本横幅仍然可见）
/// - 点击穿透 + 不抢焦点（WS_EX_TRANSPARENT / NOACTIVATE），完全不打断用户操作
/// - 连续提醒到来时原地刷新内容并重置停留计时（最新提醒优先）
/// 界面由纯 C# 构建（不使用 XAML），样式与主窗口的奢华极简风一致（深色胶囊 + 白字）。
/// </summary>
public sealed class HealthToastWindow : Window
{
    /// <summary>横幅停留时长（毫秒）。</summary>
    private const int StayMs = 3800;

    /// <summary>入场/出场滑动的水平位移（DIP）。</summary>
    private const double SlideOffset = 48;

    private readonly TranslateTransform _slide = new();
    private readonly TextBlock _iconText = new();
    private readonly TextBlock _titleText = new();
    private readonly TextBlock _messageText = new();
    private readonly DispatcherTimer _hideTimer;

    private IntPtr _hwnd;
    private bool _shownOnce;
    private bool _hiding;

    public HealthToastWindow()
    {
        Title = "健康提醒";
        WindowStyle = WindowStyle.None;             // 无边框
        AllowsTransparency = true;                  // 胶囊外透明
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;                             // 始终置顶（与遮罩同级，后显示者在上）
        ShowInTaskbar = false;                      // 不进任务栏
        ShowActivated = false;                      // 显示不激活
        Focusable = false;                          // 不接受键盘焦点
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight; // 尺寸随内容自适应
        Background = Brushes.Transparent;
        Left = -3000;                               // 初始藏到屏幕外
        Top = -3000;

        // 停留计时器：到点播放出场动画
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StayMs) };
        _hideTimer.Tick += (_, _) => HideAnimated();

        BuildContent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // 扩展样式：点击穿透 + 分层 + 工具窗口（不进 Alt+Tab）+ 不可激活
            long exStyle = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
            exStyle |= Win32.WS_EX_TRANSPARENT
                     | Win32.WS_EX_LAYERED
                     | Win32.WS_EX_TOOLWINDOW
                     | Win32.WS_EX_NOACTIVATE;
            Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(exStyle));
        };
    }

    /// <summary>构建横幅 UI：深色实底胶囊（黑底白字），图标 + 标题 + 正文三段水平排列。</summary>
    private void BuildContent()
    {
        _iconText.FontSize = 22;
        _iconText.VerticalAlignment = VerticalAlignment.Center;
        _iconText.FontFamily = new FontFamily("Segoe UI Emoji");

        _titleText.FontSize = 13;
        _titleText.FontWeight = FontWeights.SemiBold;
        _titleText.Foreground = Brushes.White;

        _messageText.FontSize = 12;
        _messageText.Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)); // 70% 白
        _messageText.TextWrapping = TextWrapping.Wrap;
        _messageText.MaxWidth = 300;
        _messageText.Margin = new Thickness(0, 3, 0, 0);

        var textStack = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        textStack.Children.Add(_titleText);
        textStack.Children.Add(_messageText);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(_iconText);
        row.Children.Add(textStack);

        var capsule = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x14, 0x18)), // 约 90% 深黑
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 13, 18, 13),
            Child = row,
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.18,
                Color = Color.FromRgb(0x00, 0x00, 0x00),
            },
        };

        var root = new Grid { RenderTransform = _slide };
        root.Children.Add(capsule);
        Content = root;
    }

    /// <summary>
    /// 显示一条健康提醒横幅（必须在 WPF UI 线程调用）。
    /// 连续调用时原地刷新内容并重置停留计时。
    /// </summary>
    /// <param name="icon">类型图标（emoji）。</param>
    /// <param name="title">标题（如「久坐提醒」）。</param>
    /// <param name="message">提醒文案。</param>
    public void ShowNotice(string icon, string title, string message)
    {
        _iconText.Text = icon;
        _titleText.Text = title;
        _messageText.Text = message;

        if (!_shownOnce)
        {
            // 首次：以透明态显示（窗口句柄创建 + 尺寸测量）
            Opacity = 0;
            Show();
            _shownOnce = true;
        }

        // 关键：定位与动画延迟到"布局已完成"之后执行。
        // SizeToContent 窗口 Show() 返回时 ActualWidth/ActualHeight 可能仍为 0，
        // 立即定位会把窗口算到主屏右缘之外（Width=0 时 Left=右缘-24，展开后窗口几乎全在屏外），
        // 表现为"横幅不显示"。Loaded 优先级保证在当次布局 pass 完成后执行。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (IsLoaded && !_hiding)
            {
                PositionAtCorner();
                PlayEnter();
                RestartHideTimer();
            }
        });
    }

    /// <summary>把横幅定位到主屏工作区（任务栏上方）右下角，边距 24 DIP；尺寸异常时夹回屏幕内。</summary>
    private void PositionAtCorner()
    {
        var wa = SystemParameters.WorkArea; // 主屏工作区（DIP，已扣除任务栏）
        double width = ActualWidth > 0 ? ActualWidth : 360;  // 布局未就绪时的兜底宽度
        double height = ActualHeight > 0 ? ActualHeight : 80;
        Left = Math.Max(8, wa.Right - width - 24);
        Top = Math.Max(8, wa.Bottom - height - 24);
    }

    /// <summary>入场：右侧 48DIP 滑入 + 淡入（0.3s 缓出）。</summary>
    private void PlayEnter()
    {
        _hiding = false;
        _slide.X = SlideOffset;
        Opacity = 0;

        var slideIn = new DoubleAnimation(SlideOffset, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260));

        _slide.BeginAnimation(TranslateTransform.XProperty, slideIn, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(OpacityProperty, fadeIn, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>出场：淡出 + 右滑（0.28s），完成后隐藏窗口并复位变换。</summary>
    private void HideAnimated()
    {
        if (_hiding)
        {
            return;
        }
        _hiding = true;
        _hideTimer.Stop();

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var slideOut = new DoubleAnimation(0, SlideOffset, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        fadeOut.Completed += (_, _) =>
        {
            Hide();
            _slide.X = SlideOffset;
            Opacity = 0;
            _hiding = false;
        };
        _slide.BeginAnimation(TranslateTransform.XProperty, slideOut, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>重置停留计时：到点播放出场动画。</summary>
    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }
}
