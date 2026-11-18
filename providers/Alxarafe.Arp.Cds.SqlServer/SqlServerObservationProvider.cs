using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Alxarafe.Arp.Cds.Reads;
using Alxarafe.Arp.Cds.Sql;

namespace Alxarafe.Arp.Cds.Physical;

internal static class SqlServerMetadata
{
    internal static string Quote(string value) => "[" + value.Replace("]", "]]") + "]";
    internal static void Initialize(DbCommand command, int seconds)
    {
        command.CommandText = "SET XACT_ABORT ON; SET LOCK_TIMEOUT " + checked(seconds * 1000);
        command.ExecuteNonQuery();
    }

    internal static void Protect(DbCommand command, string schema, string table)
    {
        string relation = Quote(schema) + "." + Quote(table);
        // Sch-S is only a statement-lifetime promise. A transaction-lifetime table S lock also conflicts with Sch-M.
        command.CommandText = "SELECT TOP (1) 1 FROM " + relation + " WITH (TABLOCK,HOLDLOCK)";
        using (var rows = command.ExecuteReader()) {
        }
        var facts = SqlMetadata.Query(command,
            "SELECT t.is_memory_optimized,t.temporal_type,"
            + "HAS_PERMS_BY_NAME(@qualified,'OBJECT','SELECT') AS can_read,"
            + "HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DEFINITION') AS can_observe "
            + "FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id "
            + "WHERE s.name=@schema AND t.name=@table AND t.is_ms_shipped=0 "
            + "AND NOT EXISTS (SELECT 1 FROM sys.partitions p WHERE p.object_id=t.object_id AND p.partition_number>1) "
            + "AND NOT EXISTS (SELECT 1 FROM sys.security_predicates sp WHERE sp.target_object_id=t.object_id)",
            ("qualified", relation), ("schema", schema), ("table", table));
        if (facts.Count != 1 || SqlMetadata.True(facts[0]["is_memory_optimized"]) || facts[0]["temporal_type"] != "0"
            || !SqlMetadata.True(facts[0]["can_read"]) || !SqlMetadata.True(facts[0]["can_observe"]))
            throw new ReadFault("CAPABILITY_UNAVAILABLE");
    }
}

/// <summary>SQL Server facts only, with complete metadata authority and a transaction-held schema guard.</summary>
public sealed class SqlServerObservationProvider : IPhysicalObservationProvider
{
    private readonly Func<DbConnection> connect;
    private readonly string id, schema, table;
    private readonly int timeoutSeconds;
    public string ProviderId
    {
        get;
    }

    public SqlServerObservationProvider(Func<DbConnection> connect, string provider, string id, string schema,
        string table, int timeoutSeconds = 5)
    {
        foreach (string value in new[] { provider, id, schema, table }) {
            ArgumentException.ThrowIfNullOrEmpty(value);
            if (value.Contains('\0'))
                throw new ArgumentException("Invalid registration");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);
        this.connect = connect;
        ProviderId = provider;
        this.id = id;
        this.schema = schema;
        this.table = table;
        this.timeoutSeconds = timeoutSeconds;
    }

    public JsonElement Observe()
    {
        try {
            using var connection = connect();
            if (connection.State != ConnectionState.Closed)
                throw new PhysicalObservationException();
            connection.Open();
            using var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandTimeout = timeoutSeconds;
            SqlServerMetadata.Initialize(command, timeoutSeconds);
            command.CommandText = "SELECT @@VERSION";
            string version = (string) command.ExecuteScalar()!;
            if (!version.Contains("Microsoft SQL Server", StringComparison.Ordinal))
                throw new PhysicalObservationException();
            SqlServerMetadata.Protect(command, schema, table);
            var parameters = new[] { ("schema", schema), ("table", table) };
            const string target = " FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id ";
            var checks = SqlMetadata.Query(command, "SELECT COUNT(*) AS n FROM sys.check_constraints ck "
                + "JOIN sys.tables t ON t.object_id=ck.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id "
                + "WHERE s.name=@schema AND t.name=@table", parameters);
            bool constrained = checks[0]["n"] != "0";
            var facts = SqlMetadata.Query(command,
                "SELECT c.name AS column_name,ty.name AS type_name,c.max_length,c.precision,c.scale,c.is_nullable,"
                + "c.column_id,c.is_identity,c.is_computed,ty.is_user_defined,c.is_hidden"
                + target + "JOIN sys.columns c ON c.object_id=t.object_id "
                + "JOIN sys.types ty ON ty.user_type_id=c.user_type_id "
                + "WHERE s.name=@schema AND t.name=@table ORDER BY c.column_id", parameters);
            var columns = new List<Dictionary<string, object?>>();
            foreach (var fact in facts) {
                string type = fact["type_name"]!, native = type;
                int length = int.Parse(fact["max_length"]!, CultureInfo.InvariantCulture);
                if (type is "nvarchar" or "nchar" or "varchar" or "char")
                    native += "(" + (length == -1 ? "max" : (length / (type.StartsWith('n') ? 2 : 1))
                        .ToString(CultureInfo.InvariantCulture)) + ")";
                if (type is "decimal" or "numeric")
                    native += "(" + fact["precision"] + "," + fact["scale"] + ")";
                string? scalar = constrained || SqlMetadata.True(fact["is_user_defined"]) ? null : type switch {
                    "tinyint" => "UINT8",
                    "smallint" => "INT16",
                    "int" => "INT32",
                    "bigint" => "INT64",
                    "bit" => "BOOLEAN",
                    "date" => "DATE",
                    "decimal" or "numeric" => "DECIMAL",
                    "varchar" or "nvarchar" when length == -1 => "TEXT",
                    "varchar" or "nvarchar" or "char" or "nchar" => "STRING",
                    "text" or "ntext" => "TEXT",
                    _ => null
                };
                bool nullable = SqlMetadata.True(fact["is_nullable"]);
                var column = SqlMetadata.Column(fact["column_name"]!, native,
                    int.Parse(fact["column_id"]!, CultureInfo.InvariantCulture), nullable, scalar,
                    SqlMetadata.True(fact["is_computed"]), SqlMetadata.True(fact["is_identity"]));
                if (constrained && nullable)
                    column.Remove("nullable");
                if (scalar == "STRING" && length > 0)
                    column["characterLength"] = new {
                        value = length,
                        unit = "BYTES"
                    };
                if (type is "decimal" or "numeric")
                    column["decimalGeometry"] = new {
                        precision = int.Parse(fact["precision"]!, CultureInfo.InvariantCulture),
                        scale = int.Parse(fact["scale"]!, CultureInfo.InvariantCulture)
                    };
                columns.Add(column);
            }
            var keys = SqlMetadata.Query(command, "SELECT c.name AS column_name" + target
                + "JOIN sys.indexes i ON i.object_id=t.object_id AND i.is_primary_key=1 AND i.is_disabled=0 "
                + "JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id "
                + "JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id "
                + "WHERE s.name=@schema AND t.name=@table AND ic.key_ordinal>0 ORDER BY ic.key_ordinal", parameters);
            var result = SqlMetadata.Physical(id, ProviderId, "sqlserver", schema, table,
                version, columns, keys.Select(k => k["column_name"]!));
            tx.Commit();
            return result;
        } catch (DbException) {
            throw new PhysicalObservationException();
        } catch (TimeoutException) {
            throw new PhysicalObservationException();
        } catch (ReadFault) {
            throw new PhysicalObservationException();
        }
    }
}