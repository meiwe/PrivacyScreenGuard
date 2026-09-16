using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using PrivacyScreenGuard.Models;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 摄像头采集服务：后台线程采集，仅抛帧给订阅者，不做推理、不保存画面。
/// 帧的 Mat 所有权归订阅者（订阅者负责 Dispose），本循环内不再释放该帧。
/// 注意：FrameCaptured / Error 事件均在后台采集线程触发，
/// 订阅方若需更新 UI（WPF）必须自行切换到 Dispatcher/UI 线程。
/// </summary>
public sealed class CameraService : ICameraService
{
    /// <summary>光线过暗的平均亮度阈值（0~255），低于该值上报 TooDark。</summary>
    private readonly double _darkThreshold;

    /// <summary>保护 _capture / _cts / _loopTask 的锁。</summary>
    private readonly object _sync = new();

    /// <summary>
    /// 全局摄像头原生操作闸门（进程级）：OpenCV 的 DSHOW/MSMF 后端不允许跨线程并发执行
    /// open / set / read / delete，而应用中存在多个并发源——向导预览、主窗口设备枚举
    /// （EnumerateDevices 会逐个短暂开关设备）、引擎采集线程——同时操作同一物理设备
    /// 会造成原生堆损坏并直接崩溃（AccessViolation / 0xc0000374）。
    /// 因此所有 VideoCapture 的构造、属性设置、Read、Dispose 一律在本锁内串行执行。
    /// </summary>
    private static readonly object CvCaptureGate = new();

    private VideoCapture? _capture;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile bool _isRunning;

    /// <summary>过暗提示的节流间隔（毫秒），距上次上报至少 5 秒。</summary>
    private const int DarkReportIntervalMs = 5000;

    /// <summary>连续读取失败达到该次数即判定摄像头断开。</summary>
    private const int MaxReadFailures = 3;

    /// <summary>累计打开失败达到该次数（首次 + 4 次重试）即上报 Busy。</summary>
    private const int MaxOpenFailures = 5;

    /// <summary>打开失败的自动重试间隔（毫秒）。</summary>
    private const int OpenRetryIntervalMs = 2000;

    /// <summary>采集分辨率（宽）。</summary>
    private const int CaptureWidth = 640;

    /// <summary>采集分辨率（高）。</summary>
    private const int CaptureHeight = 480;

    /// <summary>
    /// 创建采集服务。
    /// </summary>
    /// <param name="darkThreshold">光线过暗阈值（平均亮度 0~255），默认 18。</param>
    public CameraService(double darkThreshold = 18)
    {
        _darkThreshold = darkThreshold;
    }

    /// <summary>是否正在采集。</summary>
    public bool IsRunning => _isRunning;

    /// <summary>每一帧回调（后台采集线程触发；Mat 所有权移交订阅者，订阅方负责 Dispose）。</summary>
    public event Action<Mat>? FrameCaptured;

    /// <summary>采集错误回调（后台采集线程触发）。</summary>
    public event Action<CameraError, string>? Error;

    /// <summary>
    /// 开始采集。打开失败不抛异常，通过 Error 事件上报并自动重试。
    /// 若已有采集循环在运行，会先将其停止。
    /// </summary>
    public void Start(int deviceIndex, double fps)
    {
        // 停掉已有循环（幂等）
        Stop();

        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _cts = cts;
            _isRunning = true;
            _loopTask = Task.Run(() => CaptureLoop(deviceIndex, fps, cts.Token));
        }
    }

    /// <summary>
    /// 停止采集：取消循环、等待退出、线程安全地释放 VideoCapture。
    /// </summary>
    public void Stop()
    {
        Task? loopTask;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _cts;
            _cts = null;
            loopTask = _loopTask;
            _loopTask = null;
        }

        if (cts is not null)
        {
            cts.Cancel();
            try
            {
                // 等待循环退出（订阅者回调是同步执行的，最多等 5 秒防卡死）
                loopTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // 循环任务中的异常（如订阅者回调抛异常）不影响停止流程
            }
            finally
            {
                cts.Dispose();
            }
        }

        // 循环退出后（或等待超时后）再释放 VideoCapture，避免与采集线程的 Read 竞争。
        // 极端情况下（订阅者回调阻塞超过 5 秒）循环可能仍在运行，此时 Dispose 后的
        // Read 会因句柄失效抛出托管异常，被循环内部的读取失败分支兜底并退出。
        VideoCapture? capture;
        lock (_sync)
        {
            capture = _capture;
            _capture = null;
        }
        if (capture is not null)
        {
            lock (CvCaptureGate)
            {
                capture.Dispose();
            }
        }

        _isRunning = false;
    }

    /// <summary>释放资源（等同 Stop）。</summary>
    public void Dispose()
    {
        Stop();
    }

    /// <summary>
    /// 采集主循环：打开失败自动重试 → 读帧 → 亮度检查 → 投递帧 → 按目标帧率控节奏。
    /// </summary>
    private void CaptureLoop(int deviceIndex, double fps, CancellationToken token)
    {
        // 目标帧间隔（毫秒）；fps <= 0 表示不主动限速
        double frameIntervalMs = fps > 0 ? 1000.0 / fps : 0;

        int consecutiveReadFailures = 0;              // 连续读取失败计数（达到 MaxReadFailures 判定断开）
        int openFailCount = 0;                        // 累计打开失败计数（达到 MaxOpenFailures 上报 Busy）
        bool noCameraReported = false;                // NoCamera 是否已上报（只报一次，避免刷屏）
        bool busyReported = false;                    // Busy 是否已上报（只报一次）
        long lastDarkReportTick = long.MinValue / 2;  // 上次过暗上报时刻（用于 5 秒节流）

        while (!token.IsCancellationRequested)
        {
            // ---------- 阶段 1：确保摄像头处于打开状态 ----------
            VideoCapture? capture;
            lock (_sync)
            {
                capture = _capture;
            }

            if (capture is null || !capture.IsOpened())
            {
                VideoCapture? opened = TryOpenCapture(deviceIndex);
                if (opened is null)
                {
                    openFailCount++;
                    if (!noCameraReported)
                    {
                        noCameraReported = true;
                        // 打开失败无法区分"无设备/被占用/系统权限拒绝"，首次先报 NoCamera
                        Error?.Invoke(CameraError.NoCamera,
                            "无法打开摄像头：请检查设备是否连接，以及 Windows 设置 → 隐私和安全性 → 摄像头 中" +
                            "“允许桌面应用访问摄像头”是否开启（正在自动重试…）");
                    }
                    if (openFailCount >= MaxOpenFailures && !busyReported)
                    {
                        busyReported = true;
                        Error?.Invoke(CameraError.Busy,
                            "摄像头可能被其他程序占用或系统权限拒绝，请关闭占用摄像头的应用，" +
                            "并检查 Windows 设置 → 隐私和安全性 → 摄像头");
                    }
                    // 每 2 秒自动重试，直到 Stop 取消
                    if (SleepInterruptible(OpenRetryIntervalMs, token)) break;
                    continue;
                }

                // 打开成功：恢复各项计数与状态
                openFailCount = 0;
                busyReported = false;
                noCameraReported = false;
                consecutiveReadFailures = 0;
                lock (_sync)
                {
                    VideoCapture? old = _capture;
                    _capture = opened;
                    if (old is not null)
                    {
                        // 旧实例释放必须过全局闸门（与其他线程的 open/read 互斥）
                        lock (CvCaptureGate)
                        {
                            old.Dispose();
                        }
                    }
                }
                capture = opened;
            }

            // ---------- 阶段 2：读取一帧 ----------
            long frameStart = Environment.TickCount64;
            var frame = new Mat();
            bool readOk;
            try
            {
                // 阶段 1 结束后 capture 必然非 null（打开失败的分支已 continue）。
                // Read 必须过全局闸门：与设备枚举（逐个开关设备）互斥，否则原生层堆损坏
                lock (CvCaptureGate)
                {
                    readOk = capture!.Read(frame) && !frame.Empty();
                }
            }
            catch (Exception)
            {
                // DSHOW 后端在设备拔出等场景可能直接抛异常，按读取失败处理
                readOk = false;
            }

            if (!readOk)
            {
                frame.Dispose();
                consecutiveReadFailures++;
                if (consecutiveReadFailures >= MaxReadFailures)
                {
                    consecutiveReadFailures = 0;
                    Error?.Invoke(CameraError.Disconnected, "摄像头已断开，正在重连…");
                    // 释放旧实例并回到阶段 1，重建 VideoCapture 触发重连
                    lock (_sync)
                    {
                        VideoCapture? old = _capture;
                        _capture = null;
                        if (old is not null)
                        {
                            lock (CvCaptureGate)
                            {
                                old.Dispose();
                            }
                        }
                    }
                }
                else
                {
                    // 未达断开阈值时稍作等待再读
                    if (SleepInterruptible(50, token)) break;
                }
                continue;
            }
            consecutiveReadFailures = 0;

            // ---------- 阶段 3：亮度检查（过暗提示 5 秒节流，帧照常投递） ----------
            double luminance = Cv2.Mean(frame).Val0;
            if (luminance < _darkThreshold)
            {
                long now = Environment.TickCount64;
                if (now - lastDarkReportTick >= DarkReportIntervalMs)
                {
                    lastDarkReportTick = now;
                    Error?.Invoke(CameraError.TooDark, "光线过暗，可能影响人脸识别，请改善环境光照");
                }
            }

            // ---------- 阶段 4：投递帧（同步调用；Mat 所有权移交订阅者） ----------
            Action<Mat>? handler = FrameCaptured;
            if (handler is not null)
            {
                handler(frame); // 订阅者处理完返回后才读下一帧（天然限流）
            }
            else
            {
                frame.Dispose(); // 无订阅者时释放，避免泄漏
            }

            // ---------- 阶段 5：按目标帧率控制节奏 ----------
            if (frameIntervalMs > 0)
            {
                long elapsedMs = Environment.TickCount64 - frameStart;
                long remainMs = (long)frameIntervalMs - elapsedMs;
                if (remainMs > 0 && SleepInterruptible((int)remainMs, token))
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 尝试打开指定索引的摄像头（DSHOW 后端）并设置分辨率；失败返回 null。
    /// 打开与属性设置必须过全局闸门（与其他线程的 read/dispose/枚举互斥）。
    /// </summary>
    private static VideoCapture? TryOpenCapture(int deviceIndex)
    {
        VideoCapture? capture = null;
        lock (CvCaptureGate)
        {
            try
            {
                capture = new VideoCapture(deviceIndex, VideoCaptureAPIs.DSHOW);
                if (!capture.IsOpened())
                {
                    capture.Dispose();
                    return null;
                }
                capture.FrameWidth = CaptureWidth;
                capture.FrameHeight = CaptureHeight;
                return capture;
            }
            catch
            {
                // 驱动异常 / 设备瞬间不可用等，统一视为打开失败
                capture?.Dispose();
                return null;
            }
        }
    }

    /// <summary>
    /// 可被取消的等待。返回 true 表示已收到取消信号（调用方应退出循环）。
    /// </summary>
    private static bool SleepInterruptible(int milliseconds, CancellationToken token)
    {
        if (token.IsCancellationRequested) return true;
        try
        {
            return token.WaitHandle.WaitOne(milliseconds);
        }
        catch (ObjectDisposedException)
        {
            // Stop 在等待循环超时后可能已释放 CTS，此时按取消处理
            return true;
        }
    }

    /// <summary>
    /// 枚举可用摄像头（索引 + 名称）。
    /// 索引与名称的对齐是"尽力而为"：OpenCvSharp 无原生设备枚举，
    /// 索引靠探测 0~9（打开即释放、不进入采集），名称按 WMI 返回顺序对齐，不够的用"摄像头 N"。
    /// 注意：探测会逐个短暂打开设备，可能耗时数百毫秒，不要在 UI 线程频繁调用。
    /// </summary>
    public static List<(int Index, string Name)> EnumerateDevices()
    {
        // 1. 探测索引 0~9：能打开即认为存在，探测后立即释放。
        //    整个探测过程必须过全局闸门：枚举会逐个短暂开关物理设备，
        //    与采集线程的 open/read 并发会直接导致原生堆损坏崩溃
        var availableIndexes = new List<int>();
        lock (CvCaptureGate)
        {
            for (int i = 0; i < 10; i++)
            {
                VideoCapture? probe = null;
                try
                {
                    probe = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                    if (probe.IsOpened()) availableIndexes.Add(i);
                }
                catch
                {
                    // 打不开 / 驱动异常都视为该索引无设备
                }
                finally
                {
                    probe?.Dispose();
                }
            }
        }

        // 2. 从 WMI 取设备名，按顺序与探测到的索引对齐
        List<string> names = GetDeviceNamesFromWmi();
        var result = new List<(int Index, string Name)>(availableIndexes.Count);
        for (int k = 0; k < availableIndexes.Count; k++)
        {
            int index = availableIndexes[k];
            string name = k < names.Count ? names[k] : $"摄像头 {index}";
            result.Add((index, name));
        }
        return result;
    }

    /// <summary>
    /// 通过 WMI 查询摄像头设备名（先查 PNPClass='Camera'，无结果再查 PNPClass='Image'）。
    /// 注意：若缺少 System.Management 包此方法需删除（并同步删除 EnumerateDevices 中的调用，
    /// 使其退化为仅探测索引并以"摄像头 N"命名）。
    /// </summary>
    private static List<string> GetDeviceNamesFromWmi()
    {
        var names = new List<string>();
        try
        {
            string[] queries =
            {
                "SELECT * FROM Win32_PnPEntity WHERE PNPClass='Camera'",
                "SELECT * FROM Win32_PnPEntity WHERE PNPClass='Image'"
            };
            foreach (string query in queries)
            {
                using var searcher = new System.Management.ManagementObjectSearcher(query);
                using System.Management.ManagementObjectCollection results = searcher.Get();
                foreach (System.Management.ManagementBaseObject item in results)
                {
                    string? name = item["Name"] as string;
                    item.Dispose();
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
                if (names.Count > 0) break; // 已拿到设备名，无需再查 'Image'
            }
        }
        catch
        {
            // WMI 不可用时返回空列表，由调用方用"摄像头 N"默认名称兜底
        }
        return names;
    }
}
