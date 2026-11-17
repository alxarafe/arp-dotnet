using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alxarafe.Arp.Cds.Physical;
using Alxarafe.Arp.Dictionary;
using Npgsql;

if (args.Length == 1 && args[0] is "seed" or "recover") {
    PersistenceCases.Run(args[0]);
    return;
}

string root = Environment.GetEnvironmentVariable("ARP_CONTRACT_PATH") ?? throw new Exception("Contract path required");
string password = Environment.GetEnvironmentVariable("ARP_DB_PASSWORD") ?? throw new Exception("Owned DB required");
var settings = new NpgsqlConnectionStringBuilder {
    Host = Environment.GetEnvironmentVariable("ARP_DB_HOST") ?? "db",
    Port = 5432,
    Database = "arpcds",
    Username = "arpcds",
    Password = password,
    Timeout = 3,
    CommandTimeout = 5,
    Pooling = false
};
const string Scope = "arp_scope";
using var admin = new NpgsqlConnection(settings.ConnectionString);
admin.Open();
void Exec(string sql) { using var c = new NpgsqlCommand(sql, admin); c.ExecuteNonQuery(); }
string Scalar(string sql) { using var c = new NpgsqlCommand(sql, admin); return Convert.ToString(c.ExecuteScalar())!; }
void Check(bool ok, string label) { if (!ok) throw new Exception("Assertion: " + label); }
void Equal(JsonNode? actual, JsonNode? expected, string label) => Check(JsonNode.DeepEquals(actual, expected), label);
JsonNode Node(JsonElement element) => JsonNode.Parse(element.GetRawText())!;
JsonNode Load(string path) => JsonNode.Parse(File.ReadAllText(Path.Combine(root, path)))!;
Check(Scalar("SELECT current_database()") == "arpcds", "owned database guard");
Check(Scalar("SELECT token FROM public.__arp_cds_test_owner")
    == Environment.GetEnvironmentVariable("ARP_CDS_RUN_TOKEN"),
    "owned run nonce required before DDL");
Exec("CREATE SCHEMA " + Scope + "; SET search_path TO " + Scope);
Exec("CREATE TABLE sentinel(marker text); INSERT INTO sentinel VALUES ('unchanged')");
string setup = File.ReadAllText(Path.Combine(root, "examples/demo/sql/postgresql.sql"));
Exec(setup);
var engine = Load("fixtures/cds/read/v1/database-cases.json")["engines"]!.AsArray()
    .Single(x => x!["engine"]!.GetValue<string>() == "postgresql")!;
var vector = Load("fixtures/cds/read/v1/cases.json")["cases"]!.AsArray()
    .Single(c => c!["id"]!.GetValue<string>() == engine["resolutionCase"]!.GetValue<string>())!;
var definitions = JsonSerializer.SerializeToElement(vector["input"]);
string original = definitions.GetRawText();
var dictionary = new CatalogBuilder().BuildCatalog([new SourceSnapshot("fixture", "dictionary", SourceFormat.Json,
    File.ReadAllBytes(Path.Combine(root, "fixtures/dictionary/v1/catalog.json")))]).Catalog!;
bool offline = false;
DbConnection Connect()
{
    var s = new NpgsqlConnectionStringBuilder(settings.ConnectionString);
    if (offline)
        s.Port = 1;
    return new NpgsqlConnection(s.ConnectionString);
}
var physicalId = vector["input"]!["physical"]!["storage"]!["id"]!.GetValue<string>();
var provider = new CountingProvider(new PostgreSqlObservationProvider(Connect, "primary", physicalId, Scope, "items"));
PhysicalCatalog New(CountingProvider p) => new(dictionary, definitions,
    new Dictionary<string, IPhysicalObservationProvider> { ["storage"] = p });
var session = New(provider);
CdsReadResult? first = null;
var contract = new List<string>();
var additional = new List<string>();
string Names(JsonNode n) => JsonSerializer.Serialize(n.AsArray().Select(f => f!["name"]!.GetValue<string>()));
void Assertions(JsonNode result, JsonNode spec)
{
    if (spec["logicalFieldNames"] is { } logical) {
        Equal(JsonNode.Parse(Names(result["models"]![0]!["fields"]!)), logical, "logical names");
    }
    foreach (string key in new[] { "public", "summary" }) {
        if (spec[key + "FieldNames"] is { } fields) {
            var projection = result["projections"]!.AsArray().Single(p => p!["id"]!.GetValue<string>()
                == vector["input"]!["projections"]![key]!["id"]!.GetValue<string>())!;
            Equal(JsonNode.Parse(Names(projection["fields"]!)), fields, key + " selection");
        }
    }
    Check(definitions.GetRawText() == original, "overlay bytes unchanged");
}
foreach (var scenario in engine["cases"]!.AsArray()) {
    string id = scenario!["id"]!.GetValue<string>();
    switch (id) {
        case "lazy-generation": {
                Check(provider.Count == 0, "no eager observation");
                first = session.Resolve();
                Check(first.IsValid, "first resolution");
                Check(provider.Count == 1, "one observation");
                Check(ReferenceEquals(first, session.Resolve()) && provider.Count == 1, "cached generation");
                var result = Node(first.ToProjection());
                Assertions(result, scenario["assert"]!);
                Equal(result["projections"], vector["expected"]!["projections"], "exact projections");
                var observation = result["models"]![0]!["physical"]!;
                foreach (var field in new[] { "provider", "dialect", "name" }) {
                    Equal(observation["source"]![field], engine["observedFacts"]!["source." + field], field);
                }
                Check(observation["source"]!["schema"]!.GetValue<string>() == Scope, "verified scope");
                Check(observation["source"]!["engineVersion"]!.GetValue<string>()
                    == Scalar("SHOW server_version"), "version");
                Equal(JsonNode.Parse(Names(observation["columns"]!)), engine["observedFacts"]!["columnOrder"],
                    "column order");
                foreach (var col in observation["columns"]!.AsArray()) {
                    string name = col!["name"]!.GetValue<string>();
                    Equal(col["scalarType"], engine["observedFacts"]!["scalarTypes"]![name], "scalar type");
                    Equal(col["nullable"], engine["observedFacts"]!["nullable"]![name], "nullable");
                    Check(!string.IsNullOrEmpty(col["nativeType"]!.GetValue<string>()), "observed native type");
                }
                Equal(observation["primaryKey"], engine["observedFacts"]!["primaryKey"], "PK");
                Equal(observation["columns"]![2]!["integerRange"],
                    engine["observedFacts"]!["rating.integerRange"], "range");
                Equal(observation["columns"]![1]!["characterLength"],
                    engine["observedFacts"]!["code.characterLength"], "length");
                Check(result["models"]![0]!["fields"]![2]!["compatibility"]!.GetValue<string>() == "compatible",
                    "proof");
                break;
            }
        case "add-column-and-invalidate": {
                Exec(scenario["ddl"]!.GetValue<string>());
                Check(ReferenceEquals(first, session.Resolve()) && provider.Count == 1,
                    "explicit invalidation required");
                session.Invalidate();
                Check(session.Current is null && provider.Count == 1, "lazy invalidation");
                var result = session.Resolve();
                Check(result.IsValid && provider.Count == 2, "regeneration");
                Assertions(Node(result.ToProjection()), scenario["assert"]!);
                Equal(Node(result.ToProjection())["projections"], Node(first!.ToProjection())["projections"],
                    "no exposure expansion");
                break;
            }
        case "remove-specialized-column-and-invalidate":
            Exec(scenario["ddl"]!.GetValue<string>());
            session.Invalidate();
            Equal(Node(session.Resolve().ToProjection()), scenario["expected"], "removed bound column");
            Check(session.Current is null && definitions.GetRawText() == original, "no partial generation");
            break;
        case "observation-fails-after-invalidation":
            offline = true;
            session.Invalidate();
            Equal(Node(session.Resolve().ToProjection()), scenario["expected"], "real refused connection");
            Check(session.Current is null, "no stale generation");
            offline = false;
            break;
        case "invalidation-during-generation":
            Exec("DROP TABLE items");
            Exec(setup);
            PhysicalCatalog? changing = null;
            var delayed = new CountingProvider(new PostgreSqlObservationProvider(Connect, "primary", physicalId,
                Scope, "items"));
            delayed.After = () => changing!.Invalidate();
            changing = New(delayed);
            Equal(Node(changing.Resolve().ToProjection()), scenario["expected"],
                "invalidated during actual observation");
            Check(changing.Current is null && delayed.Count == 1, "no stale publication");
            break;
        default:
            throw new Exception("Unexecuted contractual database scenario: " + id);
    }
    contract.Add(id);
}
Exec("CREATE TABLE parent(a int,b int,PRIMARY KEY(a,b)); CREATE TABLE keys_table(a int,b int,note varchar(12),"
    + "PRIMARY KEY(a,b), CONSTRAINT fk_pair FOREIGN KEY(a,b) REFERENCES parent(a,b));"
    + "CREATE INDEX ix_note ON keys_table(note)");
JsonNode Observe(string name) => Node(new PostgreSqlObservationProvider(Connect, "primary", "storage.extra",
    Scope, name).Observe());
var keys = Observe("keys_table");
Equal(keys["primaryKey"], JsonNode.Parse("[\"a\",\"b\"]"), "composite PK order");
Equal(keys["foreignKeys"]![0]!["columns"], keys["primaryKey"], "composite FK local order");
Equal(keys["foreignKeys"]![0]!["target"]!["columns"], keys["primaryKey"], "composite FK remote order");
Check(keys["indexes"]!.AsArray().Any(i => i!["name"]!.GetValue<string>() == "ix_note"), "real index");
additional.Add("composite-keys-indexes-foreign-keys");
Exec("CREATE INDEX ix_partial ON keys_table(note) WHERE a > 0");
Check(Observe("keys_table")["indexes"] is null, "unrepresentable indexes omitted, not empty");
additional.Add("unrepresentable-index-presence");
Exec("CREATE DOMAIN local_int AS integer NOT NULL; CREATE TABLE types_test(opaque jsonb, custom local_int,"
    + " large bigint, generated integer GENERATED ALWAYS AS (1) STORED, ident integer GENERATED ALWAYS AS IDENTITY)");
var types = Observe("types_test");
Check(types["columns"]![0]!["scalarType"] is null && types["columns"]![1]!["scalarType"] is null
    && types["columns"]![1]!["nullable"] is null, "opaque and domain unknown facts");
Check(types["columns"]![2]!["integerRange"]!["maximum"]!.GetValue<string>() == "9223372036854775807", "exact INT64");
Check(types["columns"]![3]!["generated"]!.GetValue<bool>()
    && types["columns"]![4]!["identity"]!.GetValue<bool>(), "flags");
additional.Add("opaque-domain-exact-int64-generated-identity");
Exec("CREATE TABLE checked(value smallint CHECK(value > 0))");
var checkedObservation = Observe("checked");
Check(checkedObservation["columns"]![0]!["scalarType"] is null
    && checkedObservation["columns"]![0]!["nullable"] is null, "CHECK domain not guessed");
var checkedDefinitions = JsonSerializer.SerializeToElement(new {
    physical = new {
    },
    logical = new {
        model = new {
            apiVersion = "arp.logical/v1",
            id = "demo.checked",
            physical = physicalId,
            fields = new {
                value = new {
                    dictionary = "test.int16"
                }
            }
        }
    },
    projections = new {
    }
});
var checkedCatalog = new PhysicalCatalog(dictionary, checkedDefinitions,
    new Dictionary<string, IPhysicalObservationProvider> {
        ["storage"] =
        new PostgreSqlObservationProvider(Connect, "primary", physicalId, Scope, "checked")
    });
Check(Node(checkedCatalog.Resolve().ToProjection())["models"]![0]!["fields"]![0]!["compatibility"]!
    .GetValue<string>() == "not-verified", "CHECK cannot produce a false range proof");
additional.Add("native-check-no-false-compatibility");

Exec("CREATE TABLE \"Odd.Table\"(id int)");
Check(Observe("Odd.Table")["columns"]!.AsArray().Count == 1, "quoted identifiers");
additional.Add("literal-physical-identifiers");
Exec("CREATE ROLE limited LOGIN PASSWORD '" + password + "'; GRANT USAGE ON SCHEMA arp_scope TO limited;"
    + "GRANT SELECT(id) ON items TO limited");
var limitedSettings = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Username = "limited" };
using (var limited = new NpgsqlConnection(limitedSettings.ConnectionString)) {
    limited.Open();
    using var c = new NpgsqlCommand("SELECT count(*) FROM information_schema.columns WHERE"
        + " table_schema='arp_scope' AND table_name='items'", limited);
    Check(Convert.ToInt32(c.ExecuteScalar()) == 1, "actual incomplete metadata visibility");
}
var denied = new CountingProvider(new PostgreSqlObservationProvider(()
    => new NpgsqlConnection(limitedSettings.ConnectionString),
    "primary", physicalId, Scope, "items"));
Equal(Node(New(denied).Resolve().ToProjection()),
    JsonNode.Parse("{\"valid\":false,\"issues\":[{\"family\":\"execution-failure\","
    + "\"code\":\"CDS_READ_OBSERVATION_FAILED\",\"at\":\"/providers/primary\"}]}"), "permission/partial visibility");
additional.Add("permissions-incomplete-metadata-no-partial-publication");
Exec("CREATE VIEW view_test AS SELECT id FROM items");
foreach (string name in new[] { "missing_table", "view_test" }) {
    try {
        Observe(name);
        throw new Exception("Expected unavailable observation");
    } catch (PhysicalObservationException) { }
}
Check(Scalar("SELECT marker FROM sentinel") == "unchanged", "other owned schema object preserved");
Check(Scalar("SELECT count(*) FROM items") == "0", "observer wrote no rows");
additional.Add("missing-view-readonly-and-sentinel-preservation");
var goodObserver = new CountingProvider(new PostgreSqlObservationProvider(Connect, "primary", physicalId, Scope,
    "items"));
var badObserver = new PostgreSqlObservationProvider(Connect, "primary", "storage.missing", Scope, "missing_table");
var atomic = new PhysicalCatalog(dictionary, definitions,
    new Dictionary<string, IPhysicalObservationProvider> { ["storage"] = goodObserver, ["zz"] = badObserver });
Equal(Node(atomic.Resolve().ToProjection()), Node(CdsReadResult.Failure("OBSERVATION_FAILED", "/providers/primary",
    "execution-failure").ToProjection()), "no partial result after a second provider fails");
Check(atomic.Current is null && goodObserver.Count == 1, "multi-provider atomic publication");
additional.Add("multi-provider-no-partial-publication");
var narrowInput = Node(definitions);
narrowInput["logical"]!["model"]!["fields"]!["id"] = new JsonObject { ["dictionary"] = "test.int32" };
var narrowDefinitions = JsonSerializer.SerializeToElement(narrowInput);
string narrowAuthored = narrowDefinitions.GetRawText();
var narrowed = new PhysicalCatalog(dictionary, narrowDefinitions,
    new Dictionary<string, IPhysicalObservationProvider> {
        ["storage"] =
        new PostgreSqlObservationProvider(Connect, "primary", physicalId, Scope, "items")
    });
var beforeNarrowing = narrowed.Resolve();
Check(beforeNarrowing.IsValid, "explicit INT32 binding before narrowing");
Exec("DROP VIEW view_test; ALTER TABLE items ALTER COLUMN id TYPE smallint");
narrowed.Invalidate();
Equal(Node(narrowed.Resolve().ToProjection()), Node(CdsReadResult.Failure("DOMAIN_NOT_REPRESENTABLE",
    "/logical/model/fields/id", "physical-incompatible").ToProjection()), "real narrowing is incompatible");
Check(narrowed.Current is null && narrowDefinitions.GetRawText() == narrowAuthored
    && definitions.GetRawText() == original, "incompatible generation preserves authored definitions");
Check(Node(beforeNarrowing.ToProjection())["models"]![0]!["fields"]![1]!["physical"]!["scalarType"]!
    .GetValue<string>() == "INT32", "old snapshot remains immutable after DDL");
additional.Add("real-narrowing-incompatible");
Check(contract.Count == 5, "all contractual DB cases executed");
Console.WriteLine(JsonSerializer.Serialize(new {
    stack = "dotnet",
    contractCases = contract,
    additionalCases = additional,
    engineVersion = Scalar("SHOW server_version"),
    contractSha = File.ReadAllText("contract.ref").Trim()
}));

sealed class CountingProvider(IPhysicalObservationProvider inner) : IPhysicalObservationProvider
{
    public int Count
    {
        get; private set;
    }
    public Action? After
    {
        get; set;
    }
    public string ProviderId => inner.ProviderId;
    public JsonElement Observe()
    {
        Count++;
        var result = inner.Observe();
        After?.Invoke();
        return result;
    }
}