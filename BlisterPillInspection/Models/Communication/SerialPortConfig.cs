using System.IO.Ports;

namespace BlisterPillInspection.Models.Communication;

/// <summary>
/// 串口（Modbus RTU）配置参数。
/// </summary>
public sealed class SerialPortConfig
{
    /// <summary>串口名（如 "COM3"）。</summary>
    public string PortName { get; init; } = "COM1";

    /// <summary>波特率。</summary>
    public int BaudRate { get; init; } = 9600;

    /// <summary>数据位。</summary>
    public int DataBits { get; init; } = 8;

    /// <summary>校验位。</summary>
    public Parity Parity { get; init; } = Parity.None;

    /// <summary>停止位。</summary>
    public StopBits StopBits { get; init; } = StopBits.One;

    /// <summary>Modbus 从站地址。</summary>
    public byte SlaveAddress { get; init; } = 1;

    /// <summary>32 位浮点数字节序。联调首日必须验证，否则浮点值解析错误。</summary>
    public FloatByteOrder FloatByteOrder { get; init; } = FloatByteOrder.ABCD;

    /// <summary>读超时（ms）。</summary>
    public int ReadTimeoutMs { get; init; } = 1000;

    /// <summary>写超时（ms）。</summary>
    public int WriteTimeoutMs { get; init; } = 500;

    /// <summary>
    /// 默认配置：COM1, 9600, 8, None, One, 从站 1, ABCD, 读 1000ms, 写 500ms。
    /// </summary>
    public static SerialPortConfig Default => new();
}
