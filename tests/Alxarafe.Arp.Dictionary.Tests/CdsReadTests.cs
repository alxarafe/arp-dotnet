using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Alxarafe.Arp.Dictionary.Tests;

public sealed class CdsReadTests
{
    private readonly CdsReadResolver resolver = new();
    public static IEnumerable<object[]> ResolutionCases => ContractData.Read(Path.Combine(ContractData.Root,
        "fixtures/cds/read/v1/cases.json")).GetProperty("cases").EnumerateArray()
        .Select(c => new object[] { c.GetProperty("id").GetString()!, c.GetRawText() });
    public static IEnumerable<object[]> IntakeCases => ContractData.Read(Path.Combine(ContractData.Root,
        "fixtures/cds/read/v1/source-cases.json")).GetProperty("cases").EnumerateArray()
        .Select(c => new object[] { c.GetProperty("id").GetString()!, c.GetRawText() });

    [Theory]
    [MemberData(nameof(ResolutionCases))]
    public void ExactResolution(string id, string payload)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 128 });
        var vector = document.RootElement;
        string path = Path.Combine(ContractData.Root, vector.GetProperty("dictionary").GetString()!);
        var dictionary = new CatalogBuilder().BuildCatalog([new SourceSnapshot("fixture", "dict",
            path.EndsWith("yaml", StringComparison.Ordinal) ? SourceFormat.Yaml : SourceFormat.Json,
            File.ReadAllBytes(path))]);
        Assert.True(dictionary.IsValid, id);
        var input = vector.GetProperty("input");
        string before = input.GetRawText();
        var result = resolver.Resolve(input, dictionary.Catalog!);
        ContractData.Compare(vector.GetProperty("expected"), result.ToProjection());
        var reversed = Reverse(JsonNode.Parse(input.GetRawText()));
        ContractData.Compare(vector.GetProperty("expected"), resolver.Resolve(
            JsonSerializer.SerializeToElement(reversed), dictionary.Catalog!).ToProjection());
        Assert.Equal(before, input.GetRawText());
        // Caller graphs and JsonDocument lifetimes cannot mutate an owned result.
        reversed!.AsObject().Clear();
        document.Dispose();
        using var expectedDocument = JsonDocument.Parse(payload);
        ContractData.Compare(expectedDocument.RootElement.GetProperty("expected"), result.ToProjection());
    }

    private static JsonNode? Reverse(JsonNode? node)
    {
        if (node is JsonObject obj) {
            var result = new JsonObject();
            foreach (var (key, value) in obj.Reverse())
                result[key] = Reverse(value);
            return result;
        }
        if (node is JsonArray array)
            return new JsonArray(array.Select(Reverse).ToArray());
        return node?.DeepClone();
    }

    [Theory]
    [MemberData(nameof(IntakeCases))]
    public void ExactIntake(string id, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var vector = document.RootElement;
        byte[] bytes = vector.TryGetProperty("bytesHex", out var hex) ? Convert.FromHexString(hex.GetString()!)
            : Encoding.UTF8.GetBytes(vector.GetProperty("text").GetString()!);
        var actual = resolver.Parse(new SourceSnapshot("fixture", "s",
            vector.GetProperty("encoding").GetString() == "json" ? SourceFormat.Json : SourceFormat.Yaml, bytes));
        if (vector.TryGetProperty("expectedError", out var error)) {
            Assert.False(actual.GetProperty("valid").GetBoolean(), id);
            ContractData.Compare(error, actual.GetProperty("issues")[0]);
        } else {
            Assert.True(actual.GetProperty("valid").GetBoolean(), id);
            ContractData.Compare(vector.GetProperty("expected"), actual.GetProperty("document"));
        }
    }

    [Fact]
    public void GenerationInvalidationAndIsolation()
    {
        var store = new CdsReadPublication();
        var other = new CdsReadPublication();
        var good = new CdsReadResult(JsonSerializer.SerializeToElement(new {
            valid = true,
            issues = Array.Empty<object>(),
            models = Array.Empty<object>(),
            projections = Array.Empty<object>()
        }));
        var bad = CdsReadResult.Failure("OBSERVATION_FAILED", "/snapshot", "execution-failure");
        long ticket = store.BeginGeneration();
        Assert.Same(good, store.Publish(ticket, good));
        Assert.Same(good, store.Current);
        Assert.Null(other.Current);
        store.Invalidate();
        Assert.Null(store.Current);
        ContractData.Compare(
                CdsReadResult.Failure("SNAPSHOT_CHANGED", "/snapshot", "execution-failure").ToProjection(),
            store.Publish(ticket, good).ToProjection());
        store.Publish(store.BeginGeneration(), bad);
        Assert.Null(store.Current);
        Assert.True(good.IsValid); // Old immutable readers retain their snapshot, never its current status.
    }

    [Fact]
    public void IntakeBudgetBeforeSyntax()
    {
        ContractData.Compare(CdsReadResult.Failure("LIMIT", "/sources/s").ToProjection(),
            resolver.Parse(new SourceSnapshot("fixture", "s", SourceFormat.Json, new byte[1048577])));
        Assert.Equal(80, ResolutionCases.Count());
        Assert.Equal(11, IntakeCases.Count());
    }
    [Fact]
    public void SchemasAndBudgets()
    {
        foreach (string name in new[] { "physical", "logical", "projection" }) {
            using var stream = typeof(CdsReadResolver).Assembly.GetManifestResourceStream(
                "Alxarafe.Arp.Dictionary.Cds.schemas." + name + ".schema.json");
            Assert.NotNull(stream);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            Assert.Equal(File.ReadAllBytes(Path.Combine(ContractData.Root, "schemas/cds/v1/" + name + ".schema.json")),
                bytes.ToArray());
        }
        var dict = new CatalogBuilder().BuildCatalog([]).Catalog!;
        foreach (int size in new[] { 256, 257 }) {
            var physical = new JsonObject();
            for (int i = 0; i < size; i++)
                physical["s" + i] = JsonSerializer.SerializeToNode(new {
                    apiVersion = "arp.physical/v1",
                    id = "storage.t" + i,
                    kind = "table",
                    source = new {
                        provider = "host",
                        dialect = "test",
                        name = "t"
                    },
                    columns = new[] { new { name = "id", nativeType = "opaque" } }
                });
            var bundle = JsonSerializer.SerializeToElement(new JsonObject {
                ["physical"] = physical,
                ["logical"] = new JsonObject(),
                ["projections"] = new JsonObject()
            });
            Assert.Equal(size == 256, resolver.Resolve(bundle, dict).IsValid);
        }
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSnapshot>> batch =
            new Dictionary<string, IReadOnlyDictionary<string, SourceSnapshot>> {
                ["physical"] = new Dictionary<string, SourceSnapshot> {
                    ["first"] = SourceSnapshot.FromUtf8("host", "ignored", SourceFormat.Json, "{}")
                },
                ["logical"] = new Dictionary<string, SourceSnapshot> {
                    ["later"] = SourceSnapshot.FromUtf8("host", "ignored", SourceFormat.Json, "{")
                }
            };
        ContractData.Compare(CdsReadResult.Failure("SOURCE_INVALID", "/sources/later").ToProjection(),
            resolver.ResolveSources(batch, dict).ToProjection());
    }

    [Fact]
    public async Task InvalidationWhileComputing()
    {
        var store = new CdsReadPublication();
        long ticket = store.BeginGeneration();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () => {
            started.SetResult();
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return store.Publish(ticket, new CdsReadResult(JsonSerializer.SerializeToElement(new {
                valid = true,
                issues = Array.Empty<object>(),
                models = Array.Empty<object>(),
                projections = Array.Empty<object>()
            })));
        });
        try {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            store.Invalidate();
            finish.SetResult();
            ContractData.Compare(
                CdsReadResult.Failure("SNAPSHOT_CHANGED", "/snapshot", "execution-failure").ToProjection(),
                (await task.WaitAsync(TimeSpan.FromSeconds(5))).ToProjection());
            Assert.Null(store.Current);
        } finally { finish.TrySetResult(); }
    }
    [Fact]
    public void StructuralBoundsAndDeterministicLimit()
    {
        var dict = new CatalogBuilder().BuildCatalog([]).Catalog!;
        foreach (int size in new[] { 2048, 2049 }) {
            var columns = new JsonArray(Enumerable.Range(0, size).Select(i => (JsonNode?) new JsonObject {
                ["name"] = "c" + i,
                ["nativeType"] = "opaque"
            }).ToArray());
            Assert.Equal(size == 2048, resolver.Resolve(Minimal(columns, new()), dict).IsValid);
        }
        foreach (int size in new[] { 64, 65 }) {
            var projections = new JsonObject();
            for (int i = 0; i < size; i++)
                projections["s" + i] = new JsonObject {
                    ["apiVersion"] = "arp.projection/v1",
                    ["id"] = "projection.p" + i,
                    ["source"] = i == 0 ? new JsonObject { ["entity"] = "logical.table" }
                    : new JsonObject { ["projection"] = "projection.p" + (i - 1) },
                    ["fields"] = new JsonArray("id"),
                    ["operations"] = new JsonArray("GET")
                };
            Assert.Equal(size == 64, resolver.Resolve(Minimal(new JsonArray(new JsonObject {
                ["name"] = "id",
                ["nativeType"] = "opaque"
            }), projections), dict).IsValid);
        }
        string longBound = new('1', 4097);
        foreach (bool reverse in new[] { false, true }) {
            var extent = new JsonObject {
                [reverse ? "maximum" : "minimum"] = longBound,
                [reverse ? "minimum" : "maximum"] = longBound
            };
            var bundle = Minimal(new JsonArray(new JsonObject {
                ["name"] = "id",
                ["nativeType"] = "opaque",
                ["integerRange"] = extent
            }), new());
            ContractData.Compare(
                CdsReadResult.Failure("LIMIT", "/physical/s/columns/0/integerRange/minimum").ToProjection(),
                resolver.Resolve(bundle, dict).ToProjection());
        }
    }

    private static JsonElement Minimal(JsonArray columns, JsonObject projections) =>
        JsonSerializer.SerializeToElement(new JsonObject {
            ["physical"] = new JsonObject {
                ["s"] = new JsonObject {
                    ["apiVersion"] = "arp.physical/v1",
                    ["id"] = "storage.table",
                    ["kind"] = "table",
                    ["source"] = new JsonObject { ["provider"] = "host", ["dialect"] = "test", ["name"] = "t" },
                    ["columns"] = columns
                }
            },
            ["logical"] = new JsonObject {
                ["s"] = new JsonObject {
                    ["apiVersion"] = "arp.logical/v1",
                    ["id"] = "logical.table",
                    ["physical"] = "storage.table",
                    ["fields"] = new JsonObject()
                }
            },
            ["projections"] = projections
        });
}