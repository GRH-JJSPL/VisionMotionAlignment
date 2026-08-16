namespace BlisterPillInspection.Models.Force;

/// <summary>
/// 500B 力值测量模块 Modbus 寄存器地址映射表（集中管理，见开发文档 6.4.1）。
/// </summary>
/// <remarks>
/// <para>500B 出厂默认：从站地址 1，19200bps，8N1，Modbus-RTU。</para>
/// <para>地址值为 500B 说明书定义的协议地址（十六进制转十进制），可直接写入 Modbus RTU 帧。</para>
/// <para>字节序：<see cref="Communication.SerialPortConfig.FloatByteOrder"/>，默认 ABCD（大端），
/// 500B 实际字节序需联调确认，首版提供配置项。</para>
/// </remarks>
public static class ForceModuleRegisterMap
{
    // ─── 读测量值 ───

    /// <summary>GROSS 测量值,总力值（float 格式）。协议地址 0x0206 = 518。</summary>
    public const ushort GrossFloatAddr = 0x0206;

    /// <summary>GROSS 测量值（long 格式，备用）。协议地址 0x0606 = 1542。</summary>
    public const ushort GrossLongAddr = 0x0606;

    // ─── 写多功能寄存器 ───

    /// <summary>多功能寄存器（清零/标定/恢复出厂）。协议地址 0x062A = 1578。</summary>
    /// <remarks>
    /// 写入值含义：1=清零，11=把当前输入当砝码标定，30=恢复出厂，
    /// 31=恢复默认（慎用），40=保存出厂设置。
    /// </remarks>
    public const ushort MultiFunctionAddr = 0x062A;

    // ─── 通讯参数（只读参考，运行时通常不修改） ───

    /// <summary>通讯地址 Add。协议地址 0x0442 = 1090。</summary>
    public const ushort CommAddressAddr = 0x0442;

    /// <summary>通讯速率 bAud。协议地址 0x0440 = 1088。</summary>
    public const ushort BaudRateAddr = 0x0440;

    /// <summary>校验方式 oES。协议地址 0x0094 = 148。</summary>
    public const ushort ParityAddr = 0x0094;

    /// <summary>通讯协议 Pro。协议地址 0x043C = 1084。0=TC-ASCII, 1=Modbus, 2=SEND。</summary>
    public const ushort ProtocolAddr = 0x043C;

    /// <summary>停止位 StoP。协议地址 0x043E = 1086。</summary>
    public const ushort StopBitsAddr = 0x043E;

    /// <summary>单位 unit。协议地址 0x042A = 1066。1~6: t/kN/kg/lb/N/g。</summary>
    public const ushort UnitAddr = 0x042A;

    // ─── 多功能寄存器写入值常量 ───

    /// <summary>多功能寄存器写入值：清零。</summary>
    public const long MultiFunctionZero = 1;

    /// <summary>多功能寄存器写入值：把当前输入当砝码标定。</summary>
    public const long MultiFunctionCalibrate = 11;

    /// <summary>多功能寄存器写入值：恢复出厂。</summary>
    public const long MultiFunctionFactoryReset = 30;

    /// <summary>多功能寄存器写入值：恢复默认（慎用）。</summary>
    public const long MultiFunctionDefaultReset = 31;

    /// <summary>多功能寄存器写入值：保存出厂设置。</summary>
    public const long MultiFunctionSaveFactory = 40;

    // ─── 单位代码 → 字符串映射 ───

    /// <summary>
    /// 将 500B 单位代码转换为显示字符串。
    /// </summary>
    /// <param name="unitCode">单位代码（1~6）。</param>
    /// <returns>单位字符串；未知代码返回 "?"。</returns>
    public static string UnitCodeToString(int unitCode) => unitCode switch
    {
        1 => "t",
        2 => "kN",
        3 => "kg",
        4 => "lb",
        5 => "N",
        6 => "g",
        _ => "?"
    };
}
