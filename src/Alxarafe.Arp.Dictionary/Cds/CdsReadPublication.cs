namespace Alxarafe.Arp.Dictionary;

/// <summary>Local atomic generation boundary; computation runs outside the publication lock.</summary>
public sealed class CdsReadPublication
{
    private readonly object gate = new();
    private long generation;
    private CdsReadResult? current;
    public long Generation
    {
        get
        {
            lock (gate) {
                return generation;
            }
        }
    }
    public long BeginGeneration()
    {
        lock (gate) {
            current = null;
            return ++generation;
        }
    }
    public void Invalidate() => BeginGeneration();
    public CdsReadResult? Current
    {
        get
        {
            lock (gate) {
                return current;
            }
        }
    }
    public CdsReadResult Publish(long ticket, CdsReadResult result)
    {
        lock (gate) {
            if (ticket != generation)
                return CdsReadResult.Failure("SNAPSHOT_CHANGED", "/snapshot", "execution-failure");
            current = result.IsValid ? result : null;
            return result;
        }
    }
}