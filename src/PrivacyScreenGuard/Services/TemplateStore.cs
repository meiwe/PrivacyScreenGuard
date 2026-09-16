using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 主人人脸特征模板的加密存储：使用 DPAPI（CurrentUser）+ 固定附加熵加密，
/// 落盘为 %LOCALAPPDATA%\PrivacyScreenGuard\owner.bin，仅当前 Windows 用户可解密。
/// </summary>
public static class TemplateStore
{
    /// <summary>模板文件名。</summary>
    public const string OwnerTemplateFileName = "owner.bin";

    /// <summary>特征向量维度（SFace 模型输出 128 维）。</summary>
    public const int FeatureLength = 128;

    /// <summary>DPAPI 加密的固定熵（附加熵，密文与该熵绑定，防止密文被整体替换）。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PrivacyScreenGuard.OwnerTemplate.v1");

    /// <summary>获取模板文件完整路径（与设置文件同目录）。</summary>
    public static string GetTemplatePath()
    {
        return Path.Combine(SettingsService.GetSettingsDir(), OwnerTemplateFileName);
    }

    /// <summary>判断是否已保存主人模板。</summary>
    public static bool Exists()
    {
        return File.Exists(GetTemplatePath());
    }

    /// <summary>
    /// 保存 128 维主人特征：float[] → byte[] → DPAPI 加密 → 写入 owner.bin。
    /// 维度不等于 128 时抛出 ArgumentException。
    /// </summary>
    public static void Save(float[] feature)
    {
        if (feature is null || feature.Length != FeatureLength)
        {
            throw new ArgumentException(
                $"主人特征向量长度必须为 {FeatureLength} 维，实际为 {feature?.Length.ToString() ?? "null"}。",
                nameof(feature));
        }

        // 每个 float 占 4 字节，整块复制到字节数组
        var bytes = new byte[feature.Length * sizeof(float)];
        Buffer.BlockCopy(feature, 0, bytes, 0, bytes.Length);

        // DPAPI 按当前 Windows 用户加密：换用户或换机器均无法解密
        byte[] protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetTemplatePath(), protectedBytes);
    }

    /// <summary>
    /// 读取主人特征：文件不存在返回 false；解密失败（密文损坏/被篡改/换用户）或
    /// 解密后长度非法均视为失败返回 false，绝不抛出。
    /// </summary>
    public static bool TryLoad(out float[] feature)
    {
        feature = Array.Empty<float>();
        string path = GetTemplatePath();
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            byte[] cipherBytes = File.ReadAllBytes(path);
            byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);

            // 解密结果必须恰好是 128 个 float（512 字节），否则视为数据损坏
            if (plainBytes.Length != FeatureLength * sizeof(float))
            {
                return false;
            }

            var result = new float[FeatureLength];
            Buffer.BlockCopy(plainBytes, 0, result, 0, plainBytes.Length);
            feature = result;
            return true;
        }
        catch (CryptographicException)
        {
            // 密文被篡改、熵不匹配或跨用户解密失败
            return false;
        }
        catch (IOException)
        {
            // 密文文件读取失败
            return false;
        }
    }

    /// <summary>
    /// 删除主人模板（文件不存在则无事发生）；删除时的 IO 异常静默忽略。
    /// </summary>
    public static void Delete()
    {
        try
        {
            string path = GetTemplatePath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 删除失败（如文件被占用）不影响主流程
        }
    }
}
