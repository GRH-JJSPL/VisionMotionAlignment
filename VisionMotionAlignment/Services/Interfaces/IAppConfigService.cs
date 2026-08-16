namespace VisionMotionAlignment.Services.Interfaces;

/// <summary>
/// 应用配置服务接口。负责按 key 读取/写入配置节，并支持持久化与重新加载。
/// </summary>
public interface IAppConfigService
{
    /// <summary>
    /// 读取指定配置节并反序列化为类型 <typeparamref name="T"/>。
    /// 失败时返回 default，不抛出异常（见健壮性 R5）。
    /// </summary>
    /// <typeparam name="T">目标反序列化类型。</typeparam>
    /// <param name="key">配置节键名。</param>
    /// <returns>反序列化后的值；读取或反序列化失败时返回 default。</returns>
    T Get<T>(string key);

    /// <summary>
    /// 将值写入指定配置节（内存中，需调用 <see cref="SaveAsync"/> 持久化）。
    /// </summary>
    /// <typeparam name="T">值类型。</typeparam>
    /// <param name="key">配置节键名。</param>
    /// <param name="value">待写入的值。</param>
    void Set<T>(string key, T value);

    /// <summary>
    /// 将当前内存中的配置持久化到配置文件。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task SaveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 从配置文件重新加载配置，覆盖内存中的当前配置。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task ReloadAsync(CancellationToken cancellationToken = default);
}
