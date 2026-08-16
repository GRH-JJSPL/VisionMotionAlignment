using MotionShared.Dtos;
using MotionShared.Protocol;
using Serilog;
using BlisterPillInspection.Models;
using BlisterPillInspection.Services.Interfaces;

namespace BlisterPillInspection.Services.MotionCard;

/// <summary>
/// 固高 GTS 运动控制卡服务：断线重连、指令串行化、跨线程状态事件。
/// </summary>
public sealed class GtsMotionCardService : IMotionCardService
{
    /// <summary>运动指令串行化锁（线程安全 T2）：保证同一时刻只有一个运动指令在执行。</summary>
    private readonly SemaphoreSlim _cmdLock = new(1, 1);

    /// <summary>底层 TCP/JSON 客户端。volatile 保证多线程可见性（重连循环与主线程均访问）。</summary>
    private volatile GtsClient? _client;

    /// <summary>当前连接状态。volatile 保证状态变更对所有线程立即可见。</summary>
    private volatile ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>重连互斥标志：0=空闲 1=重连中。Interlocked 原子操作守卫（线程安全 T3）。</summary>
    private int _isReconnecting;

    /// <summary>控制卡/模拟器 IP 地址。DisconnectAsync 清空此字段作为"已主动断开"信号。</summary>
    private string _ip = string.Empty;

    /// <summary>控制卡/模拟器 TCP 端口。DisconnectAsync 清零此字段配合 _ip 作断开信号。</summary>
    private int _port;

    /// <summary>
    /// R1 断线重连退避序列（秒）。遵循 Modbus/工控行业惯例：
    /// 首次 1s 快速重试 → 2s → 5s → 10s 长间隔，避免网络抖动时频繁重连。
    /// </summary>
    private static readonly int[] ReconnectBackoff = [1, 2, 5, 10];

    /// <summary>
    /// 构造函数。当前无注入依赖，预留后续扩展（如注入 IOptions&lt;MotionCardConfig&gt;）。
    /// </summary>
    public GtsMotionCardService()
    {
    }

    /// <summary>
    /// 连接状态变化事件（线程安全 T5：跨线程触发）。
    /// 用 EventHandler&lt;ConnectionState&gt;（标准事件委托，含 sender 参数）。
    /// 订阅方（如 MainWindowViewModel、CommSettingPageViewModel）须自行经 DispatcherHelper 切 UI 线程。
    /// </summary>
    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>
    /// 轴状态推送事件。透传（原样转发，不处理）GtsClient.AxisStatusReceived，
    /// 携带所有轴的当前位置/速度/状态信息。
    /// 用 Action&lt;AxisStatusPush&gt;（简单委托，无 sender 参数）。
    /// 订阅方：DiagnosticPageViewModel（诊断页显示轴状态）。
    /// </summary>
    public event Action<AxisStatusPush>? AxisStatusReceived;

    /// <summary>当前连接状态（volatile，多线程安全读取）。</summary>
    public ConnectionState State => _state;

    /// <summary>是否已连接（等价于 State == Connected）。</summary>
    public bool IsConnected => _state == ConnectionState.Connected;

    /// <summary>
    /// 建立 TCP 连接并打开控制卡（GT_Open）。
    ///
    /// 流程：清理旧连接 → Connecting 状态 → 新建 GtsClient → TCP 连接 → GT_Open → Connected 状态。
    /// 若 GT_Open 返回非 Success 但 TCP 已连接，仍置为 Connected（控制卡初始化警告不阻断通讯）。
    /// </summary>
    /// <param name="ip">控制卡/模拟器 IP 地址（如 "127.0.0.1"）。</param>
    /// <param name="port">控制卡/模拟器 TCP 端口（如 5000）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="Exception">TCP 连接或 GT_Open 失败时抛出异常，状态置为 Failed。</exception>
    public async Task ConnectAsync(string ip, int port, CancellationToken ct = default)
    {
        _ip = ip;
        _port = port;

        await _cmdLock.WaitAsync(ct);
        try
        {
            // R7 资源释放：清理旧连接（防止重复连接导致 TCP 资源泄漏）
            CleanupClient();

            TransitionTo(ConnectionState.Connecting);

            var client = new GtsClient();
            client.AxisStatusReceived += OnAxisStatusReceived;
            client.OnDisconnected += OnDisconnected;

            await client.ConnectAsync(ip, port);

            // GT_Open 打开控制卡
            var r = await client.OpenAsync();
            _client = client;

            if (r.Status == ErrorCode.Success)
            {
                TransitionTo(ConnectionState.Connected);
                Log.Information("运动控制卡已连接 {Ip}:{Port}，GT_Open 成功", ip, port);
            }
            else
            {
                Log.Warning("运动控制卡 GT_Open 警告：{Msg}", r.Msg);
                TransitionTo(ConnectionState.Connected); // TCP 已连，Open 警告不阻断
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "运动控制卡连接失败 {Ip}:{Port}", ip, port);
            TransitionTo(ConnectionState.Failed);
            throw;
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    /// <summary>
    /// 清理当前 client 实例：取消订阅 + Dispose + 置空。
    /// 调用方须持有 <see cref="_cmdLock"/>，确保不会被并发访问。
    /// </summary>
    private void CleanupClient()
    {
        var old = _client;
        if (old is null) return;

        try
        {
            old.AxisStatusReceived -= OnAxisStatusReceived;
            old.OnDisconnected -= OnDisconnected;
            old.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "清理旧运动控制卡 client 异常");
        }
        _client = null;
    }

    /// <summary>
    /// 断开 TCP 连接并释放资源。
    ///
    /// 关键设计：清空 <see cref="_ip"/> 作为"已主动断开"信号，
    /// 正在运行的 <see cref="TryReconnectAsync"/> 循环会在下一次检查时退出，
    /// 避免断线重连与主动断开的竞态。
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _cmdLock.WaitAsync();
        try
        {
            // 清空连接信息，阻止 TryReconnectAsync 继续重连
            _ip = string.Empty;
            _port = 0;

            if (_client is not null)
            {
                try { await _client.CloseAsync(); } catch { /* 忽略关闭异常 */ }
            }
            CleanupClient();

            TransitionTo(ConnectionState.Disconnected);
            Log.Information("运动控制卡已断开连接");
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    /// <summary>
    /// 打开控制卡（GT_Open）。通常在 <see cref="ConnectAsync"/> 后自动调用，
    /// 也可手动调用以重新初始化控制卡。
    /// </summary>
    /// <returns>成功返回 true；控制卡未连接或指令失败返回 false。</returns>
    public async Task<bool> OpenAsync()
    {
        return await ExecuteCommandAsync(c => c.OpenAsync(), nameof(OpenAsync));
    }

    /// <summary>
    /// 关闭控制卡（GT_Close）。释放控制卡资源，TCP 连接不断开。
    /// </summary>
    /// <returns>成功返回 true；控制卡未连接或指令失败返回 false。</returns>
    public async Task<bool> CloseAsync()
    {
        return await ExecuteCommandAsync(c => c.CloseAsync(), nameof(CloseAsync));
    }

    /// <summary>
    /// 轴回零（GT_Home）。控制指定轴执行回原点操作，寻找零位开关/编码器原点。
    /// 回零是绝对定位的前提，未回零时 <see cref="MoveAbsAsync"/> 可能被控制卡拒绝。
    /// </summary>
    /// <param name="axis">轴号：1=X 2=Y 3=Z 4=U（固高 GTS 约定，从 1 开始）。</param>
    /// <returns>成功返回 true；控制卡未连接或指令失败返回 false。</returns>
    public async Task<bool> HomeAsync(int axis)
    {
        return await ExecuteCommandAsync(c => c.HomeAsync(axis), nameof(HomeAsync), axis);
    }

    /// <summary>
    /// 相对定位（GT_MoveRel）。从当前位置移动指定距离。
    ///
    /// 本项目的核心运动指令：检测编排器（InspectionOrchestrator）
    /// 调用此方法下发送料/分拣轴的动作。
    /// </summary>
    /// <param name="axis">轴号：1=传送带 2=NG拨杆 3=OK拨杆。</param>
    /// <param name="dist">相对移动距离（mm，正值正向、负值反向）。</param>
    /// <param name="vel">移动速度（mm/s）。</param>
    /// <returns>成功返回 true；控制卡未连接或指令失败返回 false。</returns>
    public async Task<bool> MoveRelAsync(int axis, double dist, double vel)
    {
        return await ExecuteCommandAsync(c => c.MoveRelAsync(axis, dist, vel), nameof(MoveRelAsync), axis, dist, vel);
    }

    /// <summary>
    /// 绝对定位（GT_MoveAbs）。控制指定轴运动到目标绝对坐标位置。
    /// 前提条件：轴已完成回零（GT_Home），否则绝对坐标无意义。
    /// </summary>
    /// <param name="axis">轴号：1=X 2=Y 3=Z 4=U。</param>
    /// <param name="pos">目标绝对位置（mm，相对于原点）。</param>
    /// <param name="vel">运动速度（mm/s）。</param>
    /// <returns>成功返回 true；控制卡未连接或指令失败返回 false。</returns>
    public async Task<bool> MoveAbsAsync(int axis, double pos, double vel)
    {
        return await ExecuteCommandAsync(c => c.MoveAbsAsync(axis, pos, vel), nameof(MoveAbsAsync), axis, pos, vel);
    }

    /// <summary>
    /// 停止单轴运动（GT_Stop）。以减速度平缓停止指定轴，非急停。
    /// 正常停止优先使用此方法；紧急情况使用 <see cref="EmergencyStopAsync"/>。
    /// </summary>
    /// <param name="axis">轴号：1=X 2=Y 3=Z 4=U。</param>
    /// <returns>成功返回 true；控制卡未连接或指令失败返回 false。</returns>
    public async Task<bool> StopAsync(int axis)
    {
        return await ExecuteCommandAsync(c => c.StopAsync(axis), nameof(StopAsync), axis);
    }

    /// <summary>
    /// 急停所有轴（GT_EmergencyStop）。立即停止所有轴运动，无减速过程。
    /// 仅在紧急情况下使用（如安全光幕触发），正常停止应使用 <see cref="StopAsync"/>。
    /// </summary>
    /// <returns>成功返回 true；控制卡未连接或指令失败返回 false。</returns>
    public async Task<bool> EmergencyStopAsync()
    {
        return await ExecuteCommandAsync(c => c.EmergencyStopAsync(), nameof(EmergencyStopAsync));
    }

    /// <summary>
    /// 清除轴报警（GT_ClearAlarm）。清除指定轴的报警状态（如超限、伺服故障等），
    /// 使轴恢复可操作状态。清除前应确保报警原因已排除。
    /// </summary>
    /// <param name="axis">轴号：1=X 2=Y 3=Z 4=U。</param>
    /// <returns>成功返回 true；控制卡未连接或指令失败返回 false。</returns>
    public async Task<bool> ClearAlarmAsync(int axis)
    {
        return await ExecuteCommandAsync(c => c.ClearAlarmAsync(axis), nameof(ClearAlarmAsync), axis);
    }

    /// <summary>
    /// 通用指令执行模板 —— 统一处理 T2 串行化 + 异常兜底 + Serilog 日志。
    ///
    /// 流程：
    /// <list type="number">
    /// <item>前置检查：<see cref="_client"/> 是否为 null、<see cref="IsConnected"/> 是否为 true。</item>
    /// <item>获取 <see cref="_cmdLock"/> 信号量（串行化，防止双工位并发冲突）。</item>
    /// <item>执行指令委托，检查 <see cref="CommandResponse.Status"/> 是否为 Success。</item>
    /// <item>异常不抛出，返回 false 并记录错误日志（健壮性：不因单次指令失败中断业务）。</item>
    /// </list>
    /// </summary>
    /// <param name="action">指令委托，接收 <see cref="GtsClient"/> 返回 <see cref="CommandResponse"/>。</param>
    /// <param name="cmdName">指令名称（结构化日志用，如 "MoveRelAsync"）。</param>
    /// <param name="args">指令参数（结构化日志用，如 axis=1, dist=0.5, vel=50.0）。</param>
    /// <returns>指令成功返回 true；未连接、指令失败或异常返回 false。</returns>
    private async Task<bool> ExecuteCommandAsync(
        Func<GtsClient, Task<CommandResponse>> action, string cmdName, params object[] args)
    {
        if (_client is null || !IsConnected)
        {
            Log.Warning("运动控制卡未连接，无法执行 {Cmd}", cmdName);
            return false;
        }

        await _cmdLock.WaitAsync();
        try
        {
            var r = await action(_client);
            if (r.Status == ErrorCode.Success)
            {
                Log.Information("运动控制卡 {Cmd} 成功 Axis={Args}", cmdName, string.Join(",", args));
                return true;
            }

            Log.Warning("运动控制卡 {Cmd} 失败 Status={Status} Msg={Msg} Axis={Args}",
                cmdName, r.Status, r.Msg, string.Join(",", args));
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "运动控制卡 {Cmd} 异常 Axis={Args}", cmdName, string.Join(",", args));
            return false;
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    /// <summary>
    /// GtsClient 轴状态推送回调。透传给上层 <see cref="AxisStatusReceived"/> 订阅者。
    /// </summary>
    /// <param name="push">轴状态推送数据（所有轴的当前位置/速度/状态）。</param>
    private void OnAxisStatusReceived(AxisStatusPush push)
    {
        AxisStatusReceived?.Invoke(push);
    }

    /// <summary>
    /// GtsClient 断线回调。记录断开原因，状态切换为 Disconnected，启动 R1 自动重连。
    /// </summary>
    /// <param name="reason">断线原因（由 TcpClientBase 提供，如 "远程主机关闭连接"）。</param>
    private void OnDisconnected(string reason)
    {
        Log.Warning("运动控制卡连接断开：{Reason}，启动自动重连", reason);
        TransitionTo(ConnectionState.Disconnected);
        _ = TryReconnectAsync();
    }

    /// <summary>
    /// R1 断线自动重连 —— 退避序列 {1, 2, 5, 10} 秒循环重试，直到成功或主动断开。
    ///
    /// 【防竞态设计】
    /// <see cref="DisconnectAsync"/> 与本方法存在竞态：用户点击"断开"时，本方法可能正在重连。
    /// 防护措施（三重检查）：
    /// <list type="number">
    /// <item>循环开头检查 <see cref="_ip"/> 是否为空（DisconnectAsync 会清空）。</item>
    /// <item>延迟后再次检查（延迟期间可能已主动断开）。</item>
    /// <item>赋值 <see cref="_client"/> 前第三次检查（避免覆盖 DisconnectAsync 设置的 null）。</item>
    /// </list>
    ///
    /// <see cref="_isReconnecting"/> 用 Interlocked.CompareExchange(ref int,...) 守卫，
    /// 确保同一时刻只有一个重连循环运行（线程安全 T3）。
    /// </summary>
    private async Task TryReconnectAsync()
    {
        // T3：Interlocked 守卫，防止多个重连循环并发
        if (Interlocked.CompareExchange(ref _isReconnecting, 1, 0) != 0) return;

        try
        {
            for (int i = 0; ; i++)
            {
                // 检查①：是否已主动断开（DisconnectAsync 会清空 _ip）
                if (string.IsNullOrEmpty(_ip) || _port == 0)
                {
                    Log.Information("运动控制卡已主动断开，停止重连");
                    return;
                }

                int delay = ReconnectBackoff[i % ReconnectBackoff.Length];
                await Task.Delay(TimeSpan.FromSeconds(delay));

                // 检查②：延迟后再次检查（延迟期间可能已主动断开）
                if (string.IsNullOrEmpty(_ip) || _port == 0)
                {
                    Log.Information("运动控制卡已主动断开，停止重连");
                    return;
                }

                try
                {
                    Log.Information("运动控制卡尝试重连 {Ip}:{Port}（第 {Attempt} 次）", _ip, _port, i + 1);

                    var client = new GtsClient();
                    client.AxisStatusReceived += OnAxisStatusReceived;
                    client.OnDisconnected += OnDisconnected;
                    await client.ConnectAsync(_ip, _port);

                    var r = await client.OpenAsync();
                    if (r.Status == ErrorCode.Success)
                    {
                        // 检查③：赋值前再次检查是否已主动断开，避免覆盖 DisconnectAsync 设置的 null
                        if (string.IsNullOrEmpty(_ip))
                        {
                            client.Dispose();
                            Log.Information("运动控制卡重连成功后检测到已主动断开，丢弃重连");
                            return;
                        }

                        CleanupClient(); // 清理旧 client（如果有）
                        _client = client;
                        TransitionTo(ConnectionState.Connected);
                        Log.Information("运动控制卡重连成功 {Ip}:{Port}", _ip, _port);
                        return;
                    }

                    // Open 失败，关闭重试
                    client.Dispose();
                    Log.Warning("运动控制卡重连 GT_Open 失败：{Msg}", r.Msg);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "运动控制卡重连失败（第 {Attempt} 次）", i + 1);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isReconnecting, 0);
        }
    }

    /// <summary>
    /// 状态机转换。更新 <see cref="_state"/> 并在状态实际变化时触发 <see cref="StateChanged"/> 事件。
    /// </summary>
    /// <param name="newState">目标状态。</param>
    /// <remarks>
    /// 线程安全 T5：事件在当前线程触发，订阅方（ViewModel）须自行经 DispatcherHelper 切 UI 线程。
    /// </remarks>
    private void TransitionTo(ConnectionState newState)
    {
        var old = _state;
        _state = newState;
        if (old != newState)
        {
            Log.Information("运动控制卡状态转换 {Old} → {New}", old, newState);
            StateChanged?.Invoke(this, newState);
        }
    }
}
