namespace BlisterPillInspection.Models.Camera;

/// <summary>
/// 相机触发模式。
/// </summary>
public enum TriggerMode
{
    /// <summary>
    /// 连续采集：相机自己按帧率持续拍照，不需要上位机指令。
    /// 适合实时监控、连续检测场景。
    /// </summary>
    Continuous = 0,

    /// <summary>
    /// 软触发：相机等待上位机发指令才拍一张。
    /// 适合需要精确控制拍照时机的场景（如定点检测）。
    /// </summary>
    Software = 1
}

/// <summary>
/// 相机采集参数。
/// </summary>
public sealed class CameraParameters
{
    /// <summary>
    /// 曝光时间（μs）。默认 8000μs（8ms）。
    /// 越长进光量越多，图像越亮。太暗就加长，太亮就缩短。
    /// </summary>
    public double ExposureUs { get; init; } = 8000;

    /// <summary>
    /// 增益（dB）。默认 0dB。
    /// 传感器信号放大倍数。增益越高图像越亮，但噪点也越多。
    /// 优先调曝光，曝光不够再加增益。
    /// </summary>
    public double Gain { get; init; }

    /// <summary>
    /// 帧率（fps）。默认 30fps（每秒 30 帧）。
    /// 帧率越高越流畅，但数据量越大、CPU 占用越高。
    /// </summary>
    public double FrameRate { get; init; } = 30;

    /// <summary>
    /// 触发模式。默认连续采集。
    /// 连续采集：相机自己持续拍照；软触发：等上位机指令才拍。
    /// </summary>
    public TriggerMode Trigger { get; init; } = TriggerMode.Continuous;
}
