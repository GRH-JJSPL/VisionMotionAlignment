using HalconDotNet;

namespace BlisterPillInspection.Models.Vision;

/// <summary>
/// 泡罩药丸检测结果：检测图像、分类区域、统计信息与 OK/NG 判定。
/// Halcon 非托管资源（DisplayImage/FinalClasses/WrongPills/MissingPills）由 UI 层消费后负责 Dispose。
/// </summary>
public sealed class BlisterCheckResult
{
    /// <summary>检测结果是否合格（所有药丸类型数量均匹配参考图）。</summary>
    public bool IsOk { get; init; }

    /// <summary>是否有效（检测过程未异常）。</summary>
    public bool IsValid { get; init; }

    /// <summary>期望的各类药丸数量（来自参考图，如 [3, 6, 6]）。</summary>
    public int[] ExpectedCounts { get; init; } = [];

    /// <summary>实际检测到的各类药丸数量。</summary>
    public int[] DetectedCounts { get; init; } = [];

    /// <summary>缺失药丸数量。</summary>
    public int MissingCount { get; init; }

    /// <summary>错误类型药丸数量。</summary>
    public int WrongCount { get; init; }

    /// <summary>对齐后的检测图像（用于 UI 显示）。消费方负责 Dispose。</summary>
    public HImage? DisplayImage { get; init; }

    /// <summary>正确分类的药丸区域（用于 UI 绿色叠加）。消费方负责 Dispose。</summary>
    public HObject? FinalClasses { get; init; }

    /// <summary>错误类型药丸区域（用于 UI 红色叠加）。消费方负责 Dispose。</summary>
    public HObject? WrongPills { get; init; }

    /// <summary>缺失药丸区域（用于 UI 黄色叠加）。消费方负责 Dispose。</summary>
    public HObject? MissingPills { get; init; }

    /// <summary>无效结果单例（IsValid = false）。</summary>
    public static readonly BlisterCheckResult Invalid = new() { IsValid = false };
}
