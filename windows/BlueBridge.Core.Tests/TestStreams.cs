using System.Threading.Channels;
namespace BlueBridge.Core.Tests;
internal sealed class TestStream(Channel<byte[]> incoming, Channel<byte[]> outgoing, int fragment = 17) : Stream
{
    private byte[]? pending;
    private int position;
    public static (TestStream, TestStream) Pair()
    {
        var a = Channel.CreateUnbounded<byte[]>(); var b = Channel.CreateUnbounded<byte[]>();
        return (new(a, b), new(b, a));
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (pending is null || position == pending.Length)
        {
            if (!await incoming.Reader.WaitToReadAsync(cancellationToken)) return 0;
            pending = await incoming.Reader.ReadAsync(cancellationToken); position = 0;
        }
        int size = Math.Min(Math.Min(fragment, buffer.Length), pending.Length - position);
        pending.AsMemory(position, size).CopyTo(buffer); position += size; return size;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => outgoing.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    protected override void Dispose(bool disposing) { outgoing.Writer.TryComplete(); base.Dispose(disposing); }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
