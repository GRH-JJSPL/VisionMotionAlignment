namespace VisionMotionAlignment.Models;

/// <summary>
/// 工位标识。双工位系统中每个工位对应一台相机与一条定位管线。
/// </summary>
public enum WorkstationId
{
    /// <summary>工位 1</summary>
    Workstation1 = 1,

    /// <summary>工位 2</summary>
    Workstation2 = 2
}

/// <summary>
/// 连接状态。用于相机、运动控制卡、力值模块等设备的连接生命周期。
/// </summary>
public enum ConnectionState
{
    /// <summary>未初始化/未尝试连接</summary>
    Idle = 0,

    /// <summary>连接中</summary>
    Connecting = 1,

    /// <summary>已连接</summary>
    Connected = 2,

    /// <summary>正在重连（见健壮性 R1）</summary>
    Reconnecting = 3,

    /// <summary>已断开</summary>
    Disconnected = 4,

    /// <summary>连接失败（重试耗尽）</summary>
    Failed = 5
}

/// <summary>
/// Modbus 32 位浮点数字节序。
/// 与 500B 力值模块侧配置一致，默认 ABCD（大端）。
/// 字节序联调首日必须验证，否则浮点值解析错误。
/// </summary>
public enum FloatByteOrder
{
    /// <summary>大端 ABCD</summary>
    ABCD = 0,

    /// <summary>小端 DCBA</summary>
    DCBA = 1,

    /// <summary>字交换 BADC</summary>
    BADC = 2,

    /// <summary>字交换 CDAB</summary>
    CDAB = 3
}
