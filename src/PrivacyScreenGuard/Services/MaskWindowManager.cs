using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenCvSharp;
using PrivacyScreenGuard.Models;
using PrivacyScreenGuard.Native;
using PrivacyScreenGuard.Windows;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 隐私遮罩窗口管理器：按显示器守护模式在目标屏幕上创建 / 显示 / 隐藏遮罩窗口。
/// 本类所有公共方法【必须在 WPF UI 线程调用】（窗口的创建、显示、关闭只能在 UI 线程执行）。
/// </summary>
public sealed class MaskWindowManager : IDisposable
{
    /// <summary>已创建的遮罩窗口缓存（通过 MaskWindow.MonitorName 与 Screen.DeviceName 匹配显示器）。</summary>
    private readonly List<MaskWindow> _windows = new();

    /// <summary>显示器守护范围模式（All / Primary / Selected）。</summary>
    private MonitorMode _mode;

    /// <summary>MonitorMode=Selected 时生效的目标屏索引集合（Index 与 ListDisplays 一致）。</summary>
    private readonly HashSet<int> _selected = new();

    /// <summary>遮罩主文案（用户可自定义；空白时 MaskWindow 内部回退默认）。</summary>
    private string _maskTitle = "隐私保护中，屏幕已暂时隐藏";

    /// <summary>遮罩副文案。</summary>
    private string _maskSubtitle = "主人回到镜头前，画面会自动恢复";

    /// <summary>构造线程的 Dispatcher；SystemEvents 事件从系统线程触发，需封送回 UI 线程。</summary>
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private bool _disposed;

    /// <summary>显示代际计数：ShowAll 每次自增；后台捕获任务完成时校验代际，
    /// 若期间发生过 HideAll/新一轮 ShowAll 则丢弃过期的显示请求，防止旧遮罩复活。</summary>
    private int _showGeneration;

    /// <summary>
    /// 构造管理器。必须在 WPF UI 线程调用（内部缓存 Dispatcher 供事件封送）。
    /// </summary>
    /// <param name="mode">显示器守护模式。</param>
    /// <param name="selectedMonitors">Selected 模式下的目标屏索引集合（可为 null 或空）。</param>
    public MaskWindowManager(MonitorMode mode, IReadOnlyList<int> selectedMonitors)
    {
        _mode = mode;
        ApplySelected(selectedMonitors);

        // 显示器拓扑 / DPI 变化时自动重建；事件在系统线程触发，回调中封送回 UI 线程
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>
    /// 更新配置。内部会清空窗口缓存，下次 ShowAll 按新配置重建；
    /// 若需要立即生效，请在调用本方法后紧接着调用 ShowAll()。
    /// </summary>
    public void UpdateConfig(MonitorMode mode, IReadOnlyList<int> selectedMonitors)
    {
        _mode = mode;
        ApplySelected(selectedMonitors);
        RebuildAll();
    }

    /// <summary>
    /// 枚举全部显示器。Index = Screen.AllScreens 数组顺序；PhysicalBounds 为物理像素。
    /// </summary>
    public static List<(int Index, string DeviceName, Rectangle PhysicalBounds)> ListDisplays()
    {
        var result = new List<(int Index, string DeviceName, Rectangle PhysicalBounds)>();
        Screen[] screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            result.Add((i, screens[i].DeviceName, screens[i].Bounds));
        }
        return result;
    }

    /// <summary>
    /// 在所有目标屏上显示遮罩。必须在 WPF UI 线程调用。
    /// 目标屏集合：All = 全部显示器；Primary = 主屏；Selected = _selected 中合法的索引。
    /// </summary>
    /// <param name="blurStrength">
    /// 模糊强度（0–100）：&gt;0 时先在后台捕获该屏快照并高斯模糊，完成后显示模糊遮罩；
    /// 0 或捕获失败时直接显示半透明纯黑遮罩。捕获期间遮罩尚未显示，因此不会拍到遮罩自身。
    /// </param>
    public void ShowAll(int blurStrength = 0)
    {
        ThrowIfDisposed();
        int generation = ++_showGeneration;
        Screen[] screens = Screen.AllScreens;
        HashSet<int> targets = GetTargetIndices(screens);

        // 移除不再属于目标集的缓存窗口（配置变化 / 显示器减少）
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            int idx = FindScreenIndex(_windows[i].MonitorName, screens);
            if (idx < 0 || !targets.Contains(idx))
            {
                _windows[i].Close();
                _windows.RemoveAt(i);
            }
        }

        // 确保每个目标屏都有遮罩窗口并显示（优先复用缓存）
        foreach (int idx in targets)
        {
            Screen screen = screens[idx];
            MaskWindow? win = _windows.FirstOrDefault(w =>
                string.Equals(w.MonitorName, screen.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (win == null)
            {
                win = new MaskWindow(screen.DeviceName, GetDpiScale(screen));
                win.UpdateTexts(_maskTitle, _maskSubtitle); // 新建窗口立即应用当前自定义文案
                _windows.Add(win);
            }

            if (blurStrength <= 0)
            {
                // 不启用模糊：立即显示纯黑遮罩（最快路径）
                win.ShowFor(screen);
                continue;
            }

            // 启用模糊：先在后台线程捕获 + 高斯模糊（此时遮罩尚未显示，拍到的是干净画面），
            // 完成后切回 UI 线程显示；捕获失败则回退为纯黑遮罩。代际校验防止过期任务复活遮罩。
            var window = win;
            Task.Run(() =>
            {
                BitmapSource? blurred = null;
                try
                {
                    blurred = CaptureBlurredScreen(screen, blurStrength);
                }
                catch
                {
                    // 捕获失败（锁屏 / 安全桌面 / DRM 保护内容等）→ blurred 保持 null，回退纯黑
                }
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    // 期间若发生过 HideAll 或新一轮 ShowAll，则丢弃本次过期显示
                    if (_disposed || generation != _showGeneration)
                    {
                        return;
                    }
                    window.ShowFor(screen, blurred);
                }));
            });
        }
    }

    /// <summary>隐藏全部遮罩（窗口保留缓存以便快速复用）。必须在 WPF UI 线程调用。</summary>
    public void HideAll()
    {
        ThrowIfDisposed();
        _showGeneration++; // 使所有在途的模糊捕获任务过期，防止其在隐藏后把遮罩重新显示出来
        foreach (MaskWindow w in _windows)
        {
            w.HideMask();
        }
    }

    /// <summary>
    /// 显示器拓扑 / DPI 变化时的处理：先 HideAll 再清空缓存，
    /// 下次 ShowAll 按新的显示器布局重建（旧的物理坐标已失效，不能复用）。
    /// </summary>
    public void HandleDisplayChanged()
    {
        RebuildAll();
    }

    /// <summary>
    /// 热更新遮罩文案：保存最新值并对全部已创建的遮罩窗口立即生效。
    /// 必须在 WPF UI 线程调用（与其他遮罩公共方法一致）。
    /// </summary>
    public void UpdateMaskTexts(string title, string subtitle)
    {
        ThrowIfDisposed();
        _maskTitle = title;
        _maskSubtitle = subtitle;
        foreach (MaskWindow w in _windows)
        {
            w.UpdateTexts(_maskTitle, _maskSubtitle);
        }
    }

    /// <summary>
    /// 释放资源：退订系统事件并关闭全部缓存窗口。请在 WPF UI 线程调用。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        foreach (MaskWindow w in _windows)
        {
            w.Close();
        }
        _windows.Clear();
    }

    // ==================== 私有方法 ====================

    /// <summary>
    /// 捕获指定屏幕的当前画面并做高斯模糊，返回已冻结、可跨线程显示的位图。
    /// 在后台线程调用（GDI 截屏 + OpenCV 模糊，约几十毫秒，不阻塞 UI）。
    /// 隐私说明：快照仅在内存中短暂存在，模糊后不可读，不做任何落盘。
    /// </summary>
    /// <param name="screen">目标屏幕（Bounds 为物理像素）。</param>
    /// <param name="blurStrength">模糊强度 1–100（映射为高斯 sigma，值越大越模糊）。</param>
    private static BitmapSource CaptureBlurredScreen(Screen screen, int blurStrength)
    {
        var b = screen.Bounds; // 物理像素（PerMonitorV2 进程内 GDI 坐标即物理像素）

        // 1. GDI 截屏：CopyFromScreen 按虚拟桌面物理坐标逐屏捕获
        using var captured = new Bitmap(b.Width, b.Height);
        using (var g = Graphics.FromImage(captured))
        {
            g.CopyFromScreen(b.X, b.Y, 0, 0, new System.Drawing.Size(b.Width, b.Height));
        }

        // 2. Bitmap → Mat：LockBits 拿到 BGRA 像素，包装为 8UC4 Mat（外部内存，不复制）
        var boundsRect = new Rectangle(0, 0, b.Width, b.Height);
        var bmpData = captured.LockBits(boundsRect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // 外部指针包装：FromPixelData 不会释放 Scan0 指向的内存（由 captured 负责）
            using var src = Mat.FromPixelData(b.Height, b.Width, MatType.CV_8UC4, bmpData.Scan0, bmpData.Stride);

            // 3. BGRA → BGR，再做高斯模糊：sigma 随强度线性放大（50 ≈ 20，足以完全不可读）
            using var bgr = new Mat();
            Cv2.CvtColor(src, bgr, ColorConversionCodes.BGRA2BGR);
            using var blurred = new Mat();
            double sigma = Math.Max(2.0, blurStrength * 0.4);
            Cv2.GaussianBlur(bgr, blurred, new OpenCvSharp.Size(0, 0), sigma);

            // 4. 模糊结果 → 冻结的 BitmapSource（Freeze 后可安全跨线程交给 UI 显示）
            int stride = (int)blurred.Step();
            var pixels = new byte[stride * blurred.Rows];
            Marshal.Copy(blurred.Data, pixels, 0, pixels.Length);
            var result = BitmapSource.Create(blurred.Cols, blurred.Rows, 96, 96,
                System.Windows.Media.PixelFormats.Bgr24, null, pixels, stride);
            result.Freeze();
            return result;
        }
        finally
        {
            captured.UnlockBits(bmpData);
        }
    }

    /// <summary>SystemEvents 回调在系统线程触发，操作 WPF 窗口必须封送回 UI 线程。</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(new Action(HandleDisplayChanged));
    }

    /// <summary>把传入的选中屏索引写入 _selected 集合（去重）。</summary>
    private void ApplySelected(IReadOnlyList<int>? selectedMonitors)
    {
        _selected.Clear();
        if (selectedMonitors == null)
        {
            return;
        }
        foreach (int i in selectedMonitors)
        {
            _selected.Add(i);
        }
    }

    /// <summary>按当前模式计算目标屏索引集合（Index = Screen.AllScreens 数组顺序）。</summary>
    private HashSet<int> GetTargetIndices(Screen[] screens)
    {
        var targets = new HashSet<int>();
        switch (_mode)
        {
            case MonitorMode.All:
                for (int i = 0; i < screens.Length; i++)
                {
                    targets.Add(i);
                }
                break;

            case MonitorMode.Primary:
                // 主屏通常为 AllScreens[0]；但 Windows 枚举顺序不保证主屏排第一，
                // 故按 Screen.PrimaryScreen 的实际索引取值，异常时回退到 0
                Screen? primaryScreen = Screen.PrimaryScreen;
                int primary = primaryScreen != null ? Array.IndexOf(screens, primaryScreen) : -1;
                targets.Add(primary >= 0 ? primary : 0);
                break;

            case MonitorMode.Selected:
                foreach (int i in _selected)
                {
                    if (i >= 0 && i < screens.Length)
                    {
                        targets.Add(i);
                    }
                }
                break;
        }
        return targets;
    }

    /// <summary>按设备名在屏幕数组中查找索引，找不到返回 -1。</summary>
    private static int FindScreenIndex(string deviceName, Screen[] screens)
    {
        for (int i = 0; i < screens.Length; i++)
        {
            if (string.Equals(screens[i].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>读取指定屏幕的 DPI 缩放（PerMonitorV2 感知下返回该屏真实缩放，96 DPI = 1.0）。</summary>
    private static double GetDpiScale(Screen screen)
    {
        // WinForms Screen 没有 Handle 属性，用屏幕中心点取 HMONITOR
        var center = new Win32.POINT
        {
            X = screen.Bounds.X + screen.Bounds.Width / 2,
            Y = screen.Bounds.Y + screen.Bounds.Height / 2
        };
        IntPtr hMonitor = Win32.MonitorFromPoint(center, Win32.MONITOR_DEFAULTTONEAREST);
        Win32.GetDpiForMonitor(hMonitor, Win32.MDT_EFFECTIVE_DPI, out uint dpiX, out uint _);
        return dpiX > 0 ? dpiX / 96.0 : 1.0;
    }

    /// <summary>隐藏并关闭全部缓存窗口，下次 ShowAll 重建。</summary>
    private void RebuildAll()
    {
        HideAll();
        foreach (MaskWindow w in _windows)
        {
            w.Close();
        }
        _windows.Clear();
    }

    /// <summary>Dispose 之后调用公共方法则抛出 ObjectDisposedException。</summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MaskWindowManager));
        }
    }
}
