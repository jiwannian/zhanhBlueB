using BlueBridge.Desktop;
using BlueBridge.Core;
using Xunit;

namespace BlueBridge.Desktop.Tests;

public sealed class ClipboardImageStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "zhanhBlueB-image-" + Guid.NewGuid());

    public ClipboardImageStoreTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Saves_clipboard_png_with_unique_names_without_overwriting()
    {
        byte[] bytes = Png();
        string first = await ClipboardImageStore.SavePngAsync(bytes, directory);
        string second = await ClipboardImageStore.SavePngAsync(bytes, directory);

        Assert.Equal("clipboard.png", Path.GetFileName(first));
        Assert.Equal("clipboard (1).png", Path.GetFileName(second));
        Assert.Equal(bytes, File.ReadAllBytes(first));
        Assert.Equal(bytes, File.ReadAllBytes(second));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, ".partial")));
        var row = new TransferRow(Guid.NewGuid(), "clipboard.png", "电脑 → 手机", "已完成", bytes.Length, bytes.Length, SavedPath: second);
        Assert.True(row.CanOpen);
        Assert.False(row.CanRetry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_or_empty_png_is_rejected_without_leaving_a_partial_file(bool empty)
    {
        await Assert.ThrowsAsync<ProtocolException>(() => ClipboardImageStore.SavePngAsync(empty ? [] : new byte[] { 1, 2, 3 }, directory));
        Assert.Empty(Directory.GetFiles(directory));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, ".partial")));
    }

    [Fact]
    public async Task Cancelled_save_does_not_leave_a_file_or_temporary_bytes()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClipboardImageStore.SavePngAsync(Png(), directory, canceled.Token));
        Assert.Empty(Directory.GetFiles(directory));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, ".partial")));
    }

    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public void Dispose() => Directory.Delete(directory, true);
}
