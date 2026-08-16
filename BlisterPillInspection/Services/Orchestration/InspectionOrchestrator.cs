using HalconDotNet;
using Microsoft.Extensions.Logging;
using BlisterPillInspection.Infrastructure;
using BlisterPillInspection.Models;
using BlisterPillInspection.Services.Interfaces;

namespace BlisterPillInspection.Services.Orchestration;

/// <summary>
/// 检测编排器：串联运动控制卡与视觉检测，实现"送料→检测→分拣"闭环。
/// 轴1 送料、轴2 NG 拨杆、轴3 OK 拨杆。
/// </summary>
public sealed class InspectionOrchestrator : IInspectionOrchestrator
{
    private readonly IBlisterCheckService _blisterCheckService;
    private readonly IMotionCardService _motionCardService;
    private readonly ILogger<InspectionOrchestrator> _logger;

    /// <summary>编排串行化信号量：同一时刻只允许一个 RunOnceAsync 执行（T2）。</summary>
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>取消令牌源：急停时置位，中止正在执行的编排。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>Dispose 标志，防止重复释放。</summary>
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="InspectionOrchestrator"/>。
    /// </summary>
    /// <param name="blisterCheckService">泡罩检测服务（视觉判断 OK/NG）。</param>
    /// <param name="motionCardService">运动控制卡服务（送料/分拣执行机构）。</param>
    /// <param name="logger">日志。</param>
    public InspectionOrchestrator(
        IBlisterCheckService blisterCheckService,
        IMotionCardService motionCardService,
        ILogger<InspectionOrchestrator> logger)
    {
        _blisterCheckService = blisterCheckService;
        _motionCardService = motionCardService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool IsRunning => _runLock.CurrentCount == 0;

    /// <inheritdoc/>
    public event EventHandler<InspectionResult>? InspectionCompleted;

    /// <inheritdoc/>
    public async Task<InspectionResult> RunOnceAsync(HImage? testImage = null, CancellationToken ct = default)
    {
        // a. 串行化保护：同时只允许一个编排流程（T2）
        if (!await _runLock.WaitAsync(0, ct))
        {
            _logger.LogWarning("检测编排：上一次编排尚未完成，本次调用被拒绝");
            return InspectionResult.Invalid;
        }

        // 用本次调用自己的取消令牌，供急停中止（EmergencyStopAsync 会 cancel 共享的 _cts）
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _cts.Token;

        try
        {
            // b. 检查前提：运动控制卡已连接 + 检测服务已训练
            if (!_motionCardService.IsConnected)
            {
                _logger.LogWarning("检测编排：运动控制卡未连接，无法执行");
                return InspectionResult.Invalid;
            }
            if (testImage is not null && !_blisterCheckService.IsTrained)
            {
                _logger.LogWarning("检测编排：检测服务未训练，跳过视觉检测");
                return InspectionResult.Invalid;
            }

            // c. 传送带送料（轴1 相对移动送料步进）
            if (!await _motionCardService.MoveRelAsync(
                    Constants.AxisConveyor, Constants.ConveyorFeedStep, Constants.ConveyorVelocity))
            {
                _logger.LogError("检测编排：传送带送料指令下发失败");
                return InspectionResult.Invalid;
            }
            linkedCt.ThrowIfCancellationRequested();

            // d. 等待传送带到位（简单延时等待到位，真实工程可订阅轴到位事件）
            await WaitAxisSettledAsync(linkedCt);

            // e. 视觉检测（testImage == null 时跳过检测，默认走 NG 拨杆）
            var visionResult = testImage is null
                ? null
                : _blisterCheckService.Check(testImage);
            if (testImage is not null && visionResult is { IsValid: false })
            {
                _logger.LogWarning("检测编排：视觉检测无效，默认走 NG 拨杆");
            }

            // f. 判定结果并分拣：
            //    - OK  → 轴3（OK拨杆）推良品
            //    - NG  → 轴2（NG拨杆）剔除
            //    - 无图 → 默认走 NG 拨杆
            bool isOk = visionResult?.IsOk ?? false;
            int sorterAxis = isOk ? Constants.AxisOkSorter : Constants.AxisNgSorter;

            if (!await _motionCardService.MoveRelAsync(
                    sorterAxis, Constants.SorterStroke, Constants.SorterVelocity))
            {
                _logger.LogError("检测编排：分拣拨杆指令下发失败，IsOk={IsOk}", isOk);
                return InspectionResult.Invalid;
            }
            linkedCt.ThrowIfCancellationRequested();

            // g. 等待分拣到位
            await WaitAxisSettledAsync(linkedCt);

            // h. 拨杆回缩（返回原位，准备下一次）
            if (!await _motionCardService.MoveRelAsync(
                    sorterAxis, -Constants.SorterStroke, Constants.SorterVelocity))
            {
                _logger.LogError("检测编排：拨杆回缩指令下发失败，IsOk={IsOk}", isOk);
                return InspectionResult.Invalid;
            }
            await WaitAxisSettledAsync(linkedCt);

            // i. 触发完成事件 + 返回结果
            var result = new InspectionResult
            {
                IsValid = true,
                SortTriggered = true,
                SortAxis = sorterAxis,
                VisionResult = visionResult
            };
            RaiseInspectionCompleted(result);
            _logger.LogInformation("检测编排完成：IsOk={IsOk}, 分拣轴={SortAxis}", isOk, sorterAxis);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("检测编排：被取消（急停/用户停止）");
            return InspectionResult.Invalid;
        }
        catch (Exception ex)
        {
            // R4：任何异常 → 急停所有轴 + 返回 Invalid，不崩进程
            _logger.LogError(ex, "检测编排：执行异常，触发急停");
            try
            {
                await _motionCardService.EmergencyStopAsync();
            }
            catch (Exception stopEx)
            {
                _logger.LogError(stopEx, "检测编排：急停指令失败");
            }
            return InspectionResult.Invalid;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _runLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task EmergencyStopAsync()
    {
        try
        {
            // 急停所有轴 + 取消正在执行的编排
            await _motionCardService.EmergencyStopAsync();
            _cts?.Cancel();
            _logger.LogWarning("检测编排：已执行紧急停止");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检测编排：紧急停止异常");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _runLock.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检测编排：释放资源异常");
        }
    }

    /// <summary>
    /// 简单等待轴到位。真实工程可改为订阅轴状态推送（AxisStatusReceived）判断到位，
    /// 本项目以固定延时近似（运动距离/速度），兼顾简单与不阻塞 UI 线程。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    private async Task WaitAxisSettledAsync(CancellationToken ct)
    {
        // 以最慢的拨杆速度估算到位时长：30mm / 20mm/s = 1.5s，留 20% 余量
        await Task.Delay(Constants.AxisSettleDelayMs, ct);
    }

    /// <summary>
    /// 触发 <see cref="InspectionCompleted"/> 事件。
    /// 用 Interlocked.CompareExchange 取订阅委托快照，避免事件触发瞬间订阅者被并发修改（T5）。
    /// </summary>
    /// <param name="result">编排结果。</param>
    private void RaiseInspectionCompleted(InspectionResult result)
    {
        var handler = Interlocked.CompareExchange(ref InspectionCompleted, null, null);
        handler?.Invoke(this, result);
    }
}
