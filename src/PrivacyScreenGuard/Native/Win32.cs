using System;
using System.Runtime.InteropServices;

namespace PrivacyScreenGuard.Native;

/// <summary>
/// Win32 原生 API 的最小封装，仅服务于隐私遮罩窗口的置顶、点击穿透与物理像素定位。
/// </summary>
public static class Win32
{
    // ==================== 常量 ====================

    /// <summary>SetWindowPos 的插入位置参数：置于最顶层（Topmost 组）。</summary>
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    /// <summary>SetWindowPos 标志：不激活窗口（显示/移动时不抢焦点）。</summary>
    public const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>SetWindowPos 标志：显示窗口。</summary>
    public const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>SetWindowPos 标志：不改变所有者 Z 序。</summary>
    public const uint SWP_NOOWNERZORDER = 0x0200;

    /// <summary>扩展样式：鼠标消息穿透，点击直接落到下层窗口（遮罩点击穿透的核心）。</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>扩展样式：分层窗口（支持半透明渲染）。</summary>
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary>扩展样式：工具窗口，不出现在任务栏与 Alt+Tab 列表。</summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>扩展样式：窗口不可被激活（点击不抢焦点）。</summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Get/SetWindowLongPtr 的索引参数：读/写窗口扩展样式。</summary>
    public const int GWL_EXSTYLE = -20;

    /// <summary>窗口消息：鼠标即将激活窗口；返回 MA_NOACTIVATE 可拒绝激活。</summary>
    public const int WM_MOUSEACTIVATE = 0x0021;

    /// <summary>WM_MOUSEACTIVATE 的返回值：不激活窗口，仅把鼠标消息交给它。</summary>
    public const int MA_NOACTIVATE = 3;

    /// <summary>MonitorFromPoint 的标志：取距离该点最近的显示器。</summary>
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>GetDpiForMonitor 的类型参数：读取显示器当前生效 DPI（含系统缩放设置）。</summary>
    public const int MDT_EFFECTIVE_DPI = 0;

    // ==================== 结构体 ====================

    /// <summary>
    /// 屏幕坐标点。内存布局为两个连续的 int（X、Y），
    /// 与 Win32 POINT 结构体"按 2 个 int 传参"完全等价。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        /// <summary>X 坐标。</summary>
        public int X;

        /// <summary>Y 坐标。</summary>
        public int Y;

        /// <summary>构造一个坐标点。</summary>
        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    // ==================== 函数 ====================

    // 重要说明：GetWindowLongPtrW / SetWindowLongPtrW 这两个导出只存在于 64 位的
    // user32.dll（32 位系统上只有 GetWindowLongW / SetWindowLongW）。
    // 本项目强制以 win-x64 / AnyCPU 的 64 位进程运行，因此直接声明 Ptr 版本即可；
    // 若将来需要支持 32 位进程，必须在此处按进程位数增加 GetWindowLongW/SetWindowLongW 回退。

    /// <summary>读取窗口长整型属性（扩展样式等）。64 位进程专用导出 GetWindowLongPtrW。</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    /// <summary>写入窗口长整型属性（扩展样式等）。64 位进程专用导出 SetWindowLongPtrW。</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>读取窗口扩展样式等长整型属性（64 位进程）。</summary>
    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return GetWindowLongPtr64(hWnd, nIndex);
    }

    /// <summary>写入窗口扩展样式等长整型属性（64 位进程）。</summary>
    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
    }

    /// <summary>
    /// 设置窗口的位置、尺寸与 Z 序。
    /// 本项目用它以【物理像素】直接把遮罩窗口定位到目标显示器，
    /// 绕开 WPF 在 PerMonitorV2 下 DIP 换算可能产生的偏差。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>根据屏幕坐标点获取其所在（或最近）显示器的 HMONITOR。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>
    /// 获取指定显示器的 DPI（shcore.dll，Windows 8.1+）。
    /// 在 app.manifest 已声明 PerMonitorV2 感知的前提下，返回该显示器的真实 DPI；
    /// 返回值为 HRESULT（0 = S_OK）。
    /// </summary>
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
