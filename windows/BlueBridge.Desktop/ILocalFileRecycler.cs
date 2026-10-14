namespace BlueBridge.Desktop;

public enum RecycleStatus { Success, Cancelled, NotFound, Failed }
public sealed record RecycleResult(RecycleStatus Status, string? Message = null, string? RecycledPath = null);
public interface ILocalFileRecycler
{
    Task<RecycleResult> RecycleAsync(string path);
}
