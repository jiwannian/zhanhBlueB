using System.Threading.Channels;

// Test adapters for the 32feet 3.5 contracts, not a Bluetooth implementation.
// The production BluetoothHost source is compiled unmodified into this test assembly.
namespace InTheHand.Net
{
    public sealed record BluetoothAddress(string Value, bool IsPaired)
    { public override string ToString() => Value; }
    public sealed record BluetoothEndPoint(BluetoothAddress Address);
}
namespace InTheHand.Net.Bluetooth
{
    public sealed class BluetoothDeviceInfo(InTheHand.Net.BluetoothAddress address)
    { public bool Authenticated => address.IsPaired; }
}
namespace InTheHand.Net.Sockets
{
    public sealed class BluetoothListener
    {
        private readonly Channel<BluetoothClient> pending = Channel.CreateUnbounded<BluetoothClient>();
        private bool authenticate, encrypt;
        public static BluetoothListener? Last { get; private set; }
        public static bool FailAuthenticationSetter { get; set; }
        public static bool FailEncryptionSetter { get; set; }
        public BluetoothListener(Guid service) { Last = this; }
        public string? ServiceName { get; set; }
        public bool Started { get; private set; }
        public bool AuthenticationRequiredAtStart { get; private set; }
        public bool EncryptionRequiredAtStart { get; private set; }
        public bool Authenticate
        {
            get => authenticate;
            set { if (FailAuthenticationSetter) throw new System.Net.Sockets.SocketException(10022); authenticate = value; }
        }
        public bool Encrypt
        {
            get => encrypt;
            set { if (FailEncryptionSetter) throw new System.Net.Sockets.SocketException(10022); encrypt = value; }
        }
        public void Start(int backlog) { AuthenticationRequiredAtStart = Authenticate; EncryptionRequiredAtStart = Encrypt; Started = true; }
        public void Stop() { Started = false; pending.Writer.TryComplete(); }
        public void Offer(BluetoothClient client) => pending.Writer.TryWrite(client);
        public IAsyncResult BeginAcceptBluetoothClient(AsyncCallback callback, object? state)
        {
            var task = AcceptAsync();
            _ = task.ContinueWith(result => callback(result), TaskScheduler.Default);
            return task;
        }
        private async Task<BluetoothClient> AcceptAsync()
        {
            try { return await pending.Reader.ReadAsync(); }
            catch (ChannelClosedException) { throw new ObjectDisposedException(nameof(BluetoothListener)); }
        }
        public BluetoothClient EndAcceptBluetoothClient(IAsyncResult result) => ((Task<BluetoothClient>)result).GetAwaiter().GetResult();
    }
    public sealed class BluetoothClient(InTheHand.Net.BluetoothAddress address)
    {
        private readonly BlockingDuplexStream stream = new();
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InTheHand.Net.BluetoothEndPoint RemoteEndPoint => new(address);
        public int CachedPropertyReads { get; private set; }
        public bool Authenticate { get { CachedPropertyReads++; return false; } }
        public bool Encrypt { get { CachedPropertyReads++; return false; } }
        public bool StreamOpened { get; private set; }
        public byte[] WrittenBytes => stream.WrittenBytes;
        public Task MessageWritten => stream.MessageWritten;
        public Task Closed => closed.Task;
        public void ResetCachedPropertyReads() => CachedPropertyReads = 0;
        public Stream GetStream() { StreamOpened = true; return stream; }
        public void Close() { stream.Dispose(); closed.TrySetResult(); }
    }
    internal sealed class BlockingDuplexStream : Stream
    {
        private readonly MemoryStream output = new();
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource messageWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] WrittenBytes => output.ToArray();
        public Task MessageWritten => messageWritten.Task;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { await closed.Task.WaitAsync(token); return 0; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        { await output.WriteAsync(bytes, token); messageWritten.TrySetResult(); }
        protected override void Dispose(bool disposing) { closed.TrySetResult(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
