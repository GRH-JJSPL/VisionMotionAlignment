using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using BlisterPillInspection.Services.Interfaces;

namespace BlisterPillInspection.Services.Configuration;

/// <summary>
/// <see cref="IAppConfigService"/> 的真实实现。
/// 基于 JSON 文件读写，内存中维护 <see cref="ConcurrentDictionary{TKey,TValue}"/> 缓存。
/// </summary>
/// <remarks>
/// 健壮性 R5：配置文件损坏或缺失时用内存默认值 + 警告日志，不崩启动。
/// </remarks>
public sealed class AppConfigService : IAppConfigService
{
    private readonly string _filePath;
    /*
       ReadWriteLock：
        读的时候不能写
        写的时候不能读
       ConcurrentDictionary：
        读的时候可以写（读旧数据）
        写的时候可以读（读新数据或旧数据）
    */
    private readonly ConcurrentDictionary<string, JsonNode?> _cache = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,  // 驼峰命名
        WriteIndented = true                                 // 格式化输出

    };

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="filePath">配置文件路径。</param>
    public AppConfigService(string filePath = "appsettings.json")
    {
        _filePath = filePath;
        LoadFromFile();
    }

    /// <summary>
    /// 读取指定配置节并反序列化为类型 <typeparamref name="T"/>。
    /// 失败时返回 default，不抛出异常（健壮性 R5）。
    /// </summary>
    public T Get<T>(string key)
    {
        if (_cache.TryGetValue(key, out var node) && node is not null)
        {
            try
            {
                return node.Deserialize<T>(_jsonOptions)!;
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "配置节反序列化失败，key={Key}", key);
            }
        }

        // 从文件读取指定节
        try
        {
            var root = ReadRootNode();
            var section = root?[key];
            if (section is not null)
            {
                var value = section.Deserialize<T>(_jsonOptions);
                _cache[key] = section;
                return value!;
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "配置读取失败，key={Key}", key);
        }

        return default!;
    }

    /// <summary>
    /// 将值写入指定配置节（内存中，需调用 <see cref="SaveAsync"/> 持久化）。
    /// </summary>
    public void Set<T>(string key, T value)
    {
        var json = JsonSerializer.SerializeToNode(value, _jsonOptions);
        _cache[key] = json;
    }

    /// <summary>
    /// 将当前内存中的配置持久化到配置文件。
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var root = ReadRootNode() ?? new JsonObject();

            foreach (var (key, value) in _cache)
            {
                if (value is null)
                {
                    root.AsObject().Remove(key);
                }
                else
                {
                    //深拷贝，创建独立副本，不影响root，否则会，因为root内部元素是引用变量
                    root[key] = value.DeepClone();
                }
            }

            var json = root.ToJsonString(_jsonOptions);
            await File.WriteAllTextAsync(_filePath, json, cancellationToken);
            Log.Logger.Information("配置已保存到 {FilePath}", _filePath);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "配置保存失败，FilePath={FilePath}", _filePath);
        }
    }

    /// <summary>
    /// 从配置文件重新加载配置。
    /// </summary>
    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _cache.Clear();
        LoadFromFile();
        return Task.CompletedTask;
    }

    private void LoadFromFile()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                Log.Logger.Information("配置文件不存在，使用内存默认值：{FilePath}", _filePath);
                return;
            }

            var root = ReadRootNode();
            if (root is null) return;

            foreach (var property in root.AsObject())
            {
                _cache[property.Key] = property.Value?.DeepClone();
            }

            Log.Logger.Information("配置已从文件加载：{FilePath}", _filePath);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "配置文件加载失败，使用内存默认值：{FilePath}", _filePath);
        }
    }

    private JsonNode? ReadRootNode()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            var json = File.ReadAllText(_filePath);
            return JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }
    }
}
