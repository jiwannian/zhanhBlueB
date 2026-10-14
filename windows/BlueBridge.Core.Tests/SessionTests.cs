using System.Security.Cryptography;
using System.Text;
using BlueBridge.Core;
using Xunit;

namespace BlueBridge.Core.Tests;
public sealed class SessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BlueBridgeTests", Guid.NewGuid().ToString("N"));
    private static PeerInfo Peer => new(Guid.NewGuid(), "测试设备");
    [Fact]
    public async Task TrustedPeersExchangeUnicodeAndLargeFilesBothDirections()
    {
        var (sa, sb) = TestStream.Pair();
        string? receivedA = null, receivedB = null;
        await using var a = new BridgeSession(sa, Peer, Path.Combine(directory, "a"), new() { ApprovePeer = _ => Task.FromResult(true), ReceiveText = (_, text) => { receivedA = text; return Task.CompletedTask; } });
        await using var b = new BridgeSession(sb, Peer, Path.Combine(directory, "b"), new() { ApprovePeer = _ => Task.FromResult(true), ReceiveText = (_, text) => { receivedB = text; return Task.CompletedTask; } });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task ar = a.RunAsync(stop.Token), br = b.RunAsync(stop.Token);
        await Task.WhenAll(a.Ready, b.Ready).WaitAsync(stop.Token);
        await Task.WhenAll(a.SendTextAsync("你好🌉\n第二行"), b.SendTextAsync("Windows→安卓"));
        byte[] payload = new byte[180_000]; Random.Shared.NextBytes(payload);
        await Task.WhenAll(a.SendStreamAsync(new MemoryStream(payload), new("same.bin", payload.Length, "file", "application/octet-stream")), b.SendStreamAsync(new MemoryStream(), new("empty.bin", 0, "file", "application/octet-stream")));
        Assert.Equal("Windows→安卓", receivedA); Assert.Equal("你好🌉\n第二行", receivedB);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(directory, "b", "same.bin")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(directory, "a", "empty.bin")));
        await a.DisposeAsync(); await b.DisposeAsync();
        await IgnoreStopped(ar); await IgnoreStopped(br);
    }
    [Fact]
    public async Task RefusedOrPretrustTextCannotReachClipboard()
    {
        var (server, peer) = TestStream.Pair(); bool received = false;
        await using var session = new BridgeSession(server, Peer, directory, new() { ApprovePeer = _ => Task.FromResult(true), ReceiveText = (_, _) => { received = true; return Task.CompletedTask; } });
        var run = session.RunAsync();
        await Write(peer, FrameType.Text, Guid.NewGuid(), Encoding.UTF8.GetBytes("禁止"));
        await Assert.ThrowsAsync<ProtocolException>(() => run);
        Assert.False(received); await peer.DisposeAsync();
    }
    [Fact]
    public async Task MalformedTransfersRecoverAndLateCanceledChunksAreIgnored()
    {
        var (server, peer) = TestStream.Pair();
        await using var session = new BridgeSession(server, Peer, directory, new() { ApprovePeer = _ => Task.FromResult(true) });
        var run = session.RunAsync(); await Handshake(peer, session);
        Guid canceled = Guid.NewGuid();
        await WriteJson(peer, FrameType.Begin, canceled, new TransferMetadata("partial.bin", 10, "file", "application/octet-stream"));
        Assert.Equal(FrameType.Ack, (await FrameCodec.ReadAsync(peer))!.Type);
        await Write(peer, FrameType.Cancel, canceled, []);
        await Write(peer, FrameType.Chunk, canceled, [1, 2]);
        Guid bad = Guid.NewGuid();
        await WriteJson(peer, FrameType.Begin, bad, new TransferMetadata("../escape", 0, "file", "application/octet-stream"));
        Assert.Equal(FrameType.Error, (await FrameCodec.ReadAsync(peer))!.Type);
        Guid good = Guid.NewGuid();
        await WriteJson(peer, FrameType.Begin, good, new TransferMetadata("ok.bin", 0, "file", "application/octet-stream"));
        Assert.Equal(FrameType.Ack, (await FrameCodec.ReadAsync(peer))!.Type);
        await WriteJson(peer, FrameType.End, good, new EndMessage(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant()));
        Assert.Equal(FrameType.Ack, (await FrameCodec.ReadAsync(peer))!.Type);
        await WriteJson(peer, FrameType.Begin, good, new TransferMetadata("ok.bin", 0, "file", "application/octet-stream"));
        Assert.Equal(FrameType.Error, (await FrameCodec.ReadAsync(peer))!.Type);
        Guid wrong = Guid.NewGuid();
        await Write(peer, FrameType.Chunk, wrong, [1]);
        Assert.Equal(FrameType.Error, (await FrameCodec.ReadAsync(peer))!.Type);
        Assert.Single(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
        await peer.DisposeAsync(); await run;
    }
    [Fact]
    public async Task UnexpectedAckDoesNotCompleteSendAndDisconnectFailsOutstanding()
    {
        var (server, peer) = TestStream.Pair();
        await using var session = new BridgeSession(server, Peer, directory, new() { ApprovePeer = _ => Task.FromResult(true) });
        var run = session.RunAsync(); await Handshake(peer, session);
        var send = session.SendStreamAsync(new MemoryStream([1]), new("file.bin", 1, "file", "application/octet-stream"));
        var begin = await FrameCodec.ReadAsync(peer);
        await WriteJson(peer, FrameType.Ack, Guid.NewGuid(), new AckMessage("begin"));
        Assert.Equal(FrameType.Error, (await FrameCodec.ReadAsync(peer))!.Type);
        Assert.False(send.IsCompleted);
        await peer.DisposeAsync(); await run;
        await Assert.ThrowsAnyAsync<Exception>(() => send);
    }
    internal static async Task Handshake(Stream peer, BridgeSession session)
    {
        Assert.Equal(FrameType.Hello, (await FrameCodec.ReadAsync(peer))!.Type);
        await WriteJson(peer, FrameType.Hello, Guid.NewGuid(), Peer);
        Assert.Equal(FrameType.Trust, (await FrameCodec.ReadAsync(peer))!.Type);
        await WriteJson(peer, FrameType.Trust, Guid.NewGuid(), new TrustMessage(true));
        await session.Ready.WaitAsync(TimeSpan.FromSeconds(3));
    }
    internal static Task WriteJson<T>(Stream stream, FrameType type, Guid id, T value) => Write(stream, type, id, WireJson.Encode(value));
    internal static async Task Write(Stream stream, FrameType type, Guid id, byte[] bytes) { await stream.WriteAsync(FrameCodec.Encode(new(type, id, bytes))); }
    internal static async Task IgnoreStopped(Task task) { try { await task; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException) { } }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
