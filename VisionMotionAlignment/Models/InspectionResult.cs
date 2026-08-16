using VisionMotionAlignment.Models.Vision;

namespace VisionMotionAlignment.Models;

/// <summary>
/// 检测编排结果：包含视觉检测结果与分拣动作记录。
/// </summary>
public sealed class InspectionResult
{
    /// <summary>视觉检测结果。</summary>
    public BlisterCheckResult? VisionResult { get; init; }

    /// <summary>是否触发了分拣动作。</summary>
    public bool SortTriggered { get; init; }

    /// <summary>分拣轴号（1=传送带 2=NG拨杆 3=OK拨杆）。</summary>
    public int SortAxis { get; init; }

    /// <summary>编排是否成功完成（未异常）。</summary>
    public bool IsValid { get; init; }

    /// <summary>编排异常时的错误信息。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>无效结果单例。</summary>
    public static readonly InspectionResult Invalid = new() { IsValid = false, ErrorMessage = "编排未完成" };
}
