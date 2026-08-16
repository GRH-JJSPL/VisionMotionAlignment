using CommunityToolkit.Mvvm.ComponentModel;

namespace BlisterPillInspection.ViewModels;

/// <summary>
/// 所有页面 ViewModel 的抽象基类。
/// 提供页面标题、忙碌状态及导航生命周期回调，供具体页面 VM 继承。
/// </summary>
public abstract partial class PageViewModelBase : ObservableObject
{
    /// <summary>页面标题。</summary>
    [ObservableProperty]
    private string _pageTitle = string.Empty;

    /// <summary>页面是否正在执行耗时操作（用于 UI 显示加载指示器）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// 导航进入本页面时回调。派生类可重写以加载数据。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public virtual Task OnNavigatedToAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// 导航离开本页面时回调。派生类可重写以释放资源或暂停轮询。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public virtual Task OnNavigatedFromAsync(CancellationToken ct = default) => Task.CompletedTask;
}
