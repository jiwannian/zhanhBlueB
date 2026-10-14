using System.Collections.Concurrent;
using BlueBridge.Core;
using BlueBridge.Desktop;
using InTheHand.Net;
using InTheHand.Net.Sockets;
using Xunit;

namespace BlueBridge.Bluetooth.Tests;
public sealed class BluetoothAcceptTests
{
    [Fact]
    public async Task SecureListenerAcceptsBondedClientWithDefaultFalseWrapperCaches()
    {
        var connected = new TaskCompletionSource<BridgeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new ConcurrentQueue<string>();
        await using var host = MakeHost(connected, statuses);
        host.Start();
        var listener = BluetoothListener.Last!;
        Assert.True(listener.AuthenticationRequiredAtStart);
        Assert.True(listener.EncryptionRequiredAtStart);
        var accepted = new BluetoothClient(new BluetoothAddress("AABBCCDDEEFF", true));
        Assert.False(accepted.Authenticate);
        Assert.False(accepted.Encrypt);
        accepted.ResetCachedPropertyReads();
        listener.Offer(accepted);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await accepted.MessageWritten.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(accepted.StreamOpened);
        Assert.Equal(0, accepted.CachedPropertyReads);
        var frame = await FrameCodec.ReadAsync(new MemoryStream(accepted.WrittenBytes));
        Assert.Equal(FrameType.Hello, frame!.Type);
    }

    [Fact]
    public async Task UnpairedClientIsRejectedBeforeSessionOrStream()
    {
        var connected = new TaskCompletionSource<BridgeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new ConcurrentQueue<string>();
        await using var host = MakeHost(connected, statuses);
        host.Start();
        var accepted = new BluetoothClient(new BluetoothAddress("AABBCCDDEEFF", false));
        BluetoothListener.Last!.Offer(accepted);
        await accepted.Closed.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(accepted.StreamOpened);
        Assert.False(connected.Task.IsCompleted);
        Assert.Contains(statuses, value => value.Contains("已在系统中配对"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ListenerPolicyMustRemainSecureBeforeAccepting(bool authenticate, bool encrypt)
    {
        var connected = new TaskCompletionSource<BridgeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = MakeHost(connected, new());
        host.Start();
        var listener = BluetoothListener.Last!;
        listener.Authenticate = authenticate;
        listener.Encrypt = encrypt;
        var accepted = new BluetoothClient(new BluetoothAddress("AABBCCDDEEFF", true));
        listener.Offer(accepted);
        await accepted.Closed.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(accepted.StreamOpened);
        Assert.False(connected.Task.IsCompleted);
        Assert.Equal(0, accepted.CachedPropertyReads);
    }

    [Fact]
    public async Task SecondClientIsClosedWhileFirstSessionIsActive()
    {
        var connected = new TaskCompletionSource<BridgeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = MakeHost(connected, new()); host.Start();
        var first = new BluetoothClient(new BluetoothAddress("AABBCCDDEEFF", true));
        BluetoothListener.Last!.Offer(first);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = new BluetoothClient(new BluetoothAddress("112233445566", true));
        BluetoothListener.Last.Offer(second);
        await second.Closed.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(first.StreamOpened); Assert.False(second.StreamOpened);
        Assert.NotNull(host.Session);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NativeOptionSetterFailureNeverStartsListener(bool failAuthentication, bool failEncryption)
    {
        BluetoothListener.FailAuthenticationSetter = failAuthentication;
        BluetoothListener.FailEncryptionSetter = failEncryption;
        try
        {
            await using var host = MakeHost(new(TaskCreationOptions.RunContinuationsAsynchronously), new());
            Assert.Throws<System.Net.Sockets.SocketException>(() => host.Start());
            Assert.False(BluetoothListener.Last!.Started);
            Assert.Null(host.Session);
        }
        finally { BluetoothListener.FailAuthenticationSetter = false; BluetoothListener.FailEncryptionSetter = false; }
    }

    private static BluetoothHost MakeHost(TaskCompletionSource<BridgeSession> connected, ConcurrentQueue<string> statuses) => new(
        new PeerInfo(Guid.NewGuid(), "Windows test peer"), Path.GetTempPath(), _ => new SessionCallbacks(),
        session => { if (session is not null) connected.TrySetResult(session); }, statuses.Enqueue);
}
