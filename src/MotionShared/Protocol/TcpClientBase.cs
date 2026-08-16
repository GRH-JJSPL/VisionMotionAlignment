using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;

namespace MotionShared.Protocol;

/// <summary>
/// TCP 客户端基类。独立接收循环 + TaskCompletionSource 匹配响应，
/// 同时把推送消息通过 OnPushReceived 抛出。解决"在 SendCommand 里同步读响应会与推送错乱"的问题。
/// GtsClient / MvsClient 共用此基类。
/// </summary>
public abstract class TcpClientBase : IDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResponse>> _pending = new();
    private CancellationTokenSource? _readCts;
    private bool _running;

    /// <summary>收到推送（axis_status / image_frame）。</summary>
    public event Action<PushMessage>? OnPushReceived;

    /// <summary>连接断开（参数为原因）。</summary>
    public event Action<string>? OnDisconnected;

    public bool IsConnected => _running && (_client?.Connected ?? false);

    public async Task ConnectAsync(string ip, int port)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(ip, port);
        _stream = _client.GetStream();
        _readCts = new CancellationTokenSource();
        _running = true;
        _ = ReadLoopAsync(_readCts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var stream = _stream!;
        while (_running && !ct.IsCancellationRequested)
        {
            string json;
            try
            {
                json = await SocketFrame.ReceiveMessageAsync(stream);
            }
            catch
            {
                break;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                bool isResp = doc.RootElement.TryGetProperty("id", out _);
                bool isPush = doc.RootElement.TryGetProperty("push", out _);

                if (isResp)
                {
                    var resp = JsonSerializer.Deserialize<CommandResponse>(json);
                    if (resp != null && _pending.TryRemove(resp.Id, out var tcs))
                        tcs.TrySetResult(resp);
                }
                else if (isPush)
                {
                    var push = JsonSerializer.Deserialize<PushMessage>(json);
                    if (push != null) OnPushReceived?.Invoke(push);
                }
            }
            catch
            {
                // 单条消息解析失败不影响连接
            }
        }

        bool wasRunning = _running;
        _running = false;
        if (wasRunning) OnDisconnected?.Invoke("连接已断开");
    }

    /// <summary>发送命令并等待对应 id 的响应。默认 5 秒超时。</summary>
    public async Task<CommandResponse> SendCommandAsync(string cmd, object? parameters = null, int timeoutMs = 5000)
    {
        if (!_running)
            return new CommandResponse { Status = ErrorCode.NotConnected, Msg = "未连接" };

        var request = new CommandRequest
        {
            Id = Guid.NewGuid().ToString(),
            Cmd = cmd,
            Params = parameters is null
                ? default
                : JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(parameters))
        };

        var tcs = new TaskCompletionSource<CommandResponse>();
        _pending[request.Id] = tcs;

        await _sendLock.WaitAsync();
        try
        {
            await SocketFrame.SendMessageAsync(_stream!, JsonSerializer.Serialize(request));
        }
        finally
        {
            _sendLock.Release();
        }

        var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (winner != tcs.Task)
        {
            _pending.TryRemove(request.Id, out _);
            return new CommandResponse { Id = request.Id, Status = ErrorCode.Timeout, Msg = "通信超时" };
        }
        return await tcs.Task;
    }

    public virtual void Dispose()
    {
        _running = false;
        _readCts?.Cancel();
        // 唤醒所有等待响应的调用方，避免 await 卡到超时
        foreach (var kv in _pending)
            kv.Value.TrySetResult(new CommandResponse { Id = kv.Key, Status = ErrorCode.Disconnected, Msg = "连接已关闭" });
        _pending.Clear();
        _stream?.Dispose();
        _client?.Dispose();
        _sendLock.Dispose();
    }
}
