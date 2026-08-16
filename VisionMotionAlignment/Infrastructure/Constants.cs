namespace VisionMotionAlignment.Infrastructure;

/// <summary>
/// 应用全局常量。集中管理避免魔法数散落（可读性 C4）。
/// </summary>
public static class Constants
{
    /// <summary>应用配置文件名。</summary>
    public const string ConfigFileName = "appsettings.json";

    /// <summary>日志目录。</summary>
    public const string LogFolder = "logs";

    /// <summary>相机帧 Channel 容量（线程安全 T6：有界缓冲，满时丢弃最旧帧）。</summary>
    public const int CameraFrameChannelCapacity = 3;

    /// <summary>UI 刷新节流上限（fps）。</summary>
    public const int MaxUiRefreshFps = 30;

    /// <summary>轴号：传送带伺服（送料定位）。（泡罩检测 3 轴方案）</summary>
    public const int AxisConveyor = 1;

    /// <summary>轴号：NG 拨杆（剔除不良品）。（泡罩检测 3 轴方案）</summary>
    public const int AxisNgSorter = 2;

    /// <summary>轴号：OK 拨杆（良品分拣）。（泡罩检测 3 轴方案）</summary>
    public const int AxisOkSorter = 3;

    /// <summary>传送带每次送料步进（mm）。</summary>
    public const double ConveyorFeedStep = 100.0;

    /// <summary>传送带速度（mm/s）。</summary>
    public const double ConveyorVelocity = 50.0;

    /// <summary>拨杆行程（mm）。</summary>
    public const double SorterStroke = 30.0;

    /// <summary>拨杆速度（mm/s）。</summary>
    public const double SorterVelocity = 20.0;

    /// <summary>连续检测模式间隔（ms）。</summary>
    public const int ContinuousIntervalMs = 500;

    /// <summary>检测编排等待轴到位延时（ms）。本项目以固定延时近似到位（30mm/20mm/s≈1.5s，留余量）。</summary>
    public const int AxisSettleDelayMs = 2000;

    /// <summary>批量检测每张图之间的停顿间隔（ms），让 UI 有时间渲染当前图。</summary>
    public const int BatchDetectIntervalMs = 4000;

    /// <summary>力值模块默认轮询间隔（ms）。</summary>
    public const int ForcePollIntervalMs = 150;

    /// <summary>力值历史采样保留上限（健壮性 R8：防止长跑内存增长）。</summary>
    public const int ForceHistoryCapacity = 1000;

    /// <summary>断线重连退避序列（秒）：1, 2, 5, 10（健壮性 R1）。</summary>
    public static readonly int[] ReconnectBackoffSeconds = { 1, 2, 5, 10 };

    /// <summary>Modbus 单次请求默认超时（ms）。</summary>
    public const int ModbusTimeoutMs = 500;

    /// <summary>Modbus 请求失败重试次数（健壮性 R2）。</summary>
    public const int ModbusRetryCount = 2;

    /// <summary>连续失败多少次判定断连（健壮性 R2）。</summary>
    public const int ModbusFailureThreshold = 5;

    /// <summary>长跑自检间隔（分钟）（健壮性 R8）。</summary>
    public const int SelfCheckIntervalMinutes = 5;

    #region 导航页键

    /// <summary>泡罩药丸检测页键。</summary>
    public const string PageBlisterCheck = "BlisterCheck";

    /// <summary>相机参数配置页键。</summary>
    public const string PageCamera = "Camera";

    /// <summary>通讯参数配置页键。</summary>
    public const string PageComm = "Comm";

    /// <summary>力值监控页键。</summary>
    public const string PageForce = "Force";

    /// <summary>诊断页键。</summary>
    public const string PageDiagnostic = "Diagnostic";

    #endregion
}
