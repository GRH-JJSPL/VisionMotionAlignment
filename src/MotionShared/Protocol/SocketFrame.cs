using System.Net.Sockets;
using System.Text;

namespace MotionShared.Protocol;

/// <summary>
/// 长度前缀 + JSON 消息帧。4 字节小端长度 + UTF-8 JSON 正文。
/// 请求-响应与推送复用同一条 TCP 连接，按消息是否含 "id" 字段区分。
/// </summary>
public static class SocketFrame
{
    public static async Task SendMessageAsync(NetworkStream stream, string json)
    {
        byte[] data = Encoding.UTF8.GetBytes(json);
        byte[] lengthPrefix = BitConverter.GetBytes(data.Length); // 4 字节，小端序
        await stream.WriteAsync(lengthPrefix, 0, 4);
        await stream.WriteAsync(data, 0, data.Length);
    }

    public static async Task<string> ReceiveMessageAsync(NetworkStream stream)
    {
        byte[] lengthBuffer = new byte[4];
        await ReadExactAsync(stream, lengthBuffer, 4);
        int length = BitConverter.ToInt32(lengthBuffer, 0);

        if (length <= 0 || length > 64 * 1024 * 1024)
            throw new IOException($"非法消息长度: {length}");

        byte[] dataBuffer = new byte[length];
        await ReadExactAsync(stream, dataBuffer, length);
        return Encoding.UTF8.GetString(dataBuffer);
    }

    public static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer, offset, count - offset);
            if (read == 0) throw new IOException("连接已关闭");
            offset += read;
        }
    }
}
