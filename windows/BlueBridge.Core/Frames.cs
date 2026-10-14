using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace BlueBridge.Core;
public enum FrameType : byte { Hello = 1, Trust, Text, Begin, Chunk, End, Ack, Cancel, Error, Ping, Pong }
public sealed class ProtocolException(string message) : IOException(message);
public sealed class Frame
{
    private readonly byte[] payload;
    public FrameType Type { get; }
    public Guid Id { get; }
    public ReadOnlyMemory<byte> Payload => payload;
    public Frame(FrameType type, Guid id, ReadOnlySpan<byte> bytes) { Type = type; Id = id; payload = bytes.ToArray(); }
}
public static class FrameCodec
{
    public const int MaxPayload = 1_048_576;
    public const int HeaderLength = 28;
    public static byte[] Encode(Frame frame)
    {
        if (!Enum.IsDefined(frame.Type) || frame.Payload.Length > MaxPayload) throw new ProtocolException("无效的消息类型或长度");
        var bytes = new byte[HeaderLength + frame.Payload.Length];
        "BBRG"u8.CopyTo(bytes); bytes[4] = 1; bytes[5] = (byte)frame.Type;
        frame.Id.TryWriteBytes(bytes.AsSpan(8, 16), true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24, 4), (uint)frame.Payload.Length);
        frame.Payload.Span.CopyTo(bytes.AsSpan(HeaderLength));
        return bytes;
    }
    public static async ValueTask<Frame?> ReadAsync(Stream stream, CancellationToken token = default)
    {
        byte[] header = new byte[HeaderLength];
        if (await stream.ReadAsync(header.AsMemory(0, 1), token) == 0) return null;
        await ReadExactAsync(stream, header.AsMemory(1), token);
        if (!header.AsSpan(0, 4).SequenceEqual("BBRG"u8) || header[4] != 1 || header[6] != 0 || header[7] != 0 || !Enum.IsDefined((FrameType)header[5]))
            throw new ProtocolException("蓝桥协议头无效或版本不兼容");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24, 4));
        if (length > MaxPayload) throw new ProtocolException("消息超过允许大小");
        byte[] payload = new byte[(int)length];
        await ReadExactAsync(stream, payload, token);
        return new Frame((FrameType)header[5], new Guid(header.AsSpan(8, 16), true), payload);
    }
    private static async Task ReadExactAsync(Stream stream, Memory<byte> bytes, CancellationToken token)
    {
        while (bytes.Length != 0)
        {
            int count = await stream.ReadAsync(bytes, token);
            if (count == 0) throw new ProtocolException("蓝牙连接在消息中途断开");
            bytes = bytes[count..];
        }
    }
}
public static class WireJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static T Decode<T>(Frame frame)
    {
        if (frame.Payload.Length > 16_384) throw new ProtocolException("控制消息过大");
        try { return JsonSerializer.Deserialize<T>(frame.Payload.Span, Options) ?? throw new ProtocolException("空控制消息"); }
        catch (JsonException) { throw new ProtocolException("控制消息格式无效"); }
    }
    public static string Text(Frame frame)
    {
        try { return Utf8.GetString(frame.Payload.Span); }
        catch (DecoderFallbackException) { throw new ProtocolException("文字不是有效 UTF-8"); }
    }
}
