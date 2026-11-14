using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Alxarafe.Arp.Dictionary.Tests;

internal static class ContractData
{
    internal const string Sha = "2dde1a09780e29c7385606c5e018fa75a0808b68";
    internal static readonly string LibraryRoot = FindRoot();
    internal static readonly string Root = Path.GetFullPath(Environment.GetEnvironmentVariable("ARP_CONTRACT_PATH") ?? Path.Combine(LibraryRoot, "..", "arp-contract"));
    internal static readonly string DirectoryPath = Path.Combine(Root, "fixtures/dictionary/v1");
    internal static readonly JsonElement Manifest = Read(Path.Combine(DirectoryPath, "manifest.json"));
    internal static readonly List<object[]> Plan = MakePlan();
    internal static string ReportDirectory => Environment.GetEnvironmentVariable("ARP_TEST_REPORT_DIR") ?? Path.Combine(LibraryRoot, "artifacts");

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "contract.ref"))) return directory.FullName;
        throw new InvalidOperationException("Cannot locate library contract.ref");
    }
    internal static JsonElement Read(string path)
    { using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 128 }); return document.RootElement.Clone(); }
    internal static string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(Root);
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error); return output.Trim();
    }
    internal static void CheckPin()
    {
        Assert.Equal(Sha, Git("rev-parse", "HEAD")); Assert.Equal(Sha, File.ReadAllText(Path.Combine(LibraryRoot, "contract.ref")).Trim());
        Git("diff", "--quiet", "HEAD", "--", "fixtures", "schemas", "docs/features/arp-dict-001.md");
    }
    private static JsonElement Set(string name) => Read(Path.Combine(DirectoryPath, Manifest.GetProperty("sets").GetProperty(name).GetString()!));
    private static List<object[]> MakePlan()
    {
        var names = Manifest.GetProperty("sets").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.True(names.SetEquals(["catalog", "values", "definitions", "sources", "patterns", "catalogs"]));
        Assert.Equal("ARP-DICT-001", Manifest.GetProperty("feature").GetString());
        Assert.Equal("arp.dictionary/v1", Manifest.GetProperty("apiVersion").GetString());
        var cases = new List<object[]> { new object[] { "catalog", "17 scalar types", Set("catalog").GetRawText(), "" } };
        foreach (string group in new[] { "values", "definitions", "sources" })
            foreach (var vector in Set(group).EnumerateArray())
            {
                cases.Add([group, vector.GetProperty("id").GetString()!, vector.GetRawText(), ""]);
                if (group == "sources" && vector.TryGetProperty("descriptorProjection", out _)) cases.Add(["source-descriptors", vector.GetProperty("id").GetString()!, vector.GetRawText(), ""]);
            }
        foreach (var vector in Set("patterns").EnumerateArray())
        {
            string name = vector.GetProperty("id").GetString()!;
            cases.Add(["patterns", name, vector.GetRawText(), ""]);
            for (int i = 0; i < vector.GetProperty("samples").GetArrayLength(); i++) cases.Add(["pattern-samples", name + "/" + i, vector.GetRawText(), i.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        foreach (var scenario in Set("catalogs").EnumerateArray())
        {
            int i = 0;
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                Assert.Contains(step.GetProperty("operation").GetString(), new[] { "buildCatalog", "resolve", "assertRejectedGeneration" });
                string name = scenario.GetProperty("id").GetString() + "/" + i;
                cases.Add(["catalog-steps", name, scenario.GetRawText(), i.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                if (step.TryGetProperty("descriptorProjection", out _)) cases.Add(["descriptors", name, scenario.GetRawText(), i.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                i++;
            }
        }
        return cases;
    }
    internal static SourceSnapshot Source(JsonElement document) => SourceSnapshot.FromUtf8("fixture", "memory", SourceFormat.Json, document.GetRawText());
    internal static object? PortableValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => UnicodeText.DecodeJson(Encoding.UTF8.GetBytes(value.GetRawText()[1..^1]), requireValid: false),
        JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null,
        _ => value.Clone() // Wrong neutral kind remains wrong; do not coerce a JSON number to string.
    };
    internal static void Compare(JsonElement expected, JsonElement actual, bool omitDiagnosticElementId = false)
    {
        if (omitDiagnosticElementId)
        {
            var outcome = new Dictionary<string, object> { ["valid"] = actual.GetProperty("valid").GetBoolean() };
            var issues = new List<object>(); int i = 0;
            foreach (var issue in actual.GetProperty("issues").EnumerateArray())
            {
                bool hasExpected = expected.GetProperty("issues").GetArrayLength() > i;
                var fields = issue.EnumerateObject().Where(p => p.Name != "elementId" || hasExpected && expected.GetProperty("issues")[i].TryGetProperty("elementId", out _))
                    .ToDictionary(p => p.Name, p => p.Value.Clone());
                issues.Add(fields); i++;
            }
            outcome["issues"] = issues; actual = JsonSerializer.SerializeToElement(outcome);
        }
        Assert.True(Equal(expected, actual), $"Expected {expected.GetRawText()}\nActual {actual.GetRawText()}");
    }
    private static bool Equal(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count() && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var v) && Equal(p.Value, v)),
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => Equal(pair.First, pair.Second)),
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => ExactNumber.Parse(a.GetRawText()).CompareTo(ExactNumber.Parse(b.GetRawText())) == 0,
            _ => true
        };
    }
}

public sealed class ConformanceReport : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, (int Passed, int Failed)> results = new(StringComparer.Ordinal);
    public ConformanceReport() => ContractData.CheckPin();
    internal void Record(string group, bool success)
    {
        lock (gate) { results.TryGetValue(group, out var count); results[group] = (count.Passed + (success ? 1 : 0), count.Failed + (success ? 0 : 1)); }
    }
    public void Dispose()
    {
        var groups = new Dictionary<string, object>(); int passed = 0, failed = 0, pending = 0;
        foreach (var group in ContractData.Plan.GroupBy(item => (string)item[0]))
        {
            results.TryGetValue(group.Key, out var count); int missing = group.Count() - count.Passed - count.Failed;
            groups[group.Key] = new { passed = count.Passed, failed = count.Failed, notExecuted = missing };
            passed += count.Passed; failed += count.Failed; pending += missing;
        }
        Directory.CreateDirectory(ContractData.ReportDirectory);
        File.WriteAllText(Path.Combine(ContractData.ReportDirectory, "conformance-summary.json"), JsonSerializer.Serialize(new { contractSha = ContractData.Sha, passed, failed, notExecuted = pending, groups }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
