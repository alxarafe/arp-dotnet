using System.Data.Common;
using System.Text.Json;
using Alxarafe.Arp.Cds.Physical;
using Alxarafe.Arp.Cds.Reads;
using Alxarafe.Arp.Cds.Sql;
using Alxarafe.Arp.Dictionary;

namespace Alxarafe.Arp.Cds.Reads;

/// <summary>Only PostgreSQL differences. The complete read algorithm and result loop are inherited.</summary>
public class PostgreSqlRecordReader : SqlRecordReader
{
    public PostgreSqlRecordReader(Func<DbConnection> connect, int timeoutSeconds = 5) : base(connect, timeoutSeconds)
    {
        FromModifier = "ONLY ";
    }

    protected override string LimitClause(string parameter) => " LIMIT " + parameter;

    protected override string ValueExpression(string quotedColumn, ScalarType type) =>
        type == ScalarType.Boolean ? quotedColumn : quotedColumn + "::text";

    protected override bool SupportsColumn(ReadColumn column, JsonElement physical) =>
        base.SupportsColumn(column, physical)
        && (!physical.TryGetProperty("nativeType", out var type)
            || !type.GetString()!.StartsWith("character(", StringComparison.Ordinal));

    protected override void InitializeSession(DbCommand command)
    {
        command.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL DateStyle = 'ISO, YMD'; "
            + "SET LOCAL lock_timeout = '" + TimeoutSeconds + "s'";
        command.ExecuteNonQuery();
    }

    protected override void AcquireReadProtection(DbCommand command, SqlSource source, string quotedRelation)
    {
        command.CommandText = "LOCK TABLE " + quotedRelation + " IN ACCESS SHARE MODE";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT c.relkind = 'r' AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_inherits i "
            + "WHERE i.inhparent = c.oid OR i.inhrelid = c.oid) FROM pg_catalog.pg_class c "
            + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
            + "WHERE n.nspname = @schema AND c.relname = @table";
        foreach (var (name, value) in new[] { ("schema", source.Schema), ("table", source.Table) }) {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        if (command.ExecuteScalar() is not true)
            throw new ReadFault("CAPABILITY_UNAVAILABLE");
    }

    protected override JsonElement ObservePhysical(SqlSource source)
    {
        try {
            var physical = source.Physical;
            return new PostgreSqlObservationProvider(Connect,
                physical.GetProperty("source").GetProperty("provider").GetString()!,
                physical.GetProperty("id").GetString()!, source.Schema, source.Table, TimeoutSeconds).Observe();
        } catch (PhysicalObservationException) { throw new ReadFault("READ_FAILED"); }
    }
}