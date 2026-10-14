using BlueBridge.Core;
using Xunit;
namespace BlueBridge.Core.Tests;
public class HandshakeTests
{
    [Fact]
    public async Task SilentPeerTimeoutReleasesControlledStream()
    {
        var (server, peer) = TestStream.Pair();
        string directory = Path.Combine(Path.GetTempPath(), "BlueBridgeTests", Guid.NewGuid().ToString("N"));
        await using var session = new BridgeSession(server, new(Guid.NewGuid(), "Windows"), directory, new() { ApprovePeer = _ => Task.FromResult(true) });
        Task run = session.RunAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => HandshakeDeadline.WaitAsync(session.HelloReceived, session.Ready, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)));
        await session.DisposeAsync(); await peer.DisposeAsync(); await SessionTests.IgnoreStopped(run);
    }
    [Fact]
    public async Task SeparateTrustTimeoutAndCancellationAreEnforced()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<TimeoutException>(() => HandshakeDeadline.WaitAsync(Task.CompletedTask, pending.Task, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10)));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HandshakeDeadline.WaitAsync(pending.Task, pending.Task, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), cancel.Token));
        await HandshakeDeadline.WaitAsync(Task.CompletedTask, Task.CompletedTask, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
    [Fact]
    public async Task PendingApprovalCannotKeepCanceledSessionAlive()
    {
        var (server, peer) = TestStream.Pair();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new BridgeSession(server, new(Guid.NewGuid(), "Windows"), Path.GetTempPath(), new() { ApprovePeer = _ => pending.Task });
        Task run = session.RunAsync();
        await SessionTests.WriteJson(peer, FrameType.Hello, Guid.NewGuid(), new PeerInfo(Guid.NewGuid(), "Android"));
        await session.HelloReceived.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(session.Ready.IsCompleted);
        await session.DisposeAsync(); await peer.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(pending.Task.IsCompleted);
    }
}
