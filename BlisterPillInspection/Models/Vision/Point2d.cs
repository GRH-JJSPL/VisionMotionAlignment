namespace BlisterPillInspection.Models.Vision;

/// <summary>
/// 二维点（双精度）—— 像素坐标的统一承载类型。
///
/// 【设计原因】
/// HalconDotNet 的 HTuple 是 double 序列，语义不明确；
/// 此处自定义值类型承载像素坐标，X/Y 语义清晰，且值类型栈分配 + 自动值相等比较。
/// </summary>
/// <param name="X">X 坐标。</param>
/// <param name="Y">Y 坐标。</param>
public readonly record struct Point2d(double X, double Y);
