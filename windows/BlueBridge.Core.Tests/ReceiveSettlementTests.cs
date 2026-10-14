using System.Security.Cryptography;
using BlueBridge.Core;
using Xunit;
using static BlueBridge.Core.Tests.SessionTests;

namespace BlueBridge.Core.Tests;

public sealed class ReceiveSettlementTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BlueBridgeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Completed_receive_stays_pending_until_delivery_and_cleanup_finish()
    {
        var (server, peer) = TestStream.Pair();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new BridgeSession(server, new(Guid.NewGuid(), "测试"), directory, new() {
            ApprovePeer = _ => Task.FromResult(true),
            ReceiveItem = async _ => { delivered.SetResult(); await allowReturn.Task; },
            ReceiveSettled = id => settled.SetResult(id)
        });
        Task run = session.RunAsync(); await Handshake(peer, session);
        Guid id = Guid.NewGuid();
        await WriteJson(peer, FrameType.Begin, id, new TransferMetadata("empty.txt", 0, "file", "text/plain"));
        await FrameCodec.ReadAsync(peer);
        await WriteJson(peer, FrameType.End, id, new EndMessage(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant()));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(settled.Task.IsCompleted);
        allowReturn.SetResult();
        Assert.Equal(id, await settled.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(FrameType.Ack, (await FrameCodec.ReadAsync(peer))!.Type);
        using (File.Open(Path.Combine(directory, "empty.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        await peer.DisposeAsync(); await run;
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("hash")]
    [InlineData("disconnect")]
    public async Task Failed_or_cancelled_receive_settles_only_after_partial_cleanup(string ending)
    {
        var (server, peer) = TestStream.Pair();
        var settled = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new BridgeSession(server, new(Guid.NewGuid(), "测试"), directory, new() {
            ApprovePeer = _ => Task.FromResult(true),
            ReceiveSettled = id => { Assert.Empty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories)); settled.SetResult(id); }
        });
        Task run = session.RunAsync(); await Handshake(peer, session);
        Guid id = Guid.NewGuid();
        await WriteJson(peer, FrameType.Begin, id, new TransferMetadata("partial.txt", 0, "file", "text/plain"));
        await FrameCodec.ReadAsync(peer);
        Assert.False(settled.Task.IsCompleted);
        if (ending == "cancel") await Write(peer, FrameType.Cancel, id, []);
        else if (ending == "hash") await WriteJson(peer, FrameType.End, id, new EndMessage(new string('0', 64)));
        else await peer.DisposeAsync();
        Assert.Equal(id, await settled.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        if (ending == "hash") Assert.Equal(FrameType.Error, (await FrameCodec.ReadAsync(peer))!.Type);
        await peer.DisposeAsync(); await run;
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
