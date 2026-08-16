using HalconDotNet;
using VisionMotionAlignment.Models.Vision;

namespace VisionMotionAlignment.Services.Interfaces;

/// <summary>
/// 泡罩药丸检测服务接口：Train 用参考图训练 GMM 分类器，Check 检测测试图缺药/错药。
/// Train 与 Check 不可并发调用。
/// </summary>
public interface IBlisterCheckService : IDisposable
{
    /// <summary>GMM 分类器是否已训练完成。</summary>
    bool IsTrained { get; }

    /// <summary>
    /// 获取期望的各类药丸数量（来自参考图，如 [3, 6, 6]）。
    /// 用于 UI 展示期望组合。未训练时返回 <see cref="Array.Empty{T}"/>。
    /// </summary>
    /// <returns>各类期望数量数组。</returns>
    int[] GetExpectedCounts();

    /// <summary>
    /// 用参考图训练 GMM 分类器。训练后可多次调用 <see cref="Check"/> 检测后续图像。
    /// </summary>
    /// <param name="referenceImage">参考图（彩色 HImage，含标准药丸组合）。</param>
    /// <exception cref="InvalidOperationException">训练失败时抛出。</exception>
    void Train(HImage referenceImage);

    /// <summary>
    /// 检测单张泡罩图像。
    /// <see cref="Train"/> 只需训练一次，训练完成后可反复调用本方法检测多张图像，无需重复训练。
    /// </summary>
    /// <param name="testImage">待检测的泡罩图像（彩色 HImage）。</param>
    /// <returns>检测结果；未训练或检测异常时返回 <see cref="BlisterCheckResult.Invalid"/>。</returns>
    BlisterCheckResult Check(HImage testImage);
}
