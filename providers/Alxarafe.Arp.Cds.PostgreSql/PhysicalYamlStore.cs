using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alxarafe.Arp.Dictionary;

namespace Alxarafe.Arp.Cds.Physical;

/// <summary>Host-owned non-secret identity. Connection is a stable alias, never a DSN or credential.</summary>
public sealed record PhysicalStoreKey
{
    public string Application
    {
        get;
    }
    public string Connection
    {
        get;
    }
    public string Provider
    {
        get;
    }
    public string PhysicalId
    {
        get;
    }
    public string Dialect
    {
        get;
    }
    public string Schema
    {
        get;
    }
    public string Table
    {
        get;
    }
    public string Hash
    {
        get;
    }
    public PhysicalStoreKey(string application, string connection, string provider, string physicalId,
        string dialect, string schema, string table)
    {
        foreach (string id in new[] { application, connection, provider, physicalId }) {
            if (!Regex.IsMatch(id, @"\A[A-Za-z][A-Za-z0-9_.-]{0,127}\z"))
                throw new ArgumentException("Use non-secret registered aliases, not connection strings");
        }
        if (dialect is not ("postgresql" or "mariadb") || schema.Length == 0 || table.Length == 0
            || schema.Contains('\0') || table.Contains('\0'))
            throw new ArgumentException("Invalid object identity");
        Application = application;
        Connection = connection;
        Provider = provider;
        PhysicalId = physicalId;
        Dialect = dialect;
        Schema = schema;
        Table = table;
        Hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { application, connection, provider, physicalId, dialect, schema, table })));
    }
}

/// <summary>Deterministic YAML 1.2 flow document; JSON scalar quoting avoids implicit YAML coercions.</summary>
public static class PhysicalYaml
{
    private static readonly CdsReadResolver Resolver = new();
    private static readonly Catalog Empty = new CatalogBuilder().BuildCatalog([]).Catalog!;
    public static byte[] Encode(JsonElement observation, PhysicalStoreKey key)
    {
        Validate(observation, key);
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) {
            Write(writer, observation);
        }
        byte[] yaml = Encoding.UTF8.GetBytes("---\n" + Encoding.UTF8.GetString(bytes.ToArray()) + "\n");
        var recovered = Decode(yaml, key);
        using var check = new MemoryStream();
        using (var writer = new Utf8JsonWriter(check)) {
            Write(writer, recovered);
        }
        if (!bytes.ToArray().AsSpan().SequenceEqual(check.ToArray()))
            throw new PhysicalObservationException();
        return yaml;
    }
    public static JsonElement Decode(byte[] yaml, PhysicalStoreKey key)
    {
        var parsed = Resolver.Parse(new SourceSnapshot("physical-store", "stored", SourceFormat.Yaml, yaml));
        if (!parsed.GetProperty("valid").GetBoolean())
            throw new PhysicalObservationException();
        var doc = parsed.GetProperty("document");
        Validate(doc, key);
        return doc.Clone();
    }
    private static void Validate(JsonElement doc, PhysicalStoreKey key)
    {
        // Enforce portable intake budgets before schema/semantic checks, also for supplied provider values.
        var intake = Resolver.Parse(new SourceSnapshot("physical-store", "observed", SourceFormat.Json,
            Encoding.UTF8.GetBytes(doc.GetRawText())));
        if (!intake.GetProperty("valid").GetBoolean() || doc.ValueKind != JsonValueKind.Object)
            throw new PhysicalObservationException();
        var result = Resolver.Resolve(JsonSerializer.SerializeToElement(new {
            physical = new Dictionary<string, JsonElement> { ["stored"] = doc },
            logical = new Dictionary<string, object>(),
            projections = new Dictionary<string, object>()
        }), Empty);
        if (!result.IsValid)
            throw new PhysicalObservationException();
        var source = doc.GetProperty("source");
        if (doc.GetProperty("id").GetString() != key.PhysicalId
            || source.GetProperty("provider").GetString() != key.Provider
            || source.GetProperty("dialect").GetString() != key.Dialect
            || source.GetProperty("schema").GetString() != key.Schema
            || source.GetProperty("name").GetString() != key.Table)
            throw new PhysicalObservationException();
    }
    private static void Write(Utf8JsonWriter w, JsonElement value)
    {
        switch (value.ValueKind) {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in value.EnumerateObject().OrderBy(p => p.Name,
                    Comparer<string>.Create((a, b) => Encoding.UTF8.GetBytes(a).AsSpan()
                        .SequenceCompareTo(Encoding.UTF8.GetBytes(b))))) {
                    w.WritePropertyName(p.Name);
                    Write(w, p.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    Write(w, item);
                w.WriteEndArray();
                break;
            case JsonValueKind.Number:
                var parts = Regex.Match(value.GetRawText(), @"\A(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?\z");
                int exponent = (parts.Groups[4].Success
                    ? int.Parse(parts.Groups[4].Value, CultureInfo.InvariantCulture) : 0)
                    - parts.Groups[3].Length;
                var coefficient = BigInteger.Parse(parts.Groups[1].Value + parts.Groups[2].Value
                    + parts.Groups[3].Value, CultureInfo.InvariantCulture);
                var number = exponent >= 0 ? coefficient * BigInteger.Pow(10, exponent)
                    : coefficient / BigInteger.Pow(10, -exponent);
                w.WriteRawValue(number.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                value.WriteTo(w);
                break;
        }
    }
}

/// <summary>Optional local filesystem store. No distributed cache or hostile-filesystem guarantee.</summary>
public sealed class FilePhysicalStore
{
    private sealed record State(string Generation, string Key, string? Digest, bool Valid);
    private readonly string root;
    private readonly string[] sources;
    private readonly string[] cacheRoots;
    public FilePhysicalStore(string directory, IEnumerable<string> versionedSourceRoots,
        IEnumerable<string>? authorizedCacheRoots = null)
    {
        root = RealDirectory(directory);
        if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) != root)
            throw new PhysicalObservationException();
        cacheRoots = (authorizedCacheRoots ?? []).Select(path => {
            string real = RealDirectory(path);
            if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) != real)
                throw new PhysicalObservationException();
            return real;
        }).ToArray();
        sources = versionedSourceRoots.Select(RealDirectory).ToArray();
        if (sources.Length == 0)
            throw new ArgumentException("Declare the host's versioned source roots");
        CheckRoot();
    }
    public string FileFor(PhysicalStoreKey key) => Path.Combine(root, key.Hash + ".physical.yaml");
    public JsonElement Get(PhysicalStoreKey key, Func<JsonElement> observe)
    {
        string generation;
        try {
            using (var file = Lock(key)) {
                var state = ReadState(file, key);
                if (state.Valid && File.Exists(FileFor(key))) {
                    try {
                        CheckFile(FileFor(key));
                        if (new FileInfo(FileFor(key)).Length > 1048576)
                            throw new PhysicalObservationException();
                        var data = File.ReadAllBytes(FileFor(key));
                        if (Digest(data) != state.Digest)
                            throw new PhysicalObservationException();
                        return PhysicalYaml.Decode(data, key);
                    } catch (PhysicalObservationException) {
                        SaveState(file, Invalid(key));
                        throw;
                    }
                }
                if (state.Valid) {
                    state = Invalid(key);
                    SaveState(file, state);
                }
                generation = state.Generation;
            }
            var value = observe();
            var yaml = PhysicalYaml.Encode(value, key);
            using (var file = Lock(key)) {
                if (ReadState(file, key).Generation != generation)
                    throw new PhysicalObservationException();
                AtomicWrite(FileFor(key), yaml);
                SaveState(file, new State(generation, key.Hash, Digest(yaml), true));
            }
            return PhysicalYaml.Decode(yaml, key);
        } catch (IOException) {
            throw new PhysicalObservationException();
        } catch (UnauthorizedAccessException) {
            throw new PhysicalObservationException();
        }
    }
    public void Invalidate(PhysicalStoreKey key)
    {
        try {
            using var file = Lock(key);
            // Record invalidity durably before deleting data, including when deletion later fails.
            SaveState(file, Invalid(key));
            CheckFile(FileFor(key));
            File.Delete(FileFor(key));
        } catch (IOException) {
            throw new PhysicalObservationException();
        } catch (UnauthorizedAccessException) {
            throw new PhysicalObservationException();
        }
    }
    private FileStream Lock(PhysicalStoreKey key)
    {
        CheckRoot();
        string path = Path.Combine(root, key.Hash + ".lock");
        CheckFile(path);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (true) {
            try {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            } catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(10); }
        }
    }
    private State ReadState(FileStream file, PhysicalStoreKey key)
    {
        if (file.Length == 0) {
            var initial = Invalid(key);
            SaveState(file, initial);
            return initial;
        }
        try {
            if (file.Length > 4096)
                throw new JsonException();
            file.Position = 0;
            var state = JsonSerializer.Deserialize<State>(file) ?? throw new JsonException();
            if (state.Key != key.Hash || state.Generation is null
                || !Regex.IsMatch(state.Generation, @"\A[0-9a-f]{32}\z")
                || state.Valid && (state.Digest is null || !Regex.IsMatch(state.Digest, @"\A[0-9a-f]{64}\z")))
                throw new JsonException();
            return state;
        } catch (JsonException) { SaveState(file, Invalid(key)); throw new PhysicalObservationException(); }
    }
    private static State Invalid(PhysicalStoreKey key) => new(Guid.NewGuid().ToString("N"), key.Hash, null, false);
    private static void SaveState(FileStream file, State state)
    {
        file.Position = 0;
        file.SetLength(0);
        JsonSerializer.Serialize(file, state);
        file.Flush(true);
    }
    private static string Digest(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static void AtomicWrite(string path, byte[] data)
    {
        CheckFile(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                if (!OperatingSystem.IsWindows()) {
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                file.Write(data);
                file.Flush(true);
            }
            File.Move(temp, path, true); // Same directory/filesystem. No non-atomic copy fallback.
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void CheckFile(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || Directory.Exists(path))
            throw new PhysicalObservationException();
    }
    private static string RealDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new ArgumentException("An existing absolute storage/source directory is required");
        string current = Path.GetPathRoot(path)!;
        foreach (string part in path[Path.GetPathRoot(path)!.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries)) {
            var info = new DirectoryInfo(Path.Combine(current, part));
            current = info.ResolveLinkTarget(true)?.FullName ?? info.FullName;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
    private static bool Within(string path, string parent) => path == parent
        || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static int Git(string repository, params string[] arguments)
    {
        try {
            var start = new ProcessStartInfo("git") {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in new[] { "-C", repository }.Concat(arguments))
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new PhysicalObservationException();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) {
                process.Kill();
                throw new PhysicalObservationException();
            }
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            return process.ExitCode;
        } catch (System.ComponentModel.Win32Exception) { throw new PhysicalObservationException(); }
    }
    private void CheckRoot()
    {
        string current;
        try {
            current = RealDirectory(root);
        } catch (ArgumentException) { throw new PhysicalObservationException(); }
        if (current != root || sources.Contains(root))
            throw new PhysicalObservationException();
        string? repository = null;
        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent) {
            if (File.Exists(Path.Combine(dir.FullName, "HEAD"))
                && Directory.Exists(Path.Combine(dir.FullName, "objects")))
                throw new PhysicalObservationException();
            if (File.Exists(Path.Combine(dir.FullName, ".git"))
                || Directory.Exists(Path.Combine(dir.FullName, ".git"))) {
                repository = dir.FullName;
                break;
            }
        }
        string[] authorized = cacheRoots.Length == 0 && repository is not null
            ? [Path.Combine(repository, "cache", "arp", "physical")] : cacheRoots;
        if (sources.Any(s => Within(root, s)) || repository is not null || cacheRoots.Length != 0) {
            if (!authorized.Any(c => Within(root, c)))
                throw new PhysicalObservationException();
        }
        if (repository is not null && (Git(repository, "check-ignore", "--quiet", "--no-index", "--", root + "/") != 0
            || Git(repository, "--literal-pathspecs", "ls-files", "--error-unmatch", "--", root + "/") != 1))
            throw new PhysicalObservationException();
    }
}

/// <summary>Persistence decorator: the registered native provider remains the only observation authority.</summary>
public sealed class PersistedPhysicalProvider(IPhysicalObservationProvider inner, FilePhysicalStore store,
    PhysicalStoreKey key) : IPhysicalObservationProvider, IInvalidatablePhysicalProvider
{
    public string ProviderId => inner.ProviderId;
    public JsonElement Observe()
    {
        if (inner.ProviderId != key.Provider)
            throw new PhysicalObservationException();
        return store.Get(key, inner.Observe);
    }
    public void Invalidate() => store.Invalidate(key);
}