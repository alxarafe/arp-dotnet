using System.Globalization;
using System.Text.Json;
using Xunit;

namespace Alxarafe.Arp.Dictionary.Tests;

public sealed class ConformanceTests(ConformanceReport report) : IClassFixture<ConformanceReport>
{
    public static IEnumerable<object[]> Cases => ContractData.Plan;
    private static readonly CatalogBuilder Builder = new();
    [Theory]
    [MemberData(nameof(Cases))]
    public void Shared(string group, string name, string payload, string selector)
    {
        bool success = false;
        try
        {
            using var parsed = JsonDocument.Parse(payload); var vector = parsed.RootElement;
            switch (group)
            {
                case "catalog":
                    var baseline = Build(vector); Assert.True(baseline.IsValid); Assert.Equal(17, baseline.Catalog!.ElementIds.Length); break;
                case "values":
                    var document = JsonProjection.Create(writer =>
                    {
                        writer.WriteStartObject(); writer.WriteString("apiVersion", ContractData.Manifest.GetProperty("apiVersion").GetString());
                        writer.WritePropertyName("elements"); writer.WriteStartArray(); vector.GetProperty("element").WriteTo(writer); writer.WriteEndArray(); writer.WriteEndObject();
                    });
                    var built = Build(document); Assert.True(built.IsValid, string.Join(",", built.Issues));
                    ContractData.Compare(vector.GetProperty("expected"), built.Catalog!.Validate(vector.GetProperty("element").GetProperty("id").GetString()!, ContractData.PortableValue(vector.GetProperty("value"))).ToProjection()); break;
                case "definitions": ContractData.Compare(vector.GetProperty("expected"), Build(vector.GetProperty("document")).ToProjection(), true); break;
                case "sources":
                    string path = Path.GetFullPath(Path.Combine(ContractData.Root, vector.GetProperty("path").GetString()!));
                    Assert.StartsWith(ContractData.DirectoryPath + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
                    var source = new SourceSnapshot("fixture", name, vector.GetProperty("encoding").GetString() == "json" ? SourceFormat.Json : SourceFormat.Yaml, File.ReadAllBytes(path));
                    ContractData.Compare(vector.GetProperty("expected"), Builder.BuildCatalog([source]).ToProjection(), true); break;
                case "patterns":
                case "pattern-samples":
                    var patternDocument = JsonProjection.Create(writer =>
                    {
                        writer.WriteStartObject(); writer.WriteString("apiVersion", "arp.dictionary/v1"); writer.WritePropertyName("elements"); writer.WriteStartArray();
                        writer.WriteStartObject(); writer.WriteString("id", "fixture.pattern"); writer.WriteString("type", "STRING"); writer.WritePropertyName("constraints"); writer.WriteStartObject();
                        writer.WritePropertyName("pattern"); vector.GetProperty("pattern").WriteTo(writer); writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
                    });
                    var result = Build(patternDocument);
                    if (group == "patterns")
                    {
                        var actual = result.IsValid ? JsonSerializer.SerializeToElement(new { valid = true }) : JsonSerializer.SerializeToElement(new { valid = false, code = result.Issues[0].ContractCode });
                        ContractData.Compare(vector.GetProperty("expected"), actual);
                    }
                    else
                    {
                        Assert.True(result.IsValid); var sample = vector.GetProperty("samples")[int.Parse(selector, CultureInfo.InvariantCulture)];
                        Assert.Equal(sample.GetProperty("matches").GetBoolean(), result.Catalog!.Validate("fixture.pattern", sample.GetProperty("value").GetString()).IsValid);
                    }
                    break;
                case "catalog-steps": case "descriptors": RunScenario(vector, int.Parse(selector, CultureInfo.InvariantCulture), group == "descriptors"); break;
                default: throw new InvalidOperationException("Unknown manifest group " + group);
            }
            success = true;
        }
        finally { report.Record(group, success); }
    }
    private static BuildResult Build(JsonElement document) => Builder.BuildCatalog([ContractData.Source(document)]);
    private static void RunScenario(JsonElement scenario, int target, bool descriptor)
    {
        var catalogs = new Dictionary<string, Catalog>(StringComparer.Ordinal); var last = new Dictionary<string, BuildResult>(StringComparer.Ordinal);
        var steps = scenario.GetProperty("steps");
        for (int i = 0; i <= target; i++)
        {
            var step = steps[i]; string context = step.GetProperty("context").GetString()!;
            string operation = step.GetProperty("operation").GetString()!; JsonElement outcome;
            if (operation == "buildCatalog")
            {
                var sources = step.GetProperty("sources").EnumerateArray().Select(s => SourceSnapshot.FromUtf8(s.GetProperty("providerId").GetString()!, s.GetProperty("sourceId").GetString()!, SourceFormat.Json, s.GetProperty("document").GetRawText()));
                var build = Builder.BuildCatalog(sources); last[context] = build;
                if (build.IsValid) catalogs[context] = build.Catalog!;
                outcome = build.ToProjection();
            }
            else if (operation == "resolve")
            {
                var resolved = catalogs[context].Resolve(step.GetProperty("elementId").GetString()!);
                if (i == target && descriptor) { ContractData.Compare(step.GetProperty("descriptorProjection"), resolved.Descriptor!.ToProjection()); return; }
                outcome = resolved.ToProjection();
            }
            else if (operation == "assertRejectedGeneration") outcome = JsonSerializer.SerializeToElement(new { published = last[context].Catalog is not null });
            else throw new InvalidOperationException("Unimplemented operation " + operation);
            ContractData.Compare(step.GetProperty("expected"), outcome, operation == "buildCatalog");
        }
    }
}
