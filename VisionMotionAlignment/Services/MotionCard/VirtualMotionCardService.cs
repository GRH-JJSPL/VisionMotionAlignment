using MotionShared.Dtos;
using Serilog;
using VisionMotionAlignment.Models;
using VisionMotionAlignment.Services.Interfaces;

namespace VisionMotionAlignment.Services.MotionCard;

/// <summary>
/// 虚拟运动控制卡服务：模拟固高 GTS 3 轴运动（无真实硬件时跑通联动流程）。
/// </summary>
public sealed class VirtualMotionCardService : IMotionCardService
{
    /// <summary>指令串行化信号量（T2）：保证同一时刻只有一个运动指令在执行。</summary>
    private readonly SemaphoreSlim _cmdLock = new(1, 1);

    /// <summary>当前连接状态（volatile，多线程安全读取）。</summary>
    private volatile ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>3 个轴的当前位置（mm），volatile 保证跨线程可见。</summary>
    private readonly double[] _axisPos = new double[4]; // 索引 1~3 使用，0 占位

    /// <summary>3 个轴的当前速度（mm/s）。</summary>
    private readonly double[] _axisVel = new double[4];

    /// <summary>3 个轴的报警标志。</summary>
    private readonly bool[] _axisAlarm = new bool[4];

    /// <summary>轴状态周期性推送定时器。</summary>
    private Timer? _statusTimer;

    /// <summary>轴状态推送间隔（ms）。</summary>
    private const int StatusPushIntervalMs = 200;

    /// <summary>是否已打开控制卡（GT_Open 模拟）。</summary>
    private volatile bool _isOpen;

    /// <inheritdoc/>
    public event EventHandler<ConnectionState>? StateChanged;

    /// <inheritdoc/>
    public event Action<AxisStatusPush>? AxisStatusReceived;

    /// <inheritdoc/>
    public ConnectionState State => _state;

    /// <inheritdoc/>
    public bool IsConnected => _state == ConnectionState.Connected;

    /// <summary>
    /// 构造虚拟运动控制卡服务。当前无注入依赖，预留后续扩展。
    /// </summary>
    public VirtualMotionCardService()
    {
    }

    /// <inheritdoc/>
    public Task ConnectAsync(string ip, int port, CancellationToken ct = default)
    {
        // 虚拟卡不真正连接 TCP，直接进入 Connected 状态
        TransitionTo(ConnectionState.Connected);
        _isOpen = true;
        Log.Information("虚拟运动控制卡：已连接（模拟），3 轴就绪");

        // 启动轴状态周期性推送
        _statusTimer?.Dispose();
        _statusTimer = new Timer(_ => PushAxisStatus(), null, StatusPushIntervalMs, StatusPushIntervalMs);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisconnectAsync()
    {
        _statusTimer?.Dispose();
        _statusTimer = null;
        _isOpen = false;
        TransitionTo(ConnectionState.Disconnected);
        Log.Information("虚拟运动控制卡：已断开");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<bool> OpenAsync()
    {
        await _cmdLock.WaitAsync();
        try
        {
            _isOpen = true;
            Log.Information("虚拟运动控制卡：GT_Open 成功");
            return true;
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CloseAsync()
    {
        await _cmdLock.WaitAsync();
        try
        {
            _isOpen = false;
            Log.Information("虚拟运动控制卡：GT_Close 成功");
            return true;
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> HomeAsync(int axis)
    {
        await _cmdLock.WaitAsync();
        try
        {
            if (!CheckAxis(axis)) return false;
            Log.Information("虚拟运动控制卡：轴 {Axis} 回零", axis);
            _axisPos[axis] = 0;
            _axisVel[axis] = 0;
            PushAxisStatus();
            return true;
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> MoveRelAsync(int axis, double dist, double vel)
    {
        return await ExecuteMoveAsync(axis, async () =>
        {
            // 模拟运动耗时：距离/速度，留最小 100ms
            double seconds = Math.Max(0.1, Math.Abs(dist) / Math.Max(1, Math.Abs(vel)));
            _axisVel[axis] = vel;
            PushAxisStatus();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            _axisPos[axis] += dist;
            _axisVel[axis] = 0;
            PushAxisStatus();
            Log.Information("虚拟运动控制卡：轴 {Axis} 相对移动 {Dist}mm 完成", axis, dist);
        });
    }

    /// <inheritdoc/>
    public async Task<bool> MoveAbsAsync(int axis, double pos, double vel)
    {
        return await ExecuteMoveAsync(axis, async () =>
        {
            double dist = pos - _axisPos[axis];
            double seconds = Math.Max(0.1, Math.Abs(dist) / Math.Max(1, Math.Abs(vel)));
            _axisVel[axis] = vel;
            PushAxisStatus();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            _axisPos[axis] = pos;
            _axisVel[axis] = 0;
            PushAxisStatus();
            Log.Information("虚拟运动控制卡：轴 {Axis} 绝对定位 {Pos}mm 完成", axis, pos);
        });
    }

    /// <inheritdoc/>
    public async Task<bool> StopAsync(int axis)
    {
        await _cmdLock.WaitAsync();
        try
        {
            if (!CheckAxis(axis)) return false;
            _axisVel[axis] = 0;
            PushAxisStatus();
            Log.Information("虚拟运动控制卡：轴 {Axis} 停止", axis);
            return true;
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> EmergencyStopAsync()
    {
        await _cmdLock.WaitAsync();
        try
        {
            for (int a = 1; a <= 3; a++)
            {
                _axisVel[a] = 0;
            }
            PushAxisStatus();
            Log.Information("虚拟运动控制卡：急停所有轴");
            return true;
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ClearAlarmAsync(int axis)
    {
        await _cmdLock.WaitAsync();
        try
        {
            if (!CheckAxis(axis)) return false;
            _axisAlarm[axis] = false;
            PushAxisStatus();
            Log.Information("虚拟运动控制卡：轴 {Axis} 清报警", axis);
            return true;
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    /// <summary>
    /// 释放资源：停止状态推送定时器。
    /// </summary>
    public void Dispose()
    {
        _statusTimer?.Dispose();
        _statusTimer = null;
    }

    /// <summary>
    /// 执行一次运动指令的公共包装：串行化 + 检查连接/开卡 + 异常兜底。
    /// </summary>
    private async Task<bool> ExecuteMoveAsync(int axis, Func<Task> moveAction)
    {
        await _cmdLock.WaitAsync();
        try
        {
            if (!CheckAxis(axis)) return false;
            if (!_isOpen)
            {
                Log.Warning("虚拟运动控制卡：控制卡未打开（GT_Open），无法运动");
                return false;
            }
            await moveAction();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "虚拟运动控制卡：运动指令异常 Axis={Axis}", axis);
            return false;
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    /// <summary>
    /// 校验轴号合法性（1~3）。
    /// </summary>
    private bool CheckAxis(int axis)
    {
        if (axis < 1 || axis > 3)
        {
            Log.Warning("虚拟运动控制卡：非法轴号 {Axis}（应为 1~3）", axis);
            return false;
        }
        return true;
    }

    /// <summary>
    /// 周期性推送所有轴的当前状态（模拟控制卡 axis_status 推送）。
    /// </summary>
    private void PushAxisStatus()
    {
        var push = new AxisStatusPush();
        for (int a = 1; a <= 3; a++)
        {
            push.Axes.Add(new AxisStatusDto
            {
                Axis = a,
                Pos = _axisPos[a],
                Vel = _axisVel[a],
                Status = _axisVel[a] != 0 ? "Moving" : "Idle",
                Alarm = _axisAlarm[a]
            });
        }
        AxisStatusReceived?.Invoke(push);
    }

    /// <summary>
    /// 状态机转换。更新 <see cref="_state"/> 并在状态实际变化时触发 <see cref="StateChanged"/> 事件。
    /// </summary>
    private void TransitionTo(ConnectionState newState)
    {
        var old = _state;
        _state = newState;
        if (old != newState)
        {
            Log.Information("虚拟运动控制卡状态转换 {Old} → {New}", old, newState);
            StateChanged?.Invoke(this, newState);
        }
    }
}
