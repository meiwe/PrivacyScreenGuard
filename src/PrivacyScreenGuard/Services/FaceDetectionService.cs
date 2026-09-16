using System;
using System.Collections.Generic;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using PrivacyScreenGuard.Models;
// 主项目同时启用 WinForms（System.Drawing.Size），显式消歧
using Size = OpenCvSharp.Size;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 基于 YuNet 的人脸检测服务。非线程安全（单线程调用）。
/// 说明：本项目使用的 OpenCvSharp 4.13.0.20260627 中 FaceDetectorYN 不提供 SetInputSize/GetInputSize，
/// 推理输入尺寸只能在 Create 时指定，因此帧尺寸变化时需重建检测器
/// （摄像头分辨率固定时仅发生一两次，属一次性开销）。
/// </summary>
public sealed class FaceDetectionService : IFaceDetector
{
    /// <summary>置信度阈值：低于该值的结果丢弃（与 Create 时传入阈值一致的双保险）。</summary>
    private const float ScoreThreshold = 0.6f;

    /// <summary>NMS（非极大值抑制）阈值。</summary>
    private const float NmsThreshold = 0.3f;

    /// <summary>NMS 前保留的候选框上限。</summary>
    private const int TopK = 5000;

    private readonly string _modelPath;
    private FaceDetectorYN? _detector;

    /// <summary>当前推理输入尺寸（与 _detector 对应）。</summary>
    private Size _currentInputSize;

    public FaceDetectionService()
    {
        _modelPath = ModelLocator.FindModelFile(ModelLocator.YuNetFile)
            ?? throw new InvalidOperationException(
                $"未找到 YuNet 人脸检测模型文件（{ModelLocator.YuNetFile}），请先运行 tools/download_models.py 下载模型");

        // 构造时先按占位尺寸创建一次，尽早暴露模型缺失/损坏问题；
        // 首帧尺寸不同时会在 Detect 中按实际尺寸重建
        RebuildDetector(new Size(320, 320));
    }

    /// <summary>
    /// 检测一帧中的所有人脸。
    /// </summary>
    public List<FaceInfo> Detect(Mat bgrFrame)
    {
        // 本版本不支持动态 SetInputSize：帧尺寸变化时重建检测器
        if (_detector is null ||
            _currentInputSize.Width != bgrFrame.Width ||
            _currentInputSize.Height != bgrFrame.Height)
        {
            RebuildDetector(new Size(bgrFrame.Width, bgrFrame.Height));
        }

        var faces = new List<FaceInfo>();
        using var raw = new Mat();
        try
        {
            _detector!.Detect(bgrFrame, raw);
        }
        catch (OpenCVException ex)
        {
            // 不吞异常：包装后向上传递，由调用方决定如何处理
            throw new InvalidOperationException("人脸检测推理失败，请确认模型文件完整且输入图像有效", ex);
        }

        // 空结果：本帧没有人脸
        if (raw.Empty() || raw.Rows == 0 || raw.Cols == 0) return faces;

        // YuNet 输出为 CV_32FC1，每张脸 15 个 float：
        // 0..3 = x,y,w,h；4..13 = 5 个关键点 (x,y)；14 = score。
        // 不同版本的排布可能为 (N 行,15 列) 或 (1 行,15N 列)，两种布局在此统一兼容：
        int faceCount;
        bool rowPerFace; // true：每行一张脸；false：一行 15 列为一张脸
        if (raw.Cols == 15)
        {
            faceCount = raw.Rows;
            rowPerFace = true;
        }
        else
        {
            faceCount = raw.Cols / 15;
            rowPerFace = false;
        }

        for (int i = 0; i < faceCount; i++)
        {
            float x = ReadValue(raw, i, rowPerFace, 0);
            float y = ReadValue(raw, i, rowPerFace, 1);
            float w = ReadValue(raw, i, rowPerFace, 2);
            float h = ReadValue(raw, i, rowPerFace, 3);
            float score = ReadValue(raw, i, rowPerFace, 14);

            // 双保险：低于阈值的丢弃
            if (score < ScoreThreshold) continue;

            var landmarks = new Point2f[5];
            for (int k = 0; k < 5; k++)
            {
                float lx = ReadValue(raw, i, rowPerFace, 4 + k * 2);
                float ly = ReadValue(raw, i, rowPerFace, 4 + k * 2 + 1);
                landmarks[k] = new Point2f(lx, ly);
            }

            faces.Add(new FaceInfo
            {
                Box = ClampToFrame(x, y, w, h, bgrFrame.Width, bgrFrame.Height),
                Landmarks = landmarks,
                Score = score,
            });
        }
        return faces;
    }

    /// <summary>
    /// 按指定输入尺寸重建 YuNet 检测器（模型加载失败时抛出带中文说明的异常）。
    /// </summary>
    private void RebuildDetector(Size inputSize)
    {
        try
        {
            _detector?.Dispose();
            _detector = FaceDetectorYN.Create(_modelPath, "", inputSize, ScoreThreshold, NmsThreshold, TopK,
                OpenCvSharp.Dnn.Backend.DEFAULT, OpenCvSharp.Dnn.Target.CPU);
            _currentInputSize = inputSize;
        }
        catch (OpenCVException ex)
        {
            throw new InvalidOperationException("初始化 YuNet 人脸检测器失败，请确认模型文件完整有效", ex);
        }
    }

    /// <summary>
    /// 按布局读取第 faceIndex 张脸的第 fieldIndex 个 float 值。
    /// </summary>
    private static float ReadValue(Mat mat, int faceIndex, bool rowPerFace, int fieldIndex)
    {
        return rowPerFace
            ? mat.At<float>(faceIndex, fieldIndex)
            : mat.At<float>(0, faceIndex * 15 + fieldIndex);
    }

    /// <summary>
    /// 把 (x,y,w,h) 取整并裁剪进帧边界，避免越界矩形。
    /// </summary>
    private static Rect ClampToFrame(float x, float y, float w, float h, int frameWidth, int frameHeight)
    {
        int left = Math.Clamp((int)x, 0, frameWidth);
        int top = Math.Clamp((int)y, 0, frameHeight);
        int right = Math.Clamp((int)x + (int)w, 0, frameWidth);
        int bottom = Math.Clamp((int)y + (int)h, 0, frameHeight);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>释放模型资源。</summary>
    public void Dispose()
    {
        _detector?.Dispose();
        _detector = null;
    }
}
