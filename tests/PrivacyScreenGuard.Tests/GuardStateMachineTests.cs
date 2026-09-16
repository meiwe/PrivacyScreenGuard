using System;
using PrivacyScreenGuard.Models;
using PrivacyScreenGuard.Services;
using Xunit;

namespace PrivacyScreenGuard.Tests;

/// <summary>
/// 守护状态机单元测试：注入毫秒时间戳，验证触发/恢复/防闪烁/无人策略。
/// 语义：首帧风险开始计时，nowMs - riskSince 达到触发延迟时显示遮罩。
/// </summary>
public class GuardStateMachineTests
{
    private const int Frame = 125; // 8 FPS ≈ 125ms/帧

    [Fact]
    public void 持续非主人超过触发延迟_显示遮罩()
    {
        var sm = new GuardStateMachine(triggerDelayMs: 500, recoverDelayMs: 800);
        // t=0 首个风险帧开始计时
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 0));
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 125));
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 375));
        // t=500 风险持续达到触发延迟 → Show
        Assert.Equal(MaskAction.Show, sm.Process(FrameObservation.FaceButNoOwner, 500));
        Assert.True(sm.MaskActive);
    }

    [Fact]
    public void 仅主人在场_永不触发()
    {
        var sm = new GuardStateMachine();
        for (long t = 0; t <= 60_000; t += Frame)
        {
            Assert.Equal(MaskAction.None, sm.Process(FrameObservation.OwnerPresent, t));
        }
        Assert.False(sm.MaskActive);
    }

    [Fact]
    public void 短时晃过_不触发不闪烁()
    {
        var sm = new GuardStateMachine(triggerDelayMs: 500, recoverDelayMs: 800);
        // 风险仅持续 375ms（< 触发延迟）
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 0));
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 125));
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 375));
        // 回到安全 → 计时清零，无 Show
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.OwnerPresent, 500));
        // 之后即使再次短暂出现风险也重新计时
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 625));
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 875));
        Assert.False(sm.MaskActive);
    }

    [Fact]
    public void 触发后主人回来_持续超过恢复延迟才隐藏()
    {
        var sm = new GuardStateMachine(triggerDelayMs: 300, recoverDelayMs: 800);
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 0));   // 计时开始
        Assert.Equal(MaskAction.Show, sm.Process(FrameObservation.FaceButNoOwner, 300)); // 达到触发延迟
        // 主人回来但未满恢复延迟
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.OwnerPresent, 350));   // 恢复计时开始
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.OwnerPresent, 700));
        Assert.True(sm.MaskActive);
        // t=350+800=1150 → Hide
        Assert.Equal(MaskAction.Hide, sm.Process(FrameObservation.OwnerPresent, 1150));
        Assert.False(sm.MaskActive);
    }

    [Fact]
    public void 无人策略保持正常_无人帧不触发()
    {
        var sm = new GuardStateMachine(noFacePolicy: NoFacePolicy.KeepNormal);
        for (long t = 0; t <= 60_000; t += Frame)
        {
            Assert.Equal(MaskAction.None, sm.Process(FrameObservation.NoFace, t));
        }
        Assert.False(sm.MaskActive);
    }

    [Fact]
    public void 无人策略锁定_无人持续达到触发延迟则遮罩()
    {
        var sm = new GuardStateMachine(triggerDelayMs: 500, recoverDelayMs: 800,
            noFacePolicy: NoFacePolicy.Lock);
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.NoFace, 0));
        Assert.Equal(MaskAction.Show, sm.Process(FrameObservation.NoFace, 500));
        // 有人脸但不是主人 → 同样是风险帧，维持遮罩
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 700));
    }

    [Fact]
    public void 延迟参数被夹取到规范范围()
    {
        // 非法输入 100/2000 应被夹取为 300/1000
        var sm = new GuardStateMachine(triggerDelayMs: 100, recoverDelayMs: 2000);
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 0));
        // 触发延迟 = 300（而不是 100）：t=299 不触发，t=300 触发
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 299));
        Assert.Equal(MaskAction.Show, sm.Process(FrameObservation.FaceButNoOwner, 300));
        // 恢复延迟 = 1000（而不是 2000）：主人于 t=300 回来，t=1299 尚差 1ms，t=1300 恰好 1000ms
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.OwnerPresent, 300));
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.OwnerPresent, 1299));
        Assert.Equal(MaskAction.Hide, sm.Process(FrameObservation.OwnerPresent, 1300));
    }

    [Fact]
    public void 重置后立即回到初始状态()
    {
        var sm = new GuardStateMachine();
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.FaceButNoOwner, 0));
        Assert.Equal(MaskAction.Show, sm.Process(FrameObservation.FaceButNoOwner, 500));
        sm.Reset();
        Assert.False(sm.MaskActive);
        Assert.Equal(MaskAction.None, sm.Process(FrameObservation.OwnerPresent, 600));
    }
}
