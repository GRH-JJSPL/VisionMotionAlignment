using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using BlisterPillInspection.Infrastructure;
using BlisterPillInspection.Models;
using BlisterPillInspection.Models.Force;
using BlisterPillInspection.Services.Interfaces;
using BlisterPillInspection.ViewModels.Pages;
using Wpf.Ui.Controls;

namespace BlisterPillInspection.ViewModels;

/// <summary>
/// 主窗口 ViewModel：顶栏全局状态（连接状态/实时力值/系统就绪）与导航菜单、页面切换。
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IMotionCardService? _motionCardService;
    private readonly IForceModuleService? _forceModuleService;

    private readonly BlisterCheckPageViewModel _blisterCheckPageVm;
    private readonly CameraSettingPageViewModel _cameraSettingPageVm;
    private readonly CommSettingPageViewModel _commSettingPageVm;
    private readonly ForceMonitorPageViewModel _forceMonitorPageVm;
    private readonly DiagnosticPageViewModel _diagnosticPageVm;

    //专门跑在UI线程上的定时器
    private readonly DispatcherTimer _dateTimeTimer;
    private NavigationItem? _selectedNavigationItem;
    private PageViewModelBase _currentPage;

    /// <summary>
    /// 构造函数。注入 5 个页面 VM 与全局通讯服务。
    /// </summary>
    /// <param name="blisterCheckPageVm">泡罩药丸检测页 VM。</param>
    /// <param name="cameraSettingPageVm">相机参数配置页 VM。</param>
    /// <param name="commSettingPageVm">通讯参数配置页 VM。</param>
    /// <param name="forceMonitorPageVm">力值监控页 VM。</param>
    /// <param name="diagnosticPageVm">诊断页 VM。</param>
    /// <param name="motionCardService">运动控制卡通讯服务（可空以支持设计期实例化）。</param>
    /// <param name="forceModuleService">力值模块通讯服务（可空以支持设计期实例化）。</param>
    public MainWindowViewModel(
        BlisterCheckPageViewModel blisterCheckPageVm,
        CameraSettingPageViewModel cameraSettingPageVm,
        CommSettingPageViewModel commSettingPageVm,
        ForceMonitorPageViewModel forceMonitorPageVm,
        DiagnosticPageViewModel diagnosticPageVm,
        IMotionCardService? motionCardService = null,
        IForceModuleService? forceModuleService = null)
    {
        _blisterCheckPageVm = blisterCheckPageVm;
        _cameraSettingPageVm = cameraSettingPageVm;
        _commSettingPageVm = commSettingPageVm;
        _forceMonitorPageVm = forceMonitorPageVm;
        _diagnosticPageVm = diagnosticPageVm;
        _motionCardService = motionCardService;
        _forceModuleService = forceModuleService;

        // 导航菜单项：页键 → 标题 + Fluent 图标。
        NavigationItems = new ObservableCollection<NavigationItem>
        {
            new(Constants.PageBlisterCheck, "药丸检测", SymbolRegular.Pill24),
            new(Constants.PageCamera, "相机", SymbolRegular.Camera24),
            new(Constants.PageComm, "通讯", SymbolRegular.PlugDisconnected24),
            new(Constants.PageForce, "力值", SymbolRegular.DataHistogram24),
            new(Constants.PageDiagnostic, "诊断", SymbolRegular.WrenchScrewdriver24),
        };

        // 默认显示泡罩检测主页，并选中第一项。
        _currentPage = _blisterCheckPageVm;
        _selectedNavigationItem = NavigationItems[0];

        // 顶栏时钟：1 秒节流刷新（DispatcherTimer 本身在 UI 线程触发，无需 DispatcherHelper）。
        _dateTimeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _dateTimeTimer.Tick += OnDateTimeTimerTick;
        _dateTimeTimer.Start();
        UpdateCurrentDateTime();
    }

    /// <summary>左侧导航菜单项集合。</summary>
    public ObservableCollection<NavigationItem> NavigationItems { get; }

    /// <summary>当前选中的导航项。setter 触发 NavigateTo（线程安全 T1：UI 线程赋值）。</summary>
    public NavigationItem? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (SetProperty(ref _selectedNavigationItem, value) && value is not null)
            {
                NavigateTo(value.PageKey);
            }
        }
    }

    /// <summary>当前页面 VM。ContentControl 绑定此项并通过 DataTemplate 渲染对应 Page。</summary>
    public PageViewModelBase CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    /// <summary>运动控制卡连接状态。</summary>
    [ObservableProperty]
    private ConnectionState _motionCardConnectionState;

    /// <summary>力值模块连接状态。</summary>
    [ObservableProperty]
    private ConnectionState _forceConnectionState;

    /// <summary>当前力值（顶栏显示）。</summary>
    [ObservableProperty]
    private double _forceValue;

    /// <summary>力值单位（如 "kN"）。</summary>
    [ObservableProperty]
    private string _forceUnit = "kN";

    /// <summary>顶栏显示的当前日期时间。</summary>
    [ObservableProperty]
    private string _currentDateTime = string.Empty;

    /// <summary>系统是否就绪（运动控制卡与力值均连接）。</summary>
    [ObservableProperty]
    private bool _isSystemReady;

    /// <summary>
    /// 按页键切换当前页面。
    /// </summary>
    /// <param name="pageKey">页键（见 <see cref="Constants"/>）。</param>
    public void NavigateTo(string pageKey)
    {
        //让右侧页面切换，但是左侧导航栏还未高亮
        CurrentPage = pageKey switch
        {
            Constants.PageBlisterCheck => _blisterCheckPageVm,
            Constants.PageCamera => _cameraSettingPageVm,
            Constants.PageComm => _commSettingPageVm,
            Constants.PageForce => _forceMonitorPageVm,
            Constants.PageDiagnostic => _diagnosticPageVm,
            _ => _blisterCheckPageVm
        };

        // 同步 ListBox 选中项，避免外部调用 NavigateTo 时选中项与显示页不一致。
        // 用 SetProperty 触发 PropertyChanged 通知 UI；不会重新进入 set 方法，无递归风险。
        var target = NavigationItems.FirstOrDefault(x => x.PageKey == pageKey);
        if (target is not null && !ReferenceEquals(target, _selectedNavigationItem))
        {
            SetProperty(ref _selectedNavigationItem, target, nameof(SelectedNavigationItem));
        }
    }

    /// <summary>
    /// 初始化命令。订阅力值服务的 ReadingReceived 事件以更新实时力值，
    /// 订阅运动控制卡/力值的 StateChanged 事件以更新连接状态，并启动力值轮询。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (_forceModuleService is not null)
        {
            _forceModuleService.ReadingReceived += OnForceReadingReceived;
            _forceModuleService.StateChanged += OnForceStateChanged;

            try
            {
                await _forceModuleService.StartPollingAsync(TimeSpan.FromMilliseconds(Constants.ForcePollIntervalMs));
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "力值轮询启动失败");
            }
        }

        if (_motionCardService is not null)
        {
            _motionCardService.StateChanged += OnMotionCardStateChanged;

            // 启动时自动尝试连接运动控制卡（默认 127.0.0.1:5000，与 appsettings.json 一致）：
            // 连不上真实卡（无硬件/模拟器）时，Fallback 会自动回退到虚拟服务，
            // 保证"送料→检测→分拣"联动流程开箱即用。
            try
            {
                await _motionCardService.ConnectAsync("127.0.0.1", 5000);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "运动控制卡自动连接失败（将回退到虚拟服务）");
            }
        }
    }

    /// <summary>
    /// 力值读数事件回调。跨线程更新 ForceValue/ForceUnit（线程安全 T1）。
    /// try/catch 兜底：async void 内异常不可观察，避免触发进程终结。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">力值读数。</param>
    private async void OnForceReadingReceived(object? sender, ForceReading e)
    {
        try
        {
            await DispatcherHelper.InvokeAsync(() =>
            {
                ForceValue = e.Value;
                if (!string.IsNullOrEmpty(e.Unit))
                {
                    ForceUnit = e.Unit;
                }
                UpdateSystemReady();
            });
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "OnForceReadingReceived 处理失败");
        }
    }

    /// <summary>
    /// 力值模块连接状态变化回调。跨线程更新 ForceConnectionState（线程安全 T1）。
    /// try/catch 兜底：async void 内异常不可观察，避免触发进程终结。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">新的连接状态。</param>
    private async void OnForceStateChanged(object? sender, ConnectionState e)
    {
        try
        {
            await DispatcherHelper.InvokeAsync(() =>
            {
                ForceConnectionState = e;
                UpdateSystemReady();
            });
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "OnForceStateChanged 处理失败");
        }
    }

    /// <summary>
    /// 运动控制卡连接状态变化回调。跨线程更新 MotionCardConnectionState（线程安全 T1）。
    /// try/catch 兜底：async void 内异常不可观察，避免触发进程终结。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">新的连接状态。</param>
    private async void OnMotionCardStateChanged(object? sender, ConnectionState e)
    {
        try
        {
            await DispatcherHelper.InvokeAsync(() =>
            {
                MotionCardConnectionState = e;
                UpdateSystemReady();
            });
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "OnMotionCardStateChanged 处理失败");
        }
    }

    /// <summary>
    /// 刷新系统就绪状态：运动控制卡与力值模块均处于 Connected 才视为就绪。
    /// </summary>
    private void UpdateSystemReady()
    {
        IsSystemReady = MotionCardConnectionState == ConnectionState.Connected
                       && ForceConnectionState == ConnectionState.Connected;
    }

    /// <summary>顶栏时钟 Tick 回调。</summary>
    private void OnDateTimeTimerTick(object? sender, EventArgs e) => UpdateCurrentDateTime();

    /// <summary>更新顶栏当前日期时间显示。</summary>
    private void UpdateCurrentDateTime()
    {
        CurrentDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }
}

/// <summary>
/// 左侧导航菜单项 Model。包含页键、显示标题与 Fluent 图标符号。
/// </summary>
public sealed record NavigationItem(string PageKey, string Title, SymbolRegular IconSymbol);
