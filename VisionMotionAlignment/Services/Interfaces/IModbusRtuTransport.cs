using VisionMotionAlignment.Models.Communication;

namespace VisionMotionAlignment.Services.Interfaces;

/// <summary>
/// Modbus RTU 传输层接口。封装串口打开/关闭、保持寄存器读写，
/// 供 <see cref="IForceModuleService"/> 使用（力值模块通过 RS485 串口通讯）。
/// 实现 <see cref="IDisposable"/> 以确保串口资源被释放。
/// </summary>
/// <remarks>
/// 线程安全与健壮性约定：
/// <list type="bullet">
/// <item>内部加锁串行化所有请求，保证线程安全（见线程安全 T2）。</item>
/// <item>CRC 校验失败时丢弃该帧而不抛出异常（见健壮性 R3）。</item>
/// <item>单次请求支持超时 + 重试 + 退避策略（见健壮性 R2）。</item>
/// </list>
/// </remarks>
public interface IModbusRtuTransport : IDisposable
{
    /// <summary>
    /// 获取串口是否已打开。
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// 使用指定串口配置打开串口。
    /// </summary>
    /// <param name="config">串口配置（端口名、波特率、数据位、停止位、奇偶校验、超时等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功打开返回 true；否则返回 false。</returns>
    Task<bool> OpenAsync(SerialPortConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// 关闭当前已打开的串口。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取保持寄存器（Modbus 功能码 03）。
    /// </summary>
    /// <param name="slaveAddress">从站地址（1~247）。</param>
    /// <param name="startAddress">起始寄存器地址。</param>
    /// <param name="quantity">读取数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读到的寄存器值数组；CRC 校验失败或超时重试耗尽时返回空数组。</returns>
    Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress, ushort startAddress, ushort quantity, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写单个寄存器（Modbus 功能码 06）。
    /// </summary>
    /// <param name="slaveAddress">从站地址（1~247）。</param>
    /// <param name="address">寄存器地址。</param>
    /// <param name="value">待写入的值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入成功返回 true；否则返回 false。</returns>
    Task<bool> WriteSingleRegisterAsync(byte slaveAddress, ushort address, ushort value, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写多个寄存器（Modbus 功能码 10）。
    /// </summary>
    /// <param name="slaveAddress">从站地址（1~247）。</param>
    /// <param name="startAddress">起始寄存器地址。</param>
    /// <param name="values">待写入的值数组。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入成功返回 true；否则返回 false。</returns>
    Task<bool> WriteMultipleRegistersAsync(byte slaveAddress, ushort startAddress, ushort[] values, CancellationToken cancellationToken = default);
}
