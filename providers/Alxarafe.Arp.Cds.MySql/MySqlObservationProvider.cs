using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Alxarafe.Arp.Cds.Sql;
using Alxarafe.Arp.Cds.Reads;

namespace Alxarafe.Arp.Cds.Physical;

internal static class MySqlMetadata
{
    internal static string Quote(string name) => "`" + name.Replace("`", "``") + "`";
    internal static DbTransaction BeginReadOnly(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SET TRANSACTION READ ONLY";
        command.ExecuteNonQuery();
        return connection.BeginTransaction(IsolationLevel.ReadCommitted);
    }

    internal static void Initialize(DbCommand command, int seconds, bool maria)
    {
        foreach (string sql in new[] {
            "SET SESSION lock_wait_timeout = " + seconds,
            "SET SESSION innodb_lock_wait_timeout = " + seconds,
            maria ? "SET SESSION max_statement_time = " + seconds
                : "SET SESSION max_execution_time = " + checked(seconds * 1000) })
        {
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    internal static void Protect(DbCommand command, string schema, string table)
    {
        string relation = Quote(schema) + "." + Quote(table);
        command.CommandText = "SELECT * FROM " + relation + " LIMIT 0";
        using (var rows = command.ExecuteReader())
        {
        } // MDL remains held through the owning transaction.
        command.CommandText = "SHOW CREATE TABLE " + relation;
        using (var rows = command.ExecuteReader())
        {
        }
        // SELECT * excludes invisible columns. Require demonstrable whole-table metadata authority,
        // not a possibly incomplete set of column grants. Role-only authority is not inferred here.
        command.CommandText = "SELECT CURRENT_USER()";
        string account = (string) command.ExecuteScalar()!;
        int separator = account.LastIndexOf('@');
        if (separator < 1)
            throw new ReadFault("CAPABILITY_UNAVAILABLE");
        string grantee = "'" + account[..separator] + "'@'" + account[(separator + 1)..] + "'";
        var grants = SqlMetadata.Query(command, "SELECT COUNT(*) AS n FROM ("
            + "SELECT GRANTEE FROM information_schema.USER_PRIVILEGES WHERE PRIVILEGE_TYPE='SELECT' "
            + "UNION ALL SELECT GRANTEE FROM information_schema.SCHEMA_PRIVILEGES "
            + "WHERE PRIVILEGE_TYPE='SELECT' AND TABLE_SCHEMA=@schema "
            + "UNION ALL SELECT GRANTEE FROM information_schema.TABLE_PRIVILEGES "
            + "WHERE PRIVILEGE_TYPE='SELECT' AND TABLE_SCHEMA=@schema AND TABLE_NAME=@table"
            + ") grants_read WHERE GRANTEE=@grantee", ("schema", schema), ("table", table), ("grantee", grantee));
        if (grants[0]["n"] == "0")
            throw new ReadFault("CAPABILITY_UNAVAILABLE");
        var tables = SqlMetadata.Query(command,
            "SELECT TABLE_TYPE, ENGINE FROM information_schema.TABLES WHERE TABLE_SCHEMA=@schema AND TABLE_NAME=@table",
            ("schema", schema), ("table", table));
        if (tables.Count != 1 || tables[0]["TABLE_TYPE"] != "BASE TABLE" || tables[0]["ENGINE"] != "InnoDB")
            throw new ReadFault("CAPABILITY_UNAVAILABLE");
        var partitions = SqlMetadata.Query(command, "SELECT COUNT(*) AS n FROM information_schema.PARTITIONS "
            + "WHERE TABLE_SCHEMA=@schema AND TABLE_NAME=@table AND PARTITION_NAME IS NOT NULL",
            ("schema", schema), ("table", table));
        if (partitions[0]["n"] != "0")
            throw new ReadFault("CAPABILITY_UNAVAILABLE");
    }
}

/// <summary>One shared bounded metadata algorithm; concrete classes verify the actual server family.</summary>
public abstract class MySqlObservationProviderBase : IPhysicalObservationProvider
{
    private readonly Func<DbConnection> connect;
    private readonly string id, schema, table;
    private readonly int timeoutSeconds;
    protected bool IsMariaDb
    {
        get; init;
    }
    public string ProviderId
    {
        get;
    }

    protected MySqlObservationProviderBase(Func<DbConnection> connect, string provider, string id,
        string schema, string table, int timeoutSeconds)
    {
        foreach (string value in new[] { provider, id, schema, table })
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            if (value.Contains('\0'))
                throw new ArgumentException("Invalid registered identifier");
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
        try
        {
            using var connection = connect();
            if (connection.State != ConnectionState.Closed)
                throw new PhysicalObservationException();
            connection.Open();
            using var tx = MySqlMetadata.BeginReadOnly(connection);
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandTimeout = timeoutSeconds;
            MySqlMetadata.Initialize(command, timeoutSeconds, IsMariaDb);
            command.CommandText = "SELECT VERSION()";
            string version = (string) command.ExecuteScalar()!;
            if (version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) != IsMariaDb)
                throw new PhysicalObservationException();
            MySqlMetadata.Protect(command, schema, table);
            var parameters = new[] { ("schema", schema), ("table", table) };
            var checks = SqlMetadata.Query(command,
                "SELECT COUNT(*) AS n FROM information_schema.TABLE_CONSTRAINTS "
                + "WHERE TABLE_SCHEMA=@schema AND TABLE_NAME=@table AND CONSTRAINT_TYPE='CHECK'", parameters);
            bool constrained = checks[0]["n"] != "0";
            var facts = SqlMetadata.Query(command, "SELECT COLUMN_NAME,COLUMN_TYPE,DATA_TYPE,IS_NULLABLE,"
                + "ORDINAL_POSITION,EXTRA,CHARACTER_MAXIMUM_LENGTH,NUMERIC_PRECISION,NUMERIC_SCALE "
                + "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@schema AND TABLE_NAME=@table "
                + "ORDER BY ORDINAL_POSITION", parameters);
            var columns = new List<Dictionary<string, object?>>();
            foreach (var fact in facts)
            {
                string native = fact["COLUMN_TYPE"]!, type = fact["DATA_TYPE"]!;
                bool unsigned = native.Contains("unsigned", StringComparison.OrdinalIgnoreCase);
                string? scalar = constrained ? null : type switch
                {
                    "tinyint" => unsigned ? "UINT8" : "INT8",
                    "smallint" => unsigned ? "UINT16" : "INT16",
                    "int" => unsigned ? "UINT32" : "INT32",
                    "bigint" => unsigned ? null : "INT64",
                    "decimal" => "DECIMAL",
                    "date" => "DATE",
                    "char" or "varchar" => "STRING",
                    "tinytext" or "text" or "mediumtext" or "longtext" => "TEXT",
                    _ => null
                };
                bool nullable = fact["IS_NULLABLE"] == "YES";
                var column = SqlMetadata.Column(fact["COLUMN_NAME"]!, native,
                    int.Parse(fact["ORDINAL_POSITION"]!, CultureInfo.InvariantCulture), nullable, scalar,
                    fact["EXTRA"]!.Contains("VIRTUAL GENERATED", StringComparison.OrdinalIgnoreCase)
                    || fact["EXTRA"]!.Contains("STORED GENERATED", StringComparison.OrdinalIgnoreCase),
                    fact["EXTRA"]!.Contains("auto_increment", StringComparison.OrdinalIgnoreCase));
                if (constrained && nullable)
                    column.Remove("nullable");
                if (scalar == "STRING" && fact["CHARACTER_MAXIMUM_LENGTH"] is { } length)
                    column["characterLength"] = new
                    {
                        value = long.Parse(length, CultureInfo.InvariantCulture),
                        unit = "CHARACTERS"
                    };
                if (type == "decimal")
                    column["decimalGeometry"] = new
                    {
                        precision = int.Parse(fact["NUMERIC_PRECISION"]!, CultureInfo.InvariantCulture),
                        scale = int.Parse(fact["NUMERIC_SCALE"]!, CultureInfo.InvariantCulture)
                    };
                columns.Add(column);
            }
            var keys = SqlMetadata.Query(command, "SELECT COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE "
                + "WHERE TABLE_SCHEMA=@schema AND TABLE_NAME=@table AND CONSTRAINT_NAME='PRIMARY' "
                + "ORDER BY ORDINAL_POSITION", parameters);
            var result = SqlMetadata.Physical(id, ProviderId, IsMariaDb ? "mariadb" : "mysql", schema, table,
                version, columns, keys.Select(k => k["COLUMN_NAME"]!));
            tx.Commit();
            return result;
        } catch (DbException)
        {
            throw new PhysicalObservationException();
        } catch (TimeoutException)
        {
            throw new PhysicalObservationException();
        } catch (ReadFault)
        {
            throw new PhysicalObservationException();
        }
    }
}

public sealed class MySqlObservationProvider(Func<DbConnection> connect, string provider, string id,
    string schema, string table, int timeoutSeconds = 5)
    : MySqlObservationProviderBase(connect, provider, id, schema, table, timeoutSeconds);

public sealed class MariaDbObservationProvider : MySqlObservationProviderBase
{
    public MariaDbObservationProvider(Func<DbConnection> connect, string provider, string id,
        string schema, string table,
        int timeoutSeconds = 5) : base(connect, provider, id, schema, table, timeoutSeconds)
    {
        IsMariaDb = true;
    }
}
