using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alxarafe.Arp.Dictionary;
using Alxarafe.Arp.Cds.Reads;

namespace Alxarafe.Arp.Cds.Physical;

/// <summary>One registered table observation. Implementations own fresh connections, never scalar rules.</summary>
public interface IPhysicalObservationProvider
{
    string ProviderId
    {
        get;
    }
    JsonElement Observe();
}

public interface IInvalidatablePhysicalProvider
{
    void Invalidate();
}

/// <summary>Expected observation failure. Database messages and credentials never become public diagnostics.</summary>
public sealed class PhysicalObservationException() : Exception("Physical observation unavailable");

/// <summary>Lazy, explicitly invalidated local catalog. Definitions and dictionary are immutable snapshots.</summary>
public sealed class PhysicalCatalog : IReadCatalog
{
    private readonly object gate = new();
    private readonly object resolveGate = new();
    private readonly CdsReadPublication publication = new();
    private readonly CdsReadResolver resolver = new();
    private readonly Catalog dictionary;
    private readonly JsonElement definitions;
    private readonly KeyValuePair<string, IPhysicalObservationProvider>[] providers;

    public PhysicalCatalog(Catalog dictionary, JsonElement definitions,
        IReadOnlyDictionary<string, IPhysicalObservationProvider> providers)
    {
        this.dictionary = dictionary;
        this.definitions = definitions.Clone();
        var order = Comparer<string>.Create((a, b) =>
            Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b)));
        this.providers = providers.OrderBy(e => e.Key, order).ToArray();
        if (this.providers.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(providers));
    }

    private string? blocked;
    public void Invalidate()
    {
        lock (gate)
        {
            publication.Invalidate();
            blocked = null;
            foreach (var (_, provider) in providers)
            {
                if (provider is not IInvalidatablePhysicalProvider persisted)
                    continue;
                try
                {
                    persisted.Invalidate();
                } catch (PhysicalObservationException) { blocked ??= provider.ProviderId; }
            }
            if (blocked is not null)
                throw new PhysicalObservationException();
        }
    }
    public CdsReadResult? Current
    {
        get
        {
            lock (gate)
            {
                return publication.Current;
            }
        }
    }

    public long Generation
    {
        get
        {
            lock (gate)
            {
                return publication.Generation;
            }
        }
    }

    /// <summary>Serializes final read publication with metadata invalidation.</summary>
    public T PublishRead<T>(CdsReadResult expected, Func<T> publish)
    {
        lock (gate)
        {
            if (expected is null || !ReferenceEquals(publication.Current, expected))
                throw new InvalidOperationException("Read generation changed");
            return publish();
        }
    }

    public CdsReadResult Resolve()
    {
        lock (resolveGate)
        {
            return ResolveGeneration();
        }
    }

    private CdsReadResult ResolveGeneration()
    {
        long ticket;
        lock (gate)
        {
            if (publication.Current is { } current)
                return current;
            if (blocked is not null)
                return CdsReadResult.Failure("OBSERVATION_FAILED",
                "/providers/" + blocked.Replace("~", "~0").Replace("/", "~1"), "execution-failure");
            ticket = publication.BeginGeneration();
        }
        // Do not hold the gate across I/O: explicit invalidation must be able to invalidate this work.
        var bundle = JsonNode.Parse(definitions.GetRawText())!.AsObject();
        var physical = new JsonObject();
        foreach (var (handle, provider) in providers)
        {
            try
            {
                physical[handle] = JsonNode.Parse(provider.Observe().GetRawText());
            } catch (PhysicalObservationException)
            {
                return publication.Publish(ticket, CdsReadResult.Failure("OBSERVATION_FAILED",
                    "/providers/" + provider.ProviderId.Replace("~", "~0").Replace("/", "~1"), "execution-failure"));
            }
        }
        bundle["physical"] = physical;
        return publication.Publish(ticket, resolver.Resolve(JsonSerializer.SerializeToElement(bundle), dictionary));
    }
}
