using System.Collections.Specialized;
using System.Windows;
using ScottPlot;
using VisionMotionAlignment.Infrastructure;
using VisionMotionAlignment.ViewModels.Pages;

namespace VisionMotionAlignment.Pages;

/// <summary>
/// 力值监控页面。
/// 业务逻辑在 ViewModel；本页仅负责把 VM 的历史力值集合渲染为 ScottPlot 趋势曲线。
/// </summary>
/// <remarks>
/// 图表无法纯 MVVM 绑定（ScottPlot 需直接操作 Plot 对象），故在 code-behind 中
/// 订阅 <see cref="ForceMonitorPageViewModel.History"/> 的 CollectionChanged 事件，
/// 每次有新增样本时把集合复制为数组并重建 Signal 曲线。
/// 订阅/解绑绑定在 Loaded/Unloaded，避免页面切走后仍持有 VM 引用（防泄漏）。
/// </remarks>
public partial class ForceMonitorPage : System.Windows.Controls.UserControl
{
    /// <summary>当前页面的 ViewModel（在 Loaded 时从 DataContext 获取）。</summary>
    private ForceMonitorPageViewModel? _viewModel;

    /// <summary>力值历史曲线（Signal 图：等间隔采样点，无需 X 轴数据）。</summary>
    private ScottPlot.Plottables.Signal? _signal;

    public ForceMonitorPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 页面加载完成后：获取 VM、添加 Signal 曲线并订阅历史集合变化。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ForceMonitorPageViewModel vm)
        {
            return;
        }

        _viewModel = vm;

        ForcePlot.Plot.Title("力值历史趋势");
        ForcePlot.Plot.YLabel($"力值 ({_viewModel.Unit})");
        ForcePlot.Plot.XLabel("采样序号");

        RebuildSignal();

        // 订阅历史集合变化：每次新增力值样本即刷新曲线
        _viewModel.History.CollectionChanged += OnHistoryChanged;
    }

    /// <summary>
    /// 页面卸载后：解除订阅，释放对 VM 的引用（防泄漏）。
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.History.CollectionChanged -= OnHistoryChanged;
            _viewModel = null;
        }
    }

    /// <summary>
    /// 历史力值集合变化回调。把集合复制为 double[] 重建 Signal 曲线并刷新图表。
    /// </summary>
    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        RebuildSignal();
    }

    /// <summary>
    /// 重建 Signal 曲线。
    /// 5.1.x 中 ISignalSource 为只读接口，无 SetY 更新方法，故采用 Clear + 重建
    /// （上限 <see cref="Constants.ForceHistoryCapacity"/> 1000 点，150ms 间隔下性能充足）。
    /// </summary>
    private void RebuildSignal()
    {
        if (_viewModel is null)
        {
            return;
        }

        var values = _viewModel.History.ToArray();

        ForcePlot.Plot.Clear();  // 仅清除曲线，不影响 Title/YLabel/XLabel 设置
        _signal = ForcePlot.Plot.Add.Signal(values);
        _signal.LegendText = "力值";

        ForcePlot.Plot.Axes.AutoScale();
        ForcePlot.Refresh();
    }
}
