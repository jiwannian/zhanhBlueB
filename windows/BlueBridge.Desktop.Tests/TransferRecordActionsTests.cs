using BlueBridge.Desktop;
using Xunit;

namespace BlueBridge.Desktop.Tests;

public sealed class TransferRecordActionsTests
{
    private readonly List<TransferRow> rows = [];
    private readonly FakeRecycler recycler = new();
    private readonly List<Guid> removed = [];
    private readonly List<string> deletedPaths = [];
    private TransferRecordActions Actions() => new(rows, recycler, removed.Add, deletedPaths.Add);
    private static string PathFor(string name = "原文件.xlsx") => Path.Combine(Path.GetTempPath(), name);
    private TransferRow Add(string state = "已完成", string? source = null, string? saved = null) {
        var row = new TransferRow(Guid.NewGuid(), "文件", "电脑 → 手机", state, 10, 10, source, SavedPath: saved);
        rows.Add(row); return row;
    }

    [Fact] public void Local_path_prefers_saved_copy_and_falls_back_to_source() {
        Assert.Equal(PathFor("副本.png"), Add(source: PathFor(), saved: PathFor("副本.png")).LocalPath);
        Assert.Equal(PathFor(), Add(source: PathFor(), saved: " ").LocalPath);
        Assert.Null(Add().LocalPath);
    }

    [Theory]
    [InlineData("等待发送")]
    [InlineData("正在发送")]
    [InlineData("正在接收")]
    public async Task Active_transfers_cannot_be_removed(string state) {
        var row = Add(state, PathFor()); var actions = Actions();
        Assert.False(actions.Availability(row.Id).CanRemove);
        Assert.False(actions.Remove(row.Id).Removed);
        Assert.False((await actions.RemoveWithFileAsync(row.Id, (_, _) => throw new Exception())).Removed);
        Assert.Single(rows); Assert.Empty(recycler.Paths);
    }

    [Fact] public void Pending_cleanup_blocks_even_a_completed_or_cancelled_row() {
        var row = Add(source: PathFor()); rows[0] = row with { JobPending = true };
        Assert.False(Actions().Availability(row.Id).CanRemove);
        rows[0] = row with { State = "已取消", JobPending = true };
        Assert.False(Actions().Remove(row.Id).Removed);
    }

    [Fact] public void Record_only_removal_does_not_touch_file_and_suppresses_late_progress() {
        var row = Add("失败，可重试", PathFor()); var actions = Actions();
        Assert.True(actions.Remove(row.Id).Removed);
        Assert.Empty(rows); Assert.Empty(recycler.Paths); Assert.Equal([row.Id], removed);
        Assert.True(actions.IsRemoved(row.Id)); Assert.False(actions.Remove(row.Id).Removed);
        Assert.False(actions.IsRemoved(Guid.NewGuid()));
    }

    [Fact] public async Task Cancellation_preserves_record_and_never_calls_recycler() {
        var row = Add(source: PathFor()); var actions = Actions();
        Assert.False((await actions.RemoveWithFileAsync(row.Id, (_, _) => false)).Removed);
        Assert.Single(rows); Assert.Empty(recycler.Paths);
    }

    [Fact] public async Task Missing_path_does_not_offer_file_removal() {
        var row = Add(); var actions = Actions();
        Assert.True(actions.Availability(row.Id).CanRemove);
        Assert.False(actions.Availability(row.Id).CanRecycle);
        Assert.False((await actions.RemoveWithFileAsync(row.Id, (_, _) => true)).Removed);
        Assert.False(actions.Availability(Guid.NewGuid()).CanOpen);
    }

    [Theory]
    [InlineData("已完成")]
    [InlineData("已取消")]
    [InlineData("失败，可重试")]
    public async Task Source_file_is_confirmed_then_recycled_and_other_records_are_invalidated(string state) {
        var row = Add(state, PathFor()); var related = Add(source: PathFor()); var actions = Actions();
        var result = await actions.RemoveWithFileAsync(row.Id, (current, path) => {
            Assert.Equal(row, current); Assert.Equal(PathFor(), path); return true;
        });
        Assert.True(result.Removed); Assert.Equal([PathFor()], recycler.Paths);
        Assert.Equal(related.Id, rows.Single().Id); Assert.True(rows[0].LocalFileRemoved);
        Assert.False(rows[0].CanOpen); Assert.False((rows[0] with { State = "失败，可重试" }).CanRetry);
        Assert.Equal("已完成", rows[0].State); Assert.Contains("本地文件已移除", rows[0].Detail);
        Assert.Equal([PathFor()], deletedPaths);
    }

    [Fact] public async Task Recycle_failure_cancel_or_exception_keeps_success_state_and_unlocks_path() {
        foreach (var status in new[] { RecycleStatus.Failed, RecycleStatus.Cancelled }) {
            rows.Clear(); var row = Add(source: PathFor()); var actions = Actions();
            recycler.Result = new(status, "测试提示");
            Assert.False((await actions.RemoveWithFileAsync(row.Id, (_, _) => true)).Removed);
            Assert.Equal("已完成", rows.Single().State); Assert.False(actions.IsPathRecycling(PathFor()));
            Assert.False(rows[0].IsRecycling);
        }
        recycler.Error = new IOException("占用"); var action = Actions();
        Assert.False((await action.RemoveWithFileAsync(rows[0].Id, (_, _) => true)).Removed);
        Assert.Single(rows);
    }

    [Fact] public async Task Already_missing_file_removes_only_selected_record() {
        recycler.Result = new(RecycleStatus.NotFound, "文件已不存在"); var row = Add(saved: PathFor());
        Assert.True((await Actions().RemoveWithFileAsync(row.Id, (_, _) => true)).Removed);
        Assert.Empty(rows);
    }

    [Fact] public async Task Same_path_active_task_or_pending_cleanup_blocks_recycling() {
        var row = Add(source: PathFor()); var other = Add("正在发送", PathFor()); var actions = Actions();
        Assert.False(actions.Availability(row.Id).CanRecycle);
        Assert.False((await actions.RemoveWithFileAsync(row.Id, (_, _) => true)).Removed);
        rows[1] = other with { State = "已取消", JobPending = true };
        Assert.False(actions.Availability(row.Id).CanRecycle); Assert.Empty(recycler.Paths);
        Assert.True(actions.Remove(row.Id).Removed); // Record-only removal does not affect another task.
    }

    [Fact] public async Task Confirmation_rechecks_current_id_path_and_active_jobs() {
        var row = Add(source: PathFor()); var actions = Actions();
        Assert.False((await actions.RemoveWithFileAsync(row.Id, (_, _) => { Add("正在发送", PathFor()); return true; })).Removed);
        Assert.Empty(recycler.Paths);
        rows.RemoveAt(1);
        Assert.False((await actions.RemoveWithFileAsync(row.Id, (_, _) => { rows[0] = row with { Source = PathFor("其他.xlsx") }; return true; })).Removed);
        Assert.Empty(recycler.Paths);
        Assert.False((await actions.RemoveWithFileAsync(row.Id, (_, _) => { rows.Clear(); return true; })).Removed);
    }

    [Fact] public async Task During_recycle_duplicate_deletion_and_same_path_enqueue_are_blocked() {
        var pending = new TaskCompletionSource<RecycleResult>(); recycler.Pending = pending.Task;
        var row = Add(source: PathFor()); var related = Add(source: PathFor()); var actions = Actions();
        var first = actions.RemoveWithFileAsync(row.Id, (_, _) => true);
        Assert.True(actions.IsPathRecycling(PathFor())); Assert.True(rows.All(x => x.IsRecycling));
        Assert.False(actions.Remove(related.Id).Removed);
        Assert.False((await actions.RemoveWithFileAsync(row.Id, (_, _) => true)).Removed);
        pending.SetResult(new(RecycleStatus.Success)); Assert.True((await first).Removed);
        Assert.False(actions.IsPathRecycling(PathFor())); Assert.Single(recycler.Paths);
    }

    [Theory]
    [InlineData("relative.xlsx")]
    [InlineData("https://example.com/file")]
    public async Task Invalid_file_paths_never_reach_shell(string path) {
        var row = Add(source: path);
        Assert.False((await Actions().RemoveWithFileAsync(row.Id, (_, _) => true)).Removed);
        Assert.Empty(recycler.Paths);
    }

    [Theory]
    [InlineData("图片 (1).png")]
    [InlineData("clipboard.png")]
    public async Task Saved_receive_or_image_copy_is_recycled_instead_of_original_source(string name) {
        var source = PathFor(); var saved = PathFor(name);
        var row = Add(source: source, saved: saved); var original = Add(source: source);
        var result = await Actions().RemoveWithFileAsync(row.Id, (_, path) => { Assert.Equal(saved, path); return true; });
        Assert.True(result.Removed); Assert.Equal([saved], recycler.Paths);
        Assert.Equal(original, rows.Single()); Assert.True(rows[0].CanOpen);
    }

    private sealed class FakeRecycler : ILocalFileRecycler {
        public List<string> Paths { get; } = [];
        public RecycleResult Result = new(RecycleStatus.Success);
        public Task<RecycleResult>? Pending;
        public Exception? Error;
        public Task<RecycleResult> RecycleAsync(string path) {
            Paths.Add(path); if (Error is not null) throw Error;
            return Pending ?? Task.FromResult(Result);
        }
    }
}
