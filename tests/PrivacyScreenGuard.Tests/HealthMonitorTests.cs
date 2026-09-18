using System;
using System.Collections.Generic;
using PrivacyScreenGuard.Models;
using PrivacyScreenGuard.Services;
using Xunit;

namespace PrivacyScreenGuard.Tests;

/// <summary>
/// 健康提醒状态机单元测试：全部通过样本时间戳与 OnTimerTick(now) 注入时间驱动，无真实等待。
/// </summary>
public class HealthMonitorTests
{
    /// <summary>收集触发事件的 (类型, 文案)；xUnit 每个测试方法都会新建实例，互不干扰。</summary>
    private readonly List<(HealthReminderKind Kind, string Message)> _fired = new();

    /// <summary>创建监视器并订阅触发事件。</summary>
    private HealthMonitor Create(HealthSettings settings)
    {
        var monitor = new HealthMonitor(settings);
        monitor.ReminderTriggered += (kind, message) => _fired.Add((kind, message));
        return monitor;
    }

    /// <summary>构造帧样本：默认为"正常在场"（距离/低头均不超标）。</summary>
    private static HealthSample Sample(DateTime t, bool face = true, double widthRatio = 0.2,
        double? pitch = 0.55, bool frontal = true)
        => new(t, face, widthRatio, pitch, frontal);

    /// <summary>测试基准时刻 + 秒偏移（UTC）。</summary>
    private static DateTime T(double seconds)
        => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);

    // ---------------- 默认开关 ----------------

    [Fact]
    public void 默认全关_任意样本与定时都不触发()
    {
        var monitor = Create(new HealthSettings());   // 五项功能全部默认关闭

        // 喂各种"超标"样本：长时间在场、距离超近、持续低头
        monitor.OnSample(Sample(T(0)));
        monitor.OnSample(Sample(T(60), widthRatio: 0.9, pitch: 0.95));
        monitor.OnSample(Sample(T(7200), widthRatio: 0.9, pitch: 0.95));
        // 喝水/放松间隔早已远超
        monitor.OnTimerTick(T(60));
        monitor.OnTimerTick(T(7200));

        Assert.Empty(_fired);
    }

    // ---------------- 久坐 ----------------

    [Fact]
    public void 久坐_连续在场推进到阈值_触发一次()
    {
        var monitor = Create(new HealthSettings { SedentaryEnabled = true, SedentaryThresholdMinutes = 45 });

        // 首个在场样本只记录起点
        monitor.OnSample(Sample(T(0)));
        Assert.Empty(_fired);
        // 45 分钟后累计达到阈值 → 触发一次
        monitor.OnSample(Sample(T(45 * 60)));
        Assert.Single(_fired);
        Assert.Equal(HealthReminderKind.Sedentary, _fired[0].Kind);
        Assert.Contains("分钟", _fired[0].Message);
    }

    [Fact]
    public void 久坐_中途离场超过重置时长再回归_从零累计()
    {
        var monitor = Create(new HealthSettings { SedentaryEnabled = true, SedentaryThresholdMinutes = 15 });

        // 前 10 分钟在场
        monitor.OnSample(Sample(T(0)));
        monitor.OnSample(Sample(T(600)));                       // 累计 10 分钟
        // 离场：持续 300 秒 = 5 分钟（达到重置时长）→ 累计清零
        monitor.OnSample(Sample(T(601), face: false));
        monitor.OnSample(Sample(T(901), face: false));
        // 回归后从零累计：t=1652 仅累计 5 分钟（若未清零保留 10 分钟则会在此触发）→ 不触发
        monitor.OnSample(Sample(T(1352)));
        monitor.OnSample(Sample(T(1652)));
        Assert.Empty(_fired);
        // 从回归起点（t=1352）再累计 15 分钟 → 触发
        monitor.OnSample(Sample(T(1352 + 900)));
        Assert.Single(_fired);
    }

    [Fact]
    public void 久坐_冷却期内再次达到阈值_不触发()
    {
        var monitor = Create(new HealthSettings
        {
            SedentaryEnabled = true,
            SedentaryThresholdMinutes = 15,
            ReminderCooldownMinutes = 60,   // 拉长冷却便于验证
        });

        monitor.OnSample(Sample(T(0)));
        monitor.OnSample(Sample(T(900)));       // 累计 15 分钟 → 第一次触发
        Assert.Single(_fired);
        // 触发后清零重新计：t=1800 再达 15 分钟，但距上次触发仅 15 分钟（< 冷却 60 分钟）→ 不触发
        monitor.OnSample(Sample(T(1800)));
        Assert.Single(_fired);
        // 冷却期于 t=900+3600=4500 结束：期间累计持续增长，冷却一过 → 再次触发
        monitor.OnSample(Sample(T(4600)));
        Assert.Equal(2, _fired.Count);
    }

    // ---------------- 用眼距离 ----------------

    [Fact]
    public void 距离_超阈值连续达到时长触发_比例回落立即清零()
    {
        var monitor = Create(new HealthSettings { NearEnabled = true, NearThreshold = 0.35, NearSeconds = 10 });

        // 连续超阈值 10 秒 → 触发
        monitor.OnSample(Sample(T(0), widthRatio: 0.4));
        monitor.OnSample(Sample(T(5), widthRatio: 0.4));
        monitor.OnSample(Sample(T(10), widthRatio: 0.4));
        Assert.Single(_fired);
        Assert.Equal(HealthReminderKind.NearDistance, _fired[0].Kind);
        // 触发后清零重新计：继续近距离 5 秒不触发
        monitor.OnSample(Sample(T(15), widthRatio: 0.4));
        Assert.Single(_fired);
        // 比例回落 → 立即清零：之后重新近距离 3 秒不触发
        monitor.OnSample(Sample(T(16), widthRatio: 0.2));
        monitor.OnSample(Sample(T(19), widthRatio: 0.4));   // 重新起点
        monitor.OnSample(Sample(T(22), widthRatio: 0.4));   // 仅 3 秒
        Assert.Single(_fired);
    }

    [Fact]
    public void 距离_仅持续3秒即恢复_不触发()
    {
        var monitor = Create(new HealthSettings { NearEnabled = true, NearThreshold = 0.35, NearSeconds = 10 });

        monitor.OnSample(Sample(T(0), widthRatio: 0.5));
        monitor.OnSample(Sample(T(3), widthRatio: 0.5));
        monitor.OnSample(Sample(T(4), widthRatio: 0.2));    // 3 秒后恢复
        monitor.OnSample(Sample(T(100), widthRatio: 0.2));

        Assert.Empty(_fired);
    }

    // ---------------- 低头 ----------------

    [Fact]
    public void 低头_连续达到时长触发()
    {
        var monitor = Create(new HealthSettings { SlouchEnabled = true, SlouchThreshold = 0.62, SlouchSeconds = 30 });

        monitor.OnSample(Sample(T(0), pitch: 0.7));
        monitor.OnSample(Sample(T(30), pitch: 0.7));    // 连续低头 30 秒 → 触发

        Assert.Single(_fired);
        Assert.Equal(HealthReminderKind.Slouch, _fired[0].Kind);
        Assert.Contains("低头", _fired[0].Message);
    }

    [Fact]
    public void 低头_插入侧脸样本后恢复_不清零继续累计仍触发()
    {
        var monitor = Create(new HealthSettings { SlouchEnabled = true, SlouchThreshold = 0.62, SlouchSeconds = 30 });

        monitor.OnSample(Sample(T(0), pitch: 0.7));                         // 低头起点
        monitor.OnSample(Sample(T(10), pitch: 0.7));                        // 已低头 10 秒
        monitor.OnSample(Sample(T(10.5), pitch: null, frontal: false));     // 侧脸帧：不清零不累计
        // 恢复正脸低头：若被清零累计只有 24.5 秒（<30），保留已计时长则 35 秒 → 触发
        monitor.OnSample(Sample(T(35), pitch: 0.7));

        Assert.Single(_fired);
    }

    // ---------------- 喝水 / 放松 ----------------

    [Fact]
    public void 喝水_开启后推进60分钟触发一次_间隔内不重复()
    {
        var monitor = Create(new HealthSettings { WaterEnabled = true, WaterIntervalMinutes = 60 });

        monitor.OnTimerTick(T(0));          // 首次 tick：以当前时刻为起点，不触发
        monitor.OnTimerTick(T(59 * 60));    // 未到间隔
        Assert.Empty(_fired);
        monitor.OnTimerTick(T(60 * 60));    // 达到 60 分钟 → 触发
        Assert.Single(_fired);
        Assert.Equal(HealthReminderKind.Water, _fired[0].Kind);
        monitor.OnTimerTick(T(61 * 60));    // 1 分钟后：远未到下一间隔 → 不重复
        Assert.Single(_fired);
        monitor.OnTimerTick(T(120 * 60));   // 再过 60 分钟 → 再次触发
        Assert.Equal(2, _fired.Count);
    }

    [Fact]
    public void 喝水_热更新开启后_以下次定时时刻为起点()
    {
        var monitor = Create(new HealthSettings());     // 全关
        monitor.OnTimerTick(T(0));                      // 全关时 tick 无效果
        monitor.UpdateSettings(new HealthSettings { WaterEnabled = true, WaterIntervalMinutes = 60 });

        monitor.OnTimerTick(T(10));                     // 开启后首次 tick：定起点，不触发
        monitor.OnTimerTick(T(60 * 60 - 10));           // 距起点 59 分 50 秒 → 不触发
        Assert.Empty(_fired);
        monitor.OnTimerTick(T(60 * 60 + 10));           // 距起点恰好 60 分钟 → 触发
        Assert.Single(_fired);
    }

    [Fact]
    public void 放松_推进到间隔触发()
    {
        var monitor = Create(new HealthSettings { BreakEnabled = true, BreakIntervalMinutes = 30 });

        monitor.OnTimerTick(T(0));          // 起点
        monitor.OnTimerTick(T(29 * 60));    // 未到间隔
        Assert.Empty(_fired);
        monitor.OnTimerTick(T(30 * 60));    // 达到 30 分钟 → 触发
        Assert.Single(_fired);
        Assert.Equal(HealthReminderKind.Break, _fired[0].Kind);
    }

    // ---------------- 开启即演示（TriggerIntroOnce） ----------------

    [Fact]
    public void 演示_开启状态下立即触发一次_且从当前时刻重新起算()
    {
        var monitor = Create(new HealthSettings { WaterEnabled = true, WaterIntervalMinutes = 60 });

        // 先模拟残留起点：已推进 59 分钟（未触发）
        monitor.OnTimerTick(T(0));
        monitor.OnTimerTick(T(59 * 60));
        Assert.Empty(_fired);

        // 用户此刻打开开关 → 演示触发一次，旧起点作废
        monitor.TriggerIntroOnce(HealthReminderKind.Water, T(59 * 60 + 30));
        Assert.Single(_fired);
        Assert.Equal(HealthReminderKind.Water, _fired[0].Kind);

        // 演示后从当前时刻重新起算：10 分钟后的 tick 定新起点（anchor 为空 → 定起点不触发）
        monitor.OnTimerTick(T(69 * 60 + 30));
        Assert.Single(_fired);

        // 新起点（T 69:00+30）再过 60 分钟 → 正常触发
        monitor.OnTimerTick(T(129 * 60 + 30));
        Assert.Equal(2, _fired.Count);
    }

    [Fact]
    public void 演示_未开启或非定时类型_不触发()
    {
        var monitor = Create(new HealthSettings { WaterEnabled = false, BreakEnabled = true });

        // 喝水未开启 → 无效果
        monitor.TriggerIntroOnce(HealthReminderKind.Water, T(0));
        // 久坐是样本驱动类型，不参与演示
        monitor.TriggerIntroOnce(HealthReminderKind.Sedentary, T(0));
        Assert.Empty(_fired);

        // 放松已开启 → 触发
        monitor.TriggerIntroOnce(HealthReminderKind.Break, T(0));
        Assert.Single(_fired);
        Assert.Equal(HealthReminderKind.Break, _fired[0].Kind);
    }

    // ---------------- 热更新 ----------------

    [Fact]
    public void 热更新_关闭某功能后不再触发_重新开启后从零累计()
    {
        var monitor = Create(new HealthSettings { SedentaryEnabled = true, SedentaryThresholdMinutes = 15 });

        monitor.OnSample(Sample(T(0)));
        monitor.OnSample(Sample(T(600)));       // 累计 10 分钟（未达 15 分钟）
        // 关闭久坐 → 清零计时，之后喂多久都不触发
        monitor.UpdateSettings(new HealthSettings { SedentaryEnabled = false });
        monitor.OnSample(Sample(T(3600)));
        monitor.OnTimerTick(T(7200));
        Assert.Empty(_fired);
        // 重新开启（从关闭变为开启）：计时已清零，首样本只记录起点
        monitor.UpdateSettings(new HealthSettings { SedentaryEnabled = true, SedentaryThresholdMinutes = 15 });
        monitor.OnSample(Sample(T(7201)));
        Assert.Empty(_fired);
        monitor.OnSample(Sample(T(7201 + 900)));    // 从起点累计 15 分钟 → 触发
        Assert.Single(_fired);
    }

    // ---------------- 设置校验 ----------------

    [Fact]
    public void 健康设置_Sanitize把非法数值夹取到规范范围()
    {
        var settings = new AppSettings
        {
            Health = new HealthSettings
            {
                SedentaryThresholdMinutes = 5,
                NearThreshold = 0.1,
                NearSeconds = 1,
                SlouchThreshold = 0.3,
                SlouchSeconds = 2,
                WaterIntervalMinutes = 10,
                BreakIntervalMinutes = 5,
                ReminderCooldownMinutes = 0,
            }
        };
        settings.Sanitize();

        Assert.Equal(15, settings.Health.SedentaryThresholdMinutes);
        Assert.Equal(0.25, settings.Health.NearThreshold);
        Assert.Equal(5, settings.Health.NearSeconds);
        Assert.Equal(0.5, settings.Health.SlouchThreshold);
        Assert.Equal(10, settings.Health.SlouchSeconds);
        Assert.Equal(15, settings.Health.WaterIntervalMinutes);
        Assert.Equal(10, settings.Health.BreakIntervalMinutes);
        Assert.Equal(1, settings.Health.ReminderCooldownMinutes);
    }
}
