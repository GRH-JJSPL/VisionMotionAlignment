using VisionMotionAlignment.Models;
using VisionMotionAlignment.Models.Communication;
using VisionMotionAlignment.Models.Force;

namespace VisionMotionAlignment.Services.Interfaces;

/// <summary>
/// 力值模块通讯服务接口。负责通过串口连接力值模块、单次读取力值或周期轮询，
/// 并通过事件向上层推送力值更新与连接状态。
/// 实现 <see cref="IDisposable"/> 以确保串口资源被释放。
/// </summary>
public interface IForceModuleService : IDisposable
{
    /// <summary>
    /// 使用指定串口配置连接力值模块。
    /// </summary>
    /// <param name="config">串口配置（端口名、波特率、超时等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接成功返回 true；否则返回 false。</returns>
    Task<bool> ConnectAsync(SerialPortConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// 断开与力值模块的连接。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 单次读取当前力值。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前力值读数。</returns>
    Task<ForceReading> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 启动周期性轮询，按固定间隔持续读取力值并通过 <see cref="ReadingReceived"/> 事件推送。
    /// </summary>
    /// <param name="interval">轮询间隔。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task StartPollingAsync(TimeSpan interval, CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止周期性轮询。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task StopPollingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 清零（去皮）。向 500B 多功能寄存器（0x062A）写入 1，触发清零指令。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>清零成功返回 true；否则返回 false。</returns>
    Task<bool> ZeroAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 力值更新事件。
    /// 注意：该事件在后台线程触发，订阅者需自行将后续处理切回 UI 线程。
    /// </summary>
    event EventHandler<ForceReading>? ReadingReceived;

    /// <summary>
    /// 连接状态变化事件。
    /// </summary>
    event EventHandler<ConnectionState>? StateChanged;
}
