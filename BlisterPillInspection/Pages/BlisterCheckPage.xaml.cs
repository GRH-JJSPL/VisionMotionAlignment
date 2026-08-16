using System.ComponentModel;
using System.Windows.Controls;
using HalconDotNet;
using BlisterPillInspection.Models.Vision;
using BlisterPillInspection.ViewModels.Pages;

namespace BlisterPillInspection.Pages;

/// <summary>
/// 泡罩药丸检测页：显示检测图像与结果叠加（绿=正确/红=错药/黄=缺药），监听 VM 结果更新，R7 释放 Halcon 资源。
/// </summary>
public partial class BlisterCheckPage : UserControl
{
    private BlisterCheckPageViewModel? _viewModel;

    /// <summary>上一次显示的检测结果（用于切换时释放旧资源 R7）。</summary>
    private BlisterCheckResult? _displayedResult;

    /// <summary>
    /// 初始化页面。DataContext 由 ContentControl（DataTemplate）自动注入。
    /// </summary>
    public BlisterCheckPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // 页面卸载：取消事件订阅并释放 Halcon 资源（R7），防止内存泄漏
        Unloaded += OnPageUnloaded;
    }

    /// <summary>
    /// 页面卸载回调：取消 ViewModel 订阅并释放当前显示结果持有的 Halcon 非托管资源（R7）。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">参数。</param>
    private void OnPageUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        ReleaseDisplayedResources();
    }

    /// <summary>
    /// DataContext 变化回调：绑定到 BlisterCheckPageViewModel，订阅属性变化更新显示。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">参数。</param>
    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as BlisterCheckPageViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>
    /// ViewModel 属性变化回调。仅在 <see cref="BlisterCheckPageViewModel.CurrentResult"/> 变化时刷新显示。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">属性名。</param>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BlisterCheckPageViewModel.CurrentResult))
        {
            UpdateDisplay(_viewModel?.CurrentResult);
        }
    }

    /// <summary>
    /// 在 Halcon 窗口显示图像和结果叠加区域。
    ///
    /// 【叠加配色约定】
    /// - 绿色（描边）  → 正确分类的药丸（FinalClasses）
    /// - 红色（描边）  → 错药（WrongPills）
    /// - 黄色（描边）  → 缺药（MissingPills）
    /// 全部用描边而非填充，避免遮挡图像、便于观察格子内药丸。
    /// </summary>
    /// <param name="result">检测结果。</param>
    private void UpdateDisplay(BlisterCheckResult? result)
    {
        // 切换结果前，释放上一张的 Halcon 资源（R7），避免内存增长
        ReleaseDisplayedResources();

        if (result?.DisplayImage is null)
        {
            HalconWindow.HalconWindow.ClearWindow();
            _displayedResult = null;
            return;
        }

        var window = HalconWindow.HalconWindow;

        // 清窗并显示对齐后的检测图像
        window.ClearWindow();
        window.DispObj(result.DisplayImage);

        // 叠加正确分类区域（绿色描边）
        if (result.FinalClasses is not null)
        {
            window.SetColor("green");
            window.SetDraw("margin");
            window.SetLineWidth(2);
            window.DispObj(result.FinalClasses);
        }

        // 叠加错药区域（红色描边，不填充以便看清底下药丸）
        if (result.WrongPills is not null)
        {
            window.SetColor("red");
            window.SetDraw("margin");
            window.SetLineWidth(3);
            window.DispObj(result.WrongPills);
        }

        // 叠加缺药区域（黄色描边，不填充以便看清底下药丸）
        if (result.MissingPills is not null)
        {
            window.SetColor("yellow");
            window.SetDraw("margin");
            window.SetLineWidth(3);
            window.DispObj(result.MissingPills);
        }

        // 记录本次显示的资源，供下次切换/页面卸载时释放（R7）
        _displayedResult = result;
    }

    /// <summary>
    /// 释放当前显示结果持有的 Halcon 非托管资源（R7）。
    /// </summary>
    private void ReleaseDisplayedResources()
    {
        _displayedResult?.DisplayImage?.Dispose();
        _displayedResult?.FinalClasses?.Dispose();
        _displayedResult?.WrongPills?.Dispose();
        _displayedResult?.MissingPills?.Dispose();
        _displayedResult = null;
    }
}
