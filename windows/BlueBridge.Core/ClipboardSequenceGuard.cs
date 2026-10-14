namespace BlueBridge.Core;
/// <summary>Suppress locally applied writes by native clipboard sequence, independent of bitmap format conversion.</summary>
public sealed class ClipboardSequenceGuard
{
    private readonly object gate = new();
    private uint? lastSequence;
    public void MarkApplied(uint sequence) { lock (gate) lastSequence = sequence; }
    public bool ShouldProcess(uint sequence)
    {
        lock (gate)
        {
            if (lastSequence == sequence) return false;
            lastSequence = sequence; return true;
        }
    }
}
