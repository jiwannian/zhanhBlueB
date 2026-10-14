using System.Security.Cryptography;
using System.Text;

namespace BlueBridge.Core;
public sealed record PeerInfo(Guid DeviceId, string DeviceName);
public sealed record TrustMessage(bool Accepted);
public sealed record AckMessage(string Stage);
public sealed record EndMessage(string Sha256);
public sealed class SessionCallbacks
{
    public Func<PeerInfo, Task<bool>> ApprovePeer { get; init; } = _ => Task.FromResult(false);
    public Func<PeerInfo, CancellationToken, Task<bool>>? ApprovePeerCancelable { get; init; }
    public Func<Guid, string, Task> ReceiveText { get; init; } = (_, _) => Task.CompletedTask;
    public Func<ReceivedItem, Task> ReceiveItem { get; init; } = _ => Task.CompletedTask;
    public Action<TransferProgress> Progress { get; init; } = _ => { };
    public Action<string> Status { get; init; } = _ => { };
    public Action<Guid, string> TransferFailed { get; init; } = (_, _) => { };
    public Action<Guid> ReceiveSettled { get; init; } = _ => { };
}
public sealed class BridgeSession : IAsyncDisposable
{
    private readonly Stream input, output;
    private readonly PeerInfo local;
    private readonly string receiveDirectory;
    private readonly SessionCallbacks callbacks;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeGate = new(1), sendGate = new(1), incomingGate = new(1);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<Guid> completed = [], canceled = [], textIds = [];
    private readonly Queue<Guid> completedOrder = [], canceledOrder = [], textOrder = [];
    private IncomingTransfer? incoming;
    private volatile Outgoing? outgoing;
    private volatile bool localTrust, remoteTrust;
    private bool helloSeen, trustSeen, runStarted;
    public PeerInfo? Peer { get; private set; }
    public Task Ready => ready.Task;
    public Task HelloReceived => hello.Task;
    public bool IsReady => localTrust && remoteTrust && !lifetime.IsCancellationRequested;
    public BridgeSession(Stream duplex, PeerInfo local, string directory, SessionCallbacks callbacks) : this(duplex, duplex, local, directory, callbacks) { }
    public BridgeSession(Stream input, Stream output, PeerInfo local, string directory, SessionCallbacks callbacks)
    { this.input = input; this.output = output; this.local = local; receiveDirectory = directory; this.callbacks = callbacks; }

    public async Task RunAsync(CancellationToken token = default)
    {
        if (runStarted) throw new InvalidOperationException("会话只能启动一次");
        runStarted = true;
        using var registration = token.Register(() => { lifetime.Cancel(); input.Dispose(); });
        try
        {
            await SendControlAsync(FrameType.Hello, Guid.NewGuid(), local);
            while (!lifetime.IsCancellationRequested)
            {
                Frame? frame = await FrameCodec.ReadAsync(input, lifetime.Token);
                if (frame is null) break;
                await HandleAsync(frame);
            }
        }
        finally
        {
            lifetime.Cancel(); hello.TrySetException(new IOException("蓝牙连接已断开")); ready.TrySetException(new IOException("蓝牙连接已断开"));
            outgoing?.Fail(new IOException("蓝牙连接已断开，请从头重试"));
            await ClearIncomingAsync(); callbacks.Status("连接已断开");
        }
    }

    private async Task HandleAsync(Frame frame)
    {
        if (frame.Type is not (FrameType.Text or FrameType.Chunk) && frame.Payload.Length > 16_384) throw new ProtocolException("控制消息过大");
        if (!IsReady && frame.Type is not (FrameType.Hello or FrameType.Trust or FrameType.Ping or FrameType.Pong or FrameType.Cancel or FrameType.Error))
            throw new ProtocolException("设备尚未完成双方信任确认");
        switch (frame.Type)
        {
            case FrameType.Hello: await HandleHelloAsync(frame); break;
            case FrameType.Trust: HandleTrust(frame); break;
            case FrameType.Ping:
                if (!frame.Payload.IsEmpty) throw new ProtocolException("PING 不允许携带内容");
                await SendFrameAsync(new(FrameType.Pong, frame.Id, [])); break;
            case FrameType.Pong:
                if (!frame.Payload.IsEmpty) throw new ProtocolException("PONG 不允许携带内容"); break;
            case FrameType.Text:
                if (Remember(textIds, textOrder, frame.Id)) await callbacks.ReceiveText(frame.Id, WireJson.Text(frame)); break;
            case FrameType.Ack: await HandleAckAsync(frame); break;
            case FrameType.Cancel: await HandleCancelAsync(frame.Id, "传输已取消"); break;
            case FrameType.Error: await HandleCancelAsync(frame.Id, LimitReason(WireJson.Text(frame))); break;
            default: await HandleTransferAsync(frame); break;
        }
    }
    private async Task HandleHelloAsync(Frame frame)
    {
        if (helloSeen) throw new ProtocolException("重复的 HELLO");
        helloSeen = true; Peer = WireJson.Decode<PeerInfo>(frame);
        if (Peer.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(Peer.DeviceName) || Peer.DeviceName.Length > 200 || Peer.DeviceName.Any(char.IsControl))
            throw new ProtocolException("设备身份无效");
        hello.TrySetResult();
        Task<bool> approval = callbacks.ApprovePeerCancelable?.Invoke(Peer, lifetime.Token) ?? callbacks.ApprovePeer(Peer);
        bool approved = await approval.WaitAsync(lifetime.Token);
        localTrust = approved;
        await SendControlAsync(FrameType.Trust, frame.Id, new TrustMessage(approved));
        if (!approved) throw new ProtocolException("已拒绝该设备");
        SetReady();
    }
    private void HandleTrust(Frame frame)
    {
        if (!helloSeen || trustSeen) throw new ProtocolException("未经 HELLO 或重复的 TRUST");
        trustSeen = true; remoteTrust = WireJson.Decode<TrustMessage>(frame).Accepted;
        if (!remoteTrust) throw new ProtocolException("对方未接受信任请求");
        SetReady();
    }
    private void SetReady() { if (IsReady && ready.TrySetResult()) callbacks.Status($"已连接：{Peer!.DeviceName}"); }
    private async Task HandleAckAsync(Frame frame)
    {
        var ack = WireJson.Decode<AckMessage>(frame);
        var current = outgoing;
        if (current is null || current.Id != frame.Id || !current.AcceptAck(ack.Stage))
            await SendErrorAsync(frame.Id, "收到无效的传输确认");
    }
    private async Task HandleTransferAsync(Frame frame)
    {
        await incomingGate.WaitAsync(lifetime.Token);
        var previous = incoming;
        bool settled = false;
        try
        {
            if (canceled.Contains(frame.Id)) return;
            switch (frame.Type)
            {
                case FrameType.Begin:
                    if (incoming is not null || completed.Contains(frame.Id)) throw new ProtocolException("已有接收任务或消息编号重复");
                    incoming = new(frame.Id, WireJson.Decode<TransferMetadata>(frame), receiveDirectory);
                    callbacks.Progress(new(frame.Id, incoming.Metadata.Name, 0, incoming.Metadata.Length, true));
                    await SendControlAsync(FrameType.Ack, frame.Id, new AckMessage("begin")); break;
                case FrameType.Chunk:
                    RequireIncoming(frame.Id);
                    await incoming!.WriteAsync(frame.Payload, lifetime.Token);
                    callbacks.Progress(new(frame.Id, incoming.Metadata.Name, incoming.Bytes, incoming.Metadata.Length, true)); break;
                case FrameType.End:
                    RequireIncoming(frame.Id);
                    var item = await incoming!.CompleteAsync(WireJson.Decode<EndMessage>(frame).Sha256, lifetime.Token);
                    try { await callbacks.ReceiveItem(item); }
                    catch (Exception) when (item.Metadata.Kind == "image") { File.Delete(item.Path); throw new ProtocolException("图片解码或剪贴板写入失败"); }
                    await incoming.DisposeAsync(); incoming = null;
                    settled = true;
                    Remember(completed, completedOrder, frame.Id);
                    await SendControlAsync(FrameType.Ack, frame.Id, new AckMessage("complete")); break;
                default: throw new ProtocolException("意外的传输消息");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            bool preserveActive = frame.Type == FrameType.Begin && previous is not null && ReferenceEquals(incoming, previous);
            if (!preserveActive)
            {
                if (incoming?.Id == frame.Id) { await incoming.DisposeAsync(); incoming = null; settled = true; }
                Remember(canceled, canceledOrder, frame.Id);
            }
            string reason = error is ProtocolException ? error.Message : "无法接收文件，请检查磁盘空间和保存目录权限";
            callbacks.TransferFailed(frame.Id, reason); await SendErrorAsync(frame.Id, reason);
        }
        finally { incomingGate.Release(); if (settled) callbacks.ReceiveSettled(frame.Id); }
    }
    private void RequireIncoming(Guid id) { if (incoming is null || incoming.Id != id) throw new ProtocolException("文件块或结束消息没有匹配的 BEGIN"); }
    private async Task HandleCancelAsync(Guid id, string reason)
    {
        var current = outgoing;
        if (current?.Id == id) current.Fail(new IOException(reason));
        await incomingGate.WaitAsync();
        bool settled = false;
        try { Remember(canceled, canceledOrder, id); if (incoming?.Id == id) { await incoming.DisposeAsync(); incoming = null; settled = true; } }
        finally { incomingGate.Release(); }
        callbacks.TransferFailed(id, reason);
        if (settled) callbacks.ReceiveSettled(id);
    }
    public async Task CancelAsync(Guid id)
    {
        await HandleCancelAsync(id, "传输已取消");
        if (!lifetime.IsCancellationRequested) await SendFrameAsync(new(FrameType.Cancel, id, []));
    }
    public Task SendTextAsync(string text)
    {
        RequireReady(); byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > FrameCodec.MaxPayload) throw new ProtocolException("文字超过 1 MiB");
        return SendFrameAsync(new(FrameType.Text, Guid.NewGuid(), bytes));
    }
    public async Task SendFileAsync(string path, bool image = false, Guid? messageId = null, CancellationToken token = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await SendStreamAsync(file, new(Path.GetFileName(path), file.Length, image ? "image" : "file", image ? "image/png" : "application/octet-stream"), messageId, token);
    }
    public async Task SendStreamAsync(Stream source, TransferMetadata metadata, Guid? messageId = null, CancellationToken token = default)
    {
        RequireReady(); TransferMetadata.Validate(metadata);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        await sendGate.WaitAsync(linked.Token);
        var context = new Outgoing(messageId ?? Guid.NewGuid(), linked);
        outgoing = context;
        using var registration = token.Register(() => context.Fail(new OperationCanceledException("传输已取消", token)));
        try { await SendContentAsync(source, metadata, context); }
        catch
        {
            if (!lifetime.IsCancellationRequested) await SendFrameAsync(new(FrameType.Cancel, context.Id, []));
            throw;
        }
        finally { context.Finish(); outgoing = null; sendGate.Release(); }
    }
    private async Task SendContentAsync(Stream source, TransferMetadata metadata, Outgoing context)
    {
        await SendControlAsync(FrameType.Begin, context.Id, metadata);
        await context.Begin.Task.WaitAsync(TimeSpan.FromSeconds(30), context.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] bytes = new byte[65536]; long sent = 0;
        while (true)
        {
            int count = await source.ReadAsync(bytes, context.Token);
            if (count == 0) break;
            if (sent + count > metadata.Length) throw new ProtocolException("源文件在传输期间发生变化");
            hash.AppendData(bytes, 0, count);
            await SendFrameAsync(new(FrameType.Chunk, context.Id, bytes.AsSpan(0, count)));
            sent += count; callbacks.Progress(new(context.Id, metadata.Name, sent, metadata.Length, false));
        }
        if (sent != metadata.Length) throw new ProtocolException("源文件在传输期间发生变化");
        context.ExpectComplete = true;
        await SendControlAsync(FrameType.End, context.Id, new EndMessage(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
        await context.Complete.Task.WaitAsync(TimeSpan.FromSeconds(30), context.Token);
        callbacks.Progress(new(context.Id, metadata.Name, metadata.Length, metadata.Length, false));
    }
    private void RequireReady() { if (!IsReady) throw new IOException("请先连接并在双方确认信任"); }
    private Task SendControlAsync<T>(FrameType type, Guid id, T value) => SendFrameAsync(new(type, id, WireJson.Encode(value)));
    private Task SendErrorAsync(Guid id, string reason) => SendFrameAsync(new(FrameType.Error, id, Encoding.UTF8.GetBytes(reason)));
    private async Task SendFrameAsync(Frame frame)
    {
        await writeGate.WaitAsync(lifetime.Token);
        try { await output.WriteAsync(FrameCodec.Encode(frame), lifetime.Token); await output.FlushAsync(lifetime.Token); }
        finally { writeGate.Release(); }
    }
    private static bool Remember(HashSet<Guid> set, Queue<Guid> order, Guid id)
    {
        if (!set.Add(id)) return false;
        order.Enqueue(id); if (order.Count > 1024) set.Remove(order.Dequeue()); return true;
    }
    private static string LimitReason(string reason) => reason.Length is > 0 and <= 200 ? reason : "对方报告传输失败";
    private async Task ClearIncomingAsync()
    {
        await incomingGate.WaitAsync();
        Guid? settled = null;
        try { if (incoming is not null) { Guid id = incoming.Id; await incoming.DisposeAsync(); incoming = null; settled = id; } }
        finally { incomingGate.Release(); if (settled is { } id) callbacks.ReceiveSettled(id); }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); input.Dispose(); if (!ReferenceEquals(input, output)) output.Dispose();
        outgoing?.Fail(new IOException("会话已关闭")); await ClearIncomingAsync();
    }
    private sealed class Outgoing(Guid id, CancellationTokenSource cancellation)
    {
        private readonly object gate = new();
        private bool ended;
        public Guid Id => id;
        public CancellationToken Token => cancellation.Token;
        public TaskCompletionSource Begin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool ExpectComplete;
        public bool AcceptAck(string stage) => stage switch { "begin" when !ExpectComplete => Begin.TrySetResult(), "complete" when ExpectComplete => Complete.TrySetResult(), _ => false };
        public void Finish() { lock (gate) ended = true; }
        public void Fail(Exception error)
        {
            lock (gate)
            {
                if (ended) return;
                ended = true; Begin.TrySetException(error); Complete.TrySetException(error); cancellation.Cancel();
            }
        }
    }
}
