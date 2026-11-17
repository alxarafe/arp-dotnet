using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace Alxarafe.Arp.Cds.Physical;

/// <summary>Optional PostgreSQL table adapter. No Npgsql dependency; the host supplies a fresh DbConnection.</summary>
public sealed class PostgreSqlObservationProvider : IPhysicalObservationProvider
{
    private readonly Func<DbConnection> connect;
    private readonly string physicalId;
    private readonly string schema;
    private readonly string table;
    private readonly int timeoutSeconds;
    private static readonly string Sql = LoadSql();
    public string ProviderId { get; }

    public PostgreSqlObservationProvider(Func<DbConnection> connect, string providerId, string physicalId,
        string schema, string table, int timeoutSeconds = 10)
    {
        ArgumentNullException.ThrowIfNull(connect);
        foreach (string value in new[] { providerId, physicalId, schema, table }) {
            ArgumentException.ThrowIfNullOrEmpty(value);
            if (value.Contains('\0')) throw new ArgumentException("Invalid registered identifier");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);
        this.connect = connect; ProviderId = providerId; this.physicalId = physicalId;
        this.schema = schema; this.table = table; this.timeoutSeconds = timeoutSeconds;
    }

    public JsonElement Observe()
    {
        try {
            using var connection = connect();
            if (connection.State != ConnectionState.Closed) throw new PhysicalObservationException();
            connection.Open();
            using var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            using var command = connection.CreateCommand();
            command.Transaction = tx; command.CommandTimeout = timeoutSeconds;
            command.CommandText = "SET TRANSACTION READ ONLY"; command.ExecuteNonQuery();
            command.CommandText = "SELECT version()";
            if (command.ExecuteScalar() is not string version || !version.StartsWith("PostgreSQL ",
                StringComparison.Ordinal)) {
                throw new PhysicalObservationException();
            }
            command.CommandText = "LOCK TABLE " + Quote(schema) + "." + Quote(table) + " IN ACCESS SHARE MODE";
            command.ExecuteNonQuery();
            command.CommandText = Sql;
            foreach (var (name, value) in new[] { ("provider", ProviderId), ("id", physicalId),
                ("schema", schema), ("table", table) }) {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name; parameter.DbType = DbType.String; parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            if (command.ExecuteScalar() is not string json) throw new PhysicalObservationException();
            using var parsed = JsonDocument.Parse(json);
            var result = parsed.RootElement;
            if (!result.TryGetProperty("columns", out var columns) || columns.GetArrayLength() is < 1 or > 2048) {
                throw new PhysicalObservationException();
            }
            // The command has acquired ACCESS SHARE for the complete transaction; DDL cannot mix observations.
            tx.Commit();
            return result.Clone();
        } catch (DbException) { throw new PhysicalObservationException(); }
        catch (TimeoutException) { throw new PhysicalObservationException(); }
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string LoadSql()
    {
        using var stream = typeof(PostgreSqlObservationProvider).Assembly.GetManifestResourceStream(
            "Alxarafe.Arp.Cds.PostgreSql.postgresql.sql")
            ?? throw new InvalidOperationException("Missing provider SQL");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}