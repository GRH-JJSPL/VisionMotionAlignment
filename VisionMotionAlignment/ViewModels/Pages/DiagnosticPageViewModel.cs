using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MotionShared.Dtos;
using Serilog;
using VisionMotionAlignment.Infrastructure;
using VisionMotionAlignment.Services.Interfaces;
using VisionMotionAlignment.ViewModels;

namespace VisionMotionAlignment.ViewModels.Pages;

/// <summary>
/// 诊断页 ViewModel：Modbus 寄存器读写调试 + 运动控制卡轴状态实时监控。
/// </summary>
public sealed partial class DiagnosticPageViewModel : PageViewModelBase, IDisposable
{
    private readonly IMotionCardService _motionCardService;
    private readonly IForceModuleService _forceModuleService;
    private readonly IModbusRtuTransport _transport;

    /// <summary>
    /// 构造函数。订阅运动控制卡轴状态推送事件。
    /// </summary>
    /// <param name="motionCardService">运动控制卡服务。</param>
    /// <param name="forceModuleService">力值模块通讯服务。</param>
    /// <param name="transport">Modbus RTU 传输层（力值模块用）。</param>
    public DiagnosticPageViewModel(IMotionCardService motionCardService, IForceModuleService forceModuleService, IModbusRtuTransport transport)
    {
        _motionCardService = motionCardService;
        _forceModuleService = forceModuleService;
        _transport = transport;

        _motionCardService.AxisStatusReceived += OnAxisStatusReceived;
    }

    #region 轴状态监控

    /// <summary>X 轴当前位置（mm）。</summary>
    [ObservableProperty]
    private double _axisXPos;

    /// <summary>X 轴当前速度（mm/s）。</summary>
    [ObservableProperty]
    private double _axisXVel;

    /// <summary>X 轴当前状态（Idle/Moving/Homing/Alarm）。</summary>
    [ObservableProperty]
    private string _axisXStatus = "Idle";

    /// <summary>X 轴是否有报警。</summary>
    [ObservableProperty]
    private bool _axisXAlarm;

    /// <summary>Y 轴当前位置（mm）。</summary>
    [ObservableProperty]
    private double _axisYPos;

    /// <summary>Y 轴当前速度（mm/s）。</summary>
    [ObservableProperty]
    private double _axisYVel;

    /// <summary>Y 轴当前状态（Idle/Moving/Homing/Alarm）。</summary>
    [ObservableProperty]
    private string _axisYStatus = "Idle";

    /// <summary>Y 轴是否有报警。</summary>
    [ObservableProperty]
    private bool _axisYAlarm;

    /// <summary>
    /// 轴状态推送回调。从 <see cref="IMotionCardService.AxisStatusReceived"/> 事件接收数据，
    /// 经 DispatcherHelper 切 UI 线程更新属性（线程安全 T1）。
    /// </summary>
    /// <param name="push">所有轴的状态推送数据。</param>
    private async void OnAxisStatusReceived(AxisStatusPush push)
    {
        try
        {
            await DispatcherHelper.InvokeAsync(() =>
            {
                foreach (var axis in push.Axes)
                {
                    switch (axis.Axis)
                    {
                        case 1: // X 轴
                            AxisXPos = axis.Pos;
                            AxisXVel = axis.Vel;
                            AxisXStatus = axis.Status;
                            AxisXAlarm = axis.Alarm;
                            break;
                        case 2: // Y 轴
                            AxisYPos = axis.Pos;
                            AxisYVel = axis.Vel;
                            AxisYStatus = axis.Status;
                            AxisYAlarm = axis.Alarm;
                            break;
                        // Z/U 轴暂不显示，后续按需扩展
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "诊断页：轴状态推送处理失败");
        }
    }

    #endregion

    #region Modbus 调试

    /// <summary>Modbus 从站地址。</summary>
    [ObservableProperty]
    private byte _modbusSlaveAddress = 1;

    /// <summary>Modbus 起始寄存器地址（协议地址，0-based）。</summary>
    [ObservableProperty]
    private ushort _modbusStartAddress = 0;

    /// <summary>Modbus 读取数量。</summary>
    [ObservableProperty]
    private ushort _modbusQuantity = 1;

    /// <summary>Modbus 读写结果显示。</summary>
    [ObservableProperty]
    private string _modbusResult = string.Empty;

    /// <summary>待写入的寄存器地址（协议地址，0-based）。</summary>
    [ObservableProperty]
    private ushort _writeAddress = 0;

    /// <summary>待写入的寄存器值。</summary>
    [ObservableProperty]
    private ushort _writeValue;

    /// <summary>
    /// 读保持寄存器命令。按当前参数读取并写入 ModbusResult。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task ReadRegistersAsync()
    {
        try
        {
            var values = await _transport.ReadHoldingRegistersAsync(ModbusSlaveAddress, ModbusStartAddress, ModbusQuantity);
            ModbusResult = values.Length == 0
                ? "读取完成（无数据）"
                : string.Join(", ", values);
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "诊断页：读寄存器失败，从站={Slave} 地址={Addr} 数量={Qty}",
                ModbusSlaveAddress, ModbusStartAddress, ModbusQuantity);
            ModbusResult = $"读取失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 写单个寄存器命令。按当前地址与值写入。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task WriteRegisterAsync()
    {
        try
        {
            var ok = await _transport.WriteSingleRegisterAsync(ModbusSlaveAddress, WriteAddress, WriteValue);
            ModbusResult = ok
                ? $"写入成功：地址 {WriteAddress} = {WriteValue}"
                : $"写入失败：地址 {WriteAddress}";
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "诊断页：写寄存器失败，从站={Slave} 地址={Addr} 值={Val}",
                ModbusSlaveAddress, WriteAddress, WriteValue);
            ModbusResult = $"写入失败：{ex.Message}";
        }
    }

    #endregion

    /// <summary>
    /// 取消订阅轴状态推送事件，防止内存泄漏。
    /// </summary>
    public void Dispose()
    {
        _motionCardService.AxisStatusReceived -= OnAxisStatusReceived;
    }
}
