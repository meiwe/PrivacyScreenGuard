using System;
using System.IO;
using System.Text.Json;
using PrivacyScreenGuard.Models;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 设置持久化服务：读写 %LOCALAPPDATA%\PrivacyScreenGuard\settings.json。
/// 所有 IO / JSON 异常均在内部消化、绝不向上抛出，调用方无需处理失败分支。
/// </summary>
public static class SettingsService
{
    /// <summary>设置文件名。</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// 获取设置目录（%LOCALAPPDATA%\PrivacyScreenGuard），不存在则自动创建。
    /// </summary>
    public static string GetSettingsDir()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrivacyScreenGuard");
        Directory.CreateDirectory(dir); // 目录已存在时不会抛异常
        return dir;
    }

    /// <summary>获取设置文件完整路径。</summary>
    public static string GetSettingsPath()
    {
        return Path.Combine(GetSettingsDir(), SettingsFileName);
    }

    /// <summary>
    /// 加载设置：文件不存在返回默认值；文件损坏、JSON 非法、字段类型错误等
    /// 任何异常均回退为默认值，绝不抛出；返回前一律调用 Sanitize() 夹取非法数值。
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(GetSettingsPath()))
            {
                return new AppSettings();
            }

            // 手改 JSON 或字段类型不匹配时反序列化可能抛异常，统一按默认值兜底
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(GetSettingsPath()));
            if (loaded is null)
            {
                return new AppSettings();
            }

            loaded.Sanitize();
            return loaded;
        }
        catch
        {
            // 任何异常（文件损坏、JSON 非法、字段类型错等）都回退为夹取后的默认值
            var defaults = new AppSettings();
            defaults.Sanitize();
            return defaults;
        }
    }

    /// <summary>
    /// 保存设置：先 Sanitize 夹取，再以缩进 JSON 写入；写入失败静默忽略，不抛异常。
    /// </summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            settings.Sanitize();
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(GetSettingsPath(), JsonSerializer.Serialize(settings, options));
        }
        catch
        {
            // 静默失败：设置落盘失败不应影响主流程
        }
    }
}
