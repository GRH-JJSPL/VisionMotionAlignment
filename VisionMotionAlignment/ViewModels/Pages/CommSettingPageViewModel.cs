using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VisionMotionAlignment.Infrastructure;
using VisionMotionAlignment.Models;
using VisionMotionAlignment.Models.Communication;
using VisionMotionAlignment.Services.Interfaces;
using VisionMotionAlignment.ViewModels;

namespace VisionMotionAlignment.ViewModels.Pages;

/// <summary>
/// 通讯参数配置页 ViewModel。管理运动控制卡（TCP）与力值模块（串口）的参数及连接操作。
/// </summary>
/// <remarks>
/// 订阅 <see cref="IMotionCardService.StateChanged"/> 和 <see cref="IForceModuleService.StateChanged"/>
/// 事件，当服务层（含断线重连）状态变化时自动更新 UI。
/// </remarks>
public sealed partial class CommSettingPageViewModel : PageViewModelBase
{
    private readonly IMotionCardService _motionCardService;
    private readonly IForceModuleService _forceModuleService;

    /// <summary>
    /// 构造函数。订阅运动控制卡/力值服务的状态变化事件。
    /// </summary>
    /// <param name="motionCardService">运动控制卡服务。</param>
    /// <param name="forceModuleService">力值模块通讯服务。</param>
    public CommSettingPageViewModel(IMotionCardService motionCardService, IForceModuleService forceModuleService)
    {
        _motionCardService = motionCardService;
        _forceModuleService = forceModuleService;

        _motionCardService.StateChanged += OnMotionCardStateChanged;
        _forceModuleService.StateChanged += OnForceStateChanged;
    }

    /// <summary>运动控制卡 IP 地址。</summary>
    [ObservableProperty]
    private string _motionCardIp = "127.0.0.1";

    /// <summary>运动控制卡 TCP 端口。</summary>
    [ObservableProperty]
    private int _motionCardPort = 5000;

    /// <summary>运动控制卡连接状态。</summary>
    [ObservableProperty]
    private ConnectionState _motionCardConnectionState;

    /// <summary>力值模块串口名。</summary>
    [ObservableProperty]
    private string _forcePortName = "COM4";

    /// <summary>力值模块波特率。</summary>
    [ObservableProperty]
    private int _forceBaudRate = 19200;

    /// <summary>力值模块从站地址。</summary>
    [ObservableProperty]
    private byte _forceSlaveAddress = 1;

    /// <summary>力值模块浮点数字节序。</summary>
    [ObservableProperty]
    private FloatByteOrder _forceFloatByteOrder = FloatByteOrder.ABCD;

    /// <summary>力值模块连接状态。</summary>
    [ObservableProperty]
    private ConnectionState _forceConnectionState;

    /// <summary>
    /// 连接运动控制卡命令。按当前 IP/端口建立 TCP 连接。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task ConnectMotionCardAsync()
    {
        MotionCardConnectionState = ConnectionState.Connecting;
        try
        {
            await _motionCardService.ConnectAsync(MotionCardIp, MotionCardPort);
            // 状态由 StateChanged 事件回调更新
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "运动控制卡连接失败 {Ip}:{Port}", MotionCardIp, MotionCardPort);
            MotionCardConnectionState = ConnectionState.Failed;
        }
    }

    /// <summary>
    /// 断开运动控制卡命令。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task DisconnectMotionCardAsync()
    {
        try
        {
            await _motionCardService.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "运动控制卡断开失败");
        }
        MotionCardConnectionState = ConnectionState.Disconnected;
    }

    /// <summary>
    /// 连接力值模块命令。按当前串口参数连接力值模块。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task ConnectForceAsync()
    {
        ForceConnectionState = ConnectionState.Connecting;
        try
        {
            var config = new SerialPortConfig
            {
                PortName = ForcePortName,
                BaudRate = ForceBaudRate,
                SlaveAddress = ForceSlaveAddress,
                FloatByteOrder = ForceFloatByteOrder
            };
            var ok = await _forceModuleService.ConnectAsync(config);
            ForceConnectionState = ok ? ConnectionState.Connected : ConnectionState.Failed;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "力值模块连接失败，端口={Port}", ForcePortName);
            ForceConnectionState = ConnectionState.Failed;
        }
    }

    /// <summary>
    /// 断开力值模块命令。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task DisconnectForceAsync()
    {
        try
        {
            await _forceModuleService.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "力值模块断开失败");
        }
        ForceConnectionState = ConnectionState.Disconnected;
    }

    /// <summary>
    /// 运动控制卡状态变化回调。跨线程更新 MotionCardConnectionState（线程安全 T1）。
    /// </summary>
    private async void OnMotionCardStateChanged(object? sender, ConnectionState e)
    {
        await DispatcherHelper.InvokeAsync(() => MotionCardConnectionState = e);
    }

    /// <summary>
    /// 力值模块状态变化回调。跨线程更新 ForceConnectionState（线程安全 T1）。
    /// </summary>
    private async void OnForceStateChanged(object? sender, ConnectionState e)
    {
        await DispatcherHelper.InvokeAsync(() => ForceConnectionState = e);
    }
}
