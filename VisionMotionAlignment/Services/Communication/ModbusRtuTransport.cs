using System.IO.Ports;
using Serilog;
using VisionMotionAlignment.Infrastructure;
using VisionMotionAlignment.Models.Communication;
using VisionMotionAlignment.Services.Interfaces;

namespace VisionMotionAlignment.Services.Communication;

/// <summary>
/// Modbus RTU 传输层：基于 SerialPort 读写保持寄存器，内置 CRC16、串口锁、超时重试与断连判定。
/// 功能码 0x03 读保持寄存器 / 0x06 写单个 / 0x10 写多个；CRC16-Modbus（0xA001，低字节在前）。
/// </summary>
public sealed class ModbusRtuTransport : IModbusRtuTransport
{
    private readonly object _lock = new();
    private SerialPort? _serialPort;
    private SerialPortConfig? _config;
    private int _consecutiveFailures;

    /// <summary>
    /// 获取串口是否已打开。
    /// </summary>
    public bool IsOpen => _serialPort?.IsOpen ?? false;

    /// <summary>
    /// 使用指定串口配置打开串口。若串口已打开则先关闭再重新打开。
    /// </summary>
    /// <param name="config">串口配置（端口名、波特率、数据位、停止位、奇偶校验、超时等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功打开返回 true；否则返回 false。</returns>
    public Task<bool> OpenAsync(SerialPortConfig config, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            try
            {
                if (_serialPort?.IsOpen == true)
                {
                    _serialPort.Close();
                }

                _serialPort = new SerialPort(config.PortName, config.BaudRate, config.Parity, config.DataBits, config.StopBits)
                {
                    ReadTimeout = config.ReadTimeoutMs,
                    WriteTimeout = config.WriteTimeoutMs,
                };

                _serialPort.Open();
                _config = config;
                _consecutiveFailures = 0;

                Log.Information("Modbus RTU 串口已打开：{PortName}, 波特率 {BaudRate}",
                    config.PortName, config.BaudRate);

                //返回已完成的Task，值为 false，接口要求
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Modbus RTU 串口打开失败：{PortName}", config.PortName);
                return Task.FromResult(false);
            }
        }
    }

    /// <summary>
    /// 关闭当前已打开的串口并释放资源。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            try
            {
                if (_serialPort?.IsOpen == true)
                {
                    _serialPort.Close();
                    Log.Information("Modbus RTU 串口已关闭");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Modbus RTU 串口关闭异常");
            }
        }

        //返回一个已完成的Task
        return Task.CompletedTask;
    }

    /// <summary>
    /// 读取保持寄存器（Modbus 功能码 03）。支持超时重试，CRC 校验失败丢弃帧返回空数组。
    /// </summary>
    /// <param name="slaveAddress">从站地址（1~247）。</param>
    /// <param name="startAddress">起始寄存器地址。</param>
    /// <param name="quantity">读取数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读到的寄存器值数组；CRC 校验失败或超时重试耗尽时返回空数组。</returns>
    public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress, ushort startAddress, ushort quantity, CancellationToken cancellationToken = default)
    {
        const byte functionCode = 0x03;

        lock (_lock)
        {
            if (!IsOpen)
            {
                Log.Warning("Modbus RTU 串口未打开，无法读取");
                return Task.FromResult(Array.Empty<ushort>());
            }

            // 构建请求帧：[从站地址, 0x03, 起始地址高, 起始地址低, 数量高, 数量低, CRC低, CRC高]
            // 预留末尾 2 字节给 CRC（AppendCrc 假设 frame.Length-2 为数据长度）
            byte[] request = new byte[8];
            request[0] = slaveAddress;
            request[1] = functionCode;
            request[2] = (byte)(startAddress >> 8);
            request[3] = (byte)(startAddress & 0xFF);
            request[4] = (byte)(quantity >> 8);
            request[5] = (byte)(quantity & 0xFF);
            AppendCrc(request);

            for (int attempt = 0; attempt <= Constants.ModbusRetryCount; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Task.FromResult(Array.Empty<ushort>());

                try
                {
                    // 指数退避：首次不等待，后续每次等待 100 * 2^(attempt-1) ms
                    if (attempt > 0)
                    {
                        int backoffMs = 100 * (1 << (attempt - 1));
                        Thread.Sleep(backoffMs);
                    }

                    _serialPort!.DiscardInBuffer();      // 1. 清空接收缓冲区
                    _serialPort.DiscardOutBuffer();      // 2. 清空发送缓冲区

                    // 发送数据：从 request 数组的第0位开始，发送 Length 长度的数据
                    _serialPort.Write(request, 0, request.Length);

                    Log.Debug("Modbus RTU 发送读请求：从站 {SlaveAddress}，功能码 {FunctionCode}，起始 {StartAddress}，数量 {Quantity}",
                        slaveAddress, functionCode, startAddress, quantity);

                    // 期望响应长度：1(从站) + 1(功能码) + 1(字节数) + quantity*2(数据) + 2(CRC)
                    int expectedLength = 3 + quantity * 2 + 2;
                    byte[] response = ReadResponse(expectedLength, _config!.ReadTimeoutMs);

                    if (response.Length == 0)
                    {
                        Log.Warning("Modbus RTU 读响应超时：从站 {SlaveAddress}，功能码 {FunctionCode}，第 {Attempt} 次",
                            slaveAddress, functionCode, attempt + 1);
                        continue;
                    }

                    // CRC 校验（R3：校验失败丢弃帧，不抛异常）
                    if (!VerifyCrc(response))
                    {
                        Log.Warning("Modbus RTU 读响应 CRC 校验失败：从站 {SlaveAddress}，功能码 {FunctionCode}",
                            slaveAddress, functionCode);
                        continue;
                    }

                    // 从站地址检查
                    if (response[0] != slaveAddress)
                    {
                        Log.Warning("Modbus RTU 读响应从站地址不匹配：期望 {SlaveAddress}，实际 {ActualSlave}",
                            slaveAddress, response[0]);
                        continue;
                    }

                    // 异常响应检查（功能码最高位为 1 表示异常响应，须在功能码匹配检查之前）
                    if ((response[1] & 0x80) != 0)
                    {
                        byte exceptionCode = response.Length > 2 ? response[2] : (byte)0;
                        Log.Warning("Modbus RTU 从站返回异常：从站 {SlaveAddress}，功能码 {FunctionCode}，异常码 {ExceptionCode}",
                            slaveAddress, functionCode, exceptionCode);
                        continue;
                    }

                    // 功能码匹配检查
                    if (response[1] != functionCode)
                    {
                        Log.Warning("Modbus RTU 读响应功能码不匹配：期望 {FunctionCode}，实际 {ActualFc}",
                            functionCode, response[1]);
                        continue;
                    }

                    int byteCount = response[2];
                    ushort[] registers = new ushort[quantity];
                    for (int i = 0; i < quantity; i++)
                    {
                        registers[i] = (ushort)((response[3 + i * 2] << 8) | response[4 + i * 2]);
                    }

                    _consecutiveFailures = 0;
                    return Task.FromResult(registers);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Modbus RTU 读保持寄存器异常：从站 {SlaveAddress}，功能码 {FunctionCode}，第 {Attempt} 次",
                        slaveAddress, functionCode, attempt + 1);
                }
            }

            // 所有重试耗尽
            RecordFailure();
            return Task.FromResult(Array.Empty<ushort>());
        }
    }

    /// <summary>
    /// 写单个寄存器（Modbus 功能码 06）。支持超时重试，CRC 校验失败返回 false。
    /// </summary>
    /// <param name="slaveAddress">从站地址（1~247）。</param>
    /// <param name="address">寄存器地址。</param>
    /// <param name="value">待写入的值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入成功返回 true；否则返回 false。</returns>
    public Task<bool> WriteSingleRegisterAsync(byte slaveAddress, ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        const byte functionCode = 0x06;

        lock (_lock)
        {
            if (!IsOpen)
            {
                Log.Warning("Modbus RTU 串口未打开，无法写入");
                return Task.FromResult(false);
            }

            // 构建请求帧：[从站地址, 0x06, 地址高, 地址低, 值高, 值低, CRC低, CRC高]
            // 预留末尾 2 字节给 CRC
            byte[] request = new byte[8];
            request[0] = slaveAddress;
            request[1] = functionCode;
            request[2] = (byte)(address >> 8);
            request[3] = (byte)(address & 0xFF);
            request[4] = (byte)(value >> 8);
            request[5] = (byte)(value & 0xFF);
            AppendCrc(request);

            for (int attempt = 0; attempt <= Constants.ModbusRetryCount; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Task.FromResult(false);

                try
                {
                    if (attempt > 0)
                    {
                        int backoffMs = 100 * (1 << (attempt - 1));
                        Thread.Sleep(backoffMs);
                    }

                    _serialPort!.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();
                    _serialPort.Write(request, 0, request.Length);

                    Log.Debug("Modbus RTU 发送写单寄存器请求：从站 {SlaveAddress}，功能码 {FunctionCode}，地址 {Address}，值 {Value}",
                        slaveAddress, functionCode, address, value);

                    // 写单个寄存器响应为请求的回显：8 字节
                    byte[] response = ReadResponse(8, _config!.ReadTimeoutMs);

                    if (response.Length == 0)
                    {
                        Log.Warning("Modbus RTU 写单寄存器响应超时：从站 {SlaveAddress}，功能码 {FunctionCode}，第 {Attempt} 次",
                            slaveAddress, functionCode, attempt + 1);
                        continue;
                    }

                    if (!VerifyCrc(response))
                    {
                        Log.Warning("Modbus RTU 写单寄存器响应 CRC 校验失败：从站 {SlaveAddress}，功能码 {FunctionCode}",
                            slaveAddress, functionCode);
                        continue;
                    }

                    // 从站地址检查
                    if (response[0] != slaveAddress)
                    {
                        Log.Warning("Modbus RTU 写单寄存器响应从站地址不匹配：期望 {SlaveAddress}，实际 {ActualSlave}",
                            slaveAddress, response[0]);
                        continue;
                    }

                    // 异常响应检查（功能码最高位为 1）
                    if ((response[1] & 0x80) != 0)
                    {
                        byte exceptionCode = response.Length > 2 ? response[2] : (byte)0;
                        Log.Warning("Modbus RTU 写单寄存器从站返回异常：从站 {SlaveAddress}，功能码 {FunctionCode}，异常码 {ExceptionCode}",
                            slaveAddress, functionCode, exceptionCode);
                        continue;
                    }

                    // 验证回显：功能码、地址、值应与请求一致
                    if (response[1] == functionCode
                        && response[2] == request[2] && response[3] == request[3]
                        && response[4] == request[4] && response[5] == request[5])
                    {
                        _consecutiveFailures = 0;
                        return Task.FromResult(true);
                    }

                    Log.Warning("Modbus RTU 写单寄存器响应不匹配：从站 {SlaveAddress}，功能码 {FunctionCode}",
                        slaveAddress, functionCode);
                    continue;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Modbus RTU 写单寄存器异常：从站 {SlaveAddress}，功能码 {FunctionCode}，第 {Attempt} 次",
                        slaveAddress, functionCode, attempt + 1);
                }
            }

            RecordFailure();
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// 写多个寄存器（Modbus 功能码 10）。支持超时重试，CRC 校验失败返回 false。
    /// </summary>
    /// <param name="slaveAddress">从站地址（1~247）。</param>
    /// <param name="startAddress">起始寄存器地址。</param>
    /// <param name="values">待写入的值数组。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入成功返回 true；否则返回 false。</returns>
    public Task<bool> WriteMultipleRegistersAsync(byte slaveAddress, ushort startAddress, ushort[] values, CancellationToken cancellationToken = default)
    {
        const byte functionCode = 0x10;

        lock (_lock)
        {
            if (!IsOpen)
            {
                Log.Warning("Modbus RTU 串口未打开，无法写入");
                return Task.FromResult(false);
            }

            // 防御：values 为 null 或空数组时直接返回 false（m1b 边界校验）
            if (values is null || values.Length == 0)
            {
                Log.Warning("Modbus RTU 写多寄存器：values 为 null 或空，从站 {SlaveAddress}", slaveAddress);
                return Task.FromResult(false);
            }

            ushort quantity = (ushort)values.Length;
            byte byteCount = (byte)(quantity * 2);

            // 构建请求帧：[从站地址, 0x10, 起始地址高, 起始地址低, 数量高, 数量低, 字节数, 数据..., CRC低, CRC高]
            // 预留末尾 2 字节给 CRC
            byte[] request = new byte[7 + byteCount + 2];
            request[0] = slaveAddress;
            request[1] = functionCode;
            request[2] = (byte)(startAddress >> 8);
            request[3] = (byte)(startAddress & 0xFF);
            request[4] = (byte)(quantity >> 8);
            request[5] = (byte)(quantity & 0xFF);
            request[6] = byteCount;

            for (int i = 0; i < quantity; i++)
            {
                request[7 + i * 2] = (byte)(values[i] >> 8);
                request[8 + i * 2] = (byte)(values[i] & 0xFF);
            }

            AppendCrc(request);

            for (int attempt = 0; attempt <= Constants.ModbusRetryCount; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Task.FromResult(false);

                try
                {
                    if (attempt > 0)
                    {
                        int backoffMs = 100 * (1 << (attempt - 1));
                        Thread.Sleep(backoffMs);
                    }

                    _serialPort!.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();
                    _serialPort.Write(request, 0, request.Length);

                    Log.Debug("Modbus RTU 发送写多寄存器请求：从站 {SlaveAddress}，功能码 {FunctionCode}，起始 {StartAddress}，数量 {Quantity}",
                        slaveAddress, functionCode, startAddress, quantity);

                    // 写多个寄存器响应：8 字节 [从站, 0x10, 起始地址高, 起始地址低, 数量高, 数量低, CRC低, CRC高]
                    byte[] response = ReadResponse(8, _config!.ReadTimeoutMs);

                    if (response.Length == 0)
                    {
                        Log.Warning("Modbus RTU 写多寄存器响应超时：从站 {SlaveAddress}，功能码 {FunctionCode}，第 {Attempt} 次",
                            slaveAddress, functionCode, attempt + 1);
                        continue;
                    }

                    if (!VerifyCrc(response))
                    {
                        Log.Warning("Modbus RTU 写多寄存器响应 CRC 校验失败：从站 {SlaveAddress}，功能码 {FunctionCode}",
                            slaveAddress, functionCode);
                        continue;
                    }

                    // 从站地址检查
                    if (response[0] != slaveAddress)
                    {
                        Log.Warning("Modbus RTU 写多寄存器响应从站地址不匹配：期望 {SlaveAddress}，实际 {ActualSlave}",
                            slaveAddress, response[0]);
                        continue;
                    }

                    // 异常响应检查（功能码最高位为 1）
                    if ((response[1] & 0x80) != 0)
                    {
                        byte exceptionCode = response.Length > 2 ? response[2] : (byte)0;
                        Log.Warning("Modbus RTU 写多寄存器从站返回异常：从站 {SlaveAddress}，功能码 {FunctionCode}，异常码 {ExceptionCode}",
                            slaveAddress, functionCode, exceptionCode);
                        continue;
                    }

                    // 验证响应：功能码、起始地址、数量应与请求一致
                    if (response[1] == functionCode
                        && response[2] == request[2] && response[3] == request[3]
                        && response[4] == request[4] && response[5] == request[5])
                    {
                        _consecutiveFailures = 0;
                        return Task.FromResult(true);
                    }

                    Log.Warning("Modbus RTU 写多寄存器响应不匹配：从站 {SlaveAddress}，功能码 {FunctionCode}",
                        slaveAddress, functionCode);
                    continue;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Modbus RTU 写多寄存器异常：从站 {SlaveAddress}，功能码 {FunctionCode}，第 {Attempt} 次",
                        slaveAddress, functionCode, attempt + 1);
                }
            }

            RecordFailure();
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// 计算 Modbus RTU CRC16 校验值。
    /// </summary>
    /// <remarks>
    /// CRC16-Modbus 算法原理：
    /// <list type="number">
    /// <item>预置 16 位 CRC 寄存器为 0xFFFF（全 1）。</item>
    /// <item>将数据帧的每个字节与 CRC 低 8 位异或。</item>
    /// <item>对 CRC 寄存器循环右移 1 位：若移出位为 1，则与多项式 0xA001 异或；
    /// 若移出位为 0，则不异或。重复 8 次。</item>
    /// <item>多项式 0xA001 是标准 Modbus 多项式 0x8005 的位反转（bit-reversed）形式，
    /// 适用于 LSB-first 串行传输。</item>
    /// </list>
    /// 最终结果低字节在前、高字节在后（Modbus RTU 规范）。
    /// </remarks>
    /// <param name="data">待计算的数据字节（不含 CRC 本身）。</param>
    /// <param name="offset">起始偏移。</param>
    /// <param name="length">参与计算的长度。</param>
    /// <returns>CRC16 值，低字节在低 8 位。</returns>
    internal static ushort CalculateCrc(byte[] data, int offset, int length)
    {
        ushort crc = 0xFFFF;

        for (int i = offset; i < offset + length; i++)
        {
            crc ^= data[i];

            for (int j = 0; j < 8; j++)
            {
                bool lsb = (crc & 0x0001) != 0;
                crc >>= 1;

                if (lsb)
                {
                    crc ^= 0xA001;
                }
            }
        }

        return crc;
    }

    /// <summary>
    /// 在帧末尾追加 CRC16 校验字节（低字节在前，高字节在后）。
    /// </summary>
    /// <param name="frame">帧字节数组，需预留最后 2 字节给 CRC。</param>
    internal static void AppendCrc(byte[] frame)
    {
        ushort crc = CalculateCrc(frame, 0, frame.Length - 2);
        frame[frame.Length - 2] = (byte)(crc & 0xFF);        // CRC 低字节
        frame[frame.Length - 1] = (byte)((crc >> 8) & 0xFF); // CRC 高字节
    }

    /// <summary>
    /// 校验帧的 CRC16 是否正确（R3：校验失败丢弃帧）。
    /// </summary>
    /// <param name="frame">完整帧（含末尾 2 字节 CRC）。</param>
    /// <returns>CRC 正确返回 true；否则返回 false。</returns>
    internal static bool VerifyCrc(byte[] frame)
    {
        if (frame.Length < 3)
            return false;

        ushort crc = CalculateCrc(frame, 0, frame.Length - 2);
        return frame[frame.Length - 2] == (byte)(crc & 0xFF)
            && frame[frame.Length - 1] == (byte)((crc >> 8) & 0xFF);
    }

    /// <summary>
    /// 从串口读取指定长度的响应数据，带超时保护。
    /// </summary>
    /// <param name="expectedLength">期望读取的字节总数。</param>
    /// <param name="timeoutMs">超时时间（毫秒）。</param>
    /// <returns>读取到的字节数组；超时返回空数组。</returns>
    private byte[] ReadResponse(int expectedLength, int timeoutMs)
    {
        byte[] buffer = new byte[expectedLength];
        int totalRead = 0;

        // 例如：startTick = 123456789（毫秒）
        // 表示系统已运行约 34.3 小时
        long startTick = Environment.TickCount64;

        while (totalRead < expectedLength)
        {
            long elapsed = Environment.TickCount64 - startTick;
            if (elapsed >= timeoutMs)
            {
                Log.Warning("Modbus RTU 读响应超时：已读 {TotalRead}/{ExpectedLength}，耗时 {ElapsedMs}ms",
                    totalRead, expectedLength, elapsed);
                return Array.Empty<byte>();
            }

            if (_serialPort!.BytesToRead > 0)       //检查缓冲区是否有数据可读
            {
                int bytesToRead = Math.Min(_serialPort.BytesToRead, expectedLength - totalRead);
                //写入目标数组，写入起始位置，写入数量
                int bytesRead = _serialPort.Read(buffer, totalRead, bytesToRead);
                totalRead += bytesRead;
            }
            else
            {
                Thread.Sleep(1);
            }
        }

        return buffer;
    }

    /// <summary>
    /// 记录连续失败次数，达到阈值时判定断连并记录日志。
    /// </summary>
    private void RecordFailure()
    {
        _consecutiveFailures++;
        Log.Warning("Modbus RTU 连续失败计数：{ConsecutiveFailures}/{FailureThreshold}",
            _consecutiveFailures, Constants.ModbusFailureThreshold);

        if (_consecutiveFailures >= Constants.ModbusFailureThreshold)
        {
            Log.Error("Modbus RTU 连续失败达到 {FailureThreshold} 次，判定断连",
                Constants.ModbusFailureThreshold);
        }
    }

    /// <summary>
    /// 释放串口资源。
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放串口资源的核心方法。
    /// </summary>
    /// <param name="disposing">是否由 Dispose() 调用（true）还是终结器调用（false）。</param>
    private void Dispose(bool disposing)
    {
        if (!disposing) return;

        try
        {
            if (_serialPort?.IsOpen == true)
            {
                _serialPort.Close();
            }

            _serialPort?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Modbus RTU 释放串口资源异常");
        }
    }
}
