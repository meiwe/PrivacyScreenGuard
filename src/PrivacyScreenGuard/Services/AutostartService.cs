using Microsoft.Win32;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 开机自启动服务：通过当前用户注册表 Run 键（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）
/// 写入/删除启动项实现，值内容为带引号的可执行文件全路径。
/// </summary>
public static class AutostartService
{
    /// <summary>注册表 Run 键路径（HKCU 下）。</summary>
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>注册表值名（即本应用标识）。</summary>
    private const string AppName = "PrivacyScreenGuard";

    /// <summary>
    /// 是否已开启开机自启：Run 键下存在本应用的值即视为开启；
    /// 读取异常（权限等）按未开启处理，不抛出。
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppName) is not null;
        }
        catch
        {
            // 注册表读取失败按未开启处理，不影响主流程
            return false;
        }
    }

    /// <summary>
    /// 开启/关闭开机自启：开启时写入带引号的可执行文件全路径；关闭时删除该值。
    /// 任何异常静默捕获，不向上抛出。
    /// </summary>
    public static void SetEnabled(bool on)
    {
        try
        {
            if (on)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key?.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表写入失败（如被安全软件拦截）不影响主流程
        }
    }
}
