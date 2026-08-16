using System.Collections.Concurrent;
using System.IO;
using HalconDotNet;
using Serilog;
using VisionMotionAlignment.Models;
using VisionMotionAlignment.Models.Camera;
using VisionMotionAlignment.Services.Interfaces;

namespace VisionMotionAlignment.Services.Camera;

/// <summary>
/// ICameraService 的本地图片模拟实现：从目录循环读图按帧率推送，无真实相机时跑通取图管线。
/// </summary>
public sealed class StubCameraService : ICameraService
{
    private static readonly string[] SupportedExtensions = { ".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff" };

    /// <summary>模拟设备唯一标识。</summary>
    public const string StubDeviceKey = "LocalImage";

    /// <summary>图片目录路径。</summary>
    private readonly string _imageDirectory;

    /// <summary>图片文件列表（启动流时扫描）。</summary>
    private string[] _imageFiles = Array.Empty<string>();

    /// <summary>当前播放的图片索引（循环播放）。</summary>
    private int _playIndex;

    /// <summary>帧序号自增。</summary>
    private long _frameIndex;

    /// <summary>采集循环任务。</summary>
    private Task? _streamTask;

    /// <summary>采集循环取消源。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>当前连接状态（volatile，多线程可见）。</summary>
    private volatile ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>Dispose 标志，防止重复释放。</summary>
    private int _disposed;

    /// <summary>
    /// 构造本地图片模拟相机。
    /// </summary>
    /// <param name="imageDirectory">图片目录路径。默认 "StubImages"（相对工作目录）。</param>
    public StubCameraService(string imageDirectory = "StubImages")
    {
        _imageDirectory = imageDirectory;
    }

    /// <summary>
    /// 枚举设备。始终返回一个"本地图片模拟器"设备。
    /// </summary>
    /// <returns>包含模拟设备的列表。</returns>
    public IReadOnlyList<CameraDeviceInfo> EnumerateDevices() =>
        new[]
        {
            new CameraDeviceInfo
            {
                DeviceKey = StubDeviceKey,
                DeviceName = "本地图片模拟器",
                InterfaceType = "LocalImage"
            }
        };

    /// <summary>
    /// 打开"设备"：扫描图片目录，确认至少存在一张可用图片。
    /// </summary>
    /// <param name="deviceKey">设备唯一标识（须为 <see cref="StubDeviceKey"/>）。</param>
    /// <param name="parameters">相机参数（当前不参与，仅记录帧率用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>目录存在且包含图片时返回 true，否则 false。</returns>
    public Task<bool> OpenAsync(string deviceKey, CameraParameters parameters, CancellationToken cancellationToken = default)
    {
        if (deviceKey != StubDeviceKey)
        {
            Log.Logger.Warning("StubCamera：未知设备键 {DeviceKey}", deviceKey);
            return Task.FromResult(false);
        }

        try
        {
            if (!Directory.Exists(_imageDirectory))
            {
                Log.Logger.Warning("StubCamera：图片目录不存在 {Directory}", _imageDirectory);
                return Task.FromResult(false);
            }

            _imageFiles = Directory.GetFiles(_imageDirectory)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToArray();

            if (_imageFiles.Length == 0)
            {
                Log.Logger.Warning("StubCamera：目录 {Directory} 下没有支持的图片文件", _imageDirectory);
                return Task.FromResult(false);
            }

            TransitionTo(ConnectionState.Connected);
            Log.Logger.Information("StubCamera：打开成功，加载图片 {Count} 张，目录 {Directory}",
                _imageFiles.Length, _imageDirectory);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "StubCamera：打开失败");
            TransitionTo(ConnectionState.Failed);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// 关闭"设备"：停止采集并回到未连接状态。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        TransitionTo(ConnectionState.Disconnected);
    }

    /// <summary>
    /// 启动连续取图流：后台循环从目录读取图片并触发 <see cref="FrameReceived"/>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task StartStreamAsync(CancellationToken cancellationToken = default)
    {
        if (_imageFiles.Length == 0)
        {
            Log.Logger.Warning("StubCamera：未先 OpenAsync 或没有可用图片，拒绝启动采集");
            return Task.CompletedTask;
        }

        if (_streamTask is not null && !_streamTask.IsCompleted)
        {
            Log.Logger.Information("StubCamera：采集循环已在运行");
            return Task.CompletedTask;
        }

        // 链接外部令牌，便于外部统一取消
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _playIndex = 0;

        // 后台采集循环（默认帧率按 appsettings 的 Camera.FrameRate，此处固定 30fps 间隔）
        int frameIntervalMs = 1000 / 30;
        _streamTask = Task.Run(() => StreamLoopAsync(frameIntervalMs, _cts.Token));

        Log.Logger.Information("StubCamera：采集循环已启动，目录 {Directory}", _imageDirectory);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止连续取图流：取消后台循环。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        if (_streamTask is not null)
        {
            try
            {
                await _streamTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常取消：忽略
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "StubCamera：停止采集循环异常");
            }
        }

        _cts.Dispose();
        _cts = null;
        _streamTask = null;

        Log.Logger.Information("StubCamera：采集循环已停止");
    }

    /// <summary>帧到达事件（后台线程触发，订阅者自行切 UI 线程）。</summary>
    public event EventHandler<CameraFrame>? FrameReceived;

    /// <summary>连接状态变化事件。</summary>
    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>
    /// 后台采集循环：按固定间隔从目录循环读取图片并触发帧事件。
    /// </summary>
    private async Task StreamLoopAsync(int frameIntervalMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 循环播放：取下一张（回到末尾后从第一张继续）
                string filePath = _imageFiles[_playIndex % _imageFiles.Length];
                _playIndex++;

                // 从文件读取图片（HImage 所有权转移给消费者，由消费者 Dispose，R7）
                var image = new HImage();
                image.ReadImage(filePath);

                var frame = new CameraFrame
                {
                    Workstation = WorkstationId.Workstation1,
                    Image = image,
                    FrameIndex = Interlocked.Increment(ref _frameIndex)
                };

                FrameReceived?.Invoke(this, frame);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "StubCamera：读取图片失败，跳过该帧");
            }

            try
            {
                await Task.Delay(frameIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 状态转换。状态实际变化时触发 <see cref="StateChanged"/> 事件。
    /// </summary>
    private void TransitionTo(ConnectionState newState)
    {
        var old = _state;
        _state = newState;
        if (old != newState)
        {
            StateChanged?.Invoke(this, newState);
        }
    }

    /// <summary>释放资源：停止采集循环。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "StubCamera：Dispose 异常");
        }
    }
}
