using VisionMotionAlignment.Models.Communication;
using VisionMotionAlignment.Services.Interfaces;

namespace VisionMotionAlignment.Services.Communication;

/// <summary>
/// <see cref="IModbusRtuTransport"/> 的占位实现。所有方法返回默认值，
/// 构造函数不抛异常以确保应用可启动。后续 M4 阶段替换为真实 Modbus RTU 传输实现。
/// </summary>
public sealed class StubModbusRtuTransport : IModbusRtuTransport
{
    /// <summary>占位实现：始终返回 false（串口未打开）。</summary>
    public bool IsOpen => false;

    /// <summary>占位实现：始终返回 false（打开失败）。</summary>
    /// <param name="config">串口配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>始终返回 false。</returns>
    public Task<bool> OpenAsync(SerialPortConfig config, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>占位实现：直接返回已完成任务。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已完成的任务。</returns>
    public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>占位实现：返回空数组。</summary>
    /// <param name="slaveAddress">从站地址。</param>
    /// <param name="startAddress">起始寄存器地址。</param>
    /// <param name="quantity">读取数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>空数组。</returns>
    public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress, ushort startAddress, ushort quantity, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<ushort>());

    /// <summary>占位实现：始终返回 false（写入失败）。</summary>
    /// <param name="slaveAddress">从站地址。</param>
    /// <param name="address">寄存器地址。</param>
    /// <param name="value">待写入的值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>始终返回 false。</returns>
    public Task<bool> WriteSingleRegisterAsync(byte slaveAddress, ushort address, ushort value, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>占位实现：始终返回 false（写入失败）。</summary>
    /// <param name="slaveAddress">从站地址。</param>
    /// <param name="startAddress">起始寄存器地址。</param>
    /// <param name="values">待写入的值数组。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>始终返回 false。</returns>
    public Task<bool> WriteMultipleRegistersAsync(byte slaveAddress, ushort startAddress, ushort[] values, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

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
