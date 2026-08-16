using BlisterPillInspection.Models;
using BlisterPillInspection.Models.Communication;
using BlisterPillInspection.Models.Force;
using BlisterPillInspection.Services.Interfaces;

namespace BlisterPillInspection.Services.Force;

/// <summary>
/// <see cref="IForceModuleService"/> 的占位实现。所有方法返回默认值且不触发事件，
/// 构造函数不抛异常以确保应用可启动。后续 M4 阶段替换为真实力值模块通讯实现。
/// </summary>
#pragma warning disable CS0067 // 占位实现不触发事件，事件仅满足接口契约
public sealed class StubForceModuleService : IForceModuleService
{
    /// <summary>占位实现：始终返回 false（连接失败）。</summary>
    /// <param name="config">串口配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>始终返回 false。</returns>
    public Task<bool> ConnectAsync(SerialPortConfig config, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>占位实现：直接返回已完成任务。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已完成的任务。</returns>
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>占位实现：返回 <see cref="ForceReading.Invalid"/>。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>无效读数实例。</returns>
    public Task<ForceReading> ReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(ForceReading.Invalid);

    /// <summary>占位实现：直接返回已完成任务。</summary>
    /// <param name="interval">轮询间隔。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已完成的任务。</returns>
    public Task StartPollingAsync(TimeSpan interval, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>占位实现：直接返回已完成任务。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已完成的任务。</returns>
    public Task StopPollingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>占位实现：始终返回 false（清零失败）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>始终返回 false。</returns>
    public Task<bool> ZeroAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>力值更新事件。占位实现不触发该事件。</summary>
    public event EventHandler<ForceReading>? ReadingReceived;

    /// <summary>连接状态变化事件。占位实现不触发该事件。</summary>
    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>占位实现：无资源需要释放。</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放资源的受保护方法。占位实现无资源需要释放。
    /// </summary>
    /// <param name="disposing">是否由 Dispose 调用（而非终结器）。</param>
    private void Dispose(bool disposing)
    {
        // 占位实现：无资源需要释放。
    }
}
