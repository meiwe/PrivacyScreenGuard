using System;
using System.Collections.Generic;

namespace PrivacyScreenGuard.Models;

/// <summary>显示器守护范围模式。</summary>
public enum MonitorMode
{
    /// <summary>守护所有显示器（默认）。</summary>
    All,

    /// <summary>仅守护主显示器。</summary>
    Primary,

    /// <summary>仅守护 SelectedMonitors 中选中的显示器。</summary>
    Selected
}

/// <summary>
/// 应用设置模型：持久化为 %LOCALAPPDATA%\PrivacyScreenGuard\settings.json。
/// 带范围约定的数值字段统一由 <see cref="Sanitize"/> 夹取，保证落盘与读回后的值始终合法。
/// </summary>
public sealed class AppSettings
{
    /// <summary>主人识别余弦阈值（范围 0.3–0.9，默认 0.55），越大越严格。</summary>
    public double OwnerThreshold { get; set; } = 0.55;

    /// <summary>风险持续多久后触发遮罩（毫秒，范围 300–800，默认 500）。</summary>
    public int TriggerDelayMs { get; set; } = 500;

    /// <summary>主人回归后持续多久解除遮罩（毫秒，范围 500–1000，默认 800）。</summary>
    public int RecoverDelayMs { get; set; } = 800;

    /// <summary>摄像头采集帧率（范围 5–10，默认 8）。</summary>
    public double CaptureFps { get; set; } = 8;

    /// <summary>无人脸时的策略（默认保持正常，复用 CoreTypes 中的枚举）。</summary>
    public NoFacePolicy NoFacePolicy { get; set; } = NoFacePolicy.KeepNormal;

    /// <summary>摄像头索引（默认 0）。</summary>
    public int CameraIndex { get; set; } = 0;

    /// <summary>显示器守护模式（默认全部）。</summary>
    public MonitorMode MonitorMode { get; set; } = MonitorMode.All;

    /// <summary>MonitorMode=Selected 时生效的显示器索引列表。</summary>
    public List<int> SelectedMonitors { get; set; } = new();

    /// <summary>是否启用摄像头守护（默认 true）。</summary>
    public bool CameraEnabled { get; set; } = true;

    /// <summary>是否开机自启（默认 false）。</summary>
    public bool Autostart { get; set; } = false;

    /// <summary>全局热键修饰键（默认 Ctrl+Alt）。</summary>
    public string HotkeyModifiers { get; set; } = "Ctrl+Alt";

    /// <summary>全局热键主键（默认 P）。</summary>
    public string HotkeyKey { get; set; } = "P";

    /// <summary>遮罩模糊强度（0–100，默认 50）：0 = 纯黑遮罩；&gt;0 = 遮罩显示瞬间截屏并高斯模糊，值越大越模糊。</summary>
    public int BlurStrength { get; set; } = 50;

    /// <summary>帧平均亮度低于该值判定光线过暗（范围 0–255，默认 18）。</summary>
    public double DarkThreshold { get; set; } = 18;

    /// <summary>
    /// 把带范围约定的数值字段夹取到各自合法范围，保证设置始终可用。
    /// </summary>
    public void Sanitize()
    {
        OwnerThreshold = Math.Clamp(OwnerThreshold, 0.3, 0.9);
        TriggerDelayMs = Math.Clamp(TriggerDelayMs, 300, 800);
        RecoverDelayMs = Math.Clamp(RecoverDelayMs, 500, 1000);
        CaptureFps = Math.Clamp(CaptureFps, 5, 10);
        BlurStrength = Math.Clamp(BlurStrength, 0, 100);
        DarkThreshold = Math.Clamp(DarkThreshold, 0, 255);
    }
}
