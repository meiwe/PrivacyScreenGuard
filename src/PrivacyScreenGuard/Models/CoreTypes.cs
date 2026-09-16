namespace PrivacyScreenGuard.Models;

/// <summary>
/// 单帧观测结果（守护引擎对一帧画面的语义结论）。
/// </summary>
public enum FrameObservation
{
    /// <summary>检测到主人（仅主人或主人在场），安全。</summary>
    OwnerPresent,

    /// <summary>检测到人脸但没有任何一张是主人 → 风险。</summary>
    FaceButNoOwner,

    /// <summary>未检测到任何人脸 → 按无人策略处理。</summary>
    NoFace
}

/// <summary>无人脸时的策略。</summary>
public enum NoFacePolicy
{
    /// <summary>保持正常（默认）。</summary>
    KeepNormal,

    /// <summary>无人时触发锁定（遮罩）。</summary>
    Lock
}

/// <summary>
/// 多人在场时的策略：画面中同时出现主人与其他人脸时如何处理。
/// </summary>
public enum MultiPersonPolicy
{
    /// <summary>
    /// 主人在场即放行（默认）：帧内任意一张人脸匹配主人就不遮罩，
    /// 适合经常给别人演示/看屏幕的场景（无需手动暂停）。
    /// </summary>
    OwnerPresenceOpens,

    /// <summary>
    /// 有陌生人即遮罩（安全优先）：即使主人也在场，
    /// 只要存在未匹配到主人的人脸就触发遮罩，防止身边人偷看。
    /// </summary>
    StrangerTriggersMask
}

/// <summary>状态机对遮罩窗口的动作指令。</summary>
public enum MaskAction
{
    /// <summary>维持现状。</summary>
    None,

    /// <summary>显示遮罩。</summary>
    Show,

    /// <summary>隐藏遮罩。</summary>
    Hide
}

/// <summary>摄像头错误类型。</summary>
public enum CameraError
{
    /// <summary>无可用摄像头。</summary>
    NoCamera,

    /// <summary>摄像头被其他程序占用。</summary>
    Busy,

    /// <summary>摄像头中途断开。</summary>
    Disconnected,

    /// <summary>光线过暗，画面不可用。</summary>
    TooDark,

    /// <summary>模型文件缺失。</summary>
    ModelMissing,

    /// <summary>其他未知错误。</summary>
    Unknown
}
