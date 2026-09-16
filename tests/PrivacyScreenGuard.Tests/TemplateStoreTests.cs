using System;
using PrivacyScreenGuard.Services;
using Xunit;

namespace PrivacyScreenGuard.Tests;

/// <summary>
/// 主人模板加密存储测试：直接读写真实 %LOCALAPPDATA% 下的 owner.bin。
/// 每个用例开头先 Delete() 清场，保证测试顺序无关。
/// </summary>
public class TemplateStoreTests
{
    [Fact]
    public void 保存128维特征后_文件存在且读回逐项相等()
    {
        TemplateStore.Delete(); // 清场，保证顺序无关
        var original = new float[TemplateStore.FeatureLength];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = i * 0.01f - 0.5f;
        }

        TemplateStore.Save(original);

        Assert.True(TemplateStore.Exists());
        Assert.True(TemplateStore.TryLoad(out float[] loaded));
        Assert.Equal(TemplateStore.FeatureLength, loaded.Length);
        for (int i = 0; i < original.Length; i++)
        {
            Assert.True(Math.Abs(original[i] - loaded[i]) < 1e-6, $"第 {i} 项不相等：{original[i]} vs {loaded[i]}");
        }
    }

    [Fact]
    public void 保存长度非128的特征_抛ArgumentException()
    {
        TemplateStore.Delete();

        // 127 维（缺 1 维）应被拒绝
        Assert.Throws<ArgumentException>(() => TemplateStore.Save(new float[127]));
    }

    [Fact]
    public void 删除后_文件不存在且读取失败()
    {
        TemplateStore.Delete();
        TemplateStore.Save(new float[TemplateStore.FeatureLength]); // 先写入，再验证删除

        TemplateStore.Delete();

        Assert.False(TemplateStore.Exists());
        Assert.False(TemplateStore.TryLoad(out _));
    }

    [Fact]
    public void 读取不存在的文件_返回false且输出为空数组()
    {
        TemplateStore.Delete(); // 清场，确保文件不存在

        Assert.False(TemplateStore.TryLoad(out float[] feature));
        Assert.Empty(feature);
    }
}
