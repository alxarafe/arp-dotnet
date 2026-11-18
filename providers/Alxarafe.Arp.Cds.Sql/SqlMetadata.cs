using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Alxarafe.Arp.Cds.Physical;

namespace Alxarafe.Arp.Cds.Sql;

/// <summary>Bounded metadata plumbing shared by SQL observers, not another row-query or dialect framework.</summary>
public static class SqlMetadata
{
    public static List<Dictionary<string, string?>> Query(DbCommand command, string sql,
        params (string Name, string Value)[] parameters)
    {
        command.CommandText = sql;
        command.Parameters.Clear();
        foreach (var (name, value) in parameters) {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.DbType = DbType.String;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        var result = new List<Dictionary<string, string?>>();
        using var rows = command.ExecuteReader();
        while (rows.Read()) {
            if (result.Count >= 4096)
                throw new PhysicalObservationException();
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.FieldCount; i++)
                row.Add(rows.GetName(i), rows.IsDBNull(i) ? null :
                    Convert.ToString(rows.GetValue(i), CultureInfo.InvariantCulture));
            result.Add(row);
        }
        return result;
    }

    public static Dictionary<string, object?> Column(string name, string nativeType, int ordinal,
        bool nullable, string? scalar, bool generated = false, bool identity = false)
    {
        var value = new Dictionary<string, object?> {
            ["name"] = name,
            ["nativeType"] = nativeType,
            ["ordinal"] = ordinal,
            ["nullable"] = nullable,
            ["generated"] = generated,
            ["identity"] = identity
        };
        if (scalar is not null) {
            value["scalarType"] = scalar;
            var range = scalar switch {
                "INT8" => (8, true),
                "UINT8" => (8, false),
                "INT16" => (16, true),
                "UINT16" => (16, false),
                "INT32" => (32, true),
                "UINT32" => (32, false),
                "INT64" => (64, true),
                _ => (0, true)
            };
            if (range.Item1 != 0) {
                var high = BigInteger.One << (range.Item1 - (range.Item2 ? 1 : 0));
                value["integerRange"] = new {
                    minimum = (range.Item2 ? -high : BigInteger.Zero).ToString(CultureInfo.InvariantCulture),
                    maximum = (high - 1).ToString(CultureInfo.InvariantCulture)
                };
            }
        }
        return value;
    }

    public static JsonElement Physical(string id, string provider, string dialect, string schema, string table,
        string version, IReadOnlyList<Dictionary<string, object?>> columns, IEnumerable<string> keys)
    {
        if (columns.Count is < 1 or > 2048)
            throw new PhysicalObservationException();
        // Index/FK completeness is not asserted by observers that have not introspected those facts.
        return JsonSerializer.SerializeToElement(new {
            apiVersion = "arp.physical/v1",
            id,
            kind = "table",
            source = new {
                provider,
                dialect,
                schema,
                name = table,
                engineVersion = version
            },
            columns,
            primaryKey = keys.ToArray()
        });
    }

    public static bool True(string? value) => value is "1" or "YES" or "True" or "true";
}