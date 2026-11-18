using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alxarafe.Arp.Cds.Physical;
using Alxarafe.Arp.Cds.Reads;
using Alxarafe.Arp.Dictionary;
using Npgsql;

internal static class ListGetCases
{
    private static readonly ReadContext Context = new("commercial-reader/company-7", "policy-1");
    private static JsonNode products = null!;
    private static JsonArray vectors = null!;
    private static string root = "";
    private static string connectionString = "";
    private static readonly List<string> passed = [];
    private static readonly HashSet<string> direct = [];
    private static readonly Dictionary<string, string> equivalents = new(StringComparer.Ordinal);

    internal static void Run(string settings)
    {
        root = Environment.GetEnvironmentVariable("ARP_LISTGET_CONTRACT") ?? "";
        if (root.Length == 0)
            return;
        connectionString = settings;
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "git", $"-C {root} rev-parse HEAD") {
            RedirectStandardOutput = true
        })!;
        string sha = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        Check(git.ExitCode == 0 && sha == File.ReadAllText("listget-contract.ref").Trim(), "pinned LISTGET contract");
        products = Load("products.json");
        vectors = Load("cases.json")["cases"]!.AsArray();
        using (var c = Connect()) {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SHOW server_version";
            Check(((string) cmd.ExecuteScalar()!).StartsWith("18.6", StringComparison.Ordinal), "PostgreSQL 18.6");
            cmd.CommandText = "SELECT token FROM public.__arp_cds_test_owner";
            Check((string) cmd.ExecuteScalar()! == Environment.GetEnvironmentVariable("ARP_CDS_RUN_TOKEN"),
                "owned database nonce before DDL");
        }
        foreach (string layout in new[] { "simple", "composite" }) {
            Test(layout + ": values, paging and GET", () => Values(layout));
            Test(layout + ": bindings and reference input", () => Bindings(layout));
            Test(layout + ": authorization and registry", () => Authorization(layout));
            Test(layout + ": empty SQL and derived restrictions", () => Empty(layout));
            Test(layout + ": metadata rebuild and provider failure", () => Metadata(layout));
            Test(layout + ": scalar failure discards page", () => Decode(layout));
            Test(layout + ": in-flight revocation and composition", () => Revocation(layout));
            Test(layout + ": concurrent invalidation and DDL", () => Concurrent(layout));
        }
        Test("shared request vectors", Inputs);
        Test("authorization before definition failure", Conceal);
        Test("empty GET final fence", EmptyFence);
        Test("storage capacity and incompatible domains", Capacity);
        Test("unsupported storage forms", Unsupported);
        Coverage();
        Console.WriteLine("LISTGET_SUMMARY:" + JsonSerializer.Serialize(new {
            tests = passed.Count,
            direct = direct.Count,
            equivalent = equivalents.Count,
            pending = vectors.Count - direct.Count - equivalents.Count,
            passed
        }));
    }

    private static JsonNode Load(string file) => JsonNode.Parse(File.ReadAllText(
        Path.Combine(root, "fixtures/cds/listget/v1", file)))!;
    private static NpgsqlConnection Connect() => new(connectionString);
    private static void Exec(string sql)
    {
        using var c = Connect();
        c.Open();
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }
    private static void Check(bool condition, string label)
    {
        if (!condition)
            throw new Exception("LISTGET assertion: " + label);
    }
    private static void Equal(JsonNode? actual, JsonNode? expected, string label) =>
        Check(JsonNode.DeepEquals(actual, expected), label + ": " + actual + " != " + expected);
    private static void Test(string name, Action action)
    {
        action();
        passed.Add(name);
        Console.WriteLine("LISTGET_PASS:" + name);
    }
    private static JsonNode Ok(JsonNode response)
    {
        Check(response["status"]!.GetValue<string>() == "success" && response["diagnostics"]!.AsArray().Count == 0,
            response.ToJsonString());
        return response["output"]!;
    }
    private static void Error(JsonNode response, string code)
    {
        bool failed = code is "GENERATION_CHANGED" or "READ_FAILED" or "ROW_DECODE_FAILED" or "ROW_IDENTITY_INVALID";
        string family = code switch {
            "GENERATION_CHANGED" or "READ_FAILED" or "ROW_DECODE_FAILED" or "ROW_IDENTITY_INVALID"
                => "execution-failure",
            "INVALID_KEY" or "INVALID_CURSOR" or "INPUT_SHAPE" => "invalid-data",
            "INVALID_DEFINITION" => "definition-invalid",
            "INCOMPATIBLE_STORAGE" => "physical-incompatible",
            _ => "operation-not-allowed"
        };
        Equal(response, JsonSerializer.SerializeToNode(new {
            status = failed ? "failed" : "rejected",
            diagnostics = new[] { new { family, code = "CDS_LISTGET_" + code } }
        }), code);
    }
    private static JsonObject Key(string reference) => new() { ["reference"] = reference };

    private sealed class Harness
    {
        internal readonly JsonNode Layout;
        internal readonly PhysicalCatalog Catalog;
        internal readonly ListGetHost Host;
        internal readonly Dictionary<string, ReadTarget> Targets;
        internal readonly List<string> Sql = [];
        internal readonly List<int> Takes = [];
        internal Action AfterSelect = () => { };
        internal Action OnAuthorize = () => { };
        internal bool AllowGet = true, AllowList = true, AllowFields = true, Capability = true, Offline;
        internal string Tenant = "7";
        internal string? Product, Policy;
        internal Harness(string layout, bool narrow = false, JsonNode? definitions = null)
        {
            Layout = products["layouts"]![layout]!;
            Exec("CREATE SCHEMA IF NOT EXISTS commerce; DROP TABLE IF EXISTS commerce.products; "
                + "CREATE TABLE commerce.products(tenant_id integer NOT NULL,product_id bigint NOT NULL,"
                + "sku varchar(40) NOT NULL,name varchar(200) NOT NULL,price numeric NOT NULL,"
                + "rating smallint NOT NULL,description text,available boolean NOT NULL,released date NOT NULL,"
                + "cost numeric NOT NULL,internal_note text, PRIMARY KEY("
                + (layout == "simple" ? "product_id" : "tenant_id,product_id") + "))");
            foreach (var row in Layout["rows"]!.AsArray()) {
                using var c = Connect();
                c.Open();
                using var cmd = c.CreateCommand();
                string[] names = ["tenant_id", "product_id", "sku", "name", "price", "rating",
                    "description", "available", "released", "cost", "internal_note"];
                var expressions = new List<string>();
                for (int i = 0; i < names.Length; i++) {
                    string name = names[i];
                    var node = row!["values"]![name];
                    string cast = name switch {
                        "tenant_id" or "product_id" or "rating" => "bigint",
                        "price" or "cost" => "numeric",
                        "released" => "date",
                        "available" => "boolean",
                        _ => "text"
                    };
                    expressions.Add("@p" + i + "::" + cast);
                    cmd.Parameters.AddWithValue("p" + i, NpgsqlTypes.NpgsqlDbType.Text,
                        node is null ? DBNull.Value : node.ToString());
                }
                cmd.CommandText = "INSERT INTO commerce.products VALUES(" + string.Join(",", expressions) + ")";
                Check(cmd.ExecuteNonQuery() == 1, "insert shared product");
            }
            var dictionary = products["dictionary"]!.DeepClone();
            if (narrow) {
                var rating = dictionary["elements"]!.AsArray()
                    .Single(e => e!["id"]!.GetValue<string>() == "products.rating")!;
                rating["type"] = "INT8";
            }
            var catalog = new CatalogBuilder().BuildCatalog([new SourceSnapshot("fixture", "products",
                SourceFormat.Json, Encoding.UTF8.GetBytes(dictionary.ToJsonString()))]).Catalog!;
            var definition = definitions ?? Layout["definitions"]!;
            Catalog = new PhysicalCatalog(catalog, JsonSerializer.SerializeToElement(definition),
                new Dictionary<string, IPhysicalObservationProvider> {
                    ["observed"] = new PostgreSqlObservationProvider(Connect, "commercial-db",
                        "storage.products", "commerce", "products", 5)
                });
            Targets = new Dictionary<string, ReadTarget>();
            foreach (var (target, projection) in new[] { ("catalog", "catalog"), ("reports", "summary"),
                ("identified", "identified") }) {
                Targets.Add(target, new ReadTarget("products." + projection, 2, 3,
                    Layout["definitions"]!["projections"]![projection]!["fields"]!.AsArray()
                        .Select(f => f!.GetValue<string>())));
            }
            DbConnection Monitored()
            {
                if (Offline)
                    throw new PhysicalObservationException();
                return new TracedConnection(Connect(), command => {
                    Sql.Add(command.CommandText);
                    Takes.Add((int) command.Parameters["take"].Value!);
                    AfterSelect();
                });
            }
            Host = new ListGetHost(Catalog, new PostgreSqlRecordReader(Monitored), Targets,
                (_, operation, context) => {
                    OnAuthorize();
                    var equalities = ImmutableDictionary<string, string>.Empty.Add("tenant_id", Tenant);
                    if (Product is not null)
                        equalities = equalities.Add("product_id", Product);
                    return new ReadAccess((operation == "GET" ? AllowGet : AllowList)
                        && (Policy is null || context.PolicyRevision == Policy),
                        (AllowFields ? Layout["definitions"]!["physical"]!["observed"]!["columns"]!.AsArray()
                            .Select(c => c!["name"]!.GetValue<string>()) : ["sku"]).ToImmutableHashSet(),
                        Capability ? equalities : null);
                });
            Check(Catalog.Resolve().IsValid, Catalog.Resolve().ToJson());
        }
        internal JsonNode Call(string target, string operation, object? inputs = null, ReadContext? context = null) =>
            Invoke(JsonSerializer.SerializeToUtf8Bytes(new {
                target,
                operation,
                inputs = inputs ?? new {
                }
            }), context);
        internal JsonNode Invoke(byte[] bytes, ReadContext? context = null)
        {
            var response = Host.Invoke(bytes, context ?? Context);
            Console.WriteLine("LISTGET_EVIDENCE:" + JsonSerializer.Serialize(new {
                response
            }));
            return JsonNode.Parse(response.GetRawText())!;
        }
        internal JsonNode Typed(JsonNode row)
        {
            var components = new JsonArray();
            foreach (var k in Layout["definitions"]!["physical"]!["observed"]!["primaryKey"]!.AsArray()) {
                string name = k!.GetValue<string>();
                components.Add(new JsonObject {
                    ["field"] = name,
                    ["type"] = name == "tenant_id" ? "INT32" : "INT64",
                    ["value"] = row["values"]![name]!.DeepClone()
                });
            }
            return new JsonObject { ["components"] = components };
        }
    }

    private static void Values(string layout)
    {
        var h = new Harness(layout);
        var expected = h.Layout["rows"]!.AsArray().Where(r => r!["values"]!["tenant_id"]!.GetValue<string>() == "7")
            .OrderBy(r => long.Parse(r!["values"]!["product_id"]!.GetValue<string>(), CultureInfo.InvariantCulture))
            .ToArray();
        var seen = new List<JsonNode>();
        object inputs = new {
        };
        do {
            var page = Ok(h.Call("catalog", "LIST", inputs));
            Check(page["page"]!["limit"]!.GetValue<int>() == 2, "default limit");
            foreach (var record in page["records"]!.AsArray()) {
                var get = Ok(h.Call("catalog", "GET", new {
                    key = record!["key"]
                }));
                Equal(get["record"]!["values"], record["values"], "LIST/GET same values");
                Check(get["found"]!.GetValue<bool>(), "found");
                seen.Add(record["values"]!.DeepClone());
            }
            if (!page["page"]!["hasMore"]!.GetValue<bool>()) {
                Check(page["page"]!["next"] is null, "terminal page");
                break;
            }
            inputs = new {
                continuation = page["page"]!["next"]!.GetValue<string>()
            };
        } while (true);
        Check(seen.Count == expected.Length, "no duplicates/omissions");
        Check(h.Takes.All(t => t is 2 or 3), "GET at most two; LIST limit plus one");
        for (int i = 0; i < seen.Count; i++) {
            var values = new JsonObject();
            foreach (string name in h.Targets["catalog"].Fields)
                values.Add(name, expected[i]!["values"]![name]?.DeepClone());
            Equal(seen[i], values, "exact shared values, null/Unicode/decimal/history");
        }
        var missing = h.Typed(expected[0]!);
        missing["components"]!.AsArray()[^1]!["value"] = "999";
        Check(!Ok(h.Call("identified", "GET", new {
            key = missing
        }))["found"]!.GetValue<bool>(), "missing typed");
        var denied = h.Layout["rows"]!.AsArray().Single(r => r!["values"]!["tenant_id"]!.GetValue<string>() != "7")!;
        Check(!Ok(h.Call("identified", "GET", new {
            key = h.Typed(denied)
        }))["found"]!.GetValue<bool>(),
            "inaccessible indistinguishable");
        foreach (var row in expected)
            Check(Ok(h.Call("identified", "GET", new {
                key = h.Typed(row!)
            }))["found"]!.GetValue<bool>(), "typed GET");
        var identified = Ok(h.Call("identified", "LIST", new {
            limit = 100
        }));
        Check(identified["page"]!["limit"]!.GetValue<int>() == 3, "clamp");
        string sql = h.Sql.Last();
        Check(sql.Contains(" WHERE ") && sql.IndexOf(" WHERE ", StringComparison.Ordinal)
            < sql.IndexOf(" ORDER BY ", StringComparison.Ordinal) && sql.Contains(" LIMIT @take")
            && !sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase), "SQL bounded before paging");
        Check(Ok(h.Call("identified", "GET", new {
            key = h.Typed(expected[0]!)
        },
            new ReadContext("public", "public-1")))["found"]!.GetValue<bool>(), "no principal required");
    }

    private static void Bindings(string layout)
    {
        var h = new Harness(layout);
        var page = Ok(h.Call("catalog", "LIST"));
        string reference = page["records"]![0]!["key"]!["reference"]!.GetValue<string>();
        string cursor = page["page"]!["next"]!.GetValue<string>();
        foreach (string invalid in new[] { "", new string('x', 129) }) {
            h.Sql.Clear();
            Error(h.Call("catalog", "GET", new {
                key = Key(invalid)
            }), "INVALID_KEY");
            Check(h.Sql.Count == 0, "invalid reference: no row SELECT");
        }
        foreach (string unknown in new[] { "x", new string('x', 128), "rec-unissued",
            (reference.StartsWith('A') ? "B" : "A") + reference[1..], cursor }) {
            h.Sql.Clear();
            Equal(Ok(h.Call("catalog", "GET", new {
                key = Key(unknown)
            })),
                JsonNode.Parse("{\"found\":false}"), "unknown/tampered/wrong purpose");
            Check(h.Sql.Count == 0, "unusable reference: no row SELECT");
        }
        foreach (var context in new[] { new ReadContext("other-reader/company-7", "policy-1"),
            new ReadContext("commercial-reader/company-8", "policy-1"),
            Context with { PolicyRevision = "policy-2" } }) {
            h.Sql.Clear();
            Check(!Ok(h.Call("catalog", "GET", new {
                key = Key(reference)
            }, context))["found"]!.GetValue<bool>(),
                "identity/scope/policy binding");
            Error(h.Call("catalog", "LIST", new {
                continuation = cursor
            }, context), "INVALID_CURSOR");
            Check(h.Sql.Count == 0, "binding no SQL");
        }
        Error(h.Call("catalog", "LIST", new {
            continuation = cursor,
            limit = 1
        }), "INVALID_CURSOR");
        Error(h.Call("catalog", "GET", new {
            key = h.Typed(h.Layout["rows"]![0]!)
        }), "INVALID_KEY");
        var definitions = vectors[10]!["host"]!["definitions"]!;
        h.Host.ChangeAccess(h.Targets, () => { });
        h.Sql.Clear();
        Check(!Ok(h.Call("catalog", "GET", new {
            key = Key(reference)
        }))["found"]!.GetValue<bool>(),
            "policy epoch revokes earlier reference even after ABA");
        Error(h.Call("catalog", "LIST", new {
            continuation = cursor
        }), "INVALID_CURSOR");
        Check(h.Sql.Count == 0, "revoked evidence no SQL");
        var other = new Harness(layout, definitions: definitions);
        Check(!Ok(other.Call("catalog", "GET", new {
            key = Key(reference)
        }))["found"]!.GetValue<bool>(),
            "host recreation revokes references");
        Check(other.Sql.Count == 0, "recreated host no row SQL");
        var catalog = Ok(other.Call("catalog", "LIST"));
        string r = catalog["records"]![0]!["key"]!["reference"]!.GetValue<string>();
        other.Sql.Clear();
        Check(!Ok(other.Call("reports", "GET", new {
            key = Key(r)
        }))["found"]!.GetValue<bool>(), "projection binding");
        Check(other.Sql.Count == 0, "projection mismatch no SQL");
        var reports = Ok(other.Call("reports", "LIST"));
        Check(Ok(other.Call("reports", "GET", new {
            key = reports["records"]![0]!["key"]
        }))["found"]!.GetValue<bool>(),
            "alternate GET projection");
        // The preceding harness recreated the same table; use its current issued evidence.
        string fresh = reports["records"]![0]!["key"]!["reference"]!.GetValue<string>();
        other.Catalog.Invalidate();
        Error(other.Call("reports", "GET", new {
            key = Key(fresh)
        }), "GENERATION_EXPIRED");
        var p = Ok(other.Call("catalog", "LIST"));
        string next = p["page"]!["next"]!.GetValue<string>();
        other.Catalog.Invalidate();
        Error(other.Call("catalog", "LIST", new {
            continuation = next
        }), "GENERATION_EXPIRED");
    }

    private static void Authorization(string layout)
    {
        var h = new Harness(layout);
        h.AllowGet = false;
        Error(h.Call("catalog", "GET", new {
            key = Key("unknown")
        }), "UNAVAILABLE");
        Check(h.Sql.Count == 0, "independent GET denial");
        Ok(h.Call("catalog", "LIST"));
        h.Sql.Clear();
        h.AllowGet = true;
        h.AllowFields = false;
        Error(h.Call("catalog", "LIST"), "UNAVAILABLE");
        h.AllowFields = true;
        h.Capability = false;
        Error(h.Call("catalog", "LIST"), "CAPABILITY_UNAVAILABLE");
        h.Capability = true;
        Error(h.Call("reports", "GET", new {
            key = Key("unknown")
        }), "UNAVAILABLE");
        h.Host.ChangeAccess(h.Targets.Where(p => p.Key != "reports").ToDictionary(), () => { });
        Error(h.Call("reports", "LIST"), "UNAVAILABLE");
        Error(h.Call("unknown", "LIST"), "UNAVAILABLE");
        h.Host.ChangeAccess(new Dictionary<string, ReadTarget>(), () => { });
        Error(h.Call("catalog", "LIST"), "UNAVAILABLE");
        Check(h.Sql.Count == 0, "denials no row SELECT");
    }

    private static void Empty(string layout)
    {
        var h = new Harness(layout);
        h.Tenant = "999";
        Check(Ok(h.Call("catalog", "LIST"))["records"]!.AsArray().Count == 0, "empty authorized set");
        h.Tenant = "7";
        h.Product = "2";
        var page = Ok(h.Call("reports", "LIST"));
        Check(page["records"]!.AsArray().Count == 1, "derived integer conjunction before limit");
        Check(page["records"]![0]!["values"]!.AsObject().Count == 2, "derived exact fields");
        Check(h.Sql.Last().Contains("\"product_id\" = @"), "second SQL predicate");
        h.Product = null;
        var one = Ok(h.Call("catalog", "LIST", new {
            limit = 1
        }));
        Check(one["records"]!.AsArray().Count == 1 && one["page"]!["limit"]!.GetValue<int>() == 1, "limit one");
        string cursor = one["page"]!["next"]!.GetValue<string>();
        Exec("DELETE FROM commerce.products WHERE tenant_id=7 AND product_id>2");
        var empty = Ok(h.Call("catalog", "LIST", new {
            continuation = cursor,
            limit = 1
        }));
        Check(empty["records"]!.AsArray().Count == 0 && empty["page"]!["next"] is null, "after last SQL empty");
    }

    private static void Metadata(string layout)
    {
        var h = new Harness(layout);
        Exec("ALTER TABLE commerce.products ADD COLUMN private_new text");
        Error(h.Call("catalog", "LIST"), "GENERATION_CHANGED");
        h.Catalog.Invalidate();
        var page = Ok(h.Call("catalog", "LIST"));
        Check(page["records"]![0]!["values"]!.AsObject().Count == h.Targets["catalog"].Fields.Count, "no expansion");
        h.Offline = true;
        Error(h.Call("catalog", "LIST"), "READ_FAILED");
        h.Offline = false;
        h.AfterSelect = () => throw new NpgsqlException("private provider detail");
        Error(h.Call("catalog", "LIST"), "READ_FAILED");
        h.AfterSelect = () => { };
        Exec("ALTER TABLE commerce.products DROP COLUMN rating");
        h.Catalog.Invalidate();
        Error(h.Call("catalog", "LIST"), "INVALID_DEFINITION");
    }

    private static void Decode(string layout)
    {
        var h = new Harness(layout);
        Exec("UPDATE commerce.products SET price='NaN' WHERE tenant_id=7 AND product_id=10");
        Error(h.Call("catalog", "LIST"), "ROW_DECODE_FAILED");
        Exec("UPDATE commerce.products SET price=15.5,released='infinity' WHERE tenant_id=7 AND product_id=10");
        Error(h.Call("catalog", "LIST"), "ROW_DECODE_FAILED");
        var narrow = new Harness(layout, narrow: true);
        Exec("UPDATE commerce.products SET rating=128 WHERE tenant_id=7 AND product_id=10");
        Error(narrow.Call("catalog", "LIST"), "ROW_DECODE_FAILED");
        var exact = new Harness(layout);
        string huge = "1234567890123456789012345678901234567890.12345678901234567890123456789";
        Exec("UPDATE commerce.products SET price=" + huge + " WHERE tenant_id=7 AND product_id=2");
        var p = Ok(exact.Call("catalog", "LIST"));
        Check(p["records"]![0]!["values"]!["price"]!.GetValue<string>() == huge, "beyond System.Decimal exactness");
    }

    private static void Revocation(string layout)
    {
        var h = new Harness(layout);
        h.AfterSelect = () => h.Host.ChangeAccess(h.Targets, () => h.AllowGet = false);
        var key = h.Typed(h.Layout["rows"]![0]!);
        Error(h.Call("identified", "GET", new {
            key
        }), "UNAVAILABLE");
        h.AfterSelect = () => { };
        h.AllowGet = true;
        h.AfterSelect = () => h.Host.ChangeAccess(h.Targets, () => h.AllowList = false);
        Error(h.Call("catalog", "LIST"), "UNAVAILABLE");
        h.AllowList = true;
        h.AfterSelect = () => h.Host.ChangeAccess(new Dictionary<string, ReadTarget>(), () => { });
        Error(h.Call("reports", "LIST"), "GENERATION_CHANGED");
        h.Host.ChangeAccess(h.Targets, () => { });
        h.AfterSelect = () => h.Host.ChangeAccess(h.Targets, () => h.Policy = "policy-2");
        Error(h.Call("catalog", "LIST"), "UNAVAILABLE");
    }

    private static void Concurrent(string layout)
    {
        var h = new Harness(layout);
        var snapshot = h.Catalog.Current!;
        long generation = h.Catalog.Generation;
        using var selected = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        h.AfterSelect = () => {
            selected.Set();
            Check(resume.Wait(TimeSpan.FromSeconds(10)), "resume deadline");
        };
        var task = Task.Run(() => h.Call("catalog", "LIST"));
        try {
            Check(selected.Wait(TimeSpan.FromSeconds(10)), "actual SELECT entered");
            using (var c = Connect()) {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SET lock_timeout='150ms'; ALTER TABLE commerce.products ADD COLUMN blocked text";
                try {
                    cmd.ExecuteNonQuery();
                    throw new Exception("DDL should have timed out");
                } catch (PostgresException e) { Check(e.SqlState == "55P03", "ACCESS SHARE blocks DDL"); }
            }
            h.Catalog.Invalidate();
            Check(h.Catalog.Generation > generation, "generation advanced concurrently");
            bool invoked = false;
            try {
                h.Catalog.PublishRead(snapshot, () => invoked = true);
                throw new Exception("stale snapshot accepted");
            } catch (InvalidOperationException) { }
            Check(!invoked, "stale callback never runs");
        } finally { resume.Set(); }
        Check(task.Wait(TimeSpan.FromSeconds(10)), "read finishes");
        Error(task.Result, "GENERATION_CHANGED");
        h.AfterSelect = () => { };
        Exec("ALTER TABLE commerce.products ADD COLUMN after_read text");
        h.Catalog.Invalidate();
        Ok(h.Call("catalog", "LIST"));
    }

    private static void Inputs()
    {
        var h = new Harness("composite");
        var indexes = Enumerable.Range(14, 8).Concat(Enumerable.Range(29, 12)).Concat(new[] { 49, 50, 51, 85 });
        foreach (int i in indexes) {
            var vector = vectors[i]!;
            var request = vector["request"]!.DeepClone();
            request["target"] = vector["host"]?["projection"]?.GetValue<string>() == "products.identified"
                ? "identified" : "catalog";
            h.AllowList = i != 50;
            h.Sql.Clear();
            Equal(h.Invoke(Encoding.UTF8.GetBytes(request.ToJsonString())), vector["expected"]!["response"],
                vector["id"]!.GetValue<string>());
            Check(h.Sql.Count == 0, "negative dispatch no SQL");
            direct.Add(vector["id"]!.GetValue<string>());
        }
        h.AllowList = true;
        foreach (string raw in new[] {
            "{\"target\":\"catalog\",\"operation\":\"LIST\",\"inputs\":{},\"inputs\":{}}",
            "{\"target\":\"catalog\",\"operation\":\"LIST\",\"inputs\":{\"x\":\"\\ud800\"}}",
            "{\"target\":\"catalog\",\"operation\":\"LIST\",\"inputs\":{\"limit\":1e129}}" })
            Error(h.Invoke(Encoding.UTF8.GetBytes(raw)), "INPUT_SHAPE");
        Ok(h.Invoke(Encoding.UTF8.GetBytes(
            "{\"target\":\"catalog\",\"operation\":\"LIST\",\"inputs\":{\"limit\":2e0}}")));
        h.Sql.Clear();
        foreach (object? key in new object?[] { null, new { components = new object?[] { null, null } },
            new { components = new object[] { 1, 2 } } })
            Error(h.Call("identified", "GET", new {
                key
            }), "INVALID_KEY");
        Check(h.Sql.Count == 0, "malformed input no SQL");
    }

    private static void Conceal()
    {
        var h = new Harness("composite");
        Exec("ALTER TABLE commerce.products DROP COLUMN rating");
        h.Catalog.Invalidate();
        h.AllowFields = false;
        Error(h.Call("catalog", "LIST"), "UNAVAILABLE");
        h.AllowFields = true;
        Error(h.Call("catalog", "LIST"), "INVALID_DEFINITION");
        Check(h.Sql.Count == 0, "conceal definition no SQL");
    }

    private static void EmptyFence()
    {
        var h = new Harness("composite");
        int calls = 0;
        h.OnAuthorize = () => {
            if (++calls == 2)
                h.Catalog.Invalidate();
        };
        Error(h.Call("catalog", "GET", new {
            key = Key("unknown-reference")
        }), "GENERATION_CHANGED");
        Check(h.Sql.Count == 0, "empty GET fence without row SQL");
        bool invoked = false;
        try {
            h.Catalog.PublishRead<string>(null!, () => { invoked = true; return ""; });
            throw new Exception("null snapshot accepted");
        } catch (InvalidOperationException) { }
        Check(!invoked, "null callback never runs");
    }

    private static void Capacity()
    {
        var h = new Harness("composite");
        try {
            _ = new ReadTarget("products.catalog", 2, 0, h.Targets["catalog"].Fields);
            throw new Exception("invalid server limits accepted");
        } catch (ArgumentOutOfRangeException) { }
        var keys = new List<string> { "tenant_id", "product_id" };
        for (int i = 3; i <= 7; i++) {
            Exec("ALTER TABLE commerce.products ADD COLUMN key_" + i + " integer NOT NULL DEFAULT 0");
            keys.Add("key_" + i);
        }
        Exec("ALTER TABLE commerce.products DROP CONSTRAINT products_pkey, ADD PRIMARY KEY("
            + string.Join(",", keys) + ")");
        h.Catalog.Invalidate();
        var page = Ok(h.Call("catalog", "LIST"));
        string reference = page["records"]![0]!["key"]!["reference"]!.GetValue<string>();
        Check(reference.Length == 128 && page["page"]!["next"]!.GetValue<string>().Length == 128,
            "7-component capacity");
        Check(Ok(h.Call("catalog", "GET", new {
            key = Key(reference)
        }))["found"]!.GetValue<bool>(), "7-key GET");
        Ok(h.Call("catalog", "LIST", new {
            continuation = page["page"]!["next"]!.GetValue<string>()
        }));
        Exec("ALTER TABLE commerce.products ADD COLUMN key_8 integer NOT NULL DEFAULT 0; "
            + "ALTER TABLE commerce.products DROP CONSTRAINT products_pkey, "
            + "ADD PRIMARY KEY(tenant_id,product_id,key_3,key_4,key_5,key_6,key_7,key_8)");
        h.Catalog.Invalidate();
        h.Sql.Clear();
        Error(h.Call("catalog", "LIST"), "CAPABILITY_UNAVAILABLE");
        Check(h.Sql.Count == 0, "8-key no row SQL");
        var missing = new Harness("composite");
        Exec("ALTER TABLE commerce.products DROP CONSTRAINT products_pkey");
        missing.Catalog.Invalidate();
        Error(missing.Call("catalog", "LIST"), "CAPABILITY_UNAVAILABLE");
        var narrow = new Harness("composite");
        Exec("DELETE FROM commerce.products; ALTER TABLE commerce.products ALTER COLUMN product_id TYPE smallint");
        narrow.Catalog.Invalidate();
        Error(narrow.Call("catalog", "LIST"), "INCOMPATIBLE_STORAGE");
    }

    private static void Unsupported()
    {
        var h = new Harness("composite");
        Exec("CREATE TABLE commerce.child_products() INHERITS (commerce.products)");
        Error(h.Call("catalog", "LIST"), "CAPABILITY_UNAVAILABLE");
        Check(h.Sql.Count == 0, "inherited table rejected before row SELECT");
        Exec("DROP TABLE commerce.child_products");
        Exec("ALTER TABLE commerce.products ALTER COLUMN sku TYPE character(40)");
        h.Catalog.Invalidate();
        Error(h.Call("catalog", "LIST"), "CAPABILITY_UNAVAILABLE");
        Check(h.Sql.Count == 0, "fixed CHAR padding unsupported before row SELECT");
    }

    private static void Coverage()
    {
        // Each equivalence points to an executed PostgreSQL scenario, not to Java or the finite oracle.
        void Add(string evidence, params int[] indexes)
        {
            Check(passed.Contains(evidence), "coverage evidence executed: " + evidence);
            foreach (int i in indexes)
                equivalents.Add(vectors[i]!["id"]!.GetValue<string>(), evidence);
        }
        Add("composite: values, paging and GET", 0, 1, 2, 3, 4, 12, 13, 22, 23, 24, 25, 68, 74, 76);
        Add("simple: values, paging and GET", 11, 27);
        Add("composite: bindings and reference input", 5, 7, 8, 9, 10, 41, 42, 58, 59);
        Add("composite: values, paging and GET", 6);
        Add("composite: authorization and registry", 43, 44, 45, 46, 47, 48, 52, 75, 78, 79, 80, 86);
        Add("composite: empty SQL and derived restrictions", 26, 28, 71, 72, 77, 84);
        Add("storage capacity and incompatible domains", 53, 56);
        Add("composite: metadata rebuild and provider failure", 54, 62, 66, 67);
        Add("authorization before definition failure", 55);
        Add("composite: concurrent invalidation and DDL", 60);
        Add("composite: in-flight revocation and composition", 61, 70, 83, 87);
        Add("composite: scalar failure discards page", 63);
        Add("empty GET final fence", 69);
        var pending = new Dictionary<int, string> {
            [57] = "The host resolves its own catalog; externally supplied stale snapshots are not an input.",
            [64] = "Physical NOT NULL prevents this contradiction; faulty-provider null injection not executed.",
            [65] = "Physical PK prevents duplicate identities; faulty-provider duplicate injection not executed.",
            [73] = "Invalid limits rejected at ReadTarget construction (tested), outside the neutral envelope.",
            [81] = "Application validates module dependencies before registration; no general module engine.",
            [82] = "General module source/dependency manifest validation requires application integration."
        };
        Check(direct.Count == 24 && equivalents.Count == 58 && pending.Count == 6, "88-vector accounting");
        var coverage = vectors.Select((v, i) => new {
            id = v!["id"]!.GetValue<string>(),
            status = direct.Contains(v["id"]!.GetValue<string>()) ? "N"
                : equivalents.ContainsKey(v["id"]!.GetValue<string>()) ? "E" : "U",
            evidence = direct.Contains(v["id"]!.GetValue<string>()) ? "shared request vectors"
                : equivalents.GetValueOrDefault(v["id"]!.GetValue<string>()) ?? pending[i]
        }).ToArray();
        Console.WriteLine("LISTGET_COVERAGE:" + JsonSerializer.Serialize(coverage));
    }

    private sealed class TracedConnection(DbConnection inner, Action<DbCommand> after) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString
        {
            get => inner.ConnectionString; set => inner.ConnectionString = value;
        }
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        public override void Open() => inner.Open();
        public override void Close() => inner.Close();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            inner.BeginTransaction(isolationLevel);
        protected override DbCommand CreateDbCommand() => new TracedCommand(inner.CreateCommand(), after);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
    private sealed class TracedCommand(DbCommand inner, Action<DbCommand> after) : DbCommand
    {
        [AllowNull]
        public override string CommandText
        {
            get => inner.CommandText; set => inner.CommandText = value;
        }
        public override int CommandTimeout
        {
            get => inner.CommandTimeout; set => inner.CommandTimeout = value;
        }
        public override CommandType CommandType
        {
            get => inner.CommandType; set => inner.CommandType = value;
        }
        public override bool DesignTimeVisible
        {
            get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value;
        }
        public override UpdateRowSource UpdatedRowSource
        {
            get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value;
        }
        protected override DbConnection? DbConnection
        {
            get => inner.Connection; set => inner.Connection = value;
        }
        protected override DbTransaction? DbTransaction
        {
            get => inner.Transaction; set => inner.Transaction = value;
        }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        public override void Cancel() => inner.Cancel();
        public override int ExecuteNonQuery() => inner.ExecuteNonQuery();
        public override object? ExecuteScalar() => inner.ExecuteScalar();
        public override void Prepare() => inner.Prepare();
        protected override DbParameter CreateDbParameter() => inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var result = inner.ExecuteReader(behavior);
            if (CommandText.Contains(" FROM ONLY ", StringComparison.Ordinal)) {
                try {
                    after(this);
                } catch { result.Dispose(); throw; }
            }
            return result;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}