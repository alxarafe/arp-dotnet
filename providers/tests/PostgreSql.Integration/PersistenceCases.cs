using System.Diagnostics;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alxarafe.Arp.Cds.Physical;
using Alxarafe.Arp.Dictionary;
using Npgsql;

internal static class PersistenceCases
{
    private sealed class Counted(IPhysicalObservationProvider inner) : IPhysicalObservationProvider
    {
        public int Count;
        public Action? After;
        public string ProviderId => inner.ProviderId;
        public JsonElement Observe()
        {
            Count++;
            var value = inner.Observe();
            After?.Invoke();
            return value;
        }
    }
    private static void Check(bool ok, string label)
    {
        if (!ok)
            throw new Exception("Persistence assertion: " + label);
    }
    private static void Reject(Action action, string label)
    {
        bool rejected = false;
        try {
            action();
        } catch (PhysicalObservationException) { rejected = true; }
        Check(rejected, label);
    }
    public static void Run(string phase)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Docker Linux tests required");
        string root = Environment.GetEnvironmentVariable("ARP_CONTRACT_PATH")!;
        string directory = Environment.GetEnvironmentVariable("ARP_PHYSICAL_STORE")!;
        string password = Environment.GetEnvironmentVariable("ARP_DB_PASSWORD")!;
        var settings = new NpgsqlConnectionStringBuilder {
            Host = "db",
            Database = "arpcds",
            Username = "arpcds",
            Password = password,
            Pooling = false,
            Timeout = 3,
            CommandTimeout = 5
        };
        using var admin = new NpgsqlConnection(settings.ConnectionString);
        admin.Open();
        void Exec(string sql)
        {
            using var c = new NpgsqlCommand(sql, admin);
            c.ExecuteNonQuery();
        }
        string Scalar(string sql)
        {
            using var c = new NpgsqlCommand(sql, admin);
            return c.ExecuteScalar()!.ToString()!;
        }
        Check(Scalar("SELECT token FROM public.__arp_cds_test_owner")
            == Environment.GetEnvironmentVariable("ARP_CDS_RUN_TOKEN"), "owned database");
        const string schema = "persist_scope";
        if (phase == "seed") {
            Exec("CREATE SCHEMA persist_scope; CREATE TABLE persist_scope.persisted_items "
                + "(id INTEGER PRIMARY KEY, code VARCHAR(12) NOT NULL, rating SMALLINT NOT NULL)");
        }
        bool offline = false;
        DbConnection Connect()
        {
            var selected = new NpgsqlConnectionStringBuilder(settings.ConnectionString);
            if (offline)
                selected.Port = 1;
            return new NpgsqlConnection(selected.ConnectionString);
        }
        var key = new PhysicalStoreKey("tests", "db-main", "primary", "storage.items", "postgresql",
            schema, "persisted_items");
        var store = new FilePhysicalStore(directory, ["/work", root]);
        var counted = new Counted(new PostgreSqlObservationProvider(Connect, "primary", key.PhysicalId,
            schema, key.Table));
        var vector = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "fixtures/cds/read/v1/cases.json")))!
            ["cases"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "postgresql-same-logical-projections")!;
        var definitions = JsonSerializer.SerializeToElement(vector["input"]);
        var original = definitions.GetRawText();
        var dictionary = new CatalogBuilder().BuildCatalog([new SourceSnapshot("fixture", "dictionary",
            SourceFormat.Json, File.ReadAllBytes(Path.Combine(root, "fixtures/dictionary/v1/catalog.json")))]).Catalog!;
        PhysicalCatalog New(FilePhysicalStore s, PhysicalStoreKey k, Counted? p = null) => new(dictionary,
            definitions, new Dictionary<string, IPhysicalObservationProvider> {
                ["storage"] = new PersistedPhysicalProvider(p ?? counted, s, k)
            });
        string path = store.FileFor(key);
        string Digest() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var catalog = New(store, key);
        Check(counted.Count == 0, "lazy construction");
        var result = catalog.Resolve();
        Check(result.IsValid, "initial resolution");
        if (phase == "recover") {
            Check(counted.Count == 0, "new process recovers without introspection");
            Check(File.ReadAllText(Path.Combine(directory, "proof.json")) == Digest(), "same volume checksum");
            Console.WriteLine("PERSISTENCE_RECOVERED: process/container restart, same YAML, zero observations");
            return;
        }
        var groups = new List<string>();
        Check(counted.Count == 1 && File.Exists(path), "first access generates");
        Check(ReferenceEquals(result, catalog.Resolve()) && counted.Count == 1, "memory reuse");
        Check(New(new FilePhysicalStore(directory, ["/work", root]), key).Resolve().IsValid
            && counted.Count == 1, "new catalog disk reuse");
        groups.Add("lazy-cache-and-restart");
        byte[] first = File.ReadAllBytes(path);
        var doc = PhysicalYaml.Decode(first, key);
        Check(first.AsSpan().SequenceEqual(PhysicalYaml.Encode(doc, key)), "deterministic YAML roundtrip");
        Check(JsonNode.DeepEquals(JsonNode.Parse(doc.GetRawText()), JsonNode.Parse(counted.Observe().GetRawText())),
            "same real observation");
        groups.Add("deterministic-semantic-roundtrip");
        foreach (var isolated in new[] {
            new PhysicalStoreKey("other", "db-main", "primary", key.PhysicalId, key.Dialect, schema, key.Table),
            new PhysicalStoreKey("tests", "other-db", "primary", key.PhysicalId, key.Dialect, schema, key.Table)
        }) {
            Check(store.FileFor(isolated) != path && New(store, isolated).Resolve().IsValid, "isolated identity");
        }
        Check(new PhysicalStoreKey("tests", "db-main", "primary", key.PhysicalId, key.Dialect,
            "different_schema", key.Table).Hash != key.Hash, "schema separation");
        Check(new PhysicalStoreKey("tests", "db-main", "primary", key.PhysicalId, key.Dialect,
            schema, "different_table").Hash != key.Hash, "table separation");
        groups.Add("identity-isolation");
        Exec("ALTER TABLE persist_scope.persisted_items ADD COLUMN new_column BIGINT");
        Check(!catalog.Resolve().ToProjection().GetRawText().Contains("new_column"), "explicit invalidation only");
        catalog.Invalidate();
        Check(!File.Exists(path) && catalog.Current is null, "invalidated disk and memory");
        result = catalog.Resolve();
        Check(result.IsValid, "regeneration");
        var projection = result.ToProjection();
        Check(projection.GetProperty("models")[0].GetProperty("fields").EnumerateArray()
            .Any(f => f.GetProperty("name").GetString() == "new_column"), "effective logical field");
        Check(!projection.GetProperty("projections").GetRawText().Contains("new_column"), "no exposure widening");
        Check(definitions.GetRawText() == original, "authored definitions untouched");
        groups.Add("ddl-invalidation");
        File.WriteAllText(path, "---\n{broken");
        var corrupt = New(store, key);
        Check(!corrupt.Resolve().IsValid && corrupt.Current is null, "corrupt fail closed");
        Check(corrupt.Resolve().IsValid, "corruption recovery via fresh observation");
        var altered = JsonNode.Parse(PhysicalYaml.Decode(File.ReadAllBytes(path), key).GetRawText())!;
        altered["source"]!["engineVersion"] = "tampered";
        File.WriteAllBytes(path, PhysicalYaml.Encode(JsonSerializer.SerializeToElement(altered), key));
        var tampered = New(store, key);
        Check(!tampered.Resolve().IsValid, "valid YAML checksum mismatch");
        Check(tampered.Resolve().IsValid, "checksum failure recovery");
        groups.Add("corruption-and-checksum");
        File.SetUnixFileMode(path, 0);
        var unreadable = New(store, key);
        Check(!unreadable.Resolve().IsValid && unreadable.Current is null, "read failure no publication");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Check(unreadable.Resolve().IsValid, "read access recovery");
        groups.Add("read-permission-recovery");
        catalog.Invalidate();
        offline = true;
        Check(!catalog.Resolve().IsValid && catalog.Current is null && !File.Exists(path),
            "connection failure cannot reuse invalidated YAML");
        offline = false;
        Check(catalog.Resolve().IsValid, "native recovery");
        groups.Add("failed-regeneration-no-stale-cache");
        catalog.Invalidate();
        var permissions = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Check(!catalog.Resolve().IsValid && catalog.Current is null, "temporary file creation denied");
        File.SetUnixFileMode(directory, permissions);
        Check(catalog.Resolve().IsValid, "write access recovery");
        groups.Add("write-permission-recovery");
        string statePath = Path.Combine(directory, key.Hash + ".lock");
        File.WriteAllText(statePath, "broken-state");
        var badState = New(store, key);
        Check(!badState.Resolve().IsValid, "invalid state rejected");
        Check(badState.Resolve().IsValid, "state recovery");
        groups.Add("state-recovery");
        catalog.Invalidate();
        Directory.CreateDirectory(path);
        Check(!catalog.Resolve().IsValid && catalog.Current is null, "write failure no publication");
        Directory.Delete(path);
        Check(catalog.Resolve().IsValid, "write retry");
        groups.Add("atomic-write-failure");
        File.SetUnixFileMode(statePath, UnixFileMode.UserRead);
        Reject(catalog.Invalidate, "invalidation failure signalled");
        Check(catalog.Current is null && !catalog.Resolve().IsValid, "failed invalidation blocks stale generation");
        File.SetUnixFileMode(statePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        catalog.Invalidate();
        Check(catalog.Resolve().IsValid, "retry durable invalidation");
        groups.Add("failed-invalidation-blocks-reuse");
        catalog.Invalidate();
        counted.After = catalog.Invalidate;
        Check(!catalog.Resolve().IsValid && catalog.Current is null && !File.Exists(path), "in-flight invalidation");
        counted.After = null;
        Check(catalog.Resolve().IsValid, "new generation after race");
        groups.Add("in-flight-invalidation");
        Reject(() => _ = new FilePhysicalStore("/work", ["/work", root]), "source root rejected");
        string link = Path.Combine(directory, "source-link");
        Directory.CreateSymbolicLink(link, "/work");
        Reject(() => _ = new FilePhysicalStore(link, ["/work", root]), "symlink source rejected");
        Directory.Delete(link);
        catalog.Invalidate();
        File.CreateSymbolicLink(path, Path.Combine(root, "examples/demo/cds/items.physical.yaml"));
        Check(!catalog.Resolve().IsValid, "symlink target rejected");
        File.Delete(path);
        Check(catalog.Resolve().IsValid, "symlink removal recovery");
        groups.Add("source-and-symlink-protection");
        bool secretRejected = false;
        try {
            _ = new PhysicalStoreKey("tests", settings.ConnectionString, "primary", key.PhysicalId,
            key.Dialect, schema, key.Table);
        } catch (ArgumentException) { secretRejected = true; }
        Check(secretRejected, "DSN cannot be a cache identity");
        foreach (var f in Directory.EnumerateFiles(directory)) {
            Check(!Path.GetFileName(f).Contains(password) && !File.ReadAllText(f).Contains(password), "no secret leak");
        }
        Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "no temporary leaks");
        groups.Add("secret-integrity");
        var exact = JsonNode.Parse(PhysicalYaml.Decode(File.ReadAllBytes(path), key).GetRawText())!;
        var column = exact["columns"]![0]!;
        column["integerRange"]!["minimum"] = "-9223372036854775808";
        column["integerRange"]!["maximum"] = "9223372036854775807";
        column["nativeType"] = "bigint";
        column["scalarType"] = "INT64";
        exact["columns"]![1]!["characterLength"]!["value"] = JsonNode.Parse("9223372036854775807");
        // Codec-only regression input, not a new claim about provider type equivalence.
        var encoded = PhysicalYaml.Encode(JsonSerializer.SerializeToElement(exact), key);
        Check(System.Text.Encoding.UTF8.GetString(encoded).Contains("9223372036854775807"), "exact integer YAML");
        groups.Add("exact-integer-codec");
        ApplicationCache(doc, key);
        groups.Add("git-ignored-application-cache");
        File.WriteAllText(Path.Combine(directory, "proof.json"), Digest());
        Console.WriteLine("PERSISTENCE_SEED: " + JsonSerializer.Serialize(groups));
    }
    private static void Git(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in new[] { "--literal-pathspecs", "-C", repository }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        Check(process.ExitCode == 0, "test application Git");
    }
    private static void ApplicationCache(JsonElement observation, PhysicalStoreKey key)
    {
        string app = Directory.CreateTempSubdirectory("arp-cache-app-").FullName;
        try {
            string cache = Directory.CreateDirectory(Path.Combine(app, "cache/arp/physical")).FullName;
            string definitions = Directory.CreateDirectory(Path.Combine(app, "arp/models")).FullName;
            string custom = Directory.CreateDirectory(Path.Combine(app, "custom-cache")).FullName;
            Git(app, "init", "--quiet");
            Reject(() => _ = new FilePhysicalStore(cache, [app]), "unignored cache rejected");
            File.WriteAllText(Path.Combine(app, ".gitignore"), "cache/\narp/models/\ncustom-cache/\n");
            var store = new FilePhysicalStore(cache, [app]);
            int count = 0;
            JsonElement Observe()
            {
                count++;
                return observation;
            }
            store.Get(key, Observe);
            new FilePhysicalStore(cache, [app]).Get(key, Observe);
            Check(count == 1, "ignored application cache persisted");
            store.Invalidate(key);
            store.Get(key, Observe);
            Check(count == 2, "local invalidation and regeneration");
            Reject(() => _ = new FilePhysicalStore(definitions, [app]), "definitions not authorized as cache");
            Reject(() => _ = new FilePhysicalStore(custom, [app]), "custom cache requires authorization");
            new FilePhysicalStore(custom, [app], [custom]).Get(key, Observe);
            Reject(() => _ = new FilePhysicalStore(cache, [app], [custom]), "outside authorized directory");
            string link = Path.Combine(app, "linked-cache");
            Directory.CreateSymbolicLink(link, cache);
            Reject(() => _ = new FilePhysicalStore(link, [app]), "symlink cache rejected");
            Directory.Delete(link);
            File.WriteAllText(Path.Combine(app, ".gitignore"), "");
            Reject(() => store.Get(key, Observe), "revoked ignore fails closed");
            File.WriteAllText(Path.Combine(app, ".gitignore"), "cache/\n");
            Git(app, "add", "-f", "--", store.FileFor(key));
            Reject(() => store.Get(key, Observe), "force-staged cache rejected");
        } finally { Directory.Delete(app, true); } // Only this test's disposable application.
    }

}