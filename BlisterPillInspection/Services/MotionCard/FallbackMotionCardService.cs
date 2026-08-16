using Microsoft.Extensions.Logging;
using MotionShared.Dtos;
using BlisterPillInspection.Models;
using BlisterPillInspection.Services.Interfaces;

namespace BlisterPillInspection.Services.MotionCard;

/// <summary>
/// 运动控制卡 Fallback 代理：优先真实 TCP 服务，连接失败自动回退到虚拟服务。
/// </summary>
public sealed class FallbackMotionCardService : IMotionCardService
{
    private readonly GtsMotionCardService _real;
    private readonly VirtualMotionCardService _virtual;
    private readonly Microsoft.Extensions.Logging.ILogger<GtsMotionCardService> _logger;

    /// <summary>当前激活的服务：0=真实 1=虚拟。</summary>
    private int _activeMode;

    /// <summary>是否已选择过服务（连接尝试过）。</summary>
    private int _initialized;

    /// <summary>当前激活的 IMotionCardService。</summary>
    private IMotionCardService Current => _activeMode == 1 ? _virtual : _real;

    /// <summary>是否正在使用虚拟卡（供 UI 显示"虚拟模式"）。</summary>
    public bool IsVirtual => _activeMode == 1;

    /// <summary>
    /// 构造 Fallback 代理。
    /// </summary>
    /// <param name="real">真实 TCP 服务。</param>
    /// <param name="virtualCard">虚拟服务。</param>
    /// <param name="logger">日志。</param>
    public FallbackMotionCardService(
        GtsMotionCardService real,
        VirtualMotionCardService virtualCard,
        Microsoft.Extensions.Logging.ILogger<GtsMotionCardService> logger)
    {
        _real = real;
        _virtual = virtualCard;
        _logger = logger;

        // 透传事件（无论激活哪个，UI 都能收到状态变化）
        _real.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
        _virtual.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
        _real.AxisStatusReceived += a => AxisStatusReceived?.Invoke(a);
        _virtual.AxisStatusReceived += a => AxisStatusReceived?.Invoke(a);
    }

    /// <inheritdoc/>
    public event EventHandler<ConnectionState>? StateChanged;

    /// <inheritdoc/>
    public event Action<AxisStatusPush>? AxisStatusReceived;

    /// <inheritdoc/>
    public ConnectionState State => Current.State;

    /// <inheritdoc/>
    public bool IsConnected => Current.IsConnected;

    /// <inheritdoc/>
    public async Task ConnectAsync(string ip, int port, CancellationToken ct = default)
    {
        // 只尝试一次真实连接；失败回退虚拟
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            try
            {
                await _real.ConnectAsync(ip, port, ct);
                if (_real.IsConnected)
                {
                    _activeMode = 0;
                    _logger.LogInformation("运动控制卡：真实服务已连接 {Ip}:{Port}", ip, port);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "运动控制卡：真实服务连接失败，回退到虚拟服务");
            }

            // 回退虚拟
            _activeMode = 1;
            await _virtual.ConnectAsync(ip, port, ct);
            _logger.LogInformation("运动控制卡：已回退到虚拟服务（模拟 3 轴）");
        }
        else
        {
            await Current.ConnectAsync(ip, port, ct);
        }
    }

    /// <inheritdoc/>
    public Task DisconnectAsync() => Current.DisconnectAsync();

    /// <inheritdoc/>
    public Task<bool> OpenAsync() => Current.OpenAsync();

    /// <inheritdoc/>
    public Task<bool> CloseAsync() => Current.CloseAsync();

    /// <inheritdoc/>
    public Task<bool> HomeAsync(int axis) => Current.HomeAsync(axis);

    /// <inheritdoc/>
    public Task<bool> MoveRelAsync(int axis, double dist, double vel) => Current.MoveRelAsync(axis, dist, vel);

    /// <inheritdoc/>
    public Task<bool> MoveAbsAsync(int axis, double pos, double vel) => Current.MoveAbsAsync(axis, pos, vel);

    /// <inheritdoc/>
    public Task<bool> StopAsync(int axis) => Current.StopAsync(axis);

    /// <inheritdoc/>
    public Task<bool> EmergencyStopAsync() => Current.EmergencyStopAsync();

    /// <inheritdoc/>
    public Task<bool> ClearAlarmAsync(int axis) => Current.ClearAlarmAsync(axis);
}
