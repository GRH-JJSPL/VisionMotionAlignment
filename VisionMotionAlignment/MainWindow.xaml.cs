using System.Windows;
using VisionMotionAlignment.ViewModels;
using Wpf.Ui.Controls;

namespace VisionMotionAlignment;

/// <summary>
/// 主窗口代码后置。继承 <see cref="FluentWindow"/> 以获得 Mica 背景与圆角支持。
/// 仅承载视图初始化与 VM 命令触发，业务逻辑全部下沉到 <see cref="MainWindowViewModel"/>。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;

    /// <summary>
    /// 构造函数。注入主窗口 VM 并设置 DataContext，触发 Initialize 命令订阅全局事件。
    /// </summary>
    /// <param name="viewModel">主窗口 VM。</param>
    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    /// <summary>窗口加载完成后触发 VM 初始化命令。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_viewModel.InitializeCommand.CanExecute(null))
        {
            await _viewModel.InitializeCommand.ExecuteAsync(null);
        }
    }
}
