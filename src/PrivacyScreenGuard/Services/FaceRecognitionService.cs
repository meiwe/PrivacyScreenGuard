using System;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using PrivacyScreenGuard.Models;
// 主项目同时启用 WinForms（System.Drawing.Size），显式消歧
using Size = OpenCvSharp.Size;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 基于 SFace 的人脸特征服务：5 关键点对齐裁剪到 112×112 → 提取 128 维特征。
/// 非线程安全（单线程调用）。
/// 说明：本项目使用的 OpenCvSharp 4.13.0.20260627 尚无 FaceRecognizerSF 包装类，
/// 因此直接用 CvDnn.Net 加载 SFace ONNX 自行实现对齐与特征提取，
/// 预处理参数与 OpenCV 官方 SFace 实现（modules/objdetect/src/face_recognize.cpp）保持一致，数值结果等价。
/// </summary>
public sealed class FaceRecognitionService : IFaceRecognizer
{
    /// <summary>特征向量维度（SFace 模型输出 128 维）。</summary>
    private const int FeatureLength = 128;

    /// <summary>对齐后人脸图尺寸（ArcFace 标准）。</summary>
    private const int AlignedSize = 112;

    /// <summary>
    /// ArcFace 标准参考关键点（按 YuNet 顺序：左眼、右眼、鼻尖、左嘴角、右嘴角），
    /// 与 OpenCV SFace 官方实现中的目标点完全一致。
    /// </summary>
    private static readonly Point2f[] ReferenceLandmarks =
    {
        new(38.2946f, 51.6963f),
        new(73.5318f, 51.5014f),
        new(56.0252f, 71.7366f),
        new(41.5493f, 92.3655f),
        new(70.7299f, 92.2041f),
    };

    private readonly Net _net;

    public FaceRecognitionService()
    {
        string? modelPath = ModelLocator.FindModelFile(ModelLocator.SFaceFile)
            ?? throw new InvalidOperationException(
                $"未找到 SFace 人脸识别模型文件（{ModelLocator.SFaceFile}），请先运行 tools/download_models.py 下载模型");

        try
        {
            _net = CvDnn.ReadNetFromOnnx(modelPath);
        }
        catch (OpenCVException ex)
        {
            throw new InvalidOperationException("加载 SFace 人脸识别模型失败，请确认模型文件完整有效", ex);
        }
    }

    /// <summary>
    /// 按 5 关键点将人脸对齐裁剪到 112×112（BGR）。
    /// 返回的 Mat 所有权归调用者（调用者负责 Dispose）。
    /// </summary>
    public Mat AlignCrop(Mat src, FaceInfo face)
    {
        if (face.Landmarks.Length < 5)
        {
            throw new ArgumentException("人脸关键点不足 5 个，无法进行对齐裁剪", nameof(face));
        }

        var srcPoints = new Point2f[5];
        for (int k = 0; k < 5; k++)
        {
            srcPoints[k] = face.Landmarks[k];
        }

        // 估计相似变换（4 自由度：旋转+平移+统一缩放），
        // 与 OpenCV SFace 官方 alignCrop 内部的 Umeyama 闭式解等价
        Mat warp;
        using var inliers = new Mat();
        try
        {
            warp = Cv2.EstimateAffinePartial2D(
                InputArray.Create(srcPoints), InputArray.Create(ReferenceLandmarks),
                inliers, RobustEstimationAlgorithms.RANSAC,
                3.0, 2000UL, 0.99, 10UL);
        }
        catch (OpenCVException ex)
        {
            throw new InvalidOperationException("人脸对齐失败，请确认人脸检测结果包含 5 个有效关键点", ex);
        }

        // 变换估计失败时返回空矩阵
        if (warp is null || warp.Empty())
        {
            warp?.Dispose();
            throw new InvalidOperationException("人脸对齐失败：无法从关键点估计相似变换");
        }

        var aligned = new Mat();
        Cv2.WarpAffine(src, aligned, warp, new Size(AlignedSize, AlignedSize),
            InterpolationFlags.Linear, BorderTypes.Constant, null);
        warp.Dispose();
        return aligned;
    }

    /// <summary>
    /// 从已对齐的 112×112 人脸图提取 128 维特征向量。
    /// </summary>
    public float[] ExtractFeature(Mat aligned112)
    {
        // 与 OpenCV SFace 官方 feature() 一致的预处理：
        // scale=1、尺寸 112×112、均值 0、swapRB=true、crop=false
        using var blob = CvDnn.BlobFromImage(aligned112, 1.0, new Size(AlignedSize, AlignedSize),
            new Scalar(0, 0, 0), true, false);
        _net.SetInput(blob, "");
        using var feature = _net.Forward(null);

        // 输出可能是 (1,128) 或 (1,1,1,128)，统一整理成 1 行后逐个读取
        using var flat = feature.Reshape(1, 1);
        var result = new float[FeatureLength];
        for (int i = 0; i < FeatureLength; i++)
        {
            result[i] = flat.At<float>(0, i);
        }
        return result;
    }

    /// <summary>
    /// 余弦相似度：a·b / (|a|·|b|)，范围约 -1~1，越大越相似；任一向量模长近 0 时返回 0。
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        if (denom < 1e-12) return 0f;
        return (float)(dot / denom);
    }

    /// <summary>释放模型资源。</summary>
    public void Dispose()
    {
        _net.Dispose();
    }
}
