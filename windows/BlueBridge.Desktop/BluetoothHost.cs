using BlueBridge.Core;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace BlueBridge.Desktop;
public sealed class BluetoothHost : IAsyncDisposable
{
    public static readonly Guid Service = new("dc0b3714-a8e6-4c25-a219-775346d2ef91");
    private readonly PeerInfo local;
    private readonly string directory;
    private readonly Func<string, SessionCallbacks> callbacks;
    private readonly Action<BridgeSession?> changed;
    private readonly Action<string> status;
    private readonly object gate = new();
    private BluetoothListener? listener;
    private CancellationTokenSource? stop;
    private BluetoothClient? client;
    private BridgeSession? session;
    private Task? accept;
    private Task? connectionTask;
    public BridgeSession? Session { get { lock (gate) return session; } }
    public BluetoothHost(PeerInfo local, string directory, Func<string, SessionCallbacks> callbacks, Action<BridgeSession?> changed, Action<string> status)
    { this.local = local; this.directory = directory; this.callbacks = callbacks; this.changed = changed; this.status = status; }
    public void Start()
    {
        if (stop is not null) return;
        listener = CreateSecureListener();
        try { listener.Start(1); Verify(listener); }
        catch { listener.Stop(); listener = null; throw; }
        stop = new(); accept = AcceptLoopAsync(listener, stop.Token);
        status("等待手机连接 · 请先在系统蓝牙设置中配对");
    }
    private async Task AcceptLoopAsync(BluetoothListener active, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            BluetoothClient accepted;
            try { accepted = await Task.Factory.FromAsync(active.BeginAcceptBluetoothClient, active.EndAcceptBluetoothClient, null); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException or InvalidOperationException)
            { if (!token.IsCancellationRequested) status("蓝牙监听中断，请停止后重新开启：" + e.Message); return; }
            try
            {
                Verify(active);
                var remote = accepted.RemoteEndPoint.Address;
                if (!new BluetoothDeviceInfo(remote).Authenticated) throw new IOException("仅接受已在系统中配对的设备");
                // 32feet 3.5 accepted-client flags are fresh caches, not link status.
                // Native listener options enforce authentication/encryption before accept succeeds.
                lock (gate)
                {
                    if (session is not null) { accepted.Close(); continue; }
                    client = accepted;
                    session = new(accepted.GetStream(), local, directory, callbacks(remote.ToString()));
                    changed(session);
                    connectionTask = RunConnectionAsync(session, accepted, token);
                }
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            { accepted.Close(); status("连接已拒绝：" + e.Message); }
        }
    }
    private async Task RunConnectionAsync(BridgeSession connection, BluetoothClient accepted, CancellationToken token)
    {
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task watchdog = MonitorHandshakeAsync(connection, accepted, monitorStop.Token);
        try { await connection.RunAsync(token); }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        { if (!token.IsCancellationRequested) status("连接已断开：" + e.Message); }
        finally
        {
            monitorStop.Cancel(); await watchdog;
            await connection.DisposeAsync(); accepted.Close();
            lock (gate) { if (session == connection) { session = null; client = null; } }
            changed(null);
            if (!token.IsCancellationRequested) status("等待手机重新连接");
        }
    }
    private async Task MonitorHandshakeAsync(BridgeSession connection, BluetoothClient accepted, CancellationToken token)
    {
        try { await HandshakeDeadline.WaitAsync(connection.HelloReceived, connection.Ready, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), token); }
        catch (TimeoutException) { status("连接确认超时，已释放连接，请在手机上重连"); accepted.Close(); await connection.DisposeAsync(); }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private static BluetoothListener CreateSecureListener() => new(Service) { ServiceName = "zhanhBlueB", Authenticate = true, Encrypt = true };
    private static void Verify(BluetoothListener listener)
    { if (!listener.Authenticate || !listener.Encrypt) throw new IOException("蓝牙监听未配置认证和加密，已拒绝连接"); }
    public static void Smoke()
    {
        var listener = CreateSecureListener();
        try { listener.Start(1); Verify(listener); }
        finally { listener.Stop(); }
    }
    public async ValueTask DisposeAsync()
    {
        var cancel = stop; stop = null;
        cancel?.Cancel(); client?.Close(); listener?.Stop(); listener = null;
        if (Session is { } active) await active.DisposeAsync();
        if (accept is not null) await accept;
        if (connectionTask is not null) await connectionTask;
        cancel?.Dispose(); accept = null; connectionTask = null;
    }
}
