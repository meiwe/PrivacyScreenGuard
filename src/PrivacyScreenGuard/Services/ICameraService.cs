using System;
using OpenCvSharp;
using PrivacyScreenGuard.Models;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 摄像头采集服务契约：后台线程采集，仅抛帧给订阅者，不做推理、不保存画面。
/// 帧的 Mat 所有权归订阅者（订阅者负责 Dispose）。
/// </summary>
public interface ICameraService : IDisposable
{
    /// <summary>是否正在采集。</summary>
    bool IsRunning { get; }

    /// <summary>开始采集。失败时通过 Error 事件上报而不是抛异常。</summary>
    void Start(int deviceIndex, double fps);

    /// <summary>停止采集。</summary>
    void Stop();

    /// <summary>每一帧回调（在后台采集线程触发，订阅方应快速处理）。</summary>
    event Action<Mat>? FrameCaptured;

    /// <summary>采集错误回调（断开、占用、过暗等）。</summary>
    event Action<CameraError, string>? Error;
}
