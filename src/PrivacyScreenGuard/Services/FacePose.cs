using System;
using OpenCvSharp;
using PrivacyScreenGuard.Models;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 人脸姿态/位置判定：利用 YuNet 的 5 关键点区分"正脸"与"侧脸/边缘脸"。
///
/// 背景：主人扭头看侧屏时，主摄像头拍到的是侧脸，相似度会下降而被误判为陌生人。
/// 本类提供两个判据，供守护引擎把"不可靠观测"从遮罩判定中剔除：
/// - IsFrontalEnough：关键点几何判断是否接近正脸（鼻尖相对双眼中心的水平偏移比例）。
/// - IsConfidentRegion：人脸是否位于画面中部（贴边的人脸信息不全，不足以判定身份）。
/// </summary>
public static class FacePose
{
    /// <summary>鼻尖水平偏移占人脸宽度的最大容忍比例（超过即视为侧脸）。</summary>
    /// <remarks>
    /// 正脸时鼻尖大致位于双眼中心（偏差 < 0.05）；明显扭头时偏移可达 0.25 以上。
    /// 取 0.18 兼顾灵敏度：轻微转头（找鼠标/瞥一眼）不误伤，看侧屏（持续大角度）能识别。
    /// </remarks>
    public const double MaxYawShiftRatio = 0.18;

    /// <summary>注册侧脸模板所需的最小水平偏移比例（保证采到的是"够侧"的姿态）。</summary>
    public const double SideYawShiftRatio = 0.20;

    /// <summary>
    /// "大角度侧脸"门槛（约 60°）：侧脸阶段第二张模板要求达到该偏移，
    /// 覆盖主人大幅扭头看侧屏的场景（90° 完全侧面五官不可见，检测会失效，不采集）。
    /// </summary>
    public const double StrongSideYawShiftRatio = 0.34;

    /// <summary>人脸框贴近画面边缘的比例阈值：框边缘距画面边界小于此比例视为"贴边"。</summary>
    public const double EdgeMarginRatio = 0.04;

    /// <summary>人脸朝向类别（按鼻尖相对双眼中心的水平偏移方向划分）。</summary>
    public enum PoseKind
    {
        /// <summary>正脸（偏移在容忍范围内）或姿态不可判定。</summary>
        FrontalOrUnknown,

        /// <summary>侧脸-画面左向（鼻尖偏向画面左侧；对应用户向自己的右侧转头）。</summary>
        LookingLeftInFrame,

        /// <summary>侧脸-画面右向（鼻尖偏向画面右侧；对应用户向自己的左侧转头）。</summary>
        LookingRightInFrame
    }

    /// <summary>
    /// 计算鼻尖相对双眼中心的水平偏移（占人脸框宽度的比例，带符号）。
    /// 正值 = 鼻尖偏向画面右侧（用户向自己的左侧转头）；负值 = 偏向画面左侧。
    /// 关键点不完整或人脸框异常时返回 0。
    /// </summary>
    public static double GetHorizontalShift(FaceInfo face)
    {
        if (face.Landmarks is not { Length: 5 } || face.Box.Width <= 0)
        {
            return 0;
        }

        double eyeCenterX = (face.Landmarks[0].X + face.Landmarks[1].X) / 2.0;
        return (face.Landmarks[2].X - eyeCenterX) / face.Box.Width;
    }

    /// <summary>
    /// 判定人脸朝向类别：正脸（含不可判定）/ 画面左向侧脸 / 画面右向侧脸。
    /// 供注册向导分阶段采集（"向左转头""向右转头"）时按姿态过滤帧。
    /// </summary>
    public static PoseKind GetPoseKind(FaceInfo face)
    {
        if (face.Landmarks is not { Length: 5 } || face.Box.Width <= 0)
        {
            return PoseKind.FrontalOrUnknown;
        }

        double shift = GetHorizontalShift(face);
        if (shift > SideYawShiftRatio)
        {
            return PoseKind.LookingRightInFrame;
        }
        if (shift < -SideYawShiftRatio)
        {
            return PoseKind.LookingLeftInFrame;
        }
        return PoseKind.FrontalOrUnknown;
    }

    /// <summary>
    /// 判断人脸是否"足够正脸"（可参与主人比对与遮罩判定）。
    /// 关键点缺失、几何异常时返回 false（不可判定 → 不触发遮罩，宁可漏报不误报）。
    /// </summary>
    /// <param name="face">人脸检测结果。</param>
    /// <param name="frameWidth">帧宽度（像素）。</param>
    /// <param name="frameHeight">帧高度（像素）。</param>
    public static bool IsFrontalEnough(FaceInfo face, int frameWidth, int frameHeight)
    {
        // 关键点不完整 → 无法判断姿态，按"不可判定"处理（安全侧：不触发遮罩）
        if (face.Landmarks is not { Length: 5 })
        {
            return false;
        }

        Point2f leftEye = face.Landmarks[0];
        Point2f rightEye = face.Landmarks[1];
        Point2f noseTip = face.Landmarks[2];

        // 双眼中心
        double eyeCenterX = (leftEye.X + rightEye.X) / 2.0;
        double eyeSpan = Math.Abs(rightEye.X - leftEye.X);

        // 双眼水平间距过小：可能是极侧脸（只剩一只眼的投影）或检测异常 → 不可判定
        float faceWidth = face.Box.Width;
        if (eyeSpan < faceWidth * 0.10 || faceWidth <= 0)
        {
            return false;
        }

        // 鼻尖相对双眼中心的水平偏移，归一化到人脸框宽度
        double yawShift = Math.Abs(noseTip.X - eyeCenterX) / faceWidth;

        return yawShift <= MaxYawShiftRatio;
    }

    /// <summary>
    /// 判断人脸是否位于"可信区域"（画面中部，未贴边）。
    /// 贴边的人脸可能是路过/部分入镜，特征不完整，不足以做出"陌生人"判定。
    /// </summary>
    public static bool IsConfidentRegion(FaceInfo face, int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || face.Box.Width <= 0 || face.Box.Height <= 0)
        {
            return false;
        }

        double marginX = frameWidth * EdgeMarginRatio;
        double marginY = frameHeight * EdgeMarginRatio;

        // 人脸框任一边越过"画面边界 - 余量"即视为贴边
        bool nearLeft = face.Box.X < marginX;
        bool nearRight = face.Box.Right > frameWidth - marginX;
        bool nearTop = face.Box.Y < marginY;
        bool nearBottom = face.Box.Bottom > frameHeight - marginY;

        return !(nearLeft || nearRight || nearTop || nearBottom);
    }
}
