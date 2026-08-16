using HalconDotNet;
using BlisterPillInspection.Models;

namespace BlisterPillInspection.Services.Interfaces;

/// <summary>
/// 检测编排器接口：串联运动控制卡与视觉检测，实现"送料→检测→分拣"闭环（轴1 送料/轴2 NG/轴3 OK）。
/// </summary>
public interface IInspectionOrchestrator : IDisposable
{
    /// <summary>是否正在执行检测编排。</summary>
    bool IsRunning { get; }

    /// <summary>
    /// 检测编排完成事件。每次 RunOnceAsync 完成后触发（含异常情况）。
    /// 订阅方须经 DispatcherHelper 切回 UI 线程后再更新 ViewModel（T5）。
    /// </summary>
    event EventHandler<InspectionResult>? InspectionCompleted;

    /// <summary>
    /// 执行一次完整检测编排：送料 → 检测 → 分拣。
    /// 调用前须确保 IMotionCardService 已连接且 IBlisterCheckService 已训练。
    /// </summary>
    /// <param name="testImage">待检测图像（由调用方传入，文件加载或相机拍照均可）。若为 null 则编排器只执行送料+分拣，跳过检测。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>编排结果。</returns>
    Task<InspectionResult> RunOnceAsync(HImage? testImage = null, CancellationToken ct = default);

    /// <summary>
    /// 紧急停止：急停所有轴，中止正在进行的编排。
    /// </summary>
    Task EmergencyStopAsync();
}
