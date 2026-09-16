using System;
using System.IO;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 模型文件定位：依次查找 可执行目录\models、当前工作目录\models。
/// 开发期与单文件发布后均可正确找到 models 目录。
/// </summary>
public static class ModelLocator
{
    /// <summary>YuNet 人脸检测模型文件名。</summary>
    public const string YuNetFile = "face_detection_yunet_2023mar.onnx";

    /// <summary>SFace 人脸识别模型文件名。</summary>
    public const string SFaceFile = "face_recognition_sface_2021dec.onnx";

    /// <summary>
    /// 查找模型目录；找不到返回 null。
    /// </summary>
    public static string? FindModelsDir()
    {
        // 1. exe 同目录（发布后）
        string exeDir = AppContext.BaseDirectory;
        string p1 = Path.Combine(exeDir, "models");
        if (Directory.Exists(p1)) return p1;

        // 2. 当前工作目录（开发调试）
        string p2 = Path.Combine(Directory.GetCurrentDirectory(), "models");
        if (Directory.Exists(p2)) return p2;

        // 3. exe 目录上溯两级（开发目录结构：src/xxx/bin/Debug → 仓库根/models）
        string? up = Path.GetFullPath(Path.Combine(exeDir, "..", "..", ".."));
        if (!string.IsNullOrEmpty(up))
        {
            string p3 = Path.Combine(up, "models");
            if (Directory.Exists(p3)) return p3;
        }

        return null;
    }

    /// <summary>
    /// 获取指定模型文件完整路径；缺失返回 null。
    /// </summary>
    public static string? FindModelFile(string fileName)
    {
        string? dir = FindModelsDir();
        if (dir == null) return null;
        string path = Path.Combine(dir, fileName);
        return File.Exists(path) ? path : null;
    }
}
