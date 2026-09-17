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

    /// <summary>
    /// 多人在场时的策略（默认主人在场即放行，适合演示/给别人看屏幕）；
    /// "有陌生人即遮罩"下即使主人也在场，出现未匹配主人的人脸也会遮罩。
    /// </summary>
    public MultiPersonPolicy MultiPersonPolicy { get; set; } = MultiPersonPolicy.OwnerPresenceOpens;

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

    /// <summary>启动时自动检查更新（默认 true）：静默调用 GitHub Releases API，有新版才提示；失败静默。</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>全局热键修饰键（默认 Ctrl+Alt）。</summary>
    public string HotkeyModifiers { get; set; } = "Ctrl+Alt";

    /// <summary>全局热键主键（默认 P）。</summary>
    public string HotkeyKey { get; set; } = "P";

    /// <summary>遮罩模糊强度（0–100，默认 50）：0 = 纯黑遮罩；&gt;0 = 遮罩显示瞬间截屏并高斯模糊，值越大越模糊。</summary>
    public int BlurStrength { get; set; } = 50;

    /// <summary>
    /// 侧脸样本是否参与身份比对（默认 false = 侧脸不判定，扭头看侧屏不触发遮罩）。
    /// 设为 true 后，侧脸/贴边脸也做比对：配合注册的左/右转头模板能认出侧脸的主人，
    /// 同时拦截侧脸的陌生人；未注册侧脸模板时主人侧脸可能被误判。
    /// </summary>
    public bool StrictPoseMode { get; set; } = false;

    /// <summary>帧平均亮度低于该值判定光线过暗（范围 0–255，默认 18）。</summary>
    public double DarkThreshold { get; set; } = 18;

    /// <summary>
    /// 界面主题（"light" / "dark"，默认 light）。由 ThemeManager 在启动与切换时应用。
    /// </summary>
    public string Theme { get; set; } = "light";

    /// <summary>健康提醒设置（久坐/用眼距离/低头坐姿/喝水/放松，默认全部关闭）。</summary>
    public HealthSettings Health { get; set; } = new();

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
        // 主题只允许两个合法值，非法值回退 light
        if (Theme != "dark")
        {
            Theme = "light";
        }

        // 健康设置子对象的数值字段统一夹取（JSON 显式 null 时保持 null，由持久化层兜底）
        Health?.Sanitize();
    }
}

/// <summary>
/// 健康提醒设置：久坐/用眼距离/低头坐姿由摄像头帧样本驱动，喝水/放松由定时器驱动；
/// 五项功能全部默认关闭，用户在设置界面按需开启。数值字段由 <see cref="Sanitize"/> 夹取。
/// </summary>
public sealed class HealthSettings
{
    /// <summary>是否启用久坐提醒（默认 false）。</summary>
    public bool SedentaryEnabled { get; set; } = false;

    /// <summary>连续在场多少分钟后提醒活动（范围 15–120，默认 45）。</summary>
    public int SedentaryThresholdMinutes { get; set; } = 45;

    /// <summary>是否启用用眼距离提醒（默认 false）。</summary>
    public bool NearEnabled { get; set; } = false;

    /// <summary>人脸框宽/图像宽超过该比例视为距离过近（范围 0.25–0.5，默认 0.35）。</summary>
    public double NearThreshold { get; set; } = 0.35;

    /// <summary>距离过近持续多少秒后提醒（范围 5–60，默认 10）。</summary>
    public int NearSeconds { get; set; } = 10;

    /// <summary>是否启用低头坐姿提醒（默认 false）。</summary>
    public bool SlouchEnabled { get; set; } = false;

    /// <summary>低头比例超过该值视为低头（范围 0.5–0.8，默认 0.62）。</summary>
    public double SlouchThreshold { get; set; } = 0.62;

    /// <summary>低头持续多少秒后提醒（范围 10–120，默认 30）。</summary>
    public int SlouchSeconds { get; set; } = 30;

    /// <summary>是否启用喝水提醒（默认 false）。</summary>
    public bool WaterEnabled { get; set; } = false;

    /// <summary>每隔多少分钟提醒喝水（范围 15–180，默认 60）。</summary>
    public int WaterIntervalMinutes { get; set; } = 60;

    /// <summary>是否启用放松（远眺）提醒（默认 false）。</summary>
    public bool BreakEnabled { get; set; } = false;

    /// <summary>每隔多少分钟提醒放松远眺（范围 10–120，默认 30）。</summary>
    public int BreakIntervalMinutes { get; set; } = 30;

    /// <summary>同类提醒的最小间隔分钟数（范围 1–60，默认 10），冷却期内同类型不再触发。</summary>
    public int ReminderCooldownMinutes { get; set; } = 10;

    /// <summary>把带范围约定的数值字段夹取到各自合法范围，保证设置始终可用。</summary>
    public void Sanitize()
    {
        SedentaryThresholdMinutes = Math.Clamp(SedentaryThresholdMinutes, 15, 120);
        NearThreshold = Math.Clamp(NearThreshold, 0.25, 0.5);
        NearSeconds = Math.Clamp(NearSeconds, 5, 60);
        SlouchThreshold = Math.Clamp(SlouchThreshold, 0.5, 0.8);
        SlouchSeconds = Math.Clamp(SlouchSeconds, 10, 120);
        WaterIntervalMinutes = Math.Clamp(WaterIntervalMinutes, 15, 180);
        BreakIntervalMinutes = Math.Clamp(BreakIntervalMinutes, 10, 120);
        ReminderCooldownMinutes = Math.Clamp(ReminderCooldownMinutes, 1, 60);
    }

    /// <summary>创建当前设置的副本（供健康提醒状态机内部保存快照，避免共享可变引用）。</summary>
    public HealthSettings Clone()
    {
        return (HealthSettings)MemberwiseClone();
    }
}
