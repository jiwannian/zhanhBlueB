namespace BlueBridge.Desktop;
public sealed record TransferRow(Guid Id, string Name, string Direction, string State, long Bytes, long Total, string? Source = null, bool ImageRetry = false, string? SavedPath = null, bool JobPending = false, bool IsRecycling = false, bool LocalFileRemoved = false)
{
    public string? LocalPath => !string.IsNullOrWhiteSpace(SavedPath) ? SavedPath : !string.IsNullOrWhiteSpace(Source) ? Source : null;
    public string? OpenPath => State == "已完成" && !LocalFileRemoved && !IsRecycling ? LocalPath : null;
    public bool CanOpen => !string.IsNullOrWhiteSpace(OpenPath);
    public double Percent => Total == 0 ? State == "已完成" ? 100 : 0 : Math.Clamp(100d * Bytes / Total, 0, 100);
    public string Detail => $"{Direction} · {State} · {Format(Bytes)} / {Format(Total)}" + (LocalFileRemoved ? " · 本地文件已移除" : IsRecycling ? " · 正在移入回收站" : "");
    public bool IsBusy => CanCancel || JobPending || IsRecycling;
    public bool CanRemove => !IsBusy;
    public bool CanRecycle => CanRemove && LocalPath is not null && !LocalFileRemoved;
    public bool CanCancel => State is "等待发送" or "正在发送" or "正在接收";
    public bool CanRetry => !JobPending && !IsRecycling && ((Source is not null && !LocalFileRemoved) || ImageRetry) && State is "失败，可重试" or "已取消";
    private static string Format(long value) => value >= 1 << 20 ? $"{value / (1024d * 1024):0.0} MB" : value >= 1024 ? $"{value / 1024d:0.0} KB" : $"{value} B";
}
