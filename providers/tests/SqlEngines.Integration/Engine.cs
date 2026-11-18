using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Alxarafe.Arp.Cds.Physical;
using Alxarafe.Arp.Cds.Reads;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

internal static class Engine
{
    internal static string Name => Environment.GetEnvironmentVariable("ARP_SQL_ENGINE")
        ?? throw new InvalidOperationException("Explicit engine required");
    internal static string Version { get; private set; } = "";
    private static string Password => Environment.GetEnvironmentVariable("ARP_DB_PASSWORD")!;
    internal static DbConnection Connect() => Name switch {
        "postgresql" => new NpgsqlConnection(new NpgsqlConnectionStringBuilder {
            Host = "db",
            Database = "arpcds",
            Username = "arpcds",
            Password = Password,
            Pooling = false
        }.ConnectionString),
        "mysql" or "mariadb" => new MySqlConnection(new MySqlConnectionStringBuilder {
            Server = "db",
            Database = "arpcds",
            UserID = "root",
            Password = Password,
            Pooling = false,
            TreatTinyAsBoolean = false,
            AllowPublicKeyRetrieval = true,
            SslMode = MySqlSslMode.None
        }.ConnectionString),
        "sqlserver" => new SqlConnection(new SqlConnectionStringBuilder {
            DataSource = "db",
            InitialCatalog = "arpcds",
            UserID = "sa",
            Password = Password,
            TrustServerCertificate = true,
            Pooling = false,
            ConnectTimeout = 10
        }.ConnectionString),
        _ => throw new InvalidOperationException("Unknown engine")
    };
    internal static IRecordReader Reader(Func<DbConnection> connect, int seconds) => Name switch {
        "postgresql" => new PostgreSqlRecordReader(connect, seconds),
        "mysql" => new MySqlRecordReader(connect, seconds),
        "mariadb" => new MariaDbRecordReader(connect, seconds),
        "sqlserver" => new SqlServerRecordReader(connect, seconds),
        _ => throw new InvalidOperationException()
    };
    internal static IPhysicalObservationProvider Observer(Func<DbConnection> connect) => Name switch {
        "postgresql" => new PostgreSqlObservationProvider(connect, "commercial-db",
            "storage.products", "commerce", "products"),
        "mysql" => new MySqlObservationProvider(connect, "commercial-db",
            "storage.products", "commerce", "products"),
        "mariadb" => new MariaDbObservationProvider(connect, "commercial-db",
            "storage.products", "commerce", "products"),
        "sqlserver" => new SqlServerObservationProvider(connect, "commercial-db",
            "storage.products", "commerce", "products"),
        _ => throw new InvalidOperationException()
    };
    private static DbConnection AsUser(string user)
    {
        using var original = Connect();
        if (Name is "mysql" or "mariadb") {
            var settings = new MySqlConnectionStringBuilder(original.ConnectionString) {
                UserID = user,
                Database = "commerce"
            };
            return new MySqlConnection(settings.ConnectionString);
        }
        var sql = new SqlConnectionStringBuilder(original.ConnectionString) { UserID = user };
        return new SqlConnection(sql.ConnectionString);
    }
    internal static void Permissions()
    {
        if (Name == "postgresql")
            return; // Full PostgreSQL visibility regressions remain in the original suite.
        if (Name is "mysql" or "mariadb") {
            Exec("ALTER TABLE commerce.products ADD invisible_secret int INVISIBLE; "
                + "CREATE USER 'column_reader'@'%' IDENTIFIED BY '" + Password + "'; "
                + "CREATE USER 'table_reader'@'%' IDENTIFIED BY '" + Password + "'; "
                + "GRANT SELECT(tenant_id,product_id,sku,name,price,rating,description,available,"
                + "released,cost,internal_note) "
                + "ON commerce.products TO 'column_reader'@'%'; "
                + "GRANT SELECT ON commerce.products TO 'table_reader'@'%'");
            using var limited = AsUser("column_reader");
            limited.Open();
            using var command = limited.CreateCommand();
            command.CommandText = "SELECT * FROM commerce.products LIMIT 0";
            if (Name == "mysql") {
                using var rows = command.ExecuteReader();
                Console.WriteLine("MYSQL_COLUMN_GRANTS_SELECT_STAR_SUCCEEDED");
            } else {
                try {
                    using var rows = command.ExecuteReader();
                    throw new InvalidOperationException("Expected MariaDB invisible-column privilege check");
                } catch (MySqlException error) when (error.Number == 1142) {
                    Console.WriteLine("MARIADB_COLUMN_GRANTS_SELECT_STAR_DENIED");
                }
            }
            // MySQL SELECT * cannot prove visibility of the invisible column.
        } else {
            Exec("CREATE LOGIN column_reader WITH PASSWORD='" + Password + "'; "
                + "CREATE USER column_reader FOR LOGIN column_reader; "
                + "CREATE LOGIN table_reader WITH PASSWORD='" + Password + "'; "
                + "CREATE USER table_reader FOR LOGIN table_reader; "
                + "GRANT SELECT ON commerce.products(tenant_id,product_id) TO column_reader; "
                + "GRANT SELECT ON commerce.products TO table_reader; GRANT VIEW DEFINITION TO table_reader");
        }
        try {
            Observer(() => AsUser("column_reader")).Observe();
            throw new InvalidOperationException("Incomplete visibility accepted");
        } catch (PhysicalObservationException) { }
        var physical = Observer(() => AsUser("table_reader")).Observe();
        if (physical.GetProperty("columns").GetArrayLength() < 11)
            throw new InvalidOperationException("Full table grant failed");
        if (Name == "sqlserver") {
            Exec("CREATE FUNCTION commerce.arp_filter(@tenant int) RETURNS TABLE WITH SCHEMABINDING "
                + "AS RETURN SELECT 1 AS permitted WHERE @tenant=-1");
            Exec("CREATE SECURITY POLICY commerce.arp_policy ADD FILTER PREDICATE "
                + "commerce.arp_filter(tenant_id) ON commerce.products WITH(STATE=ON)");
            foreach (var observer in new[] { Observer(Connect), Observer(() => AsUser("table_reader")) }) {
                try {
                    observer.Observe();
                    throw new InvalidOperationException("RLS table accepted");
                } catch (PhysicalObservationException) { }
            }
            Exec("DROP SECURITY POLICY commerce.arp_policy; DROP FUNCTION commerce.arp_filter");
        }
    }

    internal static void Exec(string sql)
    {
        using var c = Connect();
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 15;
        command.ExecuteNonQuery();
    }
    internal static void VerifyOwnership()
    {
        using var c = Connect();
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = "SELECT token FROM __arp_cds_test_owner";
        if ((string) command.ExecuteScalar()! != Environment.GetEnvironmentVariable("ARP_CDS_RUN_TOKEN"))
            throw new InvalidOperationException("Owned run nonce required before DDL");
        command.CommandText = Name switch {
            "postgresql" => "SHOW server_version",
            "sqlserver" => "SELECT @@VERSION",
            _ => "SELECT VERSION()"
        };
        Version = (string) command.ExecuteScalar()!;
        if (Name == "postgresql" && !Version.StartsWith("18.6", StringComparison.Ordinal))
            throw new InvalidOperationException("PostgreSQL 18.6 required");
        if (Name == "mariadb" && !Version.StartsWith("12.3", StringComparison.Ordinal))
            throw new InvalidOperationException("MariaDB 12.3 required");
    }
    internal static void ExpectBlockedDdl(string column)
    {
        using var c = Connect();
        c.Open();
        using var command = c.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = Name switch {
            "postgresql" => "SET lock_timeout='150ms'; ",
            "sqlserver" => "SET LOCK_TIMEOUT 150; ",
            _ => "SET SESSION lock_wait_timeout=1; "
        } + "ALTER TABLE commerce.products ADD " + column + " int";
        try {
            command.ExecuteNonQuery();
        } catch (PostgresException e) when (Name == "postgresql" && e.SqlState == "55P03") {
            return;
        } catch (MySqlException e) when (Name is "mysql" or "mariadb" && e.Number == 1205) {
            return;
        } catch (SqlException e) when (Name == "sqlserver" && e.Number == 1222) {
            return;
        }
        throw new InvalidOperationException("External DDL escaped transaction protection");
    }
    internal static void Seed(JsonNode layout, string keyLayout)
    {
        string begin = Name switch {
            "postgresql" => "CREATE SCHEMA IF NOT EXISTS commerce; ",
            "sqlserver" => "IF SCHEMA_ID('commerce') IS NULL EXEC('CREATE SCHEMA commerce'); ",
            _ => "CREATE DATABASE IF NOT EXISTS commerce CHARACTER SET utf8mb4; "
        };
        string text = Name == "sqlserver" ? "nvarchar(max)" : "text";
        string name = Name == "sqlserver" ? "nvarchar(200)" : "varchar(200)";
        string price = Name switch {
            "postgresql" => "numeric",
            "sqlserver" => "sql_variant",
            _ => "decimal(65,30)"
        };
        string cost = Name switch {
            "postgresql" => "numeric",
            "sqlserver" => "decimal(38,18)",
            _ => "decimal(65,30)"
        };
        string boolean = Name switch {
            "postgresql" => "boolean",
            "sqlserver" => "bit",
            _ => "tinyint(1)"
        };
        Exec(begin + "DROP TABLE IF EXISTS commerce.products; CREATE TABLE commerce.products("
            + "tenant_id int NOT NULL,product_id bigint NOT NULL,sku varchar(40) NOT NULL,name " + name
            + " NOT NULL,price " + price + " NOT NULL,rating smallint NOT NULL,description " + text
            + ",available " + boolean + " NOT NULL,released date NOT NULL,cost " + cost + " NOT NULL,internal_note "
            + text + ",CONSTRAINT products_pkey PRIMARY KEY("
            + (keyLayout == "simple" ? "product_id" : "tenant_id,product_id") + "))"
            + (Name is "mysql" or "mariadb" ? " ENGINE=InnoDB CHARACTER SET utf8mb4" : ""));
        foreach (var row in layout["rows"]!.AsArray()) {
            using var c = Connect();
            c.Open();
            using var command = c.CreateCommand();
            string[] names = ["tenant_id", "product_id", "sku", "name", "price", "rating", "description",
                "available", "released", "cost", "internal_note"];
            var expressions = new List<string>();
            for (int i = 0; i < names.Length; i++) {
                string field = names[i];
                var node = row!["values"]![field];
                string? value = node?.ToString();
                var p = command.CreateParameter();
                p.ParameterName = "p" + i;
                p.DbType = DbType.String;
                p.Value = value is null ? DBNull.Value : value;
                string type = field switch {
                    "tenant_id" or "product_id" or "rating" => Name is "mysql" or "mariadb" ? "SIGNED" : "bigint",
                    "released" => "date",
                    "price" or "cost" => price,
                    "available" => Name is "mysql" or "mariadb" ? "SIGNED" : boolean,
                    _ => Name == "sqlserver" ? "nvarchar(max)"
                        : Name == "postgresql" ? "text" : "CHAR CHARACTER SET utf8mb4"
                };
                if (field is "price" or "cost") {
                    int scale = value!.Contains('.') ? value.Length - value.IndexOf('.') - 1 : 0;
                    int precision = value.Count(char.IsDigit);
                    type = Name == "postgresql" ? "numeric"
                        : "decimal(" + Math.Max(precision, scale + 1) + "," + scale + ")";
                }
                if (field == "available" && Name != "postgresql")
                    p.Value = value == "True" || value == "true" ? "1" : "0";
                expressions.Add("CAST(@p" + i + " AS " + type + ")");
                command.Parameters.Add(p);
            }
            command.CommandText = "INSERT INTO commerce.products VALUES(" + string.Join(",", expressions) + ")";
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Fixture insert failed");
        }
    }
}