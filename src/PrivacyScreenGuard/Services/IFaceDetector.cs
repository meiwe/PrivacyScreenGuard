using System;
using System.Collections.Generic;
using OpenCvSharp;
using PrivacyScreenGuard.Models;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 人脸检测服务契约（YuNet）：输入 BGR 帧，输出全部人脸（框 + 5 关键点 + 置信度）。
/// 实现必须非线程安全（单线程调用），加载失败时抛出带中文说明的异常。
/// </summary>
public interface IFaceDetector : IDisposable
{
    /// <summary>
    /// 检测一帧中的所有人脸。
    /// </summary>
    List<FaceInfo> Detect(Mat bgrFrame);
}
