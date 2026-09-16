using System;
using System.Collections.Generic;
using System.Threading;
using OpenCvSharp;
using PrivacyScreenGuard.Models;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 守护引擎：订阅摄像头帧 → YuNet 人脸检测 → SFace 特征提取 → 与主人模板余弦比对 →
/// 状态机判定 → 触发遮罩动作事件。
///
/// <para><b>所有权约定</b>：引擎负责 Dispose 构造时注入的三个服务
/// （ICameraService / IFaceDetector / IFaceRecognizer），外部注入后不要再自行释放。</para>
///
/// <para><b>线程模型</b>：全部帧处理在摄像头后台采集线程上同步执行（CameraService 的采集循环
/// 同步调用帧回调，天然限流，引擎内部不再另开线程）。所有事件
/// （<see cref="MaskActionRequested"/> / <see cref="StatusChanged"/> / <see cref="EngineError"/>）
/// 均在后台线程触发——WPF 订阅方必须自行通过 Dispatcher 封送到 UI 线程后再操作 UI。</para>
///
/// <para><b>隐私要求</b>：任何 Mat 不得保存到磁盘、不得缓存超过一帧、处理完立即释放；
/// 对齐产生的中间 Mat 在每张人脸处理完后立即 Dispose。</para>
/// </summary>
public sealed class GuardEngine : IDisposable
{
    /// <summary>状态文本节流间隔（毫秒）：普通帧状态至少间隔该时长才上报一次。</summary>
    private const int StatusThrottleMs = 500;

    /// <summary>推理异常上报节流间隔（毫秒）：至少间隔 10 秒报一次，避免刷屏。</summary>
    private const int InferenceErrorThrottleMs = 10_000;

    /// <summary>浮点比较容差（用于判断帧率是否发生变化）。</summary>
    private const double FpsEpsilon = 0.001;

    /// <summary>摄像头采集服务（引擎所有，Dispose 时释放）。</summary>
    private readonly ICameraService _camera;

    /// <summary>人脸检测服务（引擎所有，Dispose 时释放；非线程安全，仅在帧回调线程使用）。</summary>
    private readonly IFaceDetector _detector;

    /// <summary>人脸特征服务（引擎所有，Dispose 时释放；非线程安全，仅在帧回调线程使用）。</summary>
    private readonly IFaceRecognizer _recognizer;

    /// <summary>保护下方可变字段（配置、模板、状态机、节流时间戳）的锁。</summary>
    private readonly object _sync = new();

    // ---- 配置（受 _sync 保护；由 Configure 更新） ----
    private double _ownerThreshold = 0.55;             // 主人识别余弦阈值
    private double _captureFps = 8;                    // 采集帧率（Start 时传给摄像头）
    private int _cameraIndex = 0;                      // 摄像头索引（Start 时传给摄像头）
    private double _darkThreshold = 18;                // 暗光阈值（仅记录；实际生效需重建 CameraService）
    private GuardStateMachine _stateMachine = new();   // 状态机（Configure 时整体重建，旧的丢弃）

    // ---- 运行状态 ----
    private bool _paused;                              // 是否已暂停（受 _sync 保护）
    private float[]? _ownerTemplate;                   // 主人模板（SetOwnerTemplate 存入副本）
    private volatile int _disposedFlag;                // 0=可用 1=已释放（Interlocked 保证仅 Dispose 一次）
    private long _lastStatusTick = long.MinValue / 2;  // 上次状态上报时刻（节流用，受 _sync 保护）
    private long _lastInferenceErrorTick = long.MinValue / 2; // 上次推理异常上报时刻（受 _sync 保护）

    /// <summary>
    /// 遮罩动作事件。<b>后台线程触发</b>，WPF 订阅方需经 Dispatcher 封送到 UI 线程后再操作窗口。
    /// </summary>
    public event Action<MaskAction>? MaskActionRequested;

    /// <summary>
    /// 状态文本事件（节流 ≥500ms 一次；启动/停止/暂停/遮罩切换等关键状态立即上报）。
    /// <b>后台线程触发</b>，WPF 订阅方需经 Dispatcher 封送。
    /// </summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// 引擎错误事件：转发摄像头错误；引擎自身推理异常包装为 CameraError.Unknown + 中文消息（10 秒节流）。
    /// <b>后台线程触发</b>，WPF 订阅方需经 Dispatcher 封送。
    /// </summary>
    public event Action<CameraError, string>? EngineError;

    /// <summary>
    /// 创建守护引擎。
    /// </summary>
    /// <param name="camera">摄像头采集服务（引擎负责 Dispose）。</param>
    /// <param name="detector">人脸检测服务（引擎负责 Dispose）。</param>
    /// <param name="recognizer">人脸特征服务（引擎负责 Dispose）。</param>
    public GuardEngine(ICameraService camera, IFaceDetector detector, IFaceRecognizer recognizer)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));

        // 订阅摄像头帧与错误（均在后台采集线程触发）
        _camera.FrameCaptured += OnFrameCaptured;
        _camera.Error += OnCameraError;
    }

    /// <summary>是否正在采集监控（转发摄像头运行状态）。</summary>
    public bool IsRunning => _camera.IsRunning;

    /// <summary>
    /// 应用设置：更新阈值/触发延迟/恢复延迟/无人策略/帧率/暗光阈值，
    /// 并按新参数整体重建 GuardStateMachine（旧的直接丢弃）。
    /// 帧率变化时若正在运行，则先 Stop 再以新帧率 Start（仅重启采集循环，无需重启摄像头硬件）；
    /// 暗光阈值由 CameraService 构造参数决定，此处仅记录，无需重启摄像头。
    /// </summary>
    public void Configure(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        double oldFps;
        bool wasMaskActive;
        lock (_sync)
        {
            oldFps = _captureFps;
            wasMaskActive = _stateMachine.MaskActive;

            _ownerThreshold = settings.OwnerThreshold;
            _captureFps = settings.CaptureFps;
            _cameraIndex = settings.CameraIndex;
            _darkThreshold = settings.DarkThreshold;

            // 按新参数重建状态机（旧的丢弃），保证触发/恢复延迟与无人策略立即生效
            _stateMachine = new GuardStateMachine(settings.TriggerDelayMs, settings.RecoverDelayMs,
                settings.NoFacePolicy);
        }

        // 帧率变化且正在运行 → 先 Stop 再用新帧率 Start（在锁外执行，
        // 因为 camera.Stop 会同步等待采集循环退出，而帧回调需要获取 _sync 做快照，避免互等）
        bool fpsChanged = Math.Abs(oldFps - settings.CaptureFps) > FpsEpsilon;
        if (fpsChanged && _camera.IsRunning)
        {
            _camera.Stop();
            _camera.Start(settings.CameraIndex, settings.CaptureFps);
        }

        // 重建后的新状态机 MaskActive=false，若旧状态机遮罩正激活且此后持续为安全帧，
        // 新状态机将永远不会发出 Hide，导致 UI 遮罩卡死——此处补发一次 Hide，
        // 后续状态机重新判定，必要时会再次 Show（订阅方对 Show/Hide 应做幂等处理）
        if (wasMaskActive)
        {
            MaskActionRequested?.Invoke(MaskAction.Hide);
        }
    }

    /// <summary>
    /// 设置/更新主人特征模板（内部保存副本，外部数组后续修改不影响引擎）。
    /// 有模板后才能调用 <see cref="Start"/>。
    /// </summary>
    public void SetOwnerTemplate(float[] feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        // 保存副本，避免外部持有者修改同一数组导致比对结果漂移
        var copy = new float[feature.Length];
        Array.Copy(feature, copy, feature.Length);

        lock (_sync)
        {
            _ownerTemplate = copy;
        }
    }

    /// <summary>
    /// 启动监控：以最近一次 Configure 得到的摄像头索引与帧率开始采集。
    /// 未设置主人模板时通过 EngineError 上报且不启动。
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_ownerTemplate is null)
            {
                EngineError?.Invoke(CameraError.Unknown, "尚未注册主人，请先完成主人注册后再启动守护");
                return;
            }
        }
        StartCore();
        RaiseStatus("监控中", force: true);
    }

    /// <summary>
    /// 停止监控：停止摄像头采集；若遮罩当前激活，先补发 MaskActionRequested(Hide) 让 UI 收起遮罩。
    /// 说明：本方法不重置状态机（仅 Pause 会 Reset），重新 Start 后若首帧为安全帧，
    /// 状态机会因内部 MaskActive 仍为 true 而再发一次 Hide，订阅方应幂等处理。
    /// </summary>
    public void Stop()
    {
        StopCore();
        RaiseStatus("守护已停止", force: true);
    }

    /// <summary>暂停监控：等价 Stop 逻辑，并额外重置状态机、上报"已暂停"。</summary>
    public void Pause()
    {
        StopCore();

        lock (_sync)
        {
            _paused = true;
            _stateMachine.Reset();
        }

        RaiseStatus("已暂停", force: true);
    }

    /// <summary>恢复监控：等价 Start 逻辑（无模板时报 EngineError），并上报"监控中"。</summary>
    public void Resume()
    {
        lock (_sync)
        {
            if (_ownerTemplate is null)
            {
                EngineError?.Invoke(CameraError.Unknown, "尚未注册主人，无法恢复守护");
                return;
            }
        }
        StartCore();
        RaiseStatus("监控中", force: true);
    }

    /// <summary>
    /// 释放引擎：停止采集（遮罩激活时先发 Hide）、退订事件，并负责 Dispose 注入的三个服务
    /// （所有权约定见类注释）。可安全重复调用。
    /// </summary>
    public void Dispose()
    {
        // Interlocked 保证并发 Dispose 只执行一次
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
        {
            return;
        }

        StopCore();     // 遮罩激活时先发 Hide
        _camera.Stop();

        // 退订事件：此后摄像头（若仍在退出过程中）残留的帧回调会因 _disposedFlag 直接释放帧返回
        _camera.FrameCaptured -= OnFrameCaptured;
        _camera.Error -= OnCameraError;

        // 所有权约定：引擎负责释放注入的三个服务
        _detector.Dispose();
        _recognizer.Dispose();
        _camera.Dispose();
    }

    /// <summary>
    /// 帧回调入口（摄像头后台采集线程）：帧所有权归引擎，无论任何路径都必须释放。
    /// 绝不向采集线程抛出异常（否则采集循环会终止）。
    /// </summary>
    private void OnFrameCaptured(Mat frame)
    {
        if (_disposedFlag != 0)
        {
            frame.Dispose(); // Dispose 已开始/完成：不再处理，直接释放
            return;
        }

        try
        {
            ProcessFrame(frame);
        }
        catch (Exception ex)
        {
            // 推理等所有异常统一包装上报（10 秒节流），绝不抛出
            RaiseInferenceError(ex);
        }
        finally
        {
            frame.Dispose(); // 隐私要求：本帧处理完立即释放，不缓存、不落盘
        }
    }

    /// <summary>
    /// 单帧处理：暂停检查 → 人脸检测 → 逐脸对齐/提特征/与主人模板比对 → 状态机判定 → 触发事件。
    /// </summary>
    private void ProcessFrame(Mat frame)
    {
        // 锁内只做配置快照（微秒级），推理在锁外执行，
        // 避免 Configure/Pause 持锁调用 camera.Stop 时与帧回调互等
        bool paused;
        double threshold;
        float[]? template;
        lock (_sync)
        {
            paused = _paused;
            threshold = _ownerThreshold;
            template = _ownerTemplate;
        }

        // 已暂停（或尚无模板）→ 本帧直接丢弃
        if (paused || template is null)
        {
            return;
        }

        // 1. 人脸检测
        List<FaceInfo> faces = _detector.Detect(frame);

        // 2. 逐人脸比对，得到本帧观测结论
        FrameObservation observation;
        int faceCount = faces.Count;
        double bestSim = double.NegativeInfinity;

        if (faceCount == 0)
        {
            observation = FrameObservation.NoFace;
        }
        else
        {
            bool ownerFound = false;
            foreach (FaceInfo face in faces)
            {
                // 对齐产生的中间 Mat 用完立即 Dispose（隐私要求）
                Mat aligned = _recognizer.AlignCrop(frame, face);
                try
                {
                    float[] feature = _recognizer.ExtractFeature(aligned);

                    // 余弦相似度：IFaceRecognizer.CosineSimilarity 为 static abstract 成员，
                    // 无法通过接口引用调用，故使用唯一具体实现的静态方法（纯数学运算，与注入实例无关）
                    float sim = FaceRecognitionService.CosineSimilarity(feature, template);

                    // 相似度异常（NaN）按低于阈值处理（-1 低于最小阈值 0.3）
                    if (float.IsNaN(sim))
                    {
                        sim = -1f;
                    }

                    if (sim > bestSim)
                    {
                        bestSim = sim;
                    }
                    if (sim >= threshold)
                    {
                        ownerFound = true;
                    }
                }
                finally
                {
                    aligned.Dispose();
                }
            }

            observation = ownerFound ? FrameObservation.OwnerPresent : FrameObservation.FaceButNoOwner;
        }

        // 3. 状态机判定（锁内：Configure 可能并发重建状态机）
        MaskAction action;
        lock (_sync)
        {
            action = _stateMachine.Process(observation, Environment.TickCount64);
        }

        // 4. 遮罩动作（后台线程触发，订阅方需 Dispatcher 封送）
        if (action != MaskAction.None && _disposedFlag == 0)
        {
            MaskActionRequested?.Invoke(action);
            RaiseStatus(action == MaskAction.Show ? "检测到陌生人，已触发隐私遮罩" : "主人已回归，解除隐私遮罩",
                force: true);
        }

        // 5. 常规状态文本（500ms 节流）
        RaiseStatus(observation switch
        {
            FrameObservation.NoFace => "监控中：未检测到人脸",
            FrameObservation.OwnerPresent => $"监控中：{faceCount} 张人脸，最高相似度 {bestSim:F2}",
            _ => $"监控中：{faceCount} 张人脸，未匹配到主人（最高相似度 {bestSim:F2}）"
        });
    }

    /// <summary>
    /// 摄像头错误转发（后台线程）：原样转发 EngineError，并同步一条中文状态文本。
    /// </summary>
    private void OnCameraError(CameraError error, string message)
    {
        if (_disposedFlag != 0)
        {
            return;
        }

        EngineError?.Invoke(error, message);

        // 摄像头侧自身已有节流（如过暗 5 秒一次、无设备只报一次），此处 force 立即反映到状态栏
        RaiseStatus(error switch
        {
            CameraError.NoCamera => "摄像头未连接",
            CameraError.Busy => "摄像头被其他程序占用",
            CameraError.Disconnected => "摄像头已断开，正在重连",
            CameraError.TooDark => "光线过暗，可能影响人脸识别",
            _ => "摄像头异常"
        }, force: true);
    }

    /// <summary>
    /// 启动核心：清除暂停标记，并以锁内快照的索引与帧率启动摄像头。
    /// </summary>
    private void StartCore()
    {
        int cameraIndex;
        double fps;
        lock (_sync)
        {
            _paused = false;
            cameraIndex = _cameraIndex;
            fps = _captureFps;
        }
        _camera.Start(cameraIndex, fps);
    }

    /// <summary>
    /// 停止核心（Stop/Pause/Dispose 共用）：若遮罩激活先补发 Hide，再停止摄像头采集。
    /// </summary>
    private void StopCore()
    {
        bool maskActive;
        lock (_sync)
        {
            maskActive = _stateMachine.MaskActive;
        }

        // 遮罩激活时先发 Hide（订阅方需 Dispatcher 封送，且应幂等处理）
        if (maskActive)
        {
            MaskActionRequested?.Invoke(MaskAction.Hide);
        }

        _camera.Stop();
    }

    /// <summary>
    /// 上报状态文本。普通帧状态按 500ms 节流；关键状态（force=true）立即上报并重置节流窗口。
    /// 事件在锁外触发，避免订阅方回调再进入引擎方法时与锁互等。
    /// </summary>
    private void RaiseStatus(string message, bool force = false)
    {
        if (_disposedFlag != 0)
        {
            return;
        }

        lock (_sync)
        {
            long now = Environment.TickCount64;
            if (!force && now - _lastStatusTick < StatusThrottleMs)
            {
                return; // 节流窗口内，丢弃本次普通状态
            }
            _lastStatusTick = now;
        }

        StatusChanged?.Invoke(message);
    }

    /// <summary>
    /// 上报推理异常：包装为 CameraError.Unknown + 中文消息，10 秒节流，绝不抛出。
    /// </summary>
    private void RaiseInferenceError(Exception ex)
    {
        if (_disposedFlag != 0)
        {
            return;
        }

        lock (_sync)
        {
            long now = Environment.TickCount64;
            if (now - _lastInferenceErrorTick < InferenceErrorThrottleMs)
            {
                return; // 10 秒内已报过，丢弃
            }
            _lastInferenceErrorTick = now;
        }

        EngineError?.Invoke(CameraError.Unknown, $"守护推理发生异常：{ex.Message}");
    }
}
