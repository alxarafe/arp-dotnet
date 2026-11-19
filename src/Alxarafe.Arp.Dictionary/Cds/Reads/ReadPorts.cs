using System.Collections.Immutable;
using Alxarafe.Arp.Dictionary;

namespace Alxarafe.Arp.Cds.Reads;

/// <summary>The existing CDS snapshot/generation boundary, without storage-provider dependencies.</summary>
public interface IReadCatalog
{
    CdsReadResult Resolve();
    CdsReadResult? Current
    {
        get;
    }
    long Generation
    {
        get;
    }
    T PublishRead<T>(CdsReadResult expected, Func<T> publish);
}

/// <summary>Preparation inspects trusted metadata only; Open owns a bounded, protected read.</summary>
public interface IRecordReader
{
    PreparedRead Prepare(CdsReadResult snapshot, string projection, ImmutableDictionary<string, string> equalities);
    IReadAttempt Open(ReadPlan plan, Func<bool> current);
}

/// <summary>
/// Decoded rows remain private through Complete and final publication. Physical protection survives Complete
/// and is released by Dispose; completing the data transaction must not release the publication guard.
/// </summary>
public interface IReadAttempt : IDisposable
{
    IReadOnlyList<ReadRow> Rows
    {
        get;
    }
    void Complete();
}

public sealed record ReadColumn(string Name, ScalarType Type, bool Nullable);
public sealed record ReadRow(ImmutableArray<string> Key, ImmutableDictionary<string, object?> Values);

/// <summary>Neutral immutable shape. The reader privately owns its source binding.</summary>
public abstract class PreparedRead
{
    public ImmutableArray<ReadColumn> Columns
    {
        get;
    }
    public ImmutableArray<ReadColumn> PrimaryKey
    {
        get;
    }
    public ImmutableDictionary<string, string> Equalities
    {
        get;
    }

    protected PreparedRead(IEnumerable<ReadColumn> columns, IEnumerable<ReadColumn> primaryKey,
        ImmutableDictionary<string, string> equalities)
    {
        Columns = [.. columns];
        PrimaryKey = [.. primaryKey];
        Equalities = equalities;
    }
}

public sealed record ReadPlan(PreparedRead Source, ImmutableArray<string>? Position, bool Get, int Limit);

/// <summary>Safe read outcome; no connection strings, SQL text or driver exceptions cross the port.</summary>
public sealed class ReadFault(string code) : Exception(code)
{
    public string Code { get; } = code;
}
