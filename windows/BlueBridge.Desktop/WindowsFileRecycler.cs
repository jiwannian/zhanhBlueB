using System.Runtime.InteropServices;

namespace BlueBridge.Desktop;

/// <summary>Windows 11 Shell recycling only; never falls back to File.Delete or a command.</summary>
public sealed class WindowsFileRecycler : ILocalFileRecycler
{
    public Task<RecycleResult> RecycleAsync(string path)
    {
        var completion = new TaskCompletionSource<RecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var thread = new Thread(() =>
            {
                try { completion.TrySetResult(Recycle(path)); }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.TraceError("Recycle worker failed: {0}", error);
                    completion.TrySetResult(new(RecycleStatus.Failed, "无法移入回收站，文件和记录已保留，请稍后重试"));
                }
            }) { IsBackground = true, Name = "zhanhBlueB recycle" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Cannot start recycle worker: {0}", error);
            completion.TrySetResult(new(RecycleStatus.Failed, "无法启动回收操作，文件和记录已保留，请稍后重试"));
        }
        return completion.Task;
    }

    private static RecycleResult Recycle(string path)
    {
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                return new(RecycleStatus.Failed, "仅支持将电脑本地普通文件移入回收站");
            path = Path.GetFullPath(path);
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return new(RecycleStatus.Failed, "不支持删除文件夹或链接，请在资源管理器中处理");
            for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
                if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return new(RecycleStatus.Failed, "文件位于链接目录，请在资源管理器中处理");
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
                return new(RecycleStatus.Failed, "此磁盘不支持安全回收，文件和记录已保留");

            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            var sink = new RecycleProgressSink();
            // RECYCLEONDELETE (Win8+) requires recycling. EARLYFAILURE prevents ignoring errors.
            operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00100000 | 0x0400 | 0x0010 | 0x0004);
            var iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
            operation.DeleteItem(item, sink);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            if (sink.RefusedPermanentDelete) return new(RecycleStatus.Failed, "此文件无法移入回收站，文件和记录已保留");
            if (aborted) return new(RecycleStatus.Cancelled, "回收操作已取消，记录已保留");
            return sink.Result ?? new(RecycleStatus.Failed, "未确认文件已进入回收站，记录已保留");
        }
        catch (FileNotFoundException) { return new(RecycleStatus.NotFound, "本地文件已不存在"); }
        catch (DirectoryNotFoundException) { return new(RecycleStatus.NotFound, "本地文件已不存在"); }
        catch (COMException error) when (error.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003))
        { return new(RecycleStatus.NotFound, "本地文件已不存在"); }
        catch (COMException error) when (error.HResult == unchecked((int)0x800704C7))
        { return new(RecycleStatus.Cancelled, "回收操作已取消，记录已保留"); }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(RecycleStatus.Failed, "无法移入回收站，请检查文件占用、权限或回收站设置；记录已保留"); }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}
