using Serilog;
using BlisterPillInspection.Infrastructure;
using BlisterPillInspection.Models;
using BlisterPillInspection.Models.Communication;
using BlisterPillInspection.Models.Force;
using BlisterPillInspection.Services.Interfaces;

namespace BlisterPillInspection.Services.Force;

/// <summary>
/// 500B 力值测量模块通讯服务：Modbus RTU（RS485）单次读取、周期轮询、断线重连与清零。
/// 毛重寄存器 0x0206（float），多功能寄存器 0x062A（清零/标定/保存）。
/// </summary>
/// <remarks>
/// <para>事件线程安全：ReadingReceived 和 StateChanged 在后台线程触发，
/// 订阅者需自行将后续处理切回 UI 线程。</para>
/// <para>断线重连（R1）：连续读取失败达到阈值时自动按退避序列尝试重连。</para>
/// </remarks>
public sealed class ForceModule500BService : IForceModuleService
{
    private readonly IModbusRtuTransport _transport;
    private readonly bool _ownsTransport;

    /// <summary>当前串口配置（连接后缓存，用于重连）。</summary>
    private SerialPortConfig? _config;

    /// <summary>轮询取消令牌源。</summary>
    private CancellationTokenSource? _pollingCts;

    /// <summary>连续读取失败计数（用于触发断线重连，Interlocked 原子操作）。</summary>
    private int _consecutiveFailures;

    /// <summary>重连互斥标志：0=空闲, 1=重连中。同一时刻仅允许一个重连流程。</summary>
    private int _isReconnecting;

    /// <summary>
    /// 初始化 <see cref="ForceModule500BService"/> 的新实例。
    /// </summary>
    /// <param name="transport">Modbus RTU 传输层实例。</param>
    /// <param name="ownsTransport">是否由本服务拥有传输层生命周期。true 时 Dispose 会释放传输层。</param>
    public ForceModule500BService(IModbusRtuTransport transport, bool ownsTransport = true)
    {
        _transport = transport;
        _ownsTransport = ownsTransport;
    }

    /// <inheritdoc/>
    public event EventHandler<ForceReading>? ReadingReceived;

    /// <inheritdoc/>
    public event EventHandler<ConnectionState>? StateChanged;

    /// <inheritdoc/>
    /// <remarks>
    /// 调用 <see cref="IModbusRtuTransport.OpenAsync"/> 打开串口，
    /// 成功时缓存配置并触发 <see cref="StateChanged"/>（Connected）。
    /// </remarks>
    public async Task<bool> ConnectAsync(SerialPortConfig config, CancellationToken cancellationToken = default)
    {
        Log.Information("500B 力值模块：正在连接 {PortName}，波特率 {BaudRate}，从站 {SlaveAddress}",
            config.PortName, config.BaudRate, config.SlaveAddress);

        bool ok = await _transport.OpenAsync(config, cancellationToken).ConfigureAwait(false);

        if (ok)
        {
            _config = config;
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            Log.Information("500B 力值模块：连接成功");
            OnStateChanged(ConnectionState.Connected);
        }
        else
        {
            Log.Warning("500B 力值模块：连接失败");
        }

        return ok;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 调用 <see cref="IModbusRtuTransport.CloseAsync"/> 关闭串口，
    /// 同时停止轮询（若正在轮询），触发 <see cref="StateChanged"/>（Disconnected）。
    /// </remarks>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        // 先停止轮询，避免轮询循环中继续访问已关闭的串口
        await StopPollingAsync(cancellationToken).ConfigureAwait(false);

        await _transport.CloseAsync(cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Log.Information("500B 力值模块：已断开连接");
        OnStateChanged(ConnectionState.Disconnected);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 从 <see cref="ForceModuleRegisterMap.GrossFloatAddr"/> (0x0206) 读取 2 个保持寄存器，
    /// 按 <see cref="FloatByteOrder"/> 还原为 float 后转 double。
    /// 读取失败返回 <see cref="ForceReading.Invalid"/> 并累计失败计数。
    /// </remarks>
    public async Task<ForceReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_config is null)
        {
            Log.Warning("500B 力值模块：未连接，无法读取");
            return ForceReading.Invalid;
        }

        try
        {
            ushort[] registers = await _transport.ReadHoldingRegistersAsync(
                _config.SlaveAddress,
                ForceModuleRegisterMap.GrossFloatAddr,
                2,
                cancellationToken).ConfigureAwait(false);

            if (registers is null || registers.Length < 2)
            {
                return HandleReadFailure("寄存器返回为空或长度不足");
            }

            float floatValue = RegistersToFloat(registers, _config.FloatByteOrder);
            double value = floatValue;

            Interlocked.Exchange(ref _consecutiveFailures, 0);

            Log.Debug("500B 力值模块：读取成功，值 {Value} {Unit}", value, "kN");

            return new ForceReading
            {
                Value = value,
                Unit = "kN",
                Timestamp = DateTime.UtcNow,
                IsValid = true
            };
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HandleReadFailure($"读取异常：{ex.Message}");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 使用 <see cref="CancellationTokenSource"/> 控制轮询生命周期。
    /// 轮询在后台线程（Task.Run）执行。如已在轮询中则直接返回不重复启动。
    /// </remarks>
    public Task StartPollingAsync(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        if (_pollingCts is not null)
        {
            Log.Debug("500B 力值模块：已在轮询中，忽略重复启动请求");
            return Task.CompletedTask;
        }

        _pollingCts = new CancellationTokenSource();
        var cts = _pollingCts;

        Log.Information("500B 力值模块：启动轮询，间隔 {IntervalMs}ms", (int)interval.TotalMilliseconds);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var reading = await ReadAsync(cts.Token).ConfigureAwait(false);
                    OnReadingReceived(reading);
                    await Task.Delay(interval, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，无需处理
            }
            catch (Exception ex)
            {
                Log.Error(ex, "500B 力值模块：轮询循环发生未预期异常");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 安全处理 <see cref="_pollingCts"/> 为 null 的情况。
    /// </remarks>
    public Task StopPollingAsync(CancellationToken cancellationToken = default)
    {
        if (_pollingCts is null)
        {
            return Task.CompletedTask;
        }

        Log.Information("500B 力值模块：停止轮询");
        _pollingCts.Cancel();
        _pollingCts.Dispose();
        _pollingCts = null;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 向 500B 多功能寄存器 <see cref="ForceModuleRegisterMap.MultiFunctionAddr"/> (0x062A)
    /// 写入 <see cref="ForceModuleRegisterMap.MultiFunctionZero"/> (1)，触发清零指令。
    /// 未连接时返回 false 并记录警告。
    /// </remarks>
    public async Task<bool> ZeroAsync(CancellationToken cancellationToken = default)
    {
        if (_config is null)
        {
            Log.Warning("500B 力值模块：未连接，无法执行清零");
            return false;
        }

        Log.Information("500B 力值模块：执行清零（写 0x062A=1）");

        try
        {
            bool ok = await _transport.WriteSingleRegisterAsync(
                _config.SlaveAddress,
                ForceModuleRegisterMap.MultiFunctionAddr,
                (ushort)ForceModuleRegisterMap.MultiFunctionZero,
                cancellationToken).ConfigureAwait(false);

            if (ok)
            {
                Log.Information("500B 力值模块：清零成功");
            }
            else
            {
                Log.Warning("500B 力值模块：清零失败（写入未确认）");
            }

            return ok;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "500B 力值模块：清零异常");
            return false;
        }
    }

    /// <summary>
    /// 释放资源：停止轮询、关闭传输层。
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放资源的受保护方法。
    /// </summary>
    /// <param name="disposing">是否由 Dispose 调用（而非终结器）。</param>
    private void Dispose(bool disposing)
    {
        if (!disposing) return;

        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _pollingCts = null;

        // 仅当本服务拥有传输层时才释放（DI 管理的共享实例不由本服务释放）
        if (_ownsTransport)
        {
            try
            {
                _transport.Dispose();
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "500B 力值模块：释放传输层异常");
            }
        }
    }

    // ─── 事件触发 ───

    /// <summary>触发 ReadingReceived 事件。</summary>
    private void OnReadingReceived(ForceReading reading)
    {
        ReadingReceived?.Invoke(this, reading);
    }

    /// <summary>触发 StateChanged 事件。</summary>
    private void OnStateChanged(ConnectionState state)
    {
        StateChanged?.Invoke(this, state);
    }

    // ─── 读取失败处理与断线重连 ───

    /// <summary>
    /// 处理读取失败：累计失败计数，达到阈值时触发断线重连。
    /// </summary>
    /// <param name="reason">失败原因（用于日志）。</param>
    /// <returns><see cref="ForceReading.Invalid"/></returns>
    private ForceReading HandleReadFailure(string reason)
    {
        int failures = Interlocked.Increment(ref _consecutiveFailures);
        Log.Warning("500B 力值模块：读取失败（{Reason}），连续失败 {Count}/{Threshold}",
            reason, failures, Constants.ModbusFailureThreshold);

        if (failures >= Constants.ModbusFailureThreshold)
        {
            _ = ReconnectAsync();
        }

        return ForceReading.Invalid;
    }

    /// <summary>
    /// 断线重连（健壮性 R1）。按退避序列尝试重新打开串口。
    /// </summary>
    private async Task ReconnectAsync()
    {
        // 原子互斥：已有重连流程在进行则直接返回
        if (Interlocked.CompareExchange(ref _isReconnecting, 1, 0) != 0)
        {
            return;
        }

        if (_config is null)
        {
            Interlocked.Exchange(ref _isReconnecting, 0);
            return;
        }

        OnStateChanged(ConnectionState.Reconnecting);
        Log.Warning("500B 力值模块：连续失败达到阈值，开始断线重连");

        try
        {
            foreach (int delaySec in Constants.ReconnectBackoffSeconds)
            {
                // 先关闭旧连接
                try
                {
                    await _transport.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "500B 力值模块：重连前关闭串口异常（可忽略）");
                }

                Log.Information("500B 力值模块：{DelaySec}秒后尝试重连", delaySec);
                await Task.Delay(TimeSpan.FromSeconds(delaySec)).ConfigureAwait(false);

                bool ok = await _transport.OpenAsync(_config).ConfigureAwait(false);
                if (ok)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    Log.Information("500B 力值模块：重连成功");
                    OnStateChanged(ConnectionState.Connected);
                    return;
                }

                Log.Warning("500B 力值模块：重连尝试失败，继续退避");
            }

            // 所有退避序列均失败
            Log.Error("500B 力值模块：重连耗尽所有退避尝试，状态置为 Failed");
            OnStateChanged(ConnectionState.Failed);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "500B 力值模块：重连过程发生异常");
            OnStateChanged(ConnectionState.Failed);
        }
        finally
        {
            Interlocked.Exchange(ref _isReconnecting, 0);
        }
    }

    // ─── 浮点寄存器转换 ───

    /// <summary>
    /// 将 32 位 IEEE 754 浮点数按指定字节序转换为 2 个 16 位 Modbus 寄存器值。
    /// </summary>
    /// <param name="value">待转换的浮点值。</param>
    /// <param name="order">目标字节序。</param>
    /// <returns>包含 2 个 ushort 的数组，对应 2 个 Modbus 保持寄存器（寄存器内部大端）。</returns>
    /// <remarks>
    /// <para>算法步骤（C2 关键算法注释）：</para>
    /// <list type="number">
    /// <item>用 <see cref="BitConverter.GetBytes(float)"/> 取得 4 字节原始表示。</item>
    /// <item>归一化为 ABCD 记法：A=最高位字节(MSB)，B，C，D=最低位字节(LSB)。</item>
    /// <item>按 <paramref name="order"/> 指定的字节序重排 4 字节，拆为 2 个 ushort
    /// （每个寄存器内部大端：高字节在前）。</item>
    /// </list>
    /// <para>字节序图示（Reg1 高/低, Reg2 高/低）：</para>
    /// <list type="bullet">
    /// <item>ABCD → Reg1=[A,B], Reg2=[C,D]（大端，默认）</item>
    /// <item>DCBA → Reg1=[D,C], Reg2=[B,A]（小端）</item>
    /// <item>BADC → Reg1=[B,A], Reg2=[D,C]（字内小端）</item>
    /// <item>CDAB → Reg1=[C,D], Reg2=[A,B]（字交换）</item>
    /// </list>
    /// <para>与 <see cref="RegistersToFloat"/> 互逆。</para>
    /// </remarks>
    internal static ushort[] FloatToRegisters(float value, FloatByteOrder order)
    {
        // BitConverter.GetBytes 在小端系统（Windows/x86/x64）返回 [LSB, ..., MSB]
        byte[] raw = BitConverter.GetBytes(value);

        // 归一化为 ABCD 记法：A=MSB, D=LSB
        byte a, b, c, d;
        if (BitConverter.IsLittleEndian)
        {
            a = raw[3]; b = raw[2]; c = raw[1]; d = raw[0];
        }
        else
        {
            a = raw[0]; b = raw[1]; c = raw[2]; d = raw[3];
        }

        // 按字节序重排并组装为 2 个 ushort（寄存器内部大端：高字节 << 8 | 低字节）
        return order switch
        {
            FloatByteOrder.ABCD => [(ushort)((a << 8) | b), (ushort)((c << 8) | d)],
            FloatByteOrder.DCBA => [(ushort)((d << 8) | c), (ushort)((b << 8) | a)],
            FloatByteOrder.BADC => [(ushort)((b << 8) | a), (ushort)((d << 8) | c)],
            FloatByteOrder.CDAB => [(ushort)((c << 8) | d), (ushort)((a << 8) | b)],
            _ => [(ushort)((a << 8) | b), (ushort)((c << 8) | d)],
        };
    }

    /// <summary>
    /// 将 2 个 16 位 Modbus 寄存器按指定字节序还原为 32 位 IEEE 754 浮点数。
    /// </summary>
    /// <param name="registers">2 个 ushort 寄存器值。</param>
    /// <param name="order">浮点数字节序。</param>
    /// <returns>还原后的 float 值。</returns>
    /// <remarks>
    /// 算法：2 个 ushort → 4 字节 → 按 FloatByteOrder 重排 → BitConverter.ToSingle()。
    /// 字节序含义：A=寄存器0高字节, B=寄存器0低字节, C=寄存器1高字节, D=寄存器1低字节。
    /// </remarks>
    internal static float RegistersToFloat(ushort[] registers, FloatByteOrder order)
    {
        // 拆分 2 个 ushort 为 4 字节（大端序：高字节在前）
        byte a = (byte)(registers[0] >> 8);   // 寄存器0 高字节
        byte b = (byte)(registers[0] & 0xFF);  // 寄存器0 低字节
        byte c = (byte)(registers[1] >> 8);   // 寄存器1 高字节
        byte d = (byte)(registers[1] & 0xFF);  // 寄存器1 低字节

        // 按字节序重排后放入小端序的 byte[]（x86 为小端：bytes[0]=LSB, bytes[3]=MSB）。
        // 各字节序下寄存器存储的 IEEE 字节排列：
        //   ABCD: reg0=[A,B] reg1=[C,D] → 小端 bytes = [D,C,B,A]
        //   DCBA: reg0=[D,C] reg1=[B,A] → 小端 bytes = [D,C,B,A]
        //   BADC: reg0=[B,A] reg1=[D,C] → 小端 bytes = [D,C,B,A]
        //   CDAB: reg0=[C,D] reg1=[A,B] → 小端 bytes = [D,C,B,A]
        // 其中 a/b/c/d 是寄存器拆分变量（非 IEEE 标记），D=IEEE LSB。
        byte[] bytes = new byte[4];
        (bytes[0], bytes[1], bytes[2], bytes[3]) = order switch
        {
            FloatByteOrder.ABCD => (d, c, b, a),
            FloatByteOrder.DCBA => (a, b, c, d),
            FloatByteOrder.BADC => (c, d, a, b),
            FloatByteOrder.CDAB => (b, a, d, c),
            _ => (d, c, b, a)
        };

        return BitConverter.ToSingle(bytes, 0);
    }
}
