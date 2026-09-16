using PrivacyScreenGuard.Models;
using PrivacyScreenGuard.Services;
using Xunit;

namespace PrivacyScreenGuard.Tests;

/// <summary>
/// 多人在场策略测试：主人在场时同框陌生人如何处理。
/// </summary>
public class MultiPersonPolicyTests
{
    [Fact]
    public void 宽松策略_主人独自在场_放行()
    {
        var result = GuardEngine.ResolveObservation(
            ownerFound: true, unmatchedFaces: 0, anyUsableFace: true,
            ownerPresenceOpens: true);
        Assert.Equal(FrameObservation.OwnerPresent, result);
    }

    [Fact]
    public void 宽松策略_主人与陌生人同框_放行()
    {
        // 给客人看屏幕场景：主人在场即放行，即使旁边有陌生人
        var result = GuardEngine.ResolveObservation(
            ownerFound: true, unmatchedFaces: 1, anyUsableFace: true,
            ownerPresenceOpens: true);
        Assert.Equal(FrameObservation.OwnerPresent, result);
    }

    [Fact]
    public void 宽松策略_主人和两个陌生人同框_放行()
    {
        var result = GuardEngine.ResolveObservation(
            ownerFound: true, unmatchedFaces: 2, anyUsableFace: true,
            ownerPresenceOpens: true);
        Assert.Equal(FrameObservation.OwnerPresent, result);
    }

    [Fact]
    public void 严格策略_主人与陌生人同框_遮罩()
    {
        // 防偷看场景：即使主人在场，出现未匹配主人的人脸也视为风险
        var result = GuardEngine.ResolveObservation(
            ownerFound: true, unmatchedFaces: 1, anyUsableFace: true,
            ownerPresenceOpens: false);
        Assert.Equal(FrameObservation.FaceButNoOwner, result);
    }

    [Fact]
    public void 严格策略_仅主人独自在场_放行()
    {
        var result = GuardEngine.ResolveObservation(
            ownerFound: true, unmatchedFaces: 0, anyUsableFace: true,
            ownerPresenceOpens: false);
        Assert.Equal(FrameObservation.OwnerPresent, result);
    }

    [Fact]
    public void 任何策略_仅陌生人无主人_遮罩()
    {
        var loose = GuardEngine.ResolveObservation(
            ownerFound: false, unmatchedFaces: 1, anyUsableFace: true, ownerPresenceOpens: true);
        var strict = GuardEngine.ResolveObservation(
            ownerFound: false, unmatchedFaces: 1, anyUsableFace: true, ownerPresenceOpens: false);
        Assert.Equal(FrameObservation.FaceButNoOwner, loose);
        Assert.Equal(FrameObservation.FaceButNoOwner, strict);
    }

    [Fact]
    public void 任何策略_全是侧脸贴边等不可靠人脸_按无有效观测处理()
    {
        // 宽松姿态模式下侧脸被跳过：anyUsableFace=false，不打断状态机计时
        var loose = GuardEngine.ResolveObservation(
            ownerFound: false, unmatchedFaces: 0, anyUsableFace: false, ownerPresenceOpens: true);
        var strict = GuardEngine.ResolveObservation(
            ownerFound: false, unmatchedFaces: 0, anyUsableFace: false, ownerPresenceOpens: false);
        Assert.Equal(FrameObservation.NoFace, loose);
        Assert.Equal(FrameObservation.NoFace, strict);
    }
}
