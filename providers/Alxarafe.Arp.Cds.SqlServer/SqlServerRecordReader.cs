using System.Data.Common;
using System.Text.Json;
using Alxarafe.Arp.Cds.Physical;
using Alxarafe.Arp.Cds.Sql;
using Alxarafe.Arp.Dictionary;

namespace Alxarafe.Arp.Cds.Reads;

public class SqlServerRecordReader : SqlRecordReader
{
    public SqlServerRecordReader(Func<DbConnection> connect, int timeoutSeconds = 5) : base(connect, timeoutSeconds)
    {
        IdentifierStart = "[";
        IdentifierEnd = "]";
    }

    protected override void InitializeSession(DbCommand command) =>
        SqlServerMetadata.Initialize(command, TimeoutSeconds);

    // OFFSET is a fixed zero required by this syntax, not client offset pagination.
    protected override string LimitClause(string parameter) => " OFFSET 0 ROWS FETCH NEXT " + parameter + " ROWS ONLY";

    protected override string KeyComparison(IReadOnlyList<string> keys, IReadOnlyList<string> parameters, bool get)
    {
        if (get)
            return "(" + string.Join(" AND ", keys.Select((key, i) => key + " = " + parameters[i])) + ")";
        var terms = new List<string>();
        for (int i = 0; i < keys.Count; i++) {
            var prefix = Enumerable.Range(0, i).Select(j => keys[j] + " = " + parameters[j]).ToList();
            prefix.Add(keys[i] + " > " + parameters[i]);
            terms.Add("(" + string.Join(" AND ", prefix) + ")");
        }
        return "(" + string.Join(" OR ", terms) + ")";
    }

    protected override string ValueExpression(string column, ScalarType type) => type switch {
        ScalarType.Boolean => column,
        ScalarType.Date => "CONVERT(nvarchar(max)," + column + ",23)",
        // sql_variant permits differing exact DECIMAL geometries in one column. Never accept textual/floating
        // subtypes merely because a string conversion might resemble a valid DECIMAL.
        ScalarType.Decimal => "CASE WHEN " + column + " IS NULL THEN NULL WHEN SQL_VARIANT_PROPERTY("
            + column + ",'BaseType') IN ('decimal','numeric') THEN CONVERT(nvarchar(max)," + column
            + ") ELSE N'ARP_UNSUPPORTED_DECIMAL_STORAGE' END",
        _ => "CONVERT(nvarchar(max)," + column + ")"
    };

    protected override bool SupportsColumn(ReadColumn column, JsonElement physical)
    {
        string native = physical.GetProperty("nativeType").GetString()!;
        if (native.StartsWith("char(", StringComparison.Ordinal)
            || native.StartsWith("nchar(", StringComparison.Ordinal))
            return false;
        return column.Type == ScalarType.Decimal && native == "sql_variant" || base.SupportsColumn(column, physical);
    }

    protected override void AcquireReadProtection(DbCommand command, SqlSource source, string quotedRelation) =>
        SqlServerMetadata.Protect(command, source.Schema, source.Table);

    protected override JsonElement ObservePhysical(SqlSource source)
    {
        try {
            return new SqlServerObservationProvider(Connect,
                source.Physical.GetProperty("source").GetProperty("provider").GetString()!,
                source.Physical.GetProperty("id").GetString()!, source.Schema, source.Table, TimeoutSeconds).Observe();
        } catch (PhysicalObservationException) { throw new ReadFault("READ_FAILED"); }
    }
}