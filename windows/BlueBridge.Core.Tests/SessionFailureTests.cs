using System.Security.Cryptography;
using System.Text;
using BlueBridge.Core;
using Xunit;
using static BlueBridge.Core.Tests.SessionTests;

namespace BlueBridge.Core.Tests;
public sealed class SessionFailureTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BlueBridgeTests", Guid.NewGuid().ToString("N"));
    private static PeerInfo Peer => new(Guid.NewGuid(), "Peer");
    [Fact]
    public async Task DuplicateActiveBeginPreservesOriginalTransfer()
    {
        var (server, peer) = TestStream.Pair();
        var settled = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new BridgeSession(server, Peer, directory, new() { ApprovePeer = _ => Task.FromResult(true), ReceiveSettled = id => settled.SetResult(id) });
        Task run = session.RunAsync(); await Handshake(peer, session);
        Guid id = Guid.NewGuid();
        await WriteJson(peer, FrameType.Begin, id, new TransferMetadata("original.bin", 3, "file", "application/octet-stream"));
        Assert.Equal(FrameType.Ack, (await FrameCodec.ReadAsync(peer))!.Type);
        await WriteJson(peer, FrameType.Begin, id, new TransferMetadata("replacement.bin", 3, "file", "application/octet-stream"));
        Assert.Equal(FrameType.Error, (await FrameCodec.ReadAsync(peer))!.Type);
        Assert.False(settled.Task.IsCompleted);
        await Write(peer, FrameType.Chunk, id, [1, 2, 3]);
        await WriteJson(peer, FrameType.End, id, new EndMessage(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant()));
        Assert.Equal(FrameType.Ack, (await FrameCodec.ReadAsync(peer))!.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(directory, "original.bin")));
        Assert.False(File.Exists(Path.Combine(directory, "replacement.bin")));
        Assert.Equal(id, await settled.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        await peer.DisposeAsync(); await run;
    }
    [Theory]
    [InlineData(FrameType.Trust, "{\"accepted\":true}")]
    [InlineData(FrameType.Hello, "{\"deviceId\":\"bad\",\"deviceName\":\"x\"}")]
    [InlineData(FrameType.Hello, "{\"deviceId\":\"00000000-0000-0000-0000-000000000000\",\"deviceName\":\"x\"}")]
    [InlineData(FrameType.Ping, "x")]
    [InlineData(FrameType.Pong, "x")]
    public async Task InvalidHandshakeMessagesAreRejected(FrameType type, string json)
    {
        var (server, peer) = TestStream.Pair();
        await using var session = new BridgeSession(server, Peer, directory, new() { ApprovePeer = _ => Task.FromResult(true) });
        Task run = session.RunAsync();
        await Write(peer, type, Guid.NewGuid(), Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<ProtocolException>(() => run); await peer.DisposeAsync();
    }
    [Fact]
    public async Task LocalRejectionPublishesRefusalThenCloses()
    {
        var (server, peer) = TestStream.Pair();
        await using var session = new BridgeSession(server, Peer, directory, new() { ApprovePeer = _ => Task.FromResult(false) });
        Task run = session.RunAsync(); await FrameCodec.ReadAsync(peer);
        await WriteJson(peer, FrameType.Hello, Guid.NewGuid(), Peer);
        var trust = await FrameCodec.ReadAsync(peer);
        Assert.False(WireJson.Decode<TrustMessage>(trust!).Accepted);
        await Assert.ThrowsAsync<ProtocolException>(() => run); Assert.False(session.IsReady); await peer.DisposeAsync();
    }
    [Fact]
    public async Task PingEchoesIdAndDuplicateTextIsAppliedOnce()
    {
        var (server, peer) = TestStream.Pair(); int received = 0;
        await using var session = new BridgeSession(server, Peer, directory, new() { ApprovePeer = _ => Task.FromResult(true), ReceiveText = (_, _) => { received++; return Task.CompletedTask; } });
        Task run = session.RunAsync(); await Handshake(peer, session);
        Guid id = Guid.NewGuid();
        await Write(peer, FrameType.Text, id, Encoding.UTF8.GetBytes("one"));
        await Write(peer, FrameType.Text, id, Encoding.UTF8.GetBytes("one"));
        await Write(peer, FrameType.Ping, id, []);
        var pong = await FrameCodec.ReadAsync(peer); Assert.Equal(FrameType.Pong, pong!.Type); Assert.Equal(id, pong.Id);
        Assert.Equal(1, received); await peer.DisposeAsync(); await run;
    }
    [Fact]
    public async Task OversizedChunkAndBadHashCleanPartialAndAllowRecovery()
    {
        var (server, peer) = TestStream.Pair();
        await using var session = new BridgeSession(server, Peer, directory, new() { ApprovePeer = _ => Task.FromResult(true) });
        Task run = session.RunAsync(); await Handshake(peer, session);
        Guid id = Guid.NewGuid();
        await WriteJson(peer, FrameType.Begin, id, new TransferMetadata("large.bin", 65537, "file", "application/octet-stream")); await FrameCodec.ReadAsync(peer);
        await Write(peer, FrameType.Chunk, id, new byte[65537]); Assert.Equal(FrameType.Error, (await FrameCodec.ReadAsync(peer))!.Type);
        id = Guid.NewGuid();
        await WriteJson(peer, FrameType.Begin, id, new TransferMetadata("bad.bin", 0, "file", "application/octet-stream")); await FrameCodec.ReadAsync(peer);
        await WriteJson(peer, FrameType.End, id, new EndMessage(new string('0', 64))); Assert.Equal(FrameType.Error, (await FrameCodec.ReadAsync(peer))!.Type);
        Assert.Empty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
        await peer.DisposeAsync(); await run;
    }
    [Fact]
    public async Task ImageClipboardDecodeFailureIsReportedWithoutKeepingFile()
    {
        var (server, peer) = TestStream.Pair();
        await using var session = new BridgeSession(server, Peer, directory, new() { ApprovePeer = _ => Task.FromResult(true), ReceiveItem = _ => throw new FormatException("decode") });
        Task run = session.RunAsync(); await Handshake(peer, session);
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        Guid id = Guid.NewGuid();
        await WriteJson(peer, FrameType.Begin, id, new TransferMetadata("image.png", png.Length, "image", "image/png")); await FrameCodec.ReadAsync(peer);
        await Write(peer, FrameType.Chunk, id, png);
        await WriteJson(peer, FrameType.End, id, new EndMessage(Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant()));
        Assert.Equal(FrameType.Error, (await FrameCodec.ReadAsync(peer))!.Type);
        Assert.Empty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
        await peer.DisposeAsync(); await run;
    }
    [Fact]
    public async Task ManualCancelFailsOutgoingAndCanReceiveNextTransfer()
    {
        var (server, peer) = TestStream.Pair();
        await using var session = new BridgeSession(server, Peer, directory, new() { ApprovePeer = _ => Task.FromResult(true) });
        Task run = session.RunAsync(); await Handshake(peer, session);
        Guid id = Guid.NewGuid();
        Task send = session.SendStreamAsync(new MemoryStream([1]), new("a.bin", 1, "file", "application/octet-stream"), id);
        await FrameCodec.ReadAsync(peer); await session.CancelAsync(id);
        await Assert.ThrowsAnyAsync<Exception>(() => send);
        Assert.Equal(FrameType.Cancel, (await FrameCodec.ReadAsync(peer))!.Type);
        await peer.DisposeAsync(); await run;
    }
    [Fact]
    public void WireJsonRejectsOversizeAndInvalidUtf8()
    {
        Assert.Throws<ProtocolException>(() => WireJson.Decode<TrustMessage>(new(FrameType.Trust, Guid.NewGuid(), new byte[16385])));
        Assert.Throws<ProtocolException>(() => WireJson.Text(new(FrameType.Text, Guid.NewGuid(), [255])));
        Assert.Throws<ProtocolException>(() => WireJson.Decode<TrustMessage>(new(FrameType.Trust, Guid.NewGuid(), "null"u8)));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
