using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace PrivacyScreenGuard.Models;

/// <summary>
/// 单个人脸检测结果：人脸框 + 5 个关键点 + 置信度。
/// 关键点顺序（YuNet 约定）：0=左眼 1=右眼 2=鼻尖 3=左嘴角 4=右嘴角。
/// </summary>
public sealed class FaceInfo
{
    /// <summary>人脸矩形框（相对原始帧坐标）。</summary>
    public Rect Box { get; init; }

    /// <summary>5 个关键点坐标。</summary>
    public Point2f[] Landmarks { get; init; } = Array.Empty<Point2f>();

    /// <summary>检测置信度（0~1）。</summary>
    public float Score { get; init; }
}
