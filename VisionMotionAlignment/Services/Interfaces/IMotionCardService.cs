using MotionShared.Dtos;
using VisionMotionAlignment.Models;

namespace VisionMotionAlignment.Services.Interfaces;

/// <summary>
/// 运动控制卡服务接口：封装固高 GTS 风格 API（GT_Open/MoveRel/Home 等），供编排器下发送料/分拣指令。
/// 轴号：1=传送带、2=NG 拨杆、3=OK 拨杆。方法名与真实固高 SDK 一致，便于后续替换为本地 SDK。
/// </summary>
public interface IMotionCardService
{
    /// <summary>
    /// 连接状态变化事件（线程安全 T5：跨线程触发）。
    /// 订阅方须自行经 DispatcherHelper 切 UI 线程后再修改 ViewModel 属性。
    /// </summary>
    event EventHandler<ConnectionState>? StateChanged;

    /// <summary>
    /// 轴状态推送事件。控制卡/模拟器周期性推送所有轴的当前位置/速度/状态。
    /// </summary>
    event Action<AxisStatusPush>? AxisStatusReceived;

    /// <summary>当前连接状态（Disconnected/Connecting/Connected/Reconnecting/Failed）。</summary>
    ConnectionState State { get; }

    /// <summary>是否已连接（TCP 链路存活且 GT_Open 成功）。</summary>
    bool IsConnected { get; }

    /// <summary>
    /// 建立 TCP 连接并打开控制卡（GT_Open）。
    /// 连接成功后 State 转为 Connected，触发 StateChanged 事件。
    /// </summary>
    /// <param name="ip">控制卡/模拟器 IP 地址（如 "127.0.0.1"）。</param>
    /// <param name="port">控制卡/模拟器 TCP 端口（如 5000）。</param>
    /// <param name="ct">取消令牌。</param>
    Task ConnectAsync(string ip, int port, CancellationToken ct = default);

    /// <summary>
    /// 断开 TCP 连接并释放资源。状态转为 Disconnected，同时阻止正在进行的断线重连。
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// 打开控制卡（GT_Open）。连接后须先调用此方法才能下发运动指令。
    /// 通常由 <see cref="ConnectAsync"/> 内部自动调用，也可手动调用以重新初始化。
    /// </summary>
    /// <returns>成功返回 true；未连接或指令失败返回 false。</returns>
    Task<bool> OpenAsync();

    /// <summary>
    /// 关闭控制卡（GT_Close）。释放控制卡资源，TCP 连接不断开。
    /// </summary>
    /// <returns>成功返回 true；未连接或指令失败返回 false。</returns>
    Task<bool> CloseAsync();

    /// <summary>
    /// 轴回零（GT_Home）。控制指定轴执行回原点操作，寻找零位开关/编码器原点。
    /// 回零是绝对定位的前提，未回零时 <see cref="MoveAbsAsync"/> 可能被控制卡拒绝。
    /// </summary>
    /// <param name="axis">轴号：1=传送带 2=NG拨杆 3=OK拨杆。</param>
    /// <returns>成功返回 true；未连接或指令失败返回 false。</returns>
    Task<bool> HomeAsync(int axis);

    /// <summary>
    /// 相对定位（GT_MoveRel）。从当前位置移动指定距离。
    /// 本项目的核心运动指令：编排器按轴号下发送料步进或拨杆行程。
    /// </summary>
    /// <param name="axis">轴号：1=传送带 2=NG拨杆 3=OK拨杆。</param>
    /// <param name="dist">相对移动距离（mm，正值正向、负值反向）。</param>
    /// <param name="vel">移动速度（mm/s）。</param>
    /// <returns>成功返回 true；未连接或指令失败返回 false。</returns>
    Task<bool> MoveRelAsync(int axis, double dist, double vel);

    /// <summary>
    /// 绝对定位（GT_MoveAbs）。控制指定轴运动到目标绝对坐标位置。
    /// 前提：轴已完成回零（GT_Home），否则绝对坐标无意义。
    /// </summary>
    /// <param name="axis">轴号：1=传送带 2=NG拨杆 3=OK拨杆。</param>
    /// <param name="pos">目标绝对位置（mm，相对于原点）。</param>
    /// <param name="vel">运动速度（mm/s）。</param>
    /// <returns>成功返回 true；未连接或指令失败返回 false。</returns>
    Task<bool> MoveAbsAsync(int axis, double pos, double vel);

    /// <summary>
    /// 停止单轴运动（GT_Stop）。以减速度平缓停止指定轴，非急停。
    /// </summary>
    /// <param name="axis">轴号：1=传送带 2=NG拨杆 3=OK拨杆。</param>
    /// <returns>成功返回 true；未连接或指令失败返回 false。</returns>
    Task<bool> StopAsync(int axis);

    /// <summary>
    /// 急停所有轴（GT_EmergencyStop）。立即停止所有轴运动，无减速过程。
    /// 仅在紧急情况下使用，正常停止应使用 <see cref="StopAsync"/>。
    /// </summary>
    /// <returns>成功返回 true；未连接或指令失败返回 false。</returns>
    Task<bool> EmergencyStopAsync();

    /// <summary>
    /// 清除轴报警（GT_ClearAlarm）。清除指定轴的报警状态（如超限、伺服故障等），
    /// 使轴恢复可操作状态。清除前应确保报警原因已排除。
    /// </summary>
    /// <param name="axis">轴号：1=传送带 2=NG拨杆 3=OK拨杆。</param>
    /// <returns>成功返回 true；未连接或指令失败返回 false。</returns>
    Task<bool> ClearAlarmAsync(int axis);
}
