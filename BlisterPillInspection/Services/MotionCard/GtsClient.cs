using System.Text.Json;
using MotionShared.Dtos;
using MotionShared.Protocol;

namespace BlisterPillInspection.Services.MotionCard;

/// <summary>
/// 固高 GTS 运动控制卡 TCP/JSON 客户端 —— 与运动控制卡（或模拟器）通讯的底层通道。
///
/// 【固高 GTS 运动控制卡基础知识】
/// 固高(GOOGLTECH) GTS 系列是国内工控领域主流的运动控制卡产品线，支持多轴独立/联动控制。
///   - 典型型号：GTS-400/P/GTS-800/P（4/8 轴）
///   - 通讯方式：PCI/PCIe 本地总线 或 TCP/IP 以太网（本项目使用 TCP/IP 方案）
///   - 核心指令集：GT_Open(打开) / GT_Close(关闭) / GT_Home(回零) /
///     GT_MoveAbs(绝对定位) / GT_MoveRel(相对定位) / GT_Stop(停止) /
///     GT_EmergencyStop(急停) / GT_ClearAlarm(清报警) 等
///   - 轴编号约定：1=传送带 2=NG拨杆 3=OK拨杆（泡罩检测 3 轴方案）
///
/// 【TCP/JSON 协议交互模式】
/// 本客户端基于 MotionShared 共享库的 <see cref="TcpClientBase"/> 实现，采用长度前缀 + JSON 消息帧：
///   - 帧格式：[4字节小端长度][UTF-8 JSON payload]
///   - 请求-响应模型：<see cref="CommandRequest"/>（含 Cmd + Args）→ <see cref="CommandResponse"/>（含 Status + Msg + Data）
///   - 推送模型：控制卡主动推送 <see cref="PushMessage"/>（如轴状态 "axis_status" → <see cref="AxisStatusPush"/>）
///   - 匹配机制：<see cref="TcpClientBase"/> 内部用 <see cref="TaskCompletionSource{TResult}"/> 按 SeqId 匹配响应
///
/// 数据流：上位机 → GtsClient.SendCommandAsync → TCP/JSON → 控制卡/模拟器 → CommandResponse → 上层
///         控制卡/模拟器 → PushMessage → GtsClient.HandlePush → AxisStatusReceived 事件 → 上层
///
/// 移植自 MotionControlApp.Services.GtsClient，改为本命名空间以避免依赖 WinExe 项目。
/// </summary>
public sealed class GtsClient : TcpClientBase
{
    /// <summary>
    /// 轴状态推送事件。当控制卡主动推送 "axis_status" 消息时触发，
    /// 携带所有轴的当前位置/速度/状态信息（<see cref="AxisStatusPush"/>）。
    /// </summary>
    public event Action<AxisStatusPush>? AxisStatusReceived;

    /// <summary>
    /// 构造函数：订阅 <see cref="TcpClientBase.OnPushReceived"/> 推送分发，
    /// 将 "axis_status" 类型的推送转换为 <see cref="AxisStatusReceived"/> 事件。
    /// </summary>
    public GtsClient()
    {
        OnPushReceived += HandlePush;
    }

    /// <summary>
    /// 推送消息分发处理。仅处理 "axis_status" 类型，反序列化后触发 <see cref="AxisStatusReceived"/>。
    /// </summary>
    /// <param name="push">推送消息（Push 字段标识类型，Data 字段携带 JSON 载荷）。</param>
    private void HandlePush(PushMessage push)
    {
        if (push.Push == "axis_status")
        {
            var data = push.Data.Deserialize<AxisStatusPush>();
            if (data != null) AxisStatusReceived?.Invoke(data);
        }
    }

    /// <summary>
    /// GT_Open 打开控制卡。建立与控制卡的通讯通道，通常在 TCP 连接后首先调用。
    /// </summary>
    /// <returns>控制卡响应（Status=Success 表示成功）。</returns>
    public Task<CommandResponse> OpenAsync() => SendCommandAsync("GT_Open");

    /// <summary>
    /// GT_Close 关闭控制卡。释放控制卡通讯通道，断开前应调用此方法。
    /// </summary>
    /// <returns>控制卡响应。</returns>
    public Task<CommandResponse> CloseAsync() => SendCommandAsync("GT_Close");

    /// <summary>
    /// GT_Reset 复位控制卡。清除所有轴的运动状态和报警信息，恢复到初始状态。
    /// </summary>
    /// <returns>控制卡响应。</returns>
    public Task<CommandResponse> ResetAsync() => SendCommandAsync("GT_Reset");

    /// <summary>
    /// GT_Home 轴回零。控制指定轴执行回原点操作（寻找零位开关/编码器原点）。
    /// 回零是绝对定位的前提，未回零时绝对定位指令可能被控制卡拒绝。
    /// </summary>
    /// <param name="axis">轴号：1=X 2=Y 3=Z 4=U（固高 GTS 约定，从 1 开始）。</param>
    /// <returns>控制卡响应。</returns>
    public Task<CommandResponse> HomeAsync(int axis) => SendCommandAsync("GT_Home", new { axis });

    /// <summary>
    /// GT_MoveJog 点动。控制指定轴以给定速度连续运动，直到调用 GT_Stop。
    /// 用于手动调整位置，不用于自动定位。
    /// </summary>
    /// <param name="axis">轴号：1=X 2=Y 3=Z 4=U。</param>
    /// <param name="vel">点动速度（mm/s），正值正向、负值反向。</param>
    /// <returns>控制卡响应。</returns>
    public Task<CommandResponse> MoveJogAsync(int axis, double vel) => SendCommandAsync("GT_MoveJog", new { axis, vel });

    /// <summary>
    /// GT_MoveAbs 绝对定位。控制指定轴运动到目标绝对坐标位置。
    /// 前提条件：轴已完成回零（GT_Home），否则绝对坐标无意义。
    /// </summary>
    /// <param name="axis">轴号：1=X 2=Y 3=Z 4=U。</param>
    /// <param name="pos">目标绝对位置（mm，相对于原点）。</param>
    /// <param name="vel">运动速度（mm/s）。</param>
    /// <returns>控制卡响应。</returns>
    public Task<CommandResponse> MoveAbsAsync(int axis, double pos, double vel) => SendCommandAsync("GT_MoveAbs", new { axis, pos, vel });

    /// <summary>
    /// GT_MoveRel 相对定位。控制指定轴从当前位置移动指定距离。
    /// 本项目的分拣/送料指令下发（InspectionOrchestrator）使用此方法。
    /// </summary>
    /// <param name="axis">轴号：1=X 2=Y 3=Z 4=U。</param>
    /// <param name="dist">相对移动距离（mm，正值正向、负值反向）。</param>
    /// <param name="vel">运动速度（mm/s）。</param>
    /// <returns>控制卡响应。</returns>
    public Task<CommandResponse> MoveRelAsync(int axis, double dist, double vel) => SendCommandAsync("GT_MoveRel", new { axis, dist, vel });

    /// <summary>
    /// GT_Stop 停止单轴运动。以减速度平缓停止指定轴，非急停。
    /// </summary>
    /// <param name="axis">轴号：1=X 2=Y 3=Z 4=U。</param>
    /// <returns>控制卡响应。</returns>
    public Task<CommandResponse> StopAsync(int axis) => SendCommandAsync("GT_Stop", new { axis });

    /// <summary>
    /// GT_EmergencyStop 急停所有轴。立即停止所有轴的运动，无减速过程。
    /// 仅在紧急情况下使用，正常停止应使用 <see cref="StopAsync"/>。
    /// </summary>
    /// <returns>控制卡响应。</returns>
    public Task<CommandResponse> EmergencyStopAsync() => SendCommandAsync("GT_EmergencyStop");

    /// <summary>
    /// GT_GetPos 查询轴当前位置。读取指定轴的编码器反馈位置（mm）。
    /// </summary>
    /// <param name="axis">轴号：1=X 2=Y 3=Z 4=U。</param>
    /// <returns>控制卡响应（Data 字段含位置信息）。</returns>
    public Task<CommandResponse> GetPosAsync(int axis) => SendCommandAsync("GT_GetPos", new { axis });

    /// <summary>
    /// GT_ClearAlarm 清除轴报警。清除指定轴的报警状态（如超限、伺服故障等），
    /// 使轴恢复可操作状态。清除前应确保报警原因已排除。
    /// </summary>
    /// <param name="axis">轴号：1=X 2=Y 3=Z 4=U。</param>
    /// <returns>控制卡响应。</returns>
    public Task<CommandResponse> ClearAlarmAsync(int axis) => SendCommandAsync("GT_ClearAlarm", new { axis });
}
