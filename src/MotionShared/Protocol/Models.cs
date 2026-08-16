using System.Text.Json;

namespace MotionShared.Protocol;

/// <summary>主程序 → 模拟器 的请求。</summary>
public class CommandRequest
{
    public string Id { get; set; } = "";
    public string Cmd { get; set; } = "";
    public JsonElement Params { get; set; }

    public T? GetParam<T>(string name)
    {
        if (Params.ValueKind == JsonValueKind.Undefined) return default;
        if (!Params.TryGetProperty(name, out var v)) return default;
        return v.Deserialize<T>();
    }
}

/// <summary>模拟器 → 主程序 的响应。status=0 成功，非 0 为错误码。</summary>
public class CommandResponse
{
    public string Id { get; set; } = "";
    public int Status { get; set; }
    public string Msg { get; set; } = "OK";
    public JsonElement Data { get; set; }

    public static CommandResponse Ok(string id, object? data = null) => new()
    {
        Id = id,
        Status = ErrorCode.Success,
        Msg = "OK",
        Data = data is null ? default : JsonSerializer.SerializeToElement(data)
    };

    public static CommandResponse Fail(string id, int status, string msg) => new()
    {
        Id = id,
        Status = status,
        Msg = msg
    };
}

/// <summary>模拟器 → 主程序 的单向推送（无 id）。</summary>
public class PushMessage
{
    public string Push { get; set; } = "";
    public JsonElement Data { get; set; }

    public static PushMessage Create(string push, object data) => new()
    {
        Push = push,
        Data = JsonSerializer.SerializeToElement(data)
    };
}

/// <summary>错误码定义（与设计文档第七节一致）。</summary>
public static class ErrorCode
{
    public const int Success = 0;
    public const int NotConnected = -1;
    public const int ParamError = -2;
    public const int AxisAlarm = -3;
    public const int SoftLimit = -4;
    public const int UnknownCmd = -5;
    public const int Timeout = -100;
    public const int Disconnected = -101;
}
