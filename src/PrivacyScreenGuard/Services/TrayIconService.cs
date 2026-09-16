using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PrivacyScreenGuard.Services;

/// <summary>托盘状态（决定图标右下角圆点颜色与提示文字）。</summary>
public enum TrayStatus
{
    /// <summary>守护运行中（绿色圆点）。</summary>
    Running,

    /// <summary>已暂停（黄色圆点）。</summary>
    Paused,

    /// <summary>摄像头异常（红色圆点）。</summary>
    Error
}

/// <summary>
/// 系统托盘图标服务：NotifyIcon + 右键菜单。
/// 图标为代码绘制：深蓝底圆角方块 + 白色盾牌（内含对勾）+ 右下角状态圆点（运行绿/暂停黄/异常红）。
/// 本类公共方法须在创建它的线程（WPF UI 线程）上调用——App 在 OnStartup 中创建本对象。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    /// <summary>右键菜单（Dispose 时一并释放，菜单项随菜单释放）。</summary>
    private readonly ContextMenuStrip _menu;

    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _exitItem;

    /// <summary>当前图标对象（Icon.FromHandle 包装，切换时先释放旧的）。</summary>
    private Icon? _currentIcon;

    /// <summary>当前图标的 HICON 句柄（GetHicon 产生，必须用 DestroyIcon 释放防泄漏）。</summary>
    private IntPtr _currentIconHandle;

    private bool _disposed;

    /// <summary>用户请求显示主窗口（在创建线程即 UI 线程触发）。</summary>
    public event Action? ShowMainWindowRequested;

    /// <summary>用户请求暂停/恢复守护（在 UI 线程触发）。</summary>
    public event Action? TogglePauseResumeRequested;

    /// <summary>用户请求退出应用（在 UI 线程触发）。</summary>
    public event Action? ExitRequested;

    public TrayIconService()
    {
        // 右键菜单：显示主窗口 / 暂停·恢复（文案随状态切换）/ 退出
        _showItem = new ToolStripMenuItem("显示主窗口");
        _toggleItem = new ToolStripMenuItem("暂停守护");
        _exitItem = new ToolStripMenuItem("退出");
        _menu = new ContextMenuStrip();
        _menu.Items.Add(_showItem);
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "隐私屏",
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindowRequested?.Invoke();
        _showItem.Click += (_, _) => ShowMainWindowRequested?.Invoke();
        _toggleItem.Click += (_, _) => TogglePauseResumeRequested?.Invoke();
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();

        SetStatus(TrayStatus.Running);
        _notifyIcon.Visible = true; // 先设好图标再显示，避免托盘短暂空白
    }

    /// <summary>
    /// 更新托盘状态：重绘图标（状态圆点颜色）、更新提示文字，
    /// 并切换暂停/恢复菜单文案（异常态下禁用暂停/恢复菜单项）。
    /// </summary>
    public void SetStatus(TrayStatus status)
    {
        ThrowIfDisposed();

        ApplyIcon(status);
        _notifyIcon.Text = status switch
        {
            TrayStatus.Running => "隐私屏：守护中",
            TrayStatus.Paused => "隐私屏：已暂停",
            _ => "隐私屏：摄像头异常"
        };
        _toggleItem.Text = status == TrayStatus.Running ? "暂停守护" : "恢复守护";
        _toggleItem.Enabled = status != TrayStatus.Error; // 摄像头异常时暂停/恢复无意义
    }

    /// <summary>弹出托盘气泡提示。</summary>
    public void ShowBubble(string title, string message, ToolTipIcon icon)
    {
        ThrowIfDisposed();

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(3000);
    }

    /// <summary>
    /// 释放资源：隐藏并释放托盘图标、菜单与图标句柄。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();

        _currentIcon?.Dispose();
        if (_currentIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_currentIconHandle);
            _currentIconHandle = IntPtr.Zero;
        }
    }

    // ==================== 图标绘制 ====================

    /// <summary>按状态绘制新图标并替换（先换新再释放旧句柄，避免托盘短暂空图标）。</summary>
    private void ApplyIcon(TrayStatus status)
    {
        // 按系统托盘图标尺寸选择绘制尺寸（通常 16×16，高 DPI 下可能为 32×32）
        int size = SystemInformation.SmallIconSize.Width >= 32 ? 32 : 16;
        using Bitmap bitmap = DrawIconBitmap(status, size);
        IntPtr handle = bitmap.GetHicon();
        Icon icon = Icon.FromHandle(handle);

        Icon? oldIcon = _currentIcon;
        IntPtr oldHandle = _currentIconHandle;
        _currentIcon = icon;
        _currentIconHandle = handle;
        _notifyIcon.Icon = icon; // 先替换，再释放旧句柄

        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero)
        {
            DestroyIcon(oldHandle);
        }
    }

    /// <summary>
    /// 绘制托盘图标位图：应用图标底图（SVG 渲染的盾牌 + 镜头 + 守护斜杠）+ 右下角状态圆点（白描边）。
    /// </summary>
    private static Bitmap DrawIconBitmap(TrayStatus status, int size)
    {
        var bitmap = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        float s = size;
        Color backColor = Color.FromArgb(30, 58, 95); // 深蓝

        // 1. 应用图标底图（与 exe/快捷方式图标同源，保证视觉统一）
        using (Bitmap? baseIcon = LoadBaseIcon())
        {
            if (baseIcon is not null)
            {
                g.DrawImage(baseIcon, 0, 0, size, size);
            }
            else
            {
                // 兜底：资源缺失时仅画深蓝圆角底，保证托盘不出现空白图标
                using GraphicsPath bgPath = CreateRoundedRectPath(new RectangleF(0, 0, s, s), s * 0.22f);
                using var bgBrush = new SolidBrush(backColor);
                g.FillPath(bgBrush, bgPath);
            }
        }

        // 2. 右下角状态圆点：绿=运行，黄=暂停，红=异常
        Color dotColor = status switch
        {
            TrayStatus.Running => Color.FromArgb(46, 204, 64),
            TrayStatus.Paused => Color.FromArgb(241, 196, 15),
            _ => Color.FromArgb(231, 76, 60),
        };
        float dotRadius = s * 0.17f;
        var dotRect = new RectangleF(
            s - dotRadius * 2 - s * 0.06f,
            s - dotRadius * 2 - s * 0.06f,
            dotRadius * 2,
            dotRadius * 2);
        using (var ringPen = new Pen(Color.White, s * 0.05f))
        {
            g.DrawEllipse(ringPen, dotRect);
        }
        using (var dotBrush = new SolidBrush(dotColor))
        {
            g.FillEllipse(dotBrush, dotRect);
        }

        return bitmap;
    }

    /// <summary>
    /// 加载应用图标底图（由 tools/build_icon.py 从 assets/icon.svg 渲染，作为程序集资源打包）。
    /// 用 256px 原图缩放，保证高 DPI 托盘也清晰；加载失败返回 null，由调用方兜底绘制。
    /// </summary>
    private static Bitmap? LoadBaseIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/icon_256.png");
            Stream? stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is null)
            {
                return null;
            }
            using (stream)
            {
                return new Bitmap(stream);
            }
        }
        catch
        {
            // 资源缺失或解码失败：不抛出，由调用方绘制兜底底色
            return null;
        }
    }

    /// <summary>创建圆角矩形路径（四角用 90° 圆弧拼接）。</summary>
    private static GraphicsPath CreateRoundedRectPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TrayIconService));
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
