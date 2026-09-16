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

    [Fact]
    public void 保存多姿态模板后_读回逐模板逐项相等()
    {
        TemplateStore.Delete();
        // 模拟三姿态模板：正脸 + 左转头 + 右转头
        var frontal = new float[TemplateStore.FeatureLength];
        var left = new float[TemplateStore.FeatureLength];
        var right = new float[TemplateStore.FeatureLength];
        for (int i = 0; i < TemplateStore.FeatureLength; i++)
        {
            frontal[i] = i * 0.01f;
            left[i] = -i * 0.02f + 0.3f;
            right[i] = i * 0.03f - 1.0f;
        }

        TemplateStore.Save(new[] { frontal, left, right });

        Assert.True(TemplateStore.Exists());
        Assert.True(TemplateStore.TryLoadAll(out float[][] all));
        Assert.Equal(3, all.Length);
        float[][] expected = { frontal, left, right };
        for (int t = 0; t < 3; t++)
        {
            Assert.Equal(TemplateStore.FeatureLength, all[t].Length);
            for (int i = 0; i < TemplateStore.FeatureLength; i++)
            {
                Assert.True(Math.Abs(expected[t][i] - all[t][i]) < 1e-6,
                    $"模板 {t} 第 {i} 项不相等");
            }
        }
    }

    [Fact]
    public void 多模板保存后_旧的单模板读取接口返回第一个模板()
    {
        TemplateStore.Delete();
        var first = new float[TemplateStore.FeatureLength];
        var second = new float[TemplateStore.FeatureLength];
        first[0] = 0.42f;
        second[0] = 0.99f;

        TemplateStore.Save(new[] { first, second });

        Assert.True(TemplateStore.TryLoad(out float[] single));
        Assert.Equal(0.42f, single[0], 5); // 应返回第一个（正脸）模板
    }

    [Fact]
    public void 多模板保存后_删除接口照常生效()
    {
        TemplateStore.Delete();
        TemplateStore.Save(new[] { new float[TemplateStore.FeatureLength], new float[TemplateStore.FeatureLength] });
        Assert.True(TemplateStore.Exists());

        TemplateStore.Delete();
        Assert.False(TemplateStore.Exists());
        Assert.False(TemplateStore.TryLoadAll(out _));
    }

    [Fact]
    public void 空模板列表_抛ArgumentException()
    {
        TemplateStore.Delete();
        Assert.Throws<ArgumentException>(() => TemplateStore.Save(Array.Empty<float[]>()));
    }

    [Fact]
    public void 模板数超上限_抛ArgumentException()
    {
        TemplateStore.Delete();
        var tooMany = new float[TemplateStore.MaxTemplateCount + 1][];
        for (int i = 0; i < tooMany.Length; i++)
        {
            tooMany[i] = new float[TemplateStore.FeatureLength];
        }
        Assert.Throws<ArgumentException>(() => TemplateStore.Save(tooMany));
    }
}
