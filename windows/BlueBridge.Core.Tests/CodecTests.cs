using System.Text.Json;
using BlueBridge.Core;
using Xunit;

namespace BlueBridge.Core.Tests;
public class CodecTests
{
    [Fact]
    public async Task CrossLanguageGoldenFramesAreExact()
    {
        using var document = JsonDocument.Parse(File.ReadAllText("golden.json"));
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var frame = new Frame((FrameType)item.GetProperty("type").GetInt32(), Guid.Parse(item.GetProperty("id").GetString()!), Convert.FromHexString(item.GetProperty("payloadHex").GetString()!));
            var bytes = FrameCodec.Encode(frame);
            Assert.Equal(item.GetProperty("frameHex").GetString(), Convert.ToHexString(bytes).ToLowerInvariant());
            var actual = await FrameCodec.ReadAsync(new FragmentStream(bytes));
            Assert.Equal(frame.Id, actual!.Id);
            Assert.Equal(frame.Type, actual.Type);
            Assert.Equal(frame.Payload.ToArray(), actual.Payload.ToArray());
        }
    }

    [Fact]
    public async Task EndOfStreamDistinguishesCleanAndTruncated()
    {
        Assert.Null(await FrameCodec.ReadAsync(new MemoryStream()));
        await Assert.ThrowsAsync<ProtocolException>(() => FrameCodec.ReadAsync(new MemoryStream([66])).AsTask());
        var bytes = FrameCodec.Encode(new Frame(FrameType.Text, Guid.NewGuid(), [1, 2, 3]));
        await Assert.ThrowsAsync<ProtocolException>(() => FrameCodec.ReadAsync(new MemoryStream(bytes[..^1])).AsTask());
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(4, 2)] [InlineData(5, 255)] [InlineData(6, 1)] [InlineData(24, 127)]
    public async Task RejectsInvalidHeaders(int index, byte value)
    {
        var bytes = FrameCodec.Encode(new Frame(FrameType.Ping, Guid.NewGuid(), []));
        bytes[index] = value;
        await Assert.ThrowsAsync<ProtocolException>(() => FrameCodec.ReadAsync(new MemoryStream(bytes)).AsTask());
    }

    [Fact]
    public void FrameCopiesInputAndRejectsOversize()
    {
        byte[] original = [4, 5];
        var frame = new Frame(FrameType.Chunk, Guid.NewGuid(), original);
        original[0] = 6;
        Assert.Equal(4, frame.Payload.Span[0]);
        Assert.Throws<ProtocolException>(() => FrameCodec.Encode(new Frame(FrameType.Text, Guid.NewGuid(), new byte[FrameCodec.MaxPayload + 1])));
    }

    private sealed class FragmentStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
