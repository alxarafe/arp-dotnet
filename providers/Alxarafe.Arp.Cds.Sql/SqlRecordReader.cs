using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alxarafe.Arp.Cds.Reads;
using Alxarafe.Arp.Dictionary;

namespace Alxarafe.Arp.Cds.Sql;

/// <summary>Fixed read algorithm. Engines specialize only protected syntax, values and physical protection.</summary>
public abstract class SqlRecordReader : IRecordReader
{
    protected Func<DbConnection> Connect
    {
        get;
    }
    protected int TimeoutSeconds
    {
        get;
    }
    protected string IdentifierStart { get; init; } = "\"";
    protected string IdentifierEnd { get; init; } = "\"";
    protected string FromModifier { get; init; } = "";

    protected SqlRecordReader(Func<DbConnection> connect, int timeoutSeconds = 5)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);
        Connect = connect;
        TimeoutSeconds = timeoutSeconds;
    }

    protected sealed record SqlSource(JsonElement Physical, string Schema, string Table);
    private sealed class SqlPreparedRead(SqlRecordReader owner, SqlSource source,
        IEnumerable<ReadColumn> columns, IEnumerable<ReadColumn> keys, ImmutableDictionary<string, string> equalities)
        : PreparedRead(columns, keys, equalities)
    {
        internal SqlRecordReader Owner { get; } = owner;
        internal SqlSource Source { get; } = source;
    }

    // These entry points are deliberately non-virtual: no engine supplies another LIST/GET pipeline.
    public PreparedRead Prepare(CdsReadResult snapshot, string projection,
        ImmutableDictionary<string, string> equalities)
    {
        var document = JsonNode.Parse(snapshot.ToJson())!;
        var selected = Find(document["projections"]!, projection);
        var model = Find(document["models"]!, Text(selected["entity"]));
        var physical = model["physical"]!;
        Need(Text(physical["kind"]) == "table", "CAPABILITY_UNAVAILABLE");
        var fields = model["fields"]!.AsArray().ToDictionary(f => Text(f!["name"]), f => f!);
        string[] keys = physical["primaryKey"]?.AsArray().Select(Text).ToArray() ?? [];
        Need(keys.Length > 0, "CAPABILITY_UNAVAILABLE");
        var primary = new List<ReadColumn>();
        foreach (string key in keys) {
            Need(fields.ContainsKey(key), "CAPABILITY_UNAVAILABLE");
            var field = fields[key];
            var stored = field["physical"]!;
            var type = Type(field);
            Need(Integer(type) && Integer(Type(stored)) && stored["nullable"]?.GetValue<bool>() == false,
                "CAPABILITY_UNAVAILABLE");
            primary.Add(new ReadColumn(key, type, false));
        }
        var columns = new List<ReadColumn>();
        foreach (var field in selected["fields"]!.AsArray()) {
            string name = Text(field!["name"]);
            var stored = fields[name]["physical"]!;
            var type = Type(field);
            Need(Integer(type) || type is ScalarType.Decimal or ScalarType.String or ScalarType.Text
                or ScalarType.Boolean or ScalarType.Date, "CAPABILITY_UNAVAILABLE");
            Need(stored["nullable"] is not null, "CAPABILITY_UNAVAILABLE");
            var column = new ReadColumn(name, type, stored["nullable"]!.GetValue<bool>());
            Need(SupportsColumn(column, JsonSerializer.SerializeToElement(stored)), "CAPABILITY_UNAVAILABLE");
            columns.Add(column);
        }
        var restrictions = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in equalities) {
            Need(fields.ContainsKey(name) && Integer(Type(fields[name]))
                && Integer(Type(fields[name]["physical"]!)), "CAPABILITY_UNAVAILABLE");
            var decoded = CdsReadCodec.Decode(Type(fields[name]), value);
            Need(decoded.IsValid, "CAPABILITY_UNAVAILABLE");
            restrictions.Add(name, (string) decoded.NormalizedValue!);
        }
        var source = physical["source"]!;
        return new SqlPreparedRead(this, new SqlSource(JsonSerializer.SerializeToElement(physical),
            Text(source["schema"]), Text(source["name"])), columns, primary, restrictions.ToImmutable());
    }

    public IReadAttempt Open(ReadPlan plan, Func<bool> current)
    {
        Need(plan.Source is SqlPreparedRead binding && ReferenceEquals(binding.Owner, this), "CAPABILITY_UNAVAILABLE");
        var prepared = (SqlPreparedRead) plan.Source;
        Need(plan.Limit is >= 1 and <= 100, "INPUT_SHAPE");
        Need(plan.Position is null || plan.Position.Value.Length == prepared.PrimaryKey.Length, "INVALID_KEY");
        DbConnection? connection = null;
        DbTransaction? transaction = null;
        DbConnection? protectionConnection = null;
        DbTransaction? protectionTransaction = null;
        try {
            if (!current())
                throw new ReadFault("GENERATION_CHANGED");
            connection = Connect();
            if (connection.State != ConnectionState.Closed)
                throw new ReadFault("READ_FAILED");
            connection.Open();
            transaction = BeginTransaction(connection);
            List<ReadRow> rows;
            using (var command = connection.CreateCommand()) {
                command.Transaction = transaction;
                command.CommandTimeout = TimeoutSeconds;
                InitializeSession(command);
                string relation = Quote(prepared.Source.Schema) + "." + Quote(prepared.Source.Table);
                AcquireReadProtection(command, prepared.Source, relation);
                // The attempt connection retains its protection while a fresh observation is compared.
                var observed = ObservePhysical(prepared.Source);
                if (!JsonNode.DeepEquals(JsonNode.Parse(observed.GetRawText()),
                        JsonNode.Parse(prepared.Source.Physical.GetRawText())))
                    throw new ReadFault("GENERATION_CHANGED");
                command.Parameters.Clear();
                BuildSelect(command, prepared, plan, relation);
                if (!current())
                    throw new ReadFault("GENERATION_CHANGED");
                rows = Decode(command, prepared, plan.Get ? 2 : plan.Limit + 1);
            }
            // Preserve final authorization/error precedence in the host; do not replace revocation by generation.
            // Acquire a second physical guard BEFORE the data transaction can release its own protection.
            // It remains owned by the attempt through commit and final host publication, until Dispose.
            protectionConnection = Connect();
            if (protectionConnection.State != ConnectionState.Closed) {
                throw new ReadFault("READ_FAILED");
            }
            protectionConnection.Open();
            protectionTransaction = BeginTransaction(protectionConnection);
            using (var guard = protectionConnection.CreateCommand()) {
                guard.Transaction = protectionTransaction;
                guard.CommandTimeout = TimeoutSeconds;
                InitializeSession(guard);
                string relation = Quote(prepared.Source.Schema) + "." + Quote(prepared.Source.Table);
                AcquireReadProtection(guard, prepared.Source, relation);
            }
            return new Attempt(this, connection, transaction, protectionConnection, protectionTransaction, rows);
        } catch (Exception error) {
            // Cleanup must not replace the original safe failure, but all owned resources are attempted.
            try {
                protectionTransaction?.Dispose();
            } catch (Exception) { }
            try {
                protectionConnection?.Dispose();
            } catch (Exception) { }
            try {
                transaction?.Dispose();
            } catch (Exception) { }
            try {
                connection?.Dispose();
            } catch (Exception) { }
            throw Sanitize(error);
        }
    }

    private void BuildSelect(DbCommand command, SqlPreparedRead prepared, ReadPlan plan, string relation)
    {
        var predicates = new List<string>();
        foreach (var (name, value) in prepared.Equalities) {
            string parameter = "p" + command.Parameters.Count;
            predicates.Add(Quote(name) + " = @" + parameter);
            Add(command, parameter, long.Parse(value, CultureInfo.InvariantCulture), DbType.Int64);
        }
        if (plan.Position is { } position) {
            var names = new List<string>();
            foreach (string value in position) {
                string parameter = "p" + command.Parameters.Count;
                names.Add("@" + parameter);
                Add(command, parameter, long.Parse(value, CultureInfo.InvariantCulture), DbType.Int64);
            }
            predicates.Add(KeyComparison(prepared.PrimaryKey.Select(k => Quote(k.Name)).ToArray(), names, plan.Get));
        }
        var selection = prepared.Columns.Select(c => ValueExpression(Quote(c.Name), c.Type)).ToList();
        selection.AddRange(prepared.PrimaryKey.Select(k => ValueExpression(Quote(k.Name), ScalarType.Int64)));
        command.CommandText = "SELECT " + string.Join(",", selection) + " FROM " + FromModifier + relation
            + (predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates))
            + " ORDER BY " + string.Join(",", prepared.PrimaryKey.Select(k => relation + "." + Quote(k.Name) + " ASC"))
            + LimitClause("@take");
        Add(command, "take", plan.Get ? 2 : plan.Limit + 1, DbType.Int32);
    }

    private List<ReadRow> Decode(DbCommand command, SqlPreparedRead plan, int take)
    {
        var result = new List<ReadRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            // Even an erroneous limit specialization cannot publish or buffer more than the bounded batch.
            Need(result.Count < take, "READ_FAILED");
            var values = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < plan.Columns.Length; i++) {
                var column = plan.Columns[i];
                object? value = null;
                if (reader.IsDBNull(i)) {
                    Need(column.Nullable, "ROW_DECODE_FAILED");
                } else {
                    var decoded = CdsReadCodec.Decode(column.Type, PortableValue(reader, i, column.Type));
                    Need(decoded.IsValid, "ROW_DECODE_FAILED");
                    value = decoded.NormalizedValue;
                }
                values.Add(column.Name, value);
            }
            var key = ImmutableArray.CreateBuilder<string>();
            for (int i = 0; i < plan.PrimaryKey.Length; i++) {
                int ordinal = plan.Columns.Length + i;
                Need(!reader.IsDBNull(ordinal), "ROW_IDENTITY_INVALID");
                var decoded = CdsReadCodec.Decode(ScalarType.Int64, PortableValue(reader, ordinal, ScalarType.Int64));
                Need(decoded.IsValid, "ROW_IDENTITY_INVALID");
                key.Add((string) decoded.NormalizedValue!);
            }
            Need(result.Count == 0 || !result[^1].Key.SequenceEqual(key), "ROW_IDENTITY_INVALID");
            result.Add(new ReadRow(key.ToImmutable(), values.ToImmutable()));
        }
        return result;
    }

    protected string Quote(string name) =>
        IdentifierStart + name.Replace(IdentifierEnd, IdentifierEnd + IdentifierEnd, StringComparison.Ordinal)
            + IdentifierEnd;

    protected virtual string LimitClause(string parameter) => " FETCH FIRST " + parameter + " ROWS ONLY";
    protected virtual string KeyComparison(IReadOnlyList<string> keys, IReadOnlyList<string> parameters, bool get) =>
        "(" + string.Join(",", keys) + ") " + (get ? "=" : ">") + " (" + string.Join(",", parameters) + ")";
    protected virtual string ValueExpression(string quotedColumn, ScalarType type) => quotedColumn;

    protected virtual object PortableValue(DbDataReader reader, int ordinal, ScalarType type)
    {
        // Never admit a binary floating-point intermediate. Provider-specific lossless conversions override here.
        return reader.GetValue(ordinal) switch {
            string value => value,
            bool value => value,
            sbyte or byte or short or ushort or int or uint or long or decimal =>
                Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!,
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => throw new ReadFault("ROW_DECODE_FAILED")
        };
    }

    protected virtual bool SupportsColumn(ReadColumn column, JsonElement physical)
    {
        if (!physical.TryGetProperty("scalarType", out var scalar)
            || !Enum.TryParse<ScalarType>(scalar.GetString(), true, out var stored))
            return false;
        return stored == column.Type || Integer(stored) && Integer(column.Type);
    }

    protected virtual DbTransaction BeginTransaction(DbConnection connection) =>
        connection.BeginTransaction(IsolationLevel.ReadCommitted);

    protected virtual void InitializeSession(DbCommand command)
    {
        command.CommandText = "SET TRANSACTION READ ONLY";
        command.ExecuteNonQuery();
    }

    // There is no portable default that could safely pretend to protect against external DDL.
    protected abstract void AcquireReadProtection(DbCommand command, SqlSource source, string quotedRelation);
    protected abstract JsonElement ObservePhysical(SqlSource source);
    private void CommitTransaction(DbTransaction transaction) => transaction.Commit();

    private sealed class Attempt(SqlRecordReader owner, DbConnection connection, DbTransaction transaction,
        DbConnection protectionConnection, DbTransaction protectionTransaction, IEnumerable<ReadRow> rows) : IReadAttempt
    {
        private bool completed;
        private bool disposed;
        public IReadOnlyList<ReadRow> Rows { get; } = rows.ToImmutableArray();

        public void Complete()
        {
            Need(!completed && !disposed, "READ_FAILED");
            try {
                owner.CommitTransaction(transaction);
                completed = true;
            } catch (Exception error) { throw Sanitize(error); }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            Exception? failure = null;
            try {
                transaction.Dispose();
            } catch (Exception error) { failure = error; }
            try {
                connection.Dispose();
            } catch (Exception error) { failure ??= error; }
            // Guard disposal releases the physical publication protection only AFTER completion/final fences.
            // Failure of data cleanup must never prevent an attempt to close the independent guard.
            try {
                // Explicit rollback is the guard's required close, not a best-effort Dispose that may hide errors.
                protectionTransaction.Rollback();
            } catch (Exception error) { failure ??= error; }
            try {
                protectionTransaction.Dispose();
            } catch (Exception error) { failure ??= error; }
            try {
                protectionConnection.Dispose();
            } catch (Exception error) { failure ??= error; }
            if (failure is not null)
                throw Sanitize(failure);
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

    private static ReadFault Sanitize(Exception error) => error as ReadFault ?? new ReadFault(
        error is InvalidCastException or OverflowException or FormatException ? "ROW_DECODE_FAILED" : "READ_FAILED");
    private static bool Integer(ScalarType type) => type is ScalarType.Int8 or ScalarType.UInt8 or ScalarType.Int16
        or ScalarType.UInt16 or ScalarType.Int32 or ScalarType.UInt32 or ScalarType.Int64;
    private static ScalarType Type(JsonNode field)
    {
        Need(Enum.TryParse<ScalarType>(Text(field["type"] ?? field["scalarType"]), true, out var type),
            "CAPABILITY_UNAVAILABLE");
        return type;
    }
    private static JsonNode Find(JsonNode array, string id) =>
        array.AsArray().FirstOrDefault(n => Text(n!["id"]) == id) ?? throw new ReadFault("INVALID_DEFINITION");
    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static void Need(bool condition, string code)
    {
        if (!condition)
            throw new ReadFault(code);
    }
}