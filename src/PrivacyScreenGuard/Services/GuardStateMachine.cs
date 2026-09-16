using System;
using PrivacyScreenGuard.Models;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 守护判定状态机（纯逻辑、可注入时间戳，便于单元测试）。
///
/// 规则（与 spec 一致）：
/// - 风险帧：FaceButNoOwner；或 NoFace 且策略为 Lock。
/// - 安全帧：OwnerPresent；或 NoFace 且策略为 KeepNormal。
/// - 连续风险持续 ≥ 触发延迟 → Show（遮罩激活）。
/// - 遮罩激活后连续安全持续 ≥ 恢复延迟 → Hide（防闪烁）。
/// </summary>
public sealed class GuardStateMachine
{
    private readonly TimeSpan _triggerDelay;
    private readonly TimeSpan _recoverDelay;
    private readonly NoFacePolicy _noFacePolicy;

    /// <summary>遮罩当前是否处于激活状态。</summary>
    public bool MaskActive { get; private set; }

    private long? _riskSinceMs;
    private long? _safeSinceMs;

    /// <param name="triggerDelayMs">触发延迟（默认 500，范围 300–800）。</param>
    /// <param name="recoverDelayMs">恢复延迟（默认 800，范围 500–1000）。</param>
    /// <param name="noFacePolicy">无人策略（默认保持正常）。</param>
    public GuardStateMachine(int triggerDelayMs = 500, int recoverDelayMs = 800,
        NoFacePolicy noFacePolicy = NoFacePolicy.KeepNormal)
    {
        // 夹取到 spec 允许范围
        _triggerDelay = TimeSpan.FromMilliseconds(Math.Clamp(triggerDelayMs, 300, 800));
        _recoverDelay = TimeSpan.FromMilliseconds(Math.Clamp(recoverDelayMs, 500, 1000));
        _noFacePolicy = noFacePolicy;
    }

    /// <summary>
    /// 处理一帧观测。nowMs 为单调递增毫秒时间戳（Environment.TickCount64 或测试注入）。
    /// 返回遮罩动作（仅在状态切换时返回 Show/Hide，其余为 None）。
    /// </summary>
    public MaskAction Process(FrameObservation observation, long nowMs)
    {
        bool isRisky = observation switch
        {
            FrameObservation.FaceButNoOwner => true,
            FrameObservation.NoFace => _noFacePolicy == NoFacePolicy.Lock,
            FrameObservation.OwnerPresent => false,
            _ => false
        };

        if (isRisky)
        {
            _safeSinceMs = null;
            if (MaskActive) return MaskAction.None;

            // 风险开始计时（首次进入风险则从当前时间起算）
            _riskSinceMs ??= nowMs;
            if (nowMs - _riskSinceMs.Value >= (long)_triggerDelay.TotalMilliseconds)
            {
                MaskActive = true;
                return MaskAction.Show;
            }
            return MaskAction.None;
        }
        else
        {
            _riskSinceMs = null;
            if (!MaskActive) return MaskAction.None;

            // 恢复计时（防闪烁：安全持续达到恢复延迟才隐藏）
            _safeSinceMs ??= nowMs;
            if (nowMs - _safeSinceMs.Value >= (long)_recoverDelay.TotalMilliseconds)
            {
                MaskActive = false;
                return MaskAction.Hide;
            }
            return MaskAction.None;
        }
    }

    /// <summary>重置状态机（暂停恢复、设置变更时调用）。</summary>
    public void Reset()
    {
        MaskActive = false;
        _riskSinceMs = null;
        _safeSinceMs = null;
    }
}
