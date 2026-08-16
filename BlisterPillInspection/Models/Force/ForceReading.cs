namespace BlisterPillInspection.Models.Force;

/// <summary>
/// 力值传感器单次读数。
/// </summary>
public sealed class ForceReading
{
    /// <summary>力值。</summary>
    public double Value { get; init; }

    /// <summary>单位（如 "kN"）。</summary>
    public string Unit { get; init; } = "kN";

    /// <summary>采样时间戳。</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>读数是否有效。</summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// 无效读数实例（<see cref="IsValid"/> = false）。
    /// </summary>
    public static readonly ForceReading Invalid = new() { IsValid = false };
}
