using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VisionMotionAlignment.Infrastructure;
using VisionMotionAlignment.Models.Force;
using VisionMotionAlignment.Services.Interfaces;
using VisionMotionAlignment.ViewModels;

namespace VisionMotionAlignment.ViewModels.Pages;

/// <summary>
/// 力值监控页 ViewModel。实时显示力值、历史曲线并支持清零。
/// </summary>
/// <remarks>
/// <para>订阅 <see cref="IForceModuleService.ReadingReceived"/> 事件更新当前值与历史。</para>
/// <para>历史采样集合有上限（<see cref="Constants.ForceHistoryCapacity"/>），
/// 超出后移除最旧样本，防止长跑内存增长（健壮性 R8）。</para>
/// </remarks>
public sealed partial class ForceMonitorPageViewModel : PageViewModelBase
{
    private readonly IForceModuleService _forceModuleService;

    /// <summary>
    /// 构造函数。订阅力值读数事件以更新当前值与历史。
    /// </summary>
    /// <param name="forceModuleService">力值模块通讯服务。</param>
    public ForceMonitorPageViewModel(IForceModuleService forceModuleService)
    {
        _forceModuleService = forceModuleService;
        _forceModuleService.ReadingReceived += OnReadingReceived;
    }

    /// <summary>当前力值。</summary>
    [ObservableProperty]
    private double _currentValue;

    /// <summary>力值单位（如 "kN"）。</summary>
    [ObservableProperty]
    private string _unit = "kN";

    /// <summary>力值历史采样集合（用于曲线展示，上限 <see cref="Constants.ForceHistoryCapacity"/>）。</summary>
    [ObservableProperty]
    private ObservableCollection<double> _history = new();

    /// <summary>是否正在轮询力值。</summary>
    [ObservableProperty]
    private bool _isPolling;

    /// <summary>
    /// 启动轮询命令。调用服务按固定间隔持续读取力值。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task StartPollingAsync()
    {
        try
        {
            await _forceModuleService.StartPollingAsync(TimeSpan.FromMilliseconds(Constants.ForcePollIntervalMs));
            IsPolling = true;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "力值轮询启动失败");
        }
    }

    /// <summary>
    /// 停止轮询命令。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task StopPollingAsync()
    {
        try
        {
            await _forceModuleService.StopPollingAsync();
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "力值轮询停止失败");
        }
        IsPolling = false;
    }

    /// <summary>
    /// 清零命令。通过力值服务向 500B 多功能寄存器写清零指令。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task ZeroAsync()
    {
        try
        {
            bool ok = await _forceModuleService.ZeroAsync();
            if (!ok)
            {
                Log.Logger.Warning("力值清零未成功（服务返回 false，可能未连接或写入未确认）");
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "力值清零异常");
        }
    }

    /// <summary>
    /// 力值读数事件回调。跨线程更新 CurrentValue/Unit/History（线程安全 T1）。
    /// async void 内异常不可观察，try/catch 兜底避免触发进程终结。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">力值读数。</param>
    private async void OnReadingReceived(object? sender, ForceReading e)
    {
        try
        {
            await DispatcherHelper.InvokeAsync(() =>
            {
                CurrentValue = e.Value;
                if (!string.IsNullOrEmpty(e.Unit))
                {
                    Unit = e.Unit;
                }

                History.Add(e.Value);
                // 健壮性 R8：历史采样上限，超出移除最旧样本，防止长跑内存增长。
                while (History.Count > Constants.ForceHistoryCapacity)
                {
                    History.RemoveAt(0);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "OnReadingReceived 处理失败");
        }
    }
}
