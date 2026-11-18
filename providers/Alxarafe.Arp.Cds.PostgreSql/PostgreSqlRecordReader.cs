using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alxarafe.Arp.Dictionary;

namespace Alxarafe.Arp.Cds.Reads;

internal sealed class ReadFault(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

internal sealed record ReadColumn(string Name, ScalarType Type, bool Nullable);
internal sealed record ReadRow(string[] Key, Dictionary<string, object?> Values);
internal sealed record ReadPlan(JsonNode Physical, ReadColumn[] Columns, string[] Keys,
    IReadOnlyDictionary<string, long> Equalities, string[]? Position, bool Get, int Limit);

/// <summary>PostgreSQL read adapter; SQL plans are internal and built only from trusted resolved metadata.</summary>
public sealed class PostgreSqlRecordReader(Func<DbConnection> connect, int timeoutSeconds = 5)
{
    internal static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    internal JsonElement Read(ReadPlan plan, Func<bool> current,
        Func<IReadOnlyList<ReadRow>, Action, JsonElement> publish)
    {
        try {
            if (!current())
                throw new ReadFault("GENERATION_CHANGED");
            using var connection = connect();
            if (connection.State != ConnectionState.Closed)
                throw new ReadFault("READ_FAILED");
            connection.Open();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = timeoutSeconds;
            var source = plan.Physical["source"]!;
            string schema = source["schema"]!.GetValue<string>(), table = source["name"]!.GetValue<string>();
            string relation = Quote(schema) + "." + Quote(table);
            command.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL DateStyle = 'ISO, YMD'; "
                + "SET LOCAL lock_timeout = '" + Math.Max(1, timeoutSeconds) + "s'; "
                + "LOCK TABLE " + relation + " IN ACCESS SHARE MODE";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT c.relkind = 'r' AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_inherits i "
                + "WHERE i.inhparent = c.oid OR i.inhrelid = c.oid) FROM pg_catalog.pg_class c "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE n.nspname = @schema AND c.relname = @table";
            Add(command, "schema", schema, DbType.String);
            Add(command, "table", table, DbType.String);
            if (command.ExecuteScalar() is not true)
                throw new ReadFault("CAPABILITY_UNAVAILABLE");
            // A fresh observation is made while this transaction prevents incompatible external DDL.
            var observed = new Physical.PostgreSqlObservationProvider(connect,
                source["provider"]!.GetValue<string>(), plan.Physical["id"]!.GetValue<string>(), schema, table,
                timeoutSeconds).Observe();
            if (!JsonNode.DeepEquals(JsonNode.Parse(observed.GetRawText()), plan.Physical))
                throw new ReadFault("GENERATION_CHANGED");
            command.Parameters.Clear();
            var predicates = new List<string>();
            foreach (var (name, value) in plan.Equalities) {
                string parameter = "p" + command.Parameters.Count;
                predicates.Add(Quote(name) + " = @" + parameter);
                Add(command, parameter, value, DbType.Int64);
            }
            if (plan.Position is not null) {
                var names = new List<string>();
                foreach (string value in plan.Position) {
                    string parameter = "p" + command.Parameters.Count;
                    names.Add("@" + parameter);
                    Add(command, parameter, long.Parse(value, CultureInfo.InvariantCulture), DbType.Int64);
                }
                predicates.Add("(" + string.Join(",", plan.Keys.Select(Quote)) + ") "
                    + (plan.Get ? "=" : ">") + " (" + string.Join(",", names) + ")");
            }
            // Text casts preserve arbitrary PostgreSQL numeric precision; the DICT codec normalizes exactly.
            var selection = plan.Columns.Select(c => c.Type == ScalarType.Boolean
                ? Quote(c.Name) : Quote(c.Name) + "::text").ToList();
            selection.AddRange(plan.Keys.Select(k => Quote(k) + "::text"));
            command.CommandText = "SELECT " + string.Join(",", selection) + " FROM ONLY " + relation
                + (predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates))
                + " ORDER BY " + string.Join(",", plan.Keys.Select(k => relation + "." + Quote(k) + " ASC"))
                + " LIMIT @take";
            Add(command, "take", plan.Get ? 2 : plan.Limit + 1, DbType.Int32);
            if (!current())
                throw new ReadFault("GENERATION_CHANGED");
            var result = new List<ReadRow>();
            using (var reader = command.ExecuteReader()) {
                while (reader.Read()) {
                    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (int i = 0; i < plan.Columns.Length; i++) {
                        var column = plan.Columns[i];
                        object? value = null;
                        if (reader.IsDBNull(i)) {
                            if (!column.Nullable)
                                throw new ReadFault("ROW_DECODE_FAILED");
                        } else {
                            object portable = column.Type == ScalarType.Boolean
                                ? reader.GetBoolean(i) : reader.GetString(i);
                            var decoded = CdsReadCodec.Decode(column.Type, portable);
                            if (!decoded.IsValid)
                                throw new ReadFault("ROW_DECODE_FAILED");
                            value = decoded.NormalizedValue;
                        }
                        values.Add(column.Name, value);
                    }
                    var key = new string[plan.Keys.Length];
                    for (int i = 0; i < key.Length; i++) {
                        int ordinal = plan.Columns.Length + i;
                        if (reader.IsDBNull(ordinal))
                            throw new ReadFault("ROW_IDENTITY_INVALID");
                        var decoded = CdsReadCodec.Decode(ScalarType.Int64, reader.GetString(ordinal));
                        if (!decoded.IsValid)
                            throw new ReadFault("ROW_IDENTITY_INVALID");
                        key[i] = (string) decoded.NormalizedValue!;
                    }
                    if (result.Count > 0 && result[^1].Key.SequenceEqual(key))
                        throw new ReadFault("ROW_IDENTITY_INVALID");
                    result.Add(new ReadRow(key, values));
                }
            }
            // Commit and final publication occur under the host/catalog fences, with ACCESS SHARE still held.
            return publish(result, transaction.Commit);
        } catch (DbException) {
            throw new ReadFault("READ_FAILED");
        } catch (Physical.PhysicalObservationException) {
            throw new ReadFault("READ_FAILED");
        } catch (TimeoutException) {
            throw new ReadFault("READ_FAILED");
        } catch (InvalidCastException) {
            throw new ReadFault("ROW_DECODE_FAILED");
        } catch (OverflowException) {
            throw new ReadFault("ROW_DECODE_FAILED");
        }
    }

    private static void Add(DbCommand command, string name, object value, DbType type)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}