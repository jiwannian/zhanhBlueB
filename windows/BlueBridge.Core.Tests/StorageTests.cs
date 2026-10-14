using System.Security.Cryptography;
using BlueBridge.Core;
using Xunit;

namespace BlueBridge.Core.Tests;
public class StorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BlueBridgeTests", Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("../x")] [InlineData("CON.txt")] [InlineData("x/y")] [InlineData("x\\y")] [InlineData("..")] [InlineData("x:")] [InlineData("x. ")] [InlineData("")] [InlineData("x\u007f")]
    public void UnsafeNamesAreRejected(string name) => Assert.Throws<ProtocolException>(() => TransferMetadata.Validate(new(name, 0, "file", "application/octet-stream")));

    [Fact]
    public async Task EmptyAndDuplicateFilesCommitWithoutOverwrite()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "same.bin"), "old");
        await using var transfer = new IncomingTransfer(Guid.NewGuid(), new("same.bin", 0, "file", "application/octet-stream"), directory);
        var result = await transfer.CompleteAsync(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant());
        Assert.Equal("old", File.ReadAllText(Path.Combine(directory, "same.bin")));
        Assert.Equal("same (1).bin", Path.GetFileName(result.Path));
        Assert.Empty(File.ReadAllBytes(result.Path));
    }

    [Fact]
    public async Task WrongHashAndWrongLengthNeverCommit()
    {
        await using (var transfer = new IncomingTransfer(Guid.NewGuid(), new("data.bin", 3, "file", "application/octet-stream"), directory))
        {
            await transfer.WriteAsync(new byte[] { 1, 2, 3 });
            await Assert.ThrowsAsync<ProtocolException>(() => transfer.CompleteAsync(new string('0', 64)));
        }
        await using (var transfer = new IncomingTransfer(Guid.NewGuid(), new("data.bin", 1, "file", "application/octet-stream"), directory))
            await Assert.ThrowsAsync<ProtocolException>(() => transfer.WriteAsync(new byte[] { 1, 2 }).AsTask());
        Assert.Empty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void NativeClipboardSequenceHandlesPauseDuplicateNotificationsAndSameValueCopies()
    {
        var guard = new ClipboardSequenceGuard();
        guard.MarkApplied(100); // receiving PNG/文字后 SetClipboardData 的实际序号
        Assert.False(guard.ShouldProcess(100)); // 暂停期间收到通知，不读取内容
        Assert.False(guard.ShouldProcess(100)); // 同次复制的重复通知
        Assert.True(guard.ShouldProcess(101)); // 恢复后用户主动复制同值，新序号照常发送
        Assert.False(guard.ShouldProcess(101));
        Assert.True(guard.ShouldProcess(102));
        guard.MarkApplied(uint.MaxValue);
        Assert.False(guard.ShouldProcess(uint.MaxValue));
        Assert.True(guard.ShouldProcess(0)); // 原生序号回卷仍按事件区分
    }

    [Fact]
    public void ValidatesMetadataLimitsAndPngBeforeDecode()
    {
        Assert.Throws<ProtocolException>(() => TransferMetadata.Validate(new("x", -1, "file", "application/octet-stream")));
        Assert.Throws<ProtocolException>(() => TransferMetadata.Validate(new("x", 1L << 41, "file", "application/octet-stream")));
        Assert.Throws<ProtocolException>(() => TransferMetadata.Validate(new("x", 33L << 20, "image", "image/png")));
        Assert.Throws<ProtocolException>(() => TransferMetadata.Validate(new("x", 1, "image", "image/jpeg")));
        Assert.Throws<ProtocolException>(() => PngValidation.Validate([1, 2, 3]));
        var valid = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        PngValidation.Validate(valid);
        valid[16] = 127;
        Assert.Throws<ProtocolException>(() => PngValidation.Validate(valid));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
