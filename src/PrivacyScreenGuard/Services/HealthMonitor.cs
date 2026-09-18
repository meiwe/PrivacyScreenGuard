using System;
using System.Collections.Generic;
using PrivacyScreenGuard.Models;

namespace PrivacyScreenGuard.Services;

/// <summary>健康提醒类型。</summary>
public enum HealthReminderKind
{
    /// <summary>久坐提醒：连续在场过久，起来活动。</summary>
    Sedentary,

    /// <summary>用眼距离提醒：距离屏幕过近。</summary>
    NearDistance,

    /// <summary>低头坐姿提醒：长时间低头。</summary>
    Slouch,

    /// <summary>喝水提醒：定时补充水分。</summary>
    Water,

    /// <summary>放松提醒：定时远眺休息眼睛。</summary>
    Break
}

/// <summary>
/// 健康提醒状态机：久坐/距离/低头由帧样本驱动（<see cref="OnSample"/>），
/// 喝水/放松由定时 tick 驱动（<see cref="OnTimerTick"/>，外部每分钟调用一次）；
/// 同类型提醒各自独立冷却（<see cref="HealthSettings.ReminderCooldownMinutes"/>）。
///
/// <para><b>可测性</b>：内部计时判断不读取 DateTime.Now——样本驱动判断用
/// <see cref="HealthSample.Timestamp"/>，定时驱动判断用 <see cref="OnTimerTick"/> 注入的 nowUtc，
/// 单元测试可完全用注入时间驱动，无真实等待。</para>
///
/// <para><b>线程安全</b>：全部公开方法内部加锁，可从采集线程（样本）与
/// UI 线程（设置更新/定时 tick）并发调用；<see cref="ReminderTriggered"/> 在锁外触发，
/// 避免订阅方回调与本类锁互等。</para>
/// </summary>
public sealed class HealthMonitor
{
    /// <summary>
    /// 久坐累计的重置时长（分钟）：连续离场（无可用人脸）达到该时长，累计清零重新计。
    /// </summary>
    internal const int SedentaryResetAfterMinutes = 5;

    /// <summary>保护全部内部状态的锁。</summary>
    private readonly object _sync = new();

    /// <summary>当前健康设置（内部保存副本，外部修改原对象不会绕过热更新清零逻辑）。</summary>
    private HealthSettings _settings;

    // ---- 久坐状态 ----
    /// <summary>累计在场秒数（只累计相邻两个"在场"样本间的时间差，离场段不计入）。</summary>
    private double _sedentarySeconds;

    /// <summary>最后一个样本是否在场（决定相邻时间差是否计入累计）。</summary>
    private bool _sedentaryLastPresent;

    /// <summary>最后一个样本的时刻（在场累计的时间基准）。</summary>
    private DateTime _sedentaryLastSampleUtc;

    /// <summary>连续离场起点（null = 当前不在离场计时中）。</summary>
    private DateTime? _absentSinceUtc;

    // ---- 距离/低头状态（null = 未在计时） ----
    /// <summary>距离过近的连续起点。</summary>
    private DateTime? _nearSinceUtc;

    /// <summary>低头的连续起点（侧脸帧不清零不推进，恢复正脸后按墙钟继续累计）。</summary>
    private DateTime? _slouchSinceUtc;

    // ---- 喝水/放松状态（null = 尚未定起点，下次 tick 以当时为起点，等价于"以开启时刻为起点"） ----
    /// <summary>喝水间隔的起算时刻。</summary>
    private DateTime? _waterAnchorUtc;

    /// <summary>放松间隔的起算时刻。</summary>
    private DateTime? _breakAnchorUtc;

    /// <summary>各类型上次触发时刻（同类冷却判断基准）。</summary>
    private readonly Dictionary<HealthReminderKind, DateTime> _lastTriggerUtc = new();

    /// <summary>
    /// 健康提醒触发事件：kind 为提醒类型，message 为温和中文文案。
    /// 由采集线程（样本驱动）或调用 <see cref="OnTimerTick"/> 的线程（定时驱动）触发，
    /// 订阅方如需操作 UI 应自行封送线程。
    /// </summary>
    public event Action<HealthReminderKind, string>? ReminderTriggered;

    /// <summary>创建健康提醒状态机。</summary>
    /// <param name="settings">健康设置（内部保存副本，后续热更新请调用 <see cref="UpdateSettings"/>）。</param>
    public HealthMonitor(HealthSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Clone();
    }

    /// <summary>
    /// 热更新设置：关闭某功能时清零该功能已累计的计时；刚从关闭变为开启的定时功能
    /// （喝水/放松）以开启时刻为起点（实现为下次 tick 以当时时刻起算）。
    /// </summary>
    public void UpdateSettings(HealthSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            HealthSettings old = _settings;
            _settings = settings.Clone();

            // 久坐：关闭 → 清零累计与离场计时（重新开启后从零开始）
            if (!_settings.SedentaryEnabled)
            {
                _sedentarySeconds = 0;
                _sedentaryLastPresent = false;
                _absentSinceUtc = null;
            }

            // 距离/低头：关闭 → 清零连续计时
            if (!_settings.NearEnabled)
            {
                _nearSinceUtc = null;
            }
            if (!_settings.SlouchEnabled)
            {
                _slouchSinceUtc = null;
            }

            // 喝水/放松：关闭 → 清零起点；刚从关闭变为开启 → 起点置空，
            // 下次 tick 以当时为起点（即以开启时刻起算）；保持开启 → 保留原起点
            if (!_settings.WaterEnabled || !old.WaterEnabled)
            {
                _waterAnchorUtc = null;
            }
            if (!_settings.BreakEnabled || !old.BreakEnabled)
            {
                _breakAnchorUtc = null;
            }
        }
    }

    /// <summary>
    /// 帧样本入口（守护引擎每帧检测后调用）：驱动久坐/距离/低头的累计与触发判定。
    /// </summary>
    public void OnSample(HealthSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        List<(HealthReminderKind Kind, string Message)>? pending = null;
        lock (_sync)
        {
            HealthSettings s = _settings;
            DateTime ts = sample.Timestamp;

            // ---- 久坐：在场时按相邻样本时间差累计（帧样本驱动） ----
            if (s.SedentaryEnabled)
            {
                if (sample.FacePresent)
                {
                    // 上一个样本也在场 → 两个样本间的时间差计入累计；
                    // 首个在场样本（或离场清零后的回归样本）只记录起点，不累计
                    if (_sedentaryLastPresent)
                    {
                        double delta = (ts - _sedentaryLastSampleUtc).TotalSeconds;
                        if (delta > 0)
                        {
                            _sedentarySeconds += delta;
                        }
                    }
                    _sedentaryLastSampleUtc = ts;
                    _sedentaryLastPresent = true;
                    _absentSinceUtc = null;
                }
                else
                {
                    _sedentaryLastSampleUtc = ts;
                    _sedentaryLastPresent = false;
                    _absentSinceUtc ??= ts;

                    // 离场持续达到重置时长 → 累计清零（回归后的首个在场样本重新只记录起点）
                    if (ts - _absentSinceUtc.Value >= TimeSpan.FromMinutes(SedentaryResetAfterMinutes))
                    {
                        _sedentarySeconds = 0;
                    }
                }

                // 触发判定：累计（含距最后样本的在场时长）达到阈值且不在冷却中
                double totalSeconds = _sedentarySeconds
                    + (_sedentaryLastPresent ? Math.Max(0, (ts - _sedentaryLastSampleUtc).TotalSeconds) : 0);
                if (totalSeconds >= s.SedentaryThresholdMinutes * 60
                    && !InCooldown(HealthReminderKind.Sedentary, ts))
                {
                    // 达到阈值但在冷却中：不清零，累计继续增长，冷却结束后再触发
                    _sedentarySeconds = 0;          // 触发后清零重新计
                    _sedentaryLastSampleUtc = ts;   // 重新计的起点 = 触发时刻
                    MarkTriggered(HealthReminderKind.Sedentary, ts);
                    (pending ??= new()).Add((HealthReminderKind.Sedentary,
                        BuildMessage(HealthReminderKind.Sedentary, totalSeconds / 60.0)));
                }
            }

            // ---- 用眼距离（帧样本驱动） ----
            if (s.NearEnabled)
            {
                if (sample.MaxFaceWidthRatio > s.NearThreshold)
                {
                    _nearSinceUtc ??= ts;   // 开始/继续计时
                    if (!InCooldown(HealthReminderKind.NearDistance, ts)
                        && ts - _nearSinceUtc.Value >= TimeSpan.FromSeconds(s.NearSeconds))
                    {
                        _nearSinceUtc = null;   // 触发后清零重新计
                        MarkTriggered(HealthReminderKind.NearDistance, ts);
                        (pending ??= new()).Add((HealthReminderKind.NearDistance,
                            BuildMessage(HealthReminderKind.NearDistance, 0)));
                    }
                }
                else
                {
                    _nearSinceUtc = null;   // 比例回落到阈值以内 → 立即清零
                }
            }

            // ---- 低头（帧样本驱动） ----
            if (s.SlouchEnabled)
            {
                if (!sample.IsFrontal)
                {
                    // 侧脸/无人帧：不清零不累计（保留已计时长，恢复正脸后继续）
                }
                else if (sample.PitchRatio is not double pitch)
                {
                    // 正脸但低头比例不可判定（关键点缺失/几何异常）：同样保留已计时长
                }
                else if (pitch > s.SlouchThreshold)
                {
                    _slouchSinceUtc ??= ts;   // 开始/继续计时
                    if (!InCooldown(HealthReminderKind.Slouch, ts)
                        && ts - _slouchSinceUtc.Value >= TimeSpan.FromSeconds(s.SlouchSeconds))
                    {
                        _slouchSinceUtc = null;   // 触发后清零重新计
                        MarkTriggered(HealthReminderKind.Slouch, ts);
                        (pending ??= new()).Add((HealthReminderKind.Slouch,
                            BuildMessage(HealthReminderKind.Slouch, 0)));
                    }
                }
                else
                {
                    _slouchSinceUtc = null;   // 正脸且比例回落到阈值以内 → 立即清零
                }
            }
        }

        RaisePending(pending);
    }

    /// <summary>
    /// 定时 tick 入口（外部每分钟调用一次）：驱动喝水/放松的间隔判断，
    /// 并对久坐/距离/低头做超时兜底判定（帧流停止后仍能按墙钟触发）。
    /// </summary>
    /// <param name="nowUtc">当前 UTC 时刻（由调用方注入；内部计时判断不读取系统时钟）。</param>
    public void OnTimerTick(DateTime nowUtc)
    {
        var pending = new List<(HealthReminderKind Kind, string Message)>();
        lock (_sync)
        {
            HealthSettings s = _settings;

            // ---- 喝水（定时驱动） ----
            if (s.WaterEnabled)
            {
                if (_waterAnchorUtc is null)
                {
                    _waterAnchorUtc = nowUtc;   // 首次 tick：以当前时刻为起点（等价于以开启时刻起算）
                }
                else if (nowUtc - _waterAnchorUtc.Value >= TimeSpan.FromMinutes(s.WaterIntervalMinutes)
                    && !InCooldown(HealthReminderKind.Water, nowUtc))
                {
                    _waterAnchorUtc = nowUtc;   // 触发后从当前时刻重新起算
                    MarkTriggered(HealthReminderKind.Water, nowUtc);
                    pending.Add((HealthReminderKind.Water, BuildMessage(HealthReminderKind.Water, 0)));
                }
            }

            // ---- 放松（定时驱动，逻辑与喝水一致） ----
            if (s.BreakEnabled)
            {
                if (_breakAnchorUtc is null)
                {
                    _breakAnchorUtc = nowUtc;
                }
                else if (nowUtc - _breakAnchorUtc.Value >= TimeSpan.FromMinutes(s.BreakIntervalMinutes)
                    && !InCooldown(HealthReminderKind.Break, nowUtc))
                {
                    _breakAnchorUtc = nowUtc;
                    MarkTriggered(HealthReminderKind.Break, nowUtc);
                    pending.Add((HealthReminderKind.Break, BuildMessage(HealthReminderKind.Break, 0)));
                }
            }

            // ---- 久坐超时兜底：最后一个样本在场时，用 tick 时刻补足在场时长再判一次 ----
            if (s.SedentaryEnabled && _sedentaryLastPresent
                && !InCooldown(HealthReminderKind.Sedentary, nowUtc))
            {
                double totalSeconds = _sedentarySeconds
                    + Math.Max(0, (nowUtc - _sedentaryLastSampleUtc).TotalSeconds);
                if (totalSeconds >= s.SedentaryThresholdMinutes * 60)
                {
                    _sedentarySeconds = 0;
                    _sedentaryLastSampleUtc = nowUtc;   // 从触发时刻重新计
                    MarkTriggered(HealthReminderKind.Sedentary, nowUtc);
                    pending.Add((HealthReminderKind.Sedentary,
                        BuildMessage(HealthReminderKind.Sedentary, totalSeconds / 60.0)));
                }
            }

            // ---- 距离超时兜底 ----
            if (s.NearEnabled && _nearSinceUtc is DateTime nearSince
                && !InCooldown(HealthReminderKind.NearDistance, nowUtc)
                && nowUtc - nearSince >= TimeSpan.FromSeconds(s.NearSeconds))
            {
                _nearSinceUtc = null;
                MarkTriggered(HealthReminderKind.NearDistance, nowUtc);
                pending.Add((HealthReminderKind.NearDistance, BuildMessage(HealthReminderKind.NearDistance, 0)));
            }

            // ---- 低头超时兜底 ----
            if (s.SlouchEnabled && _slouchSinceUtc is DateTime slouchSince
                && !InCooldown(HealthReminderKind.Slouch, nowUtc)
                && nowUtc - slouchSince >= TimeSpan.FromSeconds(s.SlouchSeconds))
            {
                _slouchSinceUtc = null;
                MarkTriggered(HealthReminderKind.Slouch, nowUtc);
                pending.Add((HealthReminderKind.Slouch, BuildMessage(HealthReminderKind.Slouch, 0)));
            }
        }

        RaisePending(pending);
    }

    /// <summary>
    /// 判断指定类型是否处于冷却期内（以注入时刻为基准，不读取系统时钟）。仅在锁内调用。
    /// </summary>
    private bool InCooldown(HealthReminderKind kind, DateTime nowUtc)
    {
        return _lastTriggerUtc.TryGetValue(kind, out DateTime last)
            && nowUtc - last < TimeSpan.FromMinutes(_settings.ReminderCooldownMinutes);
    }

    /// <summary>记录一次触发时刻（作为同类冷却的起点）。仅在锁内调用。</summary>
    private void MarkTriggered(HealthReminderKind kind, DateTime nowUtc)
    {
        _lastTriggerUtc[kind] = nowUtc;
    }

    /// <summary>
    /// 开启定时类提醒（喝水/放松）时的即时演示触发：立即发送一条该类型提醒，
    /// 计时起点重置（下次 tick 以当前时刻重新起算，即从开启时刻起算第一个间隔），
    /// 冷却时间戳同步设为当前时刻。类型未开启或非定时类型时不做任何事。线程安全。
    /// </summary>
    public void TriggerIntroOnce(HealthReminderKind kind, DateTime nowUtc)
    {
        if (kind is not (HealthReminderKind.Water or HealthReminderKind.Break))
        {
            return;
        }

        List<(HealthReminderKind Kind, string Message)>? pending = null;
        lock (_sync)
        {
            bool enabled = kind == HealthReminderKind.Water ? _settings.WaterEnabled : _settings.BreakEnabled;
            if (!enabled)
            {
                return;
            }

            // 起点重置：下次 tick 以当前时刻重新起算（避免开启前残留的旧起点导致立刻又触发）
            if (kind == HealthReminderKind.Water)
            {
                _waterAnchorUtc = null;
            }
            else
            {
                _breakAnchorUtc = null;
            }
            MarkTriggered(kind, nowUtc);
            pending = new List<(HealthReminderKind, string)> { (kind, BuildMessage(kind, 0)) };
        }

        RaisePending(pending);
    }

    /// <summary>生成温和中文提醒文案（仅久坐需要实际累计分钟数，其余类型忽略该参数）。</summary>
    private static string BuildMessage(HealthReminderKind kind, double sedentaryMinutes) => kind switch
    {
        HealthReminderKind.Sedentary => $"已连续使用约 {Math.Round(sedentaryMinutes)} 分钟了，起来活动一下吧",
        HealthReminderKind.NearDistance => "距离屏幕有点近了，稍往后靠一靠，保护眼睛",
        HealthReminderKind.Slouch => "好像低头很久了，挺直腰背，放松一下肩颈",
        HealthReminderKind.Water => "该喝口水了，补充一点水分吧",
        _ => "让眼睛休息一下吧，看看远处 20 秒",
    };

    /// <summary>在锁外逐条触发待发布提醒（避免订阅方回调与本类锁互等）。</summary>
    private void RaisePending(List<(HealthReminderKind Kind, string Message)>? pending)
    {
        if (pending is null)
        {
            return;
        }
        foreach (var (kind, message) in pending)
        {
            ReminderTriggered?.Invoke(kind, message);
        }
    }
}
