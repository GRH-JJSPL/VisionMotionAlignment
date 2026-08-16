using HalconDotNet;

namespace BlisterPillInspection.Models.Camera;

/// <summary>
/// 相机单帧采集结果。
/// </summary>
/// <remarks>
/// 健壮性 R7：图像资源（<see cref="Image"/>）所有权转移给消费者，
/// 由消费者负责 <see cref="IDisposable.Dispose"/>，避免重复释放或泄漏。
///
/// R7 背景：HImage 是 Halcon 图像对象，底层是非托管内存，GC 管不到，
/// 必须手动 Dispose。每拍一帧就创建一个 HImage，如果不 Dispose 内存会持续增长。
///
/// 谁负责释放：
///   - 视觉检测：HalconVisionService.Detect 消费后由 Orchestrator 释放
///   - Channel 满丢弃：Orchestrator.OnFrameReceived 中 TryWrite 失败时立即 Dispose
///   - 停止管线：Orchestrator.StopAsync 排空 Channel 时 Dispose 所有未消费帧
/// </remarks>
public sealed class CameraFrame
{
    /// <summary>所属工位。双工位系统中标识此帧来自哪台相机。</summary>
    public WorkstationId Workstation { get; init; }

    /// <summary>
    /// 相机帧图像。资源由消费者负责 Dispose（健壮性 R7）。
    /// 原因：HImage 底层是非托管内存，GC 无法自动回收，必须手动释放。
    /// </summary>
    public required HImage Image { get; init; }

    /// <summary>采集时间戳（UTC）。</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>帧序号（自增），用于调试和帧率统计。</summary>
    public long FrameIndex { get; init; }
}
