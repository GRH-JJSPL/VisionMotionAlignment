using VisionMotionAlignment.Services.Interfaces;

namespace VisionMotionAlignment.Services.Configuration;

/// <summary>
/// <see cref="IAppConfigService"/> 的占位实现。所有方法返回默认值，
/// 构造函数不抛异常以确保应用可启动。后续 M2 阶段替换为真实配置服务实现。
/// </summary>
public sealed class StubAppConfigService : IAppConfigService
{
    /// <summary>占位实现：始终返回 default。</summary>
    /// <typeparam name="T">目标反序列化类型。</typeparam>
    /// <param name="key">配置节键名。</param>
    /// <returns>类型 <typeparamref name="T"/> 的默认值。</returns>
    public T Get<T>(string key) => default!;

    /// <summary>占位实现：忽略写入操作。</summary>
    /// <typeparam name="T">值类型。</typeparam>
    /// <param name="key">配置节键名。</param>
    /// <param name="value">待写入的值。</param>
    public void Set<T>(string key, T value)
    {
        // 占位实现：不处理配置写入。
    }

    /// <summary>占位实现：直接返回已完成任务。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已完成的任务。</returns>
    public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>占位实现：直接返回已完成任务。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已完成的任务。</returns>
    public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
