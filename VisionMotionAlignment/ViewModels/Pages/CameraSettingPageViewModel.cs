using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VisionMotionAlignment.Models.Camera;
using VisionMotionAlignment.Services.Interfaces;
using VisionMotionAlignment.ViewModels;

namespace VisionMotionAlignment.ViewModels.Pages;

/// <summary>
/// 相机参数配置页 ViewModel。管理两个工位相机的设备选择与曝光/增益/帧率参数。
/// </summary>
public sealed partial class CameraSettingPageViewModel : PageViewModelBase
{
    private readonly ICameraService _cameraService1;
    private readonly ICameraService _cameraService2;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="cameraService1">工位 1 相机服务。</param>
    /// <param name="cameraService2">工位 2 相机服务。</param>
    public CameraSettingPageViewModel(ICameraService cameraService1, ICameraService cameraService2)
    {
        _cameraService1 = cameraService1;
        _cameraService2 = cameraService2;
    }

    /// <summary>工位 1 选中的相机设备标识。</summary>
    [ObservableProperty]
    private string _workstation1DeviceKey = string.Empty;

    /// <summary>工位 2 选中的相机设备标识。</summary>
    [ObservableProperty]
    private string _workstation2DeviceKey = string.Empty;

    /// <summary>曝光时间（μs）。</summary>
    [ObservableProperty]
    private double _exposureUs = 8000;

    /// <summary>增益（dB）。</summary>
    [ObservableProperty]
    private double _gain;

    /// <summary>帧率（fps）。</summary>
    [ObservableProperty]
    private double _frameRate = 30;

    /// <summary>
    /// 刷新设备列表命令。枚举两个工位的可用相机设备。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        try
        {
            var devices1 = _cameraService1.EnumerateDevices();
            var devices2 = _cameraService2.EnumerateDevices();
            Workstation1DeviceKey = devices1.FirstOrDefault()?.DeviceKey ?? string.Empty;
            Workstation2DeviceKey = devices2.FirstOrDefault()?.DeviceKey ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "枚举相机设备失败");
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 应用相机参数命令。将曝光/增益/帧率应用到两个工位相机。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        var parameters = new CameraParameters
        {
            ExposureUs = ExposureUs,
            Gain = Gain,
            FrameRate = FrameRate
        };
        try
        {
            if (!string.IsNullOrEmpty(Workstation1DeviceKey))
            {
                await _cameraService1.OpenAsync(Workstation1DeviceKey, parameters);
            }
            if (!string.IsNullOrEmpty(Workstation2DeviceKey))
            {
                await _cameraService2.OpenAsync(Workstation2DeviceKey, parameters);
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "应用相机参数失败");
        }
    }
}
