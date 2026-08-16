namespace MotionShared.Dtos;

/// <summary>单轴状态推送载荷。axis: 1=X 2=Y 3=Z 4=U。</summary>
public class AxisStatusDto
{
    public int Axis { get; set; }
    public double Pos { get; set; }
    public double Vel { get; set; }
    public string Status { get; set; } = "Idle";   // Idle / Moving / Homing / Alarm
    public bool Alarm { get; set; }
}

/// <summary>axis_status 推送的 data 载荷：所有轴的当前状态。</summary>
public class AxisStatusPush
{
    public List<AxisStatusDto> Axes { get; set; } = new();
}
