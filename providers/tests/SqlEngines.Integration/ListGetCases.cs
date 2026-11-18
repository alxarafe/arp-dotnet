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
using Alxarafe.Arp.Cds.Sql;
using Alxarafe.Arp.Dictionary;

internal static class ListGetCases
{
    private static readonly ReadContext Context = new("commercial-reader/company-7", "policy-1");
    private static JsonNode products = null!;
    private static JsonArray vectors = null!;
    private static string root = "";
    private static readonly List<string> passed = [];
    private static readonly HashSet<string> direct = [];
    private static readonly Dictionary<string, string> equivalents = new(StringComparer.Ordinal);

    internal static void Run()
    {
        root = Environment.GetEnvironmentVariable("ARP_LISTGET_CONTRACT")!;
        products = Load("products.json");
        vectors = Load("cases.json")["cases"]!.AsArray();
        Engine.VerifyOwnership();
        foreach (string layout in new[] { "simple", "composite" }) {
            Test(layout + ": exact values and pages", () => Values(layout));
            Test(layout + ": references and cursors", () => Bindings(layout));
            Test(layout + ": authorization", () => Authorization(layout));
            Test(layout + ": SQL restrictions and derived projection", () => Empty(layout));
            Test(layout + ": metadata and provider errors", () => Metadata(layout));
            Test(layout + ": revocation", () => Revocation(layout));
            Test(layout + ": invalidation and external DDL", () => Concurrent(layout));
            Test(layout + ": decoding failure discards page", () => Decode(layout));
        }
        if (Engine.Name != "postgresql")
            Test("complete metadata authority", () => { _ = new Harness("simple"); Engine.Permissions(); });
        if (Engine.Name == "sqlserver")
            Test("native DECIMAL and exact variant storage", NativeDecimal);
        Test("key capacity and incompatible storage", Capacity);
        Test("external DDL without explicit invalidation", ExternalChange);
        Test("shared negative request vectors", Inputs);
        Test("authorization before definitions", Conceal);
        Test("empty GET final fence", EmptyFence);
        Test("DDL protection after data commit", PublicationDdl);
        Test("transaction completion failures", Completion);
        Coverage();
        Console.WriteLine("ENGINE_SUMMARY:" + JsonSerializer.Serialize(new {
            engine = Engine.Name,
            version = Engine.Version,
            tests = passed.Count,
            direct = direct.Order().ToArray(),
            passed
        }));
    }

    private static JsonNode Load(string file) => JsonNode.Parse(File.ReadAllText(
        Path.Combine(root, "fixtures/cds/listget/v1", file)))!;
    private static DbConnection Connect() => Engine.Connect();
    private static void Exec(string sql) => Engine.Exec(sql);
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
        internal bool FailCommit, FailClose, FailGuardRollback;
        internal int CommitAttempts, Aborted;
        internal Action AfterCommit = () => { };
        internal string Tenant = "7";
        internal string? Product, Policy;
        internal Harness(string layout, bool narrow = false, JsonNode? definitions = null, bool inherited = false,
            int timeoutSeconds = 5)
        {
            Layout = products["layouts"]![layout]!;
            Engine.Seed(Layout, layout);
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
                    ["observed"] = Engine.Observer(Connect)
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
                }, () => {
                    CommitAttempts++;
                    if (FailCommit)
                        throw new TestProviderException("private commit failure");
                }, () => AfterCommit(), () => Aborted++, () => {
                    if (FailClose)
                        throw new TestProviderException("private close failure");
                }, () => {
                    if (FailGuardRollback)
                        throw new TestProviderException("private guard rollback failure");
                });
            }
            IRecordReader reader = Engine.Reader(Monitored, timeoutSeconds);
            Host = new ListGetHost(Catalog, reader, Targets,
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
            < sql.IndexOf(" ORDER BY ", StringComparison.Ordinal) && sql.Contains("@take", StringComparison.Ordinal),
            "SQL bounded before paging");
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
        Check(h.Sql.Last().Replace("`", "").Replace("\"", "").Replace("[", "").Replace("]", "")
            .Contains("product_id = @"), "second SQL predicate");
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
        Exec("ALTER TABLE commerce.products ADD private_new text");
        Error(h.Call("catalog", "LIST"), "GENERATION_CHANGED");
        h.Catalog.Invalidate();
        var page = Ok(h.Call("catalog", "LIST"));
        Check(page["records"]![0]!["values"]!.AsObject().Count == h.Targets["catalog"].Fields.Count, "no expansion");
        h.Offline = true;
        Error(h.Call("catalog", "LIST"), "READ_FAILED");
        h.Offline = false;
        h.AfterSelect = () => throw new TestProviderException("private provider detail");
        Error(h.Call("catalog", "LIST"), "READ_FAILED");
        h.AfterSelect = () => { };
        Exec("ALTER TABLE commerce.products DROP COLUMN rating");
        h.Catalog.Invalidate();
        Error(h.Call("catalog", "LIST"), "INVALID_DEFINITION");
    }

    private static void Decode(string layout)
    {
        var h = new Harness(layout, narrow: true);
        Exec("UPDATE commerce.products SET rating=128 WHERE tenant_id=7 AND product_id=10");
        Error(h.Call("catalog", "LIST"), "ROW_DECODE_FAILED");
        if (Engine.Name is "mysql" or "mariadb") {
            Exec("UPDATE commerce.products SET rating=5,available=2 WHERE product_id=10");
            Error(h.Call("catalog", "LIST"), "ROW_DECODE_FAILED");
        }
        if (Engine.Name == "sqlserver") {
            Exec("UPDATE commerce.products SET rating=5,price=CAST('15.5' AS varchar(10)) WHERE product_id=10");
            Error(h.Call("catalog", "LIST"), "ROW_DECODE_FAILED");
            Exec("UPDATE commerce.products SET price=CAST(1.25 AS float) WHERE product_id=10");
            Error(h.Call("catalog", "LIST"), "ROW_DECODE_FAILED");
        }
    }

    private static void NativeDecimal()
    {
        var h = new Harness("simple");
        Exec("DELETE FROM commerce.products WHERE product_id NOT IN (2,10)");
        Exec("ALTER TABLE commerce.products ADD native_price decimal(38,18)");
        Exec("UPDATE commerce.products SET native_price=CONVERT(decimal(38,18),price)");
        Exec("ALTER TABLE commerce.products ALTER COLUMN native_price decimal(38,18) NOT NULL");
        Exec("ALTER TABLE commerce.products DROP COLUMN price");
        Exec("EXEC sp_rename 'commerce.products.native_price','price','COLUMN'");
        h.Catalog.Invalidate();
        var records = Ok(h.Call("catalog", "LIST"))["records"]!.AsArray();
        Check(records.Count == 2, "native DECIMAL rows");
        Equal(records[0]!["values"]!["price"], JsonValue.Create("7.95"), "native DECIMAL canonical exact");
        Equal(records[1]!["values"]!["price"], JsonValue.Create("12.5"), "native DECIMAL canonical scale");
        Check(Ok(h.Call("catalog", "GET", new {
            key = records[0]!["key"]
        }))["found"]!.GetValue<bool>(),
            "native DECIMAL GET");
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
            Engine.ExpectBlockedDdl("blocked");
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
        Exec("ALTER TABLE commerce.products ADD after_read text");
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
        var h = new Harness("simple");
        string drop = Engine.Name is "mysql" or "mariadb" ? "DROP PRIMARY KEY" : "DROP CONSTRAINT products_pkey";
        Exec("ALTER TABLE commerce.products " + drop);
        h.Catalog.Invalidate();
        Error(h.Call("catalog", "LIST"), "CAPABILITY_UNAVAILABLE");
        h = new Harness("simple");
        Exec("DELETE FROM commerce.products");
        Exec(Engine.Name switch {
            "postgresql" => "ALTER TABLE commerce.products ALTER COLUMN product_id TYPE smallint",
            "sqlserver" => "ALTER TABLE commerce.products DROP CONSTRAINT products_pkey; "
                + "ALTER TABLE commerce.products ALTER COLUMN product_id smallint NOT NULL; "
                + "ALTER TABLE commerce.products ADD CONSTRAINT products_pkey PRIMARY KEY(product_id)",
            _ => "ALTER TABLE commerce.products MODIFY product_id smallint NOT NULL"
        });
        h.Catalog.Invalidate();
        Error(h.Call("catalog", "LIST"), "INCOMPATIBLE_STORAGE");
    }

    private static void ExternalChange()
    {
        var h = new Harness("composite");
        Exec("ALTER TABLE commerce.products ADD external_change int");
        Error(h.Call("catalog", "LIST"), "GENERATION_CHANGED");
        h.Catalog.Invalidate();
        Ok(h.Call("catalog", "LIST"));
    }

    private static void PublicationDdl()
    {
        foreach (string layout in new[] { "simple", "composite" }) {
            var h = new Harness(layout);
            int blocked = 0;
            h.AfterCommit = () => {
                // This hook runs after the server committed the DATA transaction, before final host publication.
                // No catalog invalidation occurs: only real external DDL can expose this race.
                Engine.ExpectBlockedDdl("escaped_publication_guard");
                blocked++;
                Check(blocked == h.CommitAttempts, "DDL must remain blocked AFTER data commit until publication");
            };
            Ok(h.Call("catalog", "LIST"));
            Ok(h.Call("identified", "GET", new {
                key = h.Typed(h.Layout["rows"]![0]!)
            }));
            h.Tenant = "999";
            Ok(h.Call("catalog", "LIST"));
            Check(blocked == 3, "LIST, GET and empty SQL page retain publication protection");
            h.AfterCommit = () => { };
            // Once the invocation has published and disposed its guard, the same DDL must succeed.
            Exec("ALTER TABLE commerce.products ADD escaped_publication_guard text");
            h.Catalog.Invalidate();
            h.Tenant = "7";
            Ok(h.Call("catalog", "LIST"));
        }
    }

    private static void Completion()
    {
        foreach (string layout in new[] { "simple", "composite" }) {
            var h = new Harness(layout);
            h.FailCommit = true;
            Error(h.Call("catalog", "LIST"), "READ_FAILED");
            Error(h.Call("identified", "GET", new {
                key = h.Typed(h.Layout["rows"]![0]!)
            }), "READ_FAILED");
            h.Tenant = "999";
            Error(h.Call("catalog", "LIST"), "READ_FAILED");
            Check(h.CommitAttempts == 3 && h.Aborted == 3, "failed real SQL transactions disposed/rolled back");
            h.FailCommit = false;
            h.Tenant = "7";
            Ok(h.Call("catalog", "LIST"));
            h.FailClose = true;
            Error(h.Call("catalog", "LIST"), "READ_FAILED");
            h.FailClose = false;
            h.AfterCommit = () => h.FailGuardRollback = true;
            Error(h.Call("catalog", "LIST"), "READ_FAILED");
            h.FailGuardRollback = false;
            h.AfterCommit = () => h.Catalog.Invalidate();
            Error(h.Call("catalog", "LIST"), "GENERATION_CHANGED");
            h.AfterCommit = () => h.Host.ChangeAccess(h.Targets, () => h.AllowList = false);
            Error(h.Call("catalog", "LIST"), "UNAVAILABLE");
            h.AfterCommit = () => { };
            h.AllowList = true;
            Ok(h.Call("catalog", "LIST"));
        }
    }

    private static void Coverage()
    {
        // Each equivalence points to an executed engine scenario, not to Java or the finite oracle.
        void Add(string evidence, params int[] indexes)
        {
            Check(passed.Contains(evidence), "coverage evidence executed: " + evidence);
            foreach (int i in indexes)
                equivalents.Add(vectors[i]!["id"]!.GetValue<string>(), evidence);
        }
        Add("composite: exact values and pages", 0, 1, 2, 3, 4, 12, 13, 22, 23, 24, 25, 68, 74, 76);
        Add("simple: exact values and pages", 11, 27);
        Add("composite: references and cursors", 5, 7, 8, 9, 10, 41, 42, 58, 59);
        Add("composite: exact values and pages", 6);
        Add("composite: authorization", 43, 44, 45, 46, 47, 48, 52, 75, 78, 79, 80, 86);
        Add("composite: SQL restrictions and derived projection", 26, 28, 71, 72, 77, 84);
        Add("key capacity and incompatible storage", 53, 56);
        Add("composite: metadata and provider errors", 54, 62, 66, 67);
        Add("authorization before definitions", 55);
        Add("composite: invalidation and external DDL", 60);
        Add("composite: revocation", 61, 70, 83, 87);
        Add("composite: decoding failure discards page", 63);
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
            evidence = direct.Contains(v["id"]!.GetValue<string>()) ? "shared negative request vectors"
                : equivalents.GetValueOrDefault(v["id"]!.GetValue<string>()) ?? pending[i]
        }).ToArray();
        Console.WriteLine("LISTGET_COVERAGE:" + JsonSerializer.Serialize(coverage));
    }

    private sealed class TracedConnection(DbConnection inner, Action<DbCommand> after,
        Action? beforeCommit = null, Action? afterCommit = null, Action? abort = null, Action? close = null,
        Action? beforeRollback = null)
        : DbConnection
    {
        private bool selected;
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
            new TracedTransaction(inner.BeginTransaction(isolationLevel),
                () => { if (selected) beforeCommit?.Invoke(); },
                () => { if (selected) afterCommit?.Invoke(); },
                () => { if (selected) abort?.Invoke(); }, () => beforeRollback?.Invoke());
        protected override DbCommand CreateDbCommand() => new TracedCommand(inner.CreateCommand(), command => {
            selected = true;
            after(command);
        });
        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                inner.Dispose();
                if (selected)
                    close?.Invoke();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TracedTransaction(DbTransaction inner, Action before, Action after, Action abort,
        Action beforeRollback)
        : DbTransaction
    {
        internal DbTransaction Inner => inner;
        private bool committed;
        public override IsolationLevel IsolationLevel => inner.IsolationLevel;
        protected override DbConnection? DbConnection => inner.Connection;
        public override void Commit()
        {
            before();
            inner.Commit();
            committed = true;
            after();
        }
        public override void Rollback()
        {
            beforeRollback();
            inner.Rollback();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                if (!committed)
                    abort();
                inner.Dispose();
            }
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
            get => inner.Transaction;
            set => inner.Transaction = value is TracedTransaction traced ? traced.Inner : value;
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
            if (Parameters.Contains("take") && CommandText.Contains(" ORDER BY ", StringComparison.Ordinal)) {
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
internal sealed class TestProviderException(string message) : DbException(message);