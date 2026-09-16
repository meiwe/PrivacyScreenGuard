using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 全局热键服务：基于 Win32 RegisterHotKey / UnregisterHotKey 实现。
/// 使用方式：
/// 1) App 在窗口 <see cref="System.Windows.Interop.HwndSource"/> 上挂消息 hook，
///    收到 <see cref="WM_HOTKEY"/> 消息时调用 <see cref="RaiseFromWndProc"/> 转发热键 ID；
/// 2) 本服务通过静态事件 <see cref="HotkeyPressed"/> 把热键 ID 广播给订阅者（通常为 App）。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    /// <summary>ALT 修饰键（Win32 MOD_ALT）。</summary>
    public const uint MOD_ALT = 0x1;

    /// <summary>CTRL 修饰键（Win32 MOD_CONTROL）。</summary>
    public const uint MOD_CONTROL = 0x2;

    /// <summary>SHIFT 修饰键（Win32 MOD_SHIFT）。</summary>
    public const uint MOD_SHIFT = 0x4;

    /// <summary>不重复触发：按住不放时只触发一次（Win32 MOD_NOREPEAT）。</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    /// <summary>全局热键消息（Win32 WM_HOTKEY）。</summary>
    public const int WM_HOTKEY = 0x0312;

    /// <summary>热键触发事件：参数为注册时使用的 ID（如"暂停/恢复"热键的 ID=1）。</summary>
    public static event Action<uint>? HotkeyPressed;

    /// <summary>已成功注册的 (hwnd, id) 记录，Dispose 时统一注销。</summary>
    private readonly List<(IntPtr Hwnd, uint Id)> _registered = new();

    private bool _disposed;

    /// <summary>
    /// 解析热键文本："Ctrl+Alt" + "P" → MOD_CONTROL | MOD_ALT 与虚拟键码 0x50。
    /// 主键规则：A-Z → VK 码即 ASCII 大写码（'P'→0x50）；0-9 → 0x30+数字；F1-F12 → 0x70+序号-1。
    /// </summary>
    /// <returns>解析成功返回 true；修饰键或主键非法返回 false。</returns>
    public static bool TryParse(string modifiersText, string keyText, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(modifiersText) || string.IsNullOrWhiteSpace(keyText))
        {
            return false;
        }

        // 修饰键：按 + 拆分、逐段匹配（大小写不敏感，支持 Ctrl/Control 两种写法）
        foreach (string part in modifiersText.Split('+',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    mods |= MOD_CONTROL;
                    break;
                case "ALT":
                    mods |= MOD_ALT;
                    break;
                case "SHIFT":
                    mods |= MOD_SHIFT;
                    break;
                default:
                    return false; // 出现未知修饰键 → 整体解析失败
            }
        }

        // 主键：单字符（字母/数字）或 F1-F12
        string key = keyText.Trim().ToUpperInvariant();
        if (key.Length == 1)
        {
            char c = key[0];
            if (c is >= 'A' and <= 'Z')
            {
                vk = c; // 字母 VK 码 = ASCII 大写码：'P' → 0x50
                return true;
            }
            if (c is >= '0' and <= '9')
            {
                vk = (uint)(0x30 + (c - '0')); // 数字 VK 码：0x30-0x39
                return true;
            }
            return false;
        }

        // F1-F12：F 开头 + 1~12
        if (key.Length >= 2 && key[0] == 'F'
            && int.TryParse(key.AsSpan(1), out int fn) && fn is >= 1 and <= 12)
        {
            vk = (uint)(0x70 + fn - 1); // VK_F1=0x70 ... VK_F12=0x7B
            return true;
        }
        return false;
    }

    /// <summary>
    /// 注册全局热键。同一 (hwnd, id) 已注册时先注销旧注册再重新注册；
    /// 注册失败（常见原因：组合键已被其他程序占用）返回 false。
    /// </summary>
    public bool Register(IntPtr hwnd, uint id, uint mods, uint vk)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HotkeyService));
        }
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        Unregister(hwnd, id); // 同 id 已注册则先注销
        if (!RegisterHotKey(hwnd, id, mods | MOD_NOREPEAT, vk))
        {
            return false;
        }
        _registered.Add((hwnd, id));
        return true;
    }

    /// <summary>注销指定 (hwnd, id) 的热键；未注册过则无事发生。</summary>
    public void Unregister(IntPtr hwnd, uint id)
    {
        for (int i = _registered.Count - 1; i >= 0; i--)
        {
            if (_registered[i].Hwnd == hwnd && _registered[i].Id == id)
            {
                _registered.RemoveAt(i);
                UnregisterHotKey(hwnd, id);
            }
        }
    }

    /// <summary>
    /// 供窗口消息 hook 调用：收到 WM_HOTKEY 时把消息 wParam（热键 ID）转发进来，
    /// 触发 <see cref="HotkeyPressed"/> 静态事件。
    /// </summary>
    public static void RaiseFromWndProc(uint id)
    {
        HotkeyPressed?.Invoke(id);
    }

    /// <summary>注销本服务注册的全部热键。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach ((IntPtr hwnd, uint id) in _registered)
        {
            UnregisterHotKey(hwnd, id);
        }
        _registered.Clear();
    }

    // ==================== Win32 API ====================

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, uint id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, uint id);
}
