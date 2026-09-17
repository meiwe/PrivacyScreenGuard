namespace PrivacyScreenGuard.Models;

/// <summary>
/// 健康采样：每帧检测后提取的几何摘要（仅数字，绝不含图像/特征向量）。
/// 由守护引擎在后台采集线程上构建，并经 <see cref="Services.GuardEngine.HealthSampleReady"/> 发布。
/// </summary>
/// <param name="Timestamp">采样时刻（UTC）。</param>
/// <param name="FacePresent">本帧是否存在可用人脸（宽松姿态模式下侧脸不算可用，复用引擎判定）。</param>
/// <param name="MaxFaceWidthRatio">所有检出脸中最大的人脸框宽/图像宽（0~1），用于用眼距离估计。</param>
/// <param name="PitchRatio">低头比例（正脸时有效；侧脸或关键点/几何异常为 null）。</param>
/// <param name="IsFrontal">是否正脸（复用引擎的 FacePose.IsFrontalEnough 判定）。</param>
public sealed record HealthSample(
    DateTime Timestamp,
    bool FacePresent,
    double MaxFaceWidthRatio,
    double? PitchRatio,
    bool IsFrontal);
