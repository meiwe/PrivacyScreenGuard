using System;
using System.Collections.Generic;
using OpenCvSharp;
using PrivacyScreenGuard.Models;
using PrivacyScreenGuard.Services;
using Xunit;

namespace PrivacyScreenGuard.Tests;

/// <summary>
/// 侧脸/非正脸判定测试（扭转头看侧屏场景，避免主人被误判为陌生人）。
/// 构造 640×480 帧内的人脸关键点做验证。
/// </summary>
public class FacePoseTests
{
    private const int FrameW = 640;
    private const int FrameH = 480;

    /// <summary>构造一张人脸的关键点集合。yawShift = 鼻尖相对双眼中心的水平偏移（占人脸宽度比例）。</summary>
    private static FaceInfo MakeFace(int centerX, int centerY, int faceWidth, double yawShift)
    {
        // 双眼中心固定在人脸框中心（正脸基准），鼻尖按 yawShift 比例水平偏移
        double eyeOffset = faceWidth * 0.18;           // 双眼各距中心 0.18×脸宽（真实比例）
        double leftEyeX = centerX - eyeOffset;
        double rightEyeX = centerX + eyeOffset;
        double noseX = centerX + faceWidth * yawShift; // 鼻尖偏移（判定口径与此一致）
        double noseY = centerY + faceWidth * 0.05;
        double mouthY = centerY + faceWidth * 0.28;
        double mouthHalfSpan = faceWidth * 0.14;

        return new FaceInfo
        {
            Box = new Rect(centerX - faceWidth / 2, centerY - faceWidth / 2, faceWidth, faceWidth),
            Landmarks = new[]
            {
                new Point2f((float)leftEyeX, (float)centerY),
                new Point2f((float)rightEyeX, (float)centerY),
                new Point2f((float)noseX, (float)noseY),
                new Point2f((float)(noseX - mouthHalfSpan), (float)mouthY),
                new Point2f((float)(noseX + mouthHalfSpan), (float)mouthY),
            },
            Score = 0.9f,
        };
    }

    [Theory]
    [InlineData(0.0)]   // 正脸
    [InlineData(0.08)]  // 轻微偏转
    [InlineData(-0.12)] // 轻微反向偏转
    [InlineData(0.15)]  // 明显侧头（仍在容忍范围）
    public void 正脸或轻微偏转_视为正脸(double yawShift)
    {
        var face = MakeFace(FrameW / 2, FrameH / 2, 160, yawShift);
        Assert.True(FacePose.IsFrontalEnough(face, FrameW, FrameH));
    }

    [Theory]
    [InlineData(0.30)]  // 明显扭头看侧屏
    [InlineData(-0.35)] // 反方向扭头
    [InlineData(0.50)]  // 大幅侧脸
    public void 扭头看侧屏_判定为侧脸(double yawShift)
    {
        var face = MakeFace(FrameW / 2, FrameH / 2, 160, yawShift);
        Assert.True(FacePose.IsFrontalEnough(face, FrameW, FrameH) == false);
    }

    [Theory]
    [InlineData(0.25)]  // 约 30°：轻侧（≥0.20 门槛，未到 60° 档）
    [InlineData(0.34)]  // 恰好达到 60° 档门槛
    [InlineData(0.45)]  // 约 60°：大侧
    public void 侧脸分档_30度与60度都满足采集门槛(double yawShift)
    {
        // 侧脸阶段第 1 张要求 |shift| ≥ 0.20（SideYawShiftRatio）；
        // 注册向导第 2 张要求 |shift| ≥ 0.34（StrongSideYawShiftRatio），覆盖大幅扭头
        var face = MakeFace(FrameW / 2, FrameH / 2, 160, yawShift);
        Assert.True(FacePose.GetHorizontalShift(face) >= FacePose.SideYawShiftRatio);
    }

    [Fact]
    public void 大角度档_门槛常量大于小角度档()
    {
        Assert.True(FacePose.StrongSideYawShiftRatio > FacePose.SideYawShiftRatio);
    }

    [Theory]
    [InlineData(-0.30)]
    [InlineData(-0.50)]
    public void 反方向侧脸_取绝对值后同样满足采集门槛(double yawShift)
    {
        var face = MakeFace(FrameW / 2, FrameH / 2, 160, yawShift);
        Assert.True(Math.Abs(FacePose.GetHorizontalShift(face)) >= FacePose.SideYawShiftRatio);
    }

    [Fact]
    public void 人脸太靠画面边缘_视为不可判定()
    {
        // 人脸框右缘几乎贴住画面右边界：很可能只是路过/部分入镜，不足以判定身份
        var face = MakeFace(FrameW - 60, FrameH / 2, 160, 0.0);
        Assert.False(FacePose.IsConfidentRegion(face, FrameW, FrameH));
    }

    [Fact]
    public void 画面中央的人脸_属于可信区域()
    {
        var face = MakeFace(FrameW / 2, FrameH / 2, 160, 0.0);
        Assert.True(FacePose.IsConfidentRegion(face, FrameW, FrameH));
    }

    [Fact]
    public void 关键点缺失_姿态不可判定_遮罩判定按安全侧处理()
    {
        var face = new FaceInfo
        {
            Box = new Rect(240, 160, 160, 160),
            Landmarks = Array.Empty<Point2f>(),
            Score = 0.9f,
        };
        // 姿态不可判定 → IsFrontalEnough 返回 false（引擎不把它当作"确认的陌生人"）
        Assert.False(FacePose.IsFrontalEnough(face, FrameW, FrameH));
        // 位置判定与关键点无关：画面中央仍属可信区域
        Assert.True(FacePose.IsConfidentRegion(face, FrameW, FrameH));
    }
}
