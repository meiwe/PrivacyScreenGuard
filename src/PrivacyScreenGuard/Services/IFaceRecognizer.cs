using System;
using OpenCvSharp;
using PrivacyScreenGuard.Models;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 人脸特征服务契约（SFace）：关键点对齐到 112×112 → 提取 128 维特征。
/// </summary>
public interface IFaceRecognizer : IDisposable
{
    /// <summary>
    /// 按 5 关键点将人脸对齐裁剪到 112×112。
    /// </summary>
    /// <param name="src">原始 BGR 帧。</param>
    /// <param name="face">检测结果（含关键点）。</param>
    /// <returns>对齐后的 112×112 BGR 图像，调用者负责 Dispose。</returns>
    Mat AlignCrop(Mat src, FaceInfo face);

    /// <summary>
    /// 提取 128 维特征向量（已归一化与否由实现保证：与余弦相似度兼容）。
    /// </summary>
    float[] ExtractFeature(Mat aligned112);

    /// <summary>
    /// 计算余弦相似度（范围约 -1~1，越大越相似）。
    /// </summary>
    static abstract float CosineSimilarity(float[] a, float[] b);
}
