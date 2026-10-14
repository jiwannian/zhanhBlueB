using System.ComponentModel;
using System.Diagnostics;
using BlueBridge.Desktop;
using Xunit;

namespace BlueBridge.Desktop.Tests;

public sealed class TransferFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "zhanhBlueB-open-" + Guid.NewGuid());
    public TransferFileTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Received_file_uses_its_final_renamed_path_without_becoming_retryable()
    {
        var row = Received(CreateFile("截图 & 资料 (1).jpg"));
        Assert.True(row.CanOpen);
        Assert.Equal(row.SavedPath, row.OpenPath);
        Assert.Null(row.Source);
        Assert.False(row.CanRetry);
        Assert.False((row with { State = "已取消" }).CanRetry);
    }

    [Fact]
    public void Completed_sent_file_can_open_its_original_source()
    {
        string path = CreateFile("原图片.png");
        var row = new TransferRow(Guid.NewGuid(), "原图片.png", "电脑 → 手机", "已完成", 0, 0, path);
        Assert.True(row.CanOpen);
        Assert.Equal(path, row.OpenPath);
        Assert.Equal(100, row.Percent);
    }

    [Theory]
    [InlineData("等待发送")]
    [InlineData("正在发送")]
    [InlineData("正在接收")]
    [InlineData("失败，可重试")]
    [InlineData("已取消")]
    public void Unfinished_transfers_cannot_open_even_when_all_bytes_arrived(string state)
    {
        var row = Received(CreateFile("未确认.png")) with { State = state, Bytes = 10, Total = 10 };
        int launched = 0;
        Assert.False(row.CanOpen);
        Assert.Null(row.OpenPath);
        Assert.NotNull(TransferFileOpener.Open(row, _ => launched++));
        Assert.Equal(0, launched);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Completed_clipboard_without_a_local_file_cannot_open(string? path)
    {
        var row = Received(path);
        Assert.False(row.CanOpen);
        Assert.NotNull(TransferFileOpener.Open(row, _ => throw new Exception("Must not launch")));
    }

    [Fact]
    public void Existing_file_opens_with_default_association_and_no_command_arguments()
    {
        string path = CreateFile("截图 & 中文 (1).jpg");
        ProcessStartInfo? request = null;
        string? error = TransferFileOpener.Open(Received(path), info => request = info);
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(path, request.FileName);
        Assert.True(request.UseShellExecute);
        Assert.Equal("open", request.Verb);
        Assert.Equal("", request.Arguments);
        Assert.Empty(request.ArgumentList);
        Assert.Equal(directory, request.WorkingDirectory);
    }

    [Fact]
    public void Missing_or_moved_file_shows_an_error_without_launching()
    {
        string path = CreateFile("已移动.jpg");
        File.Delete(path);
        string? error = TransferFileOpener.Open(Received(path), _ => throw new Exception("Must not launch"));
        Assert.Contains("移动或删除", error);
    }

    [Theory]
    [InlineData("https://example.com/image.png")]
    [InlineData("relative.png")]
    public void A_web_address_or_relative_path_is_never_passed_to_the_shell(string path)
    {
        Assert.NotNull(TransferFileOpener.Open(Received(path), _ => throw new Exception("Must not launch")));
    }

    [Fact]
    public void Directory_is_not_treated_as_a_received_file()
    {
        Assert.NotNull(TransferFileOpener.Open(Received(directory), _ => throw new Exception("Must not launch")));
    }

    [Fact]
    public void No_default_application_has_a_specific_message()
    {
        string? error = TransferFileOpener.Open(Received(CreateFile("document.custom")), _ => throw new Win32Exception(1155));
        Assert.Contains("默认应用", error);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void File_removed_between_validation_and_launch_is_reported(int code)
    {
        string? error = TransferFileOpener.Open(Received(CreateFile("race.png")), _ => throw new Win32Exception(code));
        Assert.Contains("移动或删除", error);
    }

    [Fact]
    public void Shell_errors_are_reported_without_crashing_the_window()
    {
        var row = Received(CreateFile("image.png"));
        Assert.Contains("无法打开", TransferFileOpener.Open(row, _ => throw new Win32Exception(5)));
        Assert.Contains("无法打开", TransferFileOpener.Open(row, _ => throw new IOException("fixture")));
        Assert.Contains("无法打开", TransferFileOpener.Open(row, _ => throw new UnauthorizedAccessException()));
        Assert.Contains("无法打开", TransferFileOpener.Open(row, _ => throw new InvalidOperationException()));
        Assert.Contains("无法打开", TransferFileOpener.Open(row, _ => throw new ArgumentException()));
    }

    private static TransferRow Received(string? path) => new(Guid.NewGuid(), "截图.jpg", "手机 → 电脑", "已完成", 0, 0, SavedPath: path);
    private string CreateFile(string name) { string path = Path.Combine(directory, name); File.WriteAllBytes(path, []); return path; }
    public void Dispose() => Directory.Delete(directory, true);
}
