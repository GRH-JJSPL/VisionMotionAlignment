using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MotionShared.Protocol;

/// <summary>
/// TCP 服务端基类。监听端口、管理客户端会话、分发命令、支持主动广播推送。
/// GtsServer / MvsServer 共用此基类。
/// </summary>
public abstract class TcpServerBase : IDisposable
{
    private TcpListener? _listener;
    private readonly List<ClientSession> _sessions = new();
    private readonly object _sessionsLock = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    public event Action<string, string>? OnCommandReceived;   // (clientId, json)
    public event Action<string>? OnClientConnected;
    public event Action<string>? OnClientDisconnected;

    public int ClientCount
    {
        get { lock (_sessionsLock) return _sessions.Count; }
    }

    public void Start(int port)
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _running = true;
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch
            {
                break;
            }
            var session = new ClientSession(client, this);
            lock (_sessionsLock) _sessions.Add(session);
            OnClientConnected?.Invoke(session.Id);
            _ = session.RunAsync();
        }
    }

    /// <summary>子类实现：处理命令，返回响应。</summary>
    protected abstract Task<CommandResponse> ProcessCommandAsync(ClientSession session, CommandRequest req);

    /// <summary>向所有连接的客户端广播推送。</summary>
    protected void BroadcastPush(PushMessage push)
    {
        var json = JsonSerializer.Serialize(push);
        List<ClientSession> snap;
        lock (_sessionsLock) snap = _sessions.ToList();
        foreach (var s in snap)
            _ = s.SendAsync(json);
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _listener?.Stop();
        List<ClientSession> snap;
        lock (_sessionsLock) snap = _sessions.ToList();
        foreach (var s in snap) s.Dispose();
        lock (_sessionsLock) _sessions.Clear();
    }

    public void Dispose() => Stop();

    /// <summary>单个客户端会话。</summary>
    public sealed class ClientSession : IDisposable
    {
        public string Id { get; }
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly TcpServerBase _owner;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public ClientSession(TcpClient client, TcpServerBase owner)
        {
            _client = client;
            _owner = owner;
            _stream = client.GetStream();
            Id = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
        }

        public async Task RunAsync()
        {
            try
            {
                while (_client.Connected)
                {
                    string json;
                    try { json = await SocketFrame.ReceiveMessageAsync(_stream); }
                    catch { break; }

                    CommandRequest? req;
                    try { req = JsonSerializer.Deserialize<CommandRequest>(json); }
                    catch { continue; }
                    if (req is null || string.IsNullOrEmpty(req.Cmd)) continue;

                    _owner.OnCommandReceived?.Invoke(Id, json);

                    CommandResponse resp;
                    try
                    {
                        resp = await _owner.ProcessCommandAsync(this, req);
                    }
                    catch (Exception ex)
                    {
                        resp = CommandResponse.Fail(req.Id, -999, "服务端异常: " + ex.Message);
                    }
                    await SendAsync(JsonSerializer.Serialize(resp));
                }
            }
            catch
            {
                // 忽略，统一在 finally 处理断开
            }
            finally
            {
                lock (_owner._sessionsLock) _owner._sessions.Remove(this);
                _owner.OnClientDisconnected?.Invoke(Id);
                Dispose();
            }
        }

        public async Task SendAsync(string json)
        {
            await _sendLock.WaitAsync();
            try { await SocketFrame.SendMessageAsync(_stream, json); }
            catch { }
            finally { _sendLock.Release(); }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _client?.Dispose();
            _sendLock.Dispose();
        }
    }
}
