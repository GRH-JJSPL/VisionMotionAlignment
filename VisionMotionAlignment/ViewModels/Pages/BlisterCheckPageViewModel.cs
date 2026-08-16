using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HalconDotNet;
using Microsoft.Win32;
using Serilog;
using VisionMotionAlignment.Infrastructure;
using VisionMotionAlignment.Models;
using VisionMotionAlignment.Models.Vision;
using VisionMotionAlignment.Services.Interfaces;
using VisionMotionAlignment.ViewModels;

namespace VisionMotionAlignment.ViewModels.Pages;

/// <summary>
/// 泡罩药丸检测页 ViewModel：参考图训练、批量检测（支持暂停/继续/停止）、联动编排。
/// </summary>
public sealed partial class BlisterCheckPageViewModel : PageViewModelBase
{
    private readonly IBlisterCheckService _blisterCheckService;
    private readonly IInspectionOrchestrator? _inspectionOrchestrator;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="blisterCheckService">泡罩检测服务。</param>
    /// <param name="inspectionOrchestrator">检测编排器（可选，运动控制卡未连接时为 null）。</param>
    /// <param name="motionCardService">运动控制卡服务（订阅连接状态，驱动联动按钮可用性）。</param>
    public BlisterCheckPageViewModel(
        IBlisterCheckService blisterCheckService,
        IInspectionOrchestrator? inspectionOrchestrator = null,
        IMotionCardService? motionCardService = null)
    {
        _blisterCheckService = blisterCheckService;
        _inspectionOrchestrator = inspectionOrchestrator;
        _motionCardService = motionCardService;

        if (_inspectionOrchestrator is not null)
        {
            _inspectionOrchestrator.InspectionCompleted += OnInspectionCompleted;
        }

        // 订阅运动卡连接状态，驱动"开始联动"按钮可用性
        if (_motionCardService is not null)
        {
            _motionCardService.StateChanged += OnMotionCardStateChanged;
            IsMotionCardConnected = _motionCardService.IsConnected;
        }
    }

    /// <summary>运动控制卡服务（订阅连接状态）。</summary>
    private readonly IMotionCardService? _motionCardService;

    /// <summary>当前显示的检测图像（HImage，供 UI 的 HSmartWindowControlWPF 绑定）。</summary>
    [ObservableProperty]
    private HImage? _currentImage;

    /// <summary>当前检测结果。</summary>
    [ObservableProperty]
    private BlisterCheckResult? _currentResult;

    /// <summary>参考图是否已加载并训练完成。</summary>
    [ObservableProperty]
    private bool _isTrained;

    /// <summary>是否正在执行纯视觉检测。</summary>
    [ObservableProperty]
    private bool _isChecking;

    /// <summary>是否正在执行联动编排（送料→检测→分拣）。</summary>
    [ObservableProperty]
    private bool _isOrchestrating;

    /// <summary>运动控制卡是否已连接（联动模式前提条件）。</summary>
    [ObservableProperty]
    private bool _isMotionCardConnected;

    /// <summary>参考图文件路径。</summary>
    [ObservableProperty]
    private string _referenceImagePath = string.Empty;

    /// <summary>测试图文件夹路径。</summary>
    [ObservableProperty]
    private string _testImageFolder = string.Empty;

    /// <summary>当前检测的图片索引（1 起）。</summary>
    [ObservableProperty]
    private int _currentIndex;

    /// <summary>测试图总数。</summary>
    [ObservableProperty]
    private int _totalCount;

    /// <summary>当前检测图片文件名。</summary>
    [ObservableProperty]
    private string _currentFileName = string.Empty;

    /// <summary>期望的各类药丸数量展示文本（如 "3黄 6红 6绿"）。</summary>
    [ObservableProperty]
    private string _expectedSummary = string.Empty;

    // ════════════════════════════════════════════════════════════════
    // 只读展示属性（XAML 绑定用）
    // 这些由各 [ObservableProperty] 的 On...Changed 钩子里手动触发通知，
    // 保证任一相关状态变化时界面同步刷新。
    // ════════════════════════════════════════════════════════════════

    /// <summary>训练状态文本（未训练 / 已训练）。</summary>
    public string TrainStatusText => IsTrained ? "状态：已训练" : "状态：未训练（请先选择参考图）";

    /// <summary>检测进度文本（如 "进度：3/12"）。</summary>
    public string CheckProgressText => TotalCount > 0 ? $"进度：{CurrentIndex}/{TotalCount}" : "进度：未开始";

    /// <summary>运动控制卡连接状态文本（未连接 / 已连接 / 已连接·虚拟模式）。</summary>
    public string MotionCardStatusText
    {
        get
        {
            if (!IsMotionCardConnected) return "运动控制卡：○ 未连接";
            // 若底层是虚拟卡（Fallback 已回退），显示"虚拟模式"便于用户识别
            bool isVirtual = _motionCardService is VisionMotionAlignment.Services.MotionCard.FallbackMotionCardService fallback && fallback.IsVirtual;
            return isVirtual ? "运动控制卡：● 已连接（虚拟模式）" : "运动控制卡：● 已连接";
        }
    }

    /// <summary>当前结果状态文本（OK / NG / 无）。</summary>
    public string ResultStatusText => CurrentResult switch
    {
        { IsValid: false } => "—",
        { IsOk: true } => "✓ OK",
        { IsOk: false } => "✗ NG",
        null => "—"
    };

    /// <summary>当前结果状态颜色（OK 绿 / NG 红 / 无灰）。</summary>
    public Brush ResultStatusBrush => CurrentResult switch
    {
        { IsValid: false } => Brushes.Gray,
        { IsOk: true } => Brushes.ForestGreen,
        { IsOk: false } => Brushes.Red,
        null => Brushes.Gray
    };

    /// <summary>当前结果详细文本（期望/实际/缺药/错药）。</summary>
    public string ResultDetailText => CurrentResult switch
    {
        { IsValid: false } or null => string.Empty,
        var r => $"期望：{FormatCounts(r.ExpectedCounts)}\n" +
                 $"实际：{FormatCounts(r.DetectedCounts)}\n" +
                 $"缺药：{r.MissingCount}   错药：{r.WrongCount}"
    };

    /// <summary>检测结果历史列表。</summary>
    public ObservableCollection<BlisterCheckSummary> Results { get; } = [];

    /// <summary>测试图文件列表（按文件名排序）。</summary>
    private readonly List<string> _testImages = [];

    /// <summary>批量检测控制令牌源（停止/换图时取消当前检测批次）。</summary>
    private CancellationTokenSource? _checkCts;

    /// <summary>是否暂停批量检测（暂停后保持当前图，继续时从下一张开始）。</summary>
    private volatile bool _isPaused;

    /// <summary>
    /// 加载参考图并训练 GMM 分类器。
    /// 若当前正在批量检测，会先中断当前批次，用新参考图重训后立即生效。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task LoadReferenceAsync()
    {
        try
        {
            // 弹出文件选择对话框选择参考图
            var dialog = new OpenFileDialog
            {
                Title = "选择参考图（泡罩标准组合）",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp",
                CheckFileExists = true
            };
            if (dialog.ShowDialog() != true) return;

            ReferenceImagePath = dialog.FileName;

            // 中断当前正在进行的批量检测（若存在），确保新模型立即生效
            await DispatcherHelper.InvokeAsync(() =>
            {
                _isPaused = false;
                IsChecking = false;
                OnPropertyChanged(nameof(CheckProgressText));
            });
            Interlocked.Exchange(ref _checkCts, null)?.Cancel();
            _checkCts?.Dispose();
            _checkCts = null;

            // 后台线程训练（Halcon 训练耗时，避免卡 UI）
            await Task.Run(() =>
            {
                using var image = new HImage(ReferenceImagePath);
                _blisterCheckService.Train(image);
            });

            // 训练成功 → 切回 UI 线程更新状态
            await DispatcherHelper.InvokeAsync(() =>
            {
                IsTrained = true;
                ExpectedSummary = FormatCounts(_blisterCheckService.GetExpectedCounts());
                Log.Logger.Information("参考图训练完成（新模型已生效）：{Path}", ReferenceImagePath);
            });
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "加载参考图并训练失败");
            await DispatcherHelper.InvokeAsync(() => IsTrained = false);
        }
    }

    /// <summary>
    /// 选择测试图文件夹并开始逐张检测（纯视觉模式，不驱动运动控制卡）。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task StartCheckAsync()
    {
        try
        {
            if (!IsTrained)
            {
                Log.Logger.Warning("泡罩检测：尚未训练，请先加载参考图");
                return;
            }

            // 弹出文件夹选择对话框
            var dialog = new OpenFolderDialog { Title = "选择测试图文件夹" };
            if (dialog.ShowDialog() != true) return;

            TestImageFolder = dialog.FolderName;

            // 枚举文件夹内图片，按文件名排序
            var files = Directory.EnumerateFiles(TestImageFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                Log.Logger.Warning("泡罩检测：所选文件夹无图片文件");
                return;
            }

            _testImages.Clear();
            _testImages.AddRange(files);
            TotalCount = files.Count;
            Results.Clear();

            // 创建新的检测控制令牌
            _checkCts?.Dispose();
            _checkCts = new CancellationTokenSource();
            _isPaused = false;

            IsChecking = true;
            await Task.Run(() => DetectAllAsync(_checkCts.Token));

            await DispatcherHelper.InvokeAsync(() =>
            {
                IsChecking = false;
                OnPropertyChanged(nameof(CheckProgressText));
            });
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "纯视觉检测失败");
            await DispatcherHelper.InvokeAsync(() => IsChecking = false);
        }
    }

    /// <summary>
    /// 暂停批量检测。暂停后保持当前图，继续时从下一张开始。
    /// </summary>
    [RelayCommand]
    private void PauseCheck()
    {
        _isPaused = true;
        Log.Logger.Information("泡罩检测：已暂停");
    }

    /// <summary>
    /// 继续批量检测（从暂停处恢复）。
    /// </summary>
    [RelayCommand]
    private void ResumeCheck()
    {
        _isPaused = false;
        Log.Logger.Information("泡罩检测：已继续");
    }

    /// <summary>
    /// 停止批量检测。取消当前检测批次，保留已检测结果。
    /// </summary>
    [RelayCommand]
    private void StopCheck()
    {
        Interlocked.Exchange(ref _checkCts, null)?.Cancel();
        Log.Logger.Information("泡罩检测：已停止");
    }

    /// <summary>
    /// 后台逐张检测所有测试图（在 Task.Run 线程池执行，不进 UI 线程）。
    /// 支持暂停/继续/停止：暂停时保持当前图，继续时从下一张开始；停止时中断批次。
    /// 每张检测完成后停顿 <see cref="Constants.BatchDetectIntervalMs"/> 毫秒，
    /// 让 UI 有时间渲染当前图，避免一次性秒过看不清结果。
    /// </summary>
    private void DetectAllAsync(CancellationToken token)
    {
        for (int i = 0; i < _testImages.Count; i++)
        {
            // 停止检查：取消令牌被触发则中断
            if (token.IsCancellationRequested)
            {
                Log.Logger.Information("泡罩检测：批次被取消，已停止于第 {Index} 张", i + 1);
                break;
            }

            // 暂停：循环等待直到继续或停止
            while (_isPaused && !token.IsCancellationRequested)
            {
                Thread.Sleep(50);
            }
            if (token.IsCancellationRequested) break;

            var path = _testImages[i];
            var result = CheckSingleImage(path);

            // 切回 UI 线程更新当前图、历史列表
            DispatcherHelper.InvokeAsync(() =>
            {
                CurrentIndex = i + 1;
                CurrentFileName = Path.GetFileName(path);
                // 注意：DisplayImage 的释放统一由 UI 层（BlisterCheckPage）负责（R7），
                //       这里只传递引用，绝不能在 VM 里 Dispose（否则与 UI 释放冲突，双重释放导致图不显示）。
                if (result?.DisplayImage is not null)
                {
                    CurrentImage = result.DisplayImage;
                }
                CurrentResult = result;

                if (result is not null)
                {
                    Results.Add(new BlisterCheckSummary
                    {
                        FileName = Path.GetFileName(path),
                        IsOk = result.IsOk,
                        MissingCount = result.MissingCount,
                        WrongCount = result.WrongCount,
                        DetectedCounts = result.DetectedCounts,
                        ExpectedCounts = result.ExpectedCounts
                    });
                }
            }).GetAwaiter().GetResult();

            // 每张停顿，让用户看清当前检测结果（后台线程 Sleep，不阻塞 UI）
            Thread.Sleep(Constants.BatchDetectIntervalMs);
        }
    }

    /// <summary>
    /// 检测单张图片。
    /// </summary>
    /// <param name="path">图片文件路径。</param>
    /// <returns>检测结果；失败返回 Invalid。</returns>
    private BlisterCheckResult? CheckSingleImage(string path)
    {
        try
        {
            using var image = new HImage(path);
            return _blisterCheckService.Check(image);
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "单张检测失败：{Path}", path);
            return null;
        }
    }

    /// <summary>
    /// 单次联动检测：调用编排器执行"送料→检测→分拣"完整闭环。
    /// 图片来源：当前选中的测试图（或文件夹中第一张）。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand(CanExecute = nameof(CanStartOrchestration))]
    private async Task StartOrchestrationAsync()
    {
        try
        {
            if (_inspectionOrchestrator is null) return;

            IsOrchestrating = true;

            // 从测试图文件夹加载一张图作为检测输入（无文件夹时用 null，仅送料+分拣）
            HImage? image = null;
            if (_testImages.Count > 0)
            {
                image = new HImage(_testImages[0]);
            }

            await _inspectionOrchestrator.RunOnceAsync(image);

            image?.Dispose();

            await DispatcherHelper.InvokeAsync(() => IsOrchestrating = false);
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "联动检测异常");
            await DispatcherHelper.InvokeAsync(() => IsOrchestrating = false);
        }
    }

    /// <summary>
    /// 紧急停止联动编排。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    [RelayCommand]
    private async Task EmergencyStopAsync()
    {
        if (_inspectionOrchestrator is null) return;
        await _inspectionOrchestrator.EmergencyStopAsync();
        await DispatcherHelper.InvokeAsync(() => IsOrchestrating = false);
    }

    /// <summary>联动编排是否可用：已训练 + 运动控制卡已连接 + 未在编排中。</summary>
    private bool CanStartOrchestration() => IsTrained && IsMotionCardConnected && !IsOrchestrating;

    /// <summary>IsTrained 变化时刷新"开始联动"按钮可用性与训练状态文本。</summary>
    partial void OnIsTrainedChanged(bool value)
    {
        StartOrchestrationCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TrainStatusText));
    }

    /// <summary>IsMotionCardConnected 变化时刷新"开始联动"按钮可用性与连接状态文本。</summary>
    partial void OnIsMotionCardConnectedChanged(bool value)
    {
        StartOrchestrationCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(MotionCardStatusText));
    }

    /// <summary>IsOrchestrating 变化时刷新"开始联动"按钮可用性（编排中禁用，结束后恢复）。</summary>
    partial void OnIsOrchestratingChanged(bool value) => StartOrchestrationCommand.NotifyCanExecuteChanged();

    /// <summary>CurrentIndex/TotalCount 变化时刷新检测进度文本。</summary>
    partial void OnCurrentIndexChanged(int value) => OnPropertyChanged(nameof(CheckProgressText));

    /// <summary>TotalCount 变化时刷新检测进度文本。</summary>
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(CheckProgressText));

    /// <summary>CurrentResult 变化时刷新结果状态文本与颜色。</summary>
    partial void OnCurrentResultChanged(BlisterCheckResult? value)
    {
        OnPropertyChanged(nameof(ResultStatusText));
        OnPropertyChanged(nameof(ResultStatusBrush));
        OnPropertyChanged(nameof(ResultDetailText));
    }

    /// <summary>
    /// 运动控制卡连接状态变化回调。跨线程触发，经 DispatcherHelper 切回 UI 线程（T5）。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">新状态。</param>
    private void OnMotionCardStateChanged(object? sender, ConnectionState e)
    {
        DispatcherHelper.InvokeAsync(() =>
        {
            IsMotionCardConnected = e == ConnectionState.Connected;
        });
    }

    /// <summary>
    /// 编排完成事件回调。跨线程触发，经 DispatcherHelper 切回 UI 线程（T5）。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">编排结果。</param>
    private void OnInspectionCompleted(object? sender, InspectionResult e)
    {
        DispatcherHelper.InvokeAsync(() =>
        {
            IsOrchestrating = false;

            // 更新当前图像与结果（DisplayImage 释放统一由 UI 层负责，R7）
            if (e.VisionResult?.DisplayImage is not null)
            {
                CurrentImage = e.VisionResult.DisplayImage;
            }
            CurrentResult = e.VisionResult;

            // 记录历史
            if (e.VisionResult is not null)
            {
                Results.Add(new BlisterCheckSummary
                {
                    FileName = CurrentFileName,
                    IsOk = e.VisionResult.IsOk,
                    MissingCount = e.VisionResult.MissingCount,
                    WrongCount = e.VisionResult.WrongCount,
                    DetectedCounts = e.VisionResult.DetectedCounts,
                    ExpectedCounts = e.VisionResult.ExpectedCounts,
                    SortAxis = e.SortAxis
                });
            }
        });
    }

    /// <summary>
    /// 上一张图。切换到 Results 列表中上一条记录。
    /// </summary>
    [RelayCommand]
    private void PreviousImage()
    {
        if (CurrentIndex <= 1) return;
        int idx = CurrentIndex - 2;
        if (idx >= 0 && idx < Results.Count)
        {
            var prev = Results[idx];
            CurrentIndex = idx + 1;
            CurrentFileName = prev.FileName;
        }
    }

    /// <summary>
    /// 下一张图。切换到 Results 列表中下一条记录。
    /// </summary>
    [RelayCommand]
    private void NextImage()
    {
        if (CurrentIndex >= Results.Count) return;
        int idx = CurrentIndex;
        if (idx < Results.Count)
        {
            var next = Results[idx];
            CurrentIndex = idx + 1;
            CurrentFileName = next.FileName;
        }
    }

    /// <summary>格式化为 "3黄 6红 6绿" 展示文本。</summary>
    private static string FormatCounts(int[] counts)
    {
        if (counts.Length < 3) return string.Join(",", counts);
        return $"{counts[0]}黄 {counts[1]}红 {counts[2]}绿";
    }
}

/// <summary>
/// 单张图片检测摘要（用于历史列表）。
/// </summary>
public sealed class BlisterCheckSummary
{
    /// <summary>文件名。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>是否 OK。</summary>
    public bool IsOk { get; init; }

    /// <summary>缺药数量。</summary>
    public int MissingCount { get; init; }

    /// <summary>错药数量。</summary>
    public int WrongCount { get; init; }

    /// <summary>实际检测到的各类药丸数量。</summary>
    public int[] DetectedCounts { get; init; } = [];

    /// <summary>期望的各类药丸数量。</summary>
    public int[] ExpectedCounts { get; init; } = [];

    /// <summary>分拣轴号（联动模式：2=NG拨杆 3=OK拨杆；纯视觉模式：0=未分拣）。</summary>
    public int SortAxis { get; init; }
}
