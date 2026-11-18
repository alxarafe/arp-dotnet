using System.Data;
using System.Data.Common;
using System.Text.Json;
using Alxarafe.Arp.Cds.Physical;
using Alxarafe.Arp.Cds.Sql;
using Alxarafe.Arp.Dictionary;

namespace Alxarafe.Arp.Cds.Reads;

/// <summary>MySQL family differences only; the entire LIST/GET algorithm is inherited from SqlRecordReader.</summary>
public abstract class MySqlRecordReaderBase : SqlRecordReader
{
    protected bool IsMariaDb
    {
        get; init;
    }

    protected MySqlRecordReaderBase(Func<DbConnection> connect, int timeoutSeconds = 5) : base(connect, timeoutSeconds)
    {
        IdentifierStart = "`";
        IdentifierEnd = "`";
    }

    protected override DbTransaction BeginTransaction(DbConnection connection) =>
        MySqlMetadata.BeginReadOnly(connection);

    protected override void InitializeSession(DbCommand command) =>
        MySqlMetadata.Initialize(command, TimeoutSeconds, IsMariaDb);

    protected override string LimitClause(string parameter) => " LIMIT " + parameter;

    // Casting even TINYINT(1) to text prevents a driver's boolean option from hiding stored values such as 2.
    protected override string ValueExpression(string quotedColumn, ScalarType type) =>
        "CAST(" + quotedColumn + " AS CHAR CHARACTER SET utf8mb4)";

    protected override object PortableValue(DbDataReader reader, int ordinal, ScalarType type) =>
        type != ScalarType.Boolean ? base.PortableValue(reader, ordinal, type) : reader.GetString(ordinal) switch {
            "0" => false,
            "1" => true,
            _ => throw new ReadFault("ROW_DECODE_FAILED")
        };

    protected override bool SupportsColumn(ReadColumn column, JsonElement physical)
    {
        string native = physical.GetProperty("nativeType").GetString()!;
        if (native.StartsWith("char(", StringComparison.OrdinalIgnoreCase))
            return false;
        if (column.Type == ScalarType.Boolean)
            return physical.TryGetProperty("scalarType", out var scalar) && scalar.GetString() == "INT8"
                && native.StartsWith("tinyint", StringComparison.OrdinalIgnoreCase);
        return base.SupportsColumn(column, physical);
    }

    protected override void AcquireReadProtection(DbCommand command, SqlSource source, string quotedRelation) =>
        MySqlMetadata.Protect(command, source.Schema, source.Table);

    protected override JsonElement ObservePhysical(SqlSource source)
    {
        try {
            var physical = source.Physical;
            string provider = physical.GetProperty("source").GetProperty("provider").GetString()!;
            string id = physical.GetProperty("id").GetString()!;
            IPhysicalObservationProvider observer = IsMariaDb
                ? new MariaDbObservationProvider(Connect, provider, id, source.Schema, source.Table, TimeoutSeconds)
                : new MySqlObservationProvider(Connect, provider, id, source.Schema, source.Table, TimeoutSeconds);
            return observer.Observe();
        } catch (PhysicalObservationException) { throw new ReadFault("READ_FAILED"); }
    }
}

public class MySqlRecordReader(Func<DbConnection> connect, int timeoutSeconds = 5)
    : MySqlRecordReaderBase(connect, timeoutSeconds);

public class MariaDbRecordReader : MySqlRecordReaderBase
{
    public MariaDbRecordReader(Func<DbConnection> connect, int timeoutSeconds = 5) : base(connect, timeoutSeconds)
    {
        IsMariaDb = true;
    }
}