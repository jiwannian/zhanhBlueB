namespace BlueBridge.Desktop;

public sealed record RecordActionResult(bool Removed, string Message);
public sealed record RecordAvailability(bool CanOpen = false, bool CanRemove = false, bool CanRecycle = false);

/// <summary>Access on the UI thread; async recycling resumes on that same synchronization context.</summary>
public sealed class TransferRecordActions(
    IList<TransferRow> rows, ILocalFileRecycler recycler,
    Action<Guid> recordRemoved, Action<string> fileRemoved)
{
    private HashSet<Guid> removed = [];
    private HashSet<string> recycling = new(StringComparer.OrdinalIgnoreCase);
    public bool IsRemoved(Guid id) => removed.Contains(id);
    public bool IsPathRecycling(string? path) => Key(path) is { } key && recycling.Contains(key);
    private TransferRow? Find(Guid id) => rows.FirstOrDefault(row => row.Id == id);

    public RecordAvailability Availability(Guid id)
    {
        var row = Find(id);
        if (row is null) return new();
        bool removable = !row.IsBusy;
        return new(row.CanOpen && !row.IsRecycling, removable,
            removable && !row.LocalFileRemoved && Key(row.LocalPath) is { } key && !PathInUse(key));
    }

    public RecordActionResult Remove(Guid id)
    {
        var row = Find(id);
        if (row is null) return new(false, "这条记录已不存在");
        if (!Availability(id).CanRemove) return new(false, "任务正在传输或清理，请稍后再删除记录");
        rows.Remove(row);
        removed = new(removed) { id };
        recordRemoved(id);
        return new(true, "记录已删除，文件已保留");
    }

    public async Task<RecordActionResult> RemoveWithFileAsync(Guid id, Func<TransferRow, string, bool> confirm)
    {
        var row = Find(id);
        string? path = Key(row?.LocalPath);
        if (row is null || path is null || !Availability(id).CanRecycle)
            return new(false, "本地文件不可删除，可能正在使用或已移除");
        if (!confirm(row, path)) return new(false, "已取消删除");
        // The confirmation runs a modal message loop: both the row and other tasks can change.
        if (Find(id) is not { } current || Key(current.LocalPath) != path || !Availability(id).CanRecycle)
            return new(false, "记录或文件使用状态已变化，请重新操作");

        recycling = new(recycling, StringComparer.OrdinalIgnoreCase) { path };
        ChangeMatching(path, item => item with { IsRecycling = true });
        try
        {
            var result = await recycler.RecycleAsync(path);
            if (result.Status is not (RecycleStatus.Success or RecycleStatus.NotFound))
                return new(false, result.Message ?? (result.Status == RecycleStatus.Cancelled ? "已取消删除" : "无法移入回收站，记录已保留"));
            ChangeMatching(path, item => item with { LocalFileRemoved = true });
            fileRemoved(path);
            // Unlock the selected row before using the common record cleanup operation.
            ChangeMatching(path, item => item with { IsRecycling = false });
            var removedResult = Remove(id);
            return removedResult with { Message = result.Status == RecycleStatus.Success
                ? "文件已移入回收站，记录已删除" : "本地文件已不存在，记录已删除" };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return new(false, "无法移入回收站，记录已保留，请检查文件占用或权限"); }
        finally
        {
            recycling = new(recycling.Where(key => !StringComparer.OrdinalIgnoreCase.Equals(key, path)), StringComparer.OrdinalIgnoreCase);
            ChangeMatching(path, item => item with { IsRecycling = false });
        }
    }

    private bool PathInUse(string path) => IsPathRecycling(path) || rows.Any(row => row.IsBusy && SamePath(row.LocalPath, path));
    private void ChangeMatching(string path, Func<TransferRow, TransferRow> change)
    {
        for (int i = 0; i < rows.Count; i++) if (SamePath(rows[i].LocalPath, path)) rows[i] = change(rows[i]);
    }
    public static bool SamePath(string? left, string? right) => Key(left) is { } key &&
        StringComparer.OrdinalIgnoreCase.Equals(key, Key(right));
    private static string? Key(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }
}
