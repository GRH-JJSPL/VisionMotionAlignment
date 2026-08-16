namespace BlisterPillInspection.Models.Camera;

/// <summary>
/// 相机设备信息，用于设备枚举与 <c>OpenAsync</c> 打开。
/// </summary>
public sealed class CameraDeviceInfo
{
    /// <summary>设备唯一标识，用于 OpenAsync 打开设备。</summary>
    public string DeviceKey { get; init; } = string.Empty;

    /// <summary>设备显示名。</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>接口类型："USB3" 或 "GigE"。</summary>
    public string InterfaceType { get; init; } = "USB3";
}
