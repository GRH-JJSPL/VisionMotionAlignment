using BlisterPillInspection.Models;
using BlisterPillInspection.Models.Camera;

namespace BlisterPillInspection.Services.Interfaces;

/// <summary>
/// 相机服务接口。负责相机设备枚举、打开/关闭、连续取图流管理，
/// 并通过事件向上层推送帧数据与连接状态。
/// 实现 <see cref="IDisposable"/> 以确保非托管相机资源被释放。
/// </summary>
public interface ICameraService : IDisposable
{
    /// <summary>
    /// 枚举当前可用的相机设备列表。
    /// </summary>
    /// <returns>只读设备信息集合，调用方不应修改返回的列表。</returns>
    IReadOnlyList<CameraDeviceInfo> EnumerateDevices();

    /// <summary>
    /// 打开指定设备并应用相机参数。
    /// </summary>
    /// <param name="deviceKey">设备唯一标识（由 <see cref="EnumerateDevices"/> 返回）。</param>
    /// <param name="parameters">需要应用的相机参数（曝光、增益、分辨率等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功打开返回 true；否则返回 false。</returns>
    Task<bool> OpenAsync(string deviceKey, CameraParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// 关闭当前已打开的设备。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 启动连续取图流。启动后通过 <see cref="FrameReceived"/> 事件推送帧。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task StartStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止连续取图流。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 帧到达事件。
    /// 注意：该事件在后台线程触发，订阅者需自行将后续处理切回 UI 线程。
    /// </summary>
    event EventHandler<CameraFrame>? FrameReceived;

    /// <summary>
    /// 连接状态变化事件。
    /// </summary>
    event EventHandler<ConnectionState>? StateChanged;
}
