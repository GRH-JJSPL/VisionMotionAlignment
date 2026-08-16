using System.Windows;
using VisionMotionAlignment.Models;

namespace VisionMotionAlignment.Views;

/// <summary>
/// 状态徽章控件：根据 ConnectionState 切换颜色与文本。
/// State 为依赖属性，由消费者通过 State="{Binding ...}" 绑定，
/// 绑定源继承父级 DataContext。
/// </summary>
public partial class StatusBadge : System.Windows.Controls.UserControl
{
    /// <summary>
    /// State 依赖属性：当前连接状态（值来自绑定源）。
    /// 依赖属性支持 XAML 绑定（{Binding}），由全局注册表管理，值可来自绑定源而非本地字段。
    /// </summary>
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(
            nameof(State),
            typeof(ConnectionState),
            typeof(StatusBadge),
            new PropertyMetadata(ConnectionState.Idle));

    /// <summary>
    /// 当前连接状态（依赖属性包装器）。
    /// GetValue/SetValue 读写依赖属性，值变化时自动通知 XAML 样式刷新。
    /// </summary>
    public ConnectionState State
    {
        get => (ConnectionState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public StatusBadge()
    {
        InitializeComponent();
    }
}
