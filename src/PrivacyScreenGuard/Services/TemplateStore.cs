using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 主人人脸特征模板的加密存储：使用 DPAPI（CurrentUser）+ 固定附加熵加密，
/// 落盘为 %LOCALAPPDATA%\PrivacyScreenGuard\owner.bin，仅当前 Windows 用户可解密。
///
/// 支持多姿态模板（正脸 + 左右侧脸）：比对时取与所有模板的最大相似度，
/// 解决"主人扭头看侧屏被误判为陌生人"的问题。
/// 文件格式 v2：[int32 模板数 N][N × 128 维 float]；
/// 兼容 v1（恰为 512 字节 = 单模板）。
/// </summary>
public static class TemplateStore
{
    /// <summary>模板文件名。</summary>
    public const string OwnerTemplateFileName = "owner.bin";

    /// <summary>特征向量维度（SFace 模型输出 128 维）。</summary>
    public const int FeatureLength = 128;

    /// <summary>允许保存的最大模板数（正脸 5 + 左侧 2 + 右侧 2 足够，防御异常数据）。</summary>
    public const int MaxTemplateCount = 9;

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
    /// 保存多姿态主人特征模板（1~9 个 128 维向量，通常为正脸 + 左右侧脸）：
    /// 全部向量按顺序拼平 → DPAPI 加密 → 写入 owner.bin。
    /// 任一向量维度不为 128 或数量超上限时抛出 ArgumentException。
    /// </summary>
    public static void Save(IReadOnlyList<float[]> features)
    {
        if (features is null || features.Count == 0 || features.Count > MaxTemplateCount)
        {
            throw new ArgumentException(
                $"模板数量必须在 1~{MaxTemplateCount} 之间，实际为 {features?.Count.ToString() ?? "null"}。",
                nameof(features));
        }
        foreach (float[] feature in features)
        {
            if (feature is null || feature.Length != FeatureLength)
            {
                throw new ArgumentException(
                    $"每个特征向量长度必须为 {FeatureLength} 维，发现无效向量（长度 {feature?.Length.ToString() ?? "null"}）。",
                    nameof(features));
            }
        }

        // v2 布局：[int32 模板数][模板1 floats][模板2 floats]...
        var bytes = new byte[sizeof(int) + features.Count * FeatureLength * sizeof(float)];
        BitConverter.GetBytes(features.Count).CopyTo(bytes, 0);
        int offset = sizeof(int);
        foreach (float[] feature in features)
        {
            Buffer.BlockCopy(feature, 0, bytes, offset, FeatureLength * sizeof(float));
            offset += FeatureLength * sizeof(float);
        }

        // DPAPI 按当前 Windows 用户加密：换用户或换机器均无法解密
        byte[] protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetTemplatePath(), protectedBytes);
    }

    /// <summary>
    /// 保存单个特征向量的便捷重载（等价于 Save(单元素列表)，兼容旧调用）。
    /// </summary>
    public static void Save(float[] feature)
    {
        Save(new[] { feature });
    }

    /// <summary>
    /// 读取全部主人特征模板：文件不存在返回 false；解密失败（密文损坏/被篡改/换用户）或
    /// 解密后长度非法均视为失败返回 false，绝不抛出。
    /// 自动兼容 v1（512 字节单模板）与 v2（多模板）两种文件格式。
    /// </summary>
    public static bool TryLoadAll(out float[][] features)
    {
        features = Array.Empty<float[]>();
        string path = GetTemplatePath();
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            byte[] cipherBytes = File.ReadAllBytes(path);
            byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
            int vectorBytes = FeatureLength * sizeof(float);

            // v1 旧格式：恰好 512 字节 = 单模板（无数量前缀）
            if (plainBytes.Length == vectorBytes)
            {
                var single = new float[FeatureLength];
                Buffer.BlockCopy(plainBytes, 0, single, 0, vectorBytes);
                features = new[] { single };
                return true;
            }

            // v2 格式：[int32 数量 N][N × 512 字节]
            if (plainBytes.Length < sizeof(int))
            {
                return false;
            }
            int count = BitConverter.ToInt32(plainBytes, 0);
            if (count < 1 || count > MaxTemplateCount
                || plainBytes.Length != sizeof(int) + count * vectorBytes)
            {
                return false; // 数量非法或长度不匹配 → 数据损坏
            }

            var result = new float[count][];
            for (int i = 0; i < count; i++)
            {
                var vector = new float[FeatureLength];
                Buffer.BlockCopy(plainBytes, sizeof(int) + i * vectorBytes, vector, 0, vectorBytes);
                result[i] = vector;
            }
            features = result;
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
    /// 读取单个模板的便捷方法（多模板时返回第一个；兼容旧调用，推荐用 TryLoadAll）。
    /// </summary>
    public static bool TryLoad(out float[] feature)
    {
        feature = Array.Empty<float>();
        if (!TryLoadAll(out float[][] all) || all.Length == 0)
        {
            return false;
        }
        feature = all[0];
        return true;
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
