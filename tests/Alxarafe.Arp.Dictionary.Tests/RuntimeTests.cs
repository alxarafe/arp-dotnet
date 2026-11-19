using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Alxarafe.Arp.Dictionary.Tests;

public sealed class RuntimeTests
{
    private static readonly CatalogBuilder Builder = new();
    private static SourceSnapshot Json(string elements) => SourceSnapshot.FromUtf8("provider", "memory", SourceFormat.Json, "{\"apiVersion\":\"arp.dictionary/v1\",\"elements\":[" + elements + "]}");
    private static SourceSnapshot Yaml(string text) => SourceSnapshot.FromUtf8("provider", "memory", SourceFormat.Yaml, text);
    private static Catalog Catalog(string definitions)
    {
        var result = Builder.BuildCatalog([Json(definitions)]);
        Assert.True(result.IsValid, string.Join(",", result.Issues));
        return result.Catalog!;
    }
    private static void Reject(SourceSnapshot source, DictionaryErrorCode code = DictionaryErrorCode.DocumentInvalid)
    { var result = Builder.BuildCatalog([source]); Assert.False(result.IsValid); Assert.Null(result.Catalog); Assert.Equal(code, result.Issues[0].Code); }

    [Fact]
    public void SourceBytesAreDefensivelyCopied()
    {
        var bytes = Json("{\"id\":\"copy.x\",\"type\":\"BOOLEAN\"}").GetContent();
        var source = new SourceSnapshot("p", "s", SourceFormat.Json, bytes);
        Array.Fill(bytes, (byte) 'x');
        var exported = source.GetContent();
        Array.Fill(exported, (byte) 'y');
        Assert.True(Builder.BuildCatalog([source]).IsValid);
    }
    [Fact]
    public void MetadataLivesBeyondTheSourceDocumentAndCannotBeMutated()
    {
        var catalog = Catalog("{\"id\":\"immutable.x\",\"type\":\"ENUM\",\"enumValues\":[\"left\",\"right\"],\"constraints\":{\"maximumLength\":8}}");
        var descriptor = catalog.Resolve("immutable.x").Descriptor!;
        var changed = descriptor.Constraints.SetItem("maximumLength", JsonSerializer.SerializeToElement(0));
        var literals = descriptor.EnumValues.SetItem(0, "changed");
        var ids = catalog.ElementIds.SetItem(0, "changed.x");
        Assert.Equal(0, changed["maximumLength"].GetInt32());
        Assert.Equal("changed", literals[0]);
        Assert.Equal("changed.x", ids[0]);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(8, descriptor.Constraints["maximumLength"].GetInt32());
        Assert.Equal("left", descriptor.EnumValues[0]);
        Assert.True(catalog.Validate("immutable.x", "right").IsValid);
        using var exported = JsonDocument.Parse(descriptor.ToJson());
        Assert.Equal(8, exported.RootElement.GetProperty("constraints").GetProperty("maximumLength").GetInt32());
        Assert.True(catalog.Validate("immutable.x", "left").IsValid);
    }
    [Fact]
    public void FailedGenerationExposesNoPartialCatalog()
    {
        var source = Json("{\"id\":\"old.x\",\"type\":\"BOOLEAN\"}");
        var previous = Builder.BuildCatalog([source]).Catalog!;
        var result = Builder.BuildCatalog([source, Json("{\"id\":\"new.x\",\"type\":\"TEXT\"}"), source]);
        Assert.False(result.IsValid);
        Assert.Null(result.Catalog);
        Assert.False(previous.Resolve("new.x").IsValid);
        Assert.True(previous.Validate("old.x", true).IsValid);
        Assert.Equal("provider", result.Issues[0].Provenance!.ProviderId);
    }
    [Fact]
    public void ContextsCanAssignDifferentTypesToIdenticalIds()
    {
        var first = Catalog("{\"id\":\"context.x\",\"type\":\"BOOLEAN\"}");
        var second = Catalog("{\"id\":\"context.x\",\"type\":\"STRING\"}");
        Assert.True(first.Validate("context.x", true).IsValid);
        Assert.False(second.Validate("context.x", true).IsValid);
        Assert.True(second.Validate("context.x", "true").IsValid);
        Assert.False(first.Validate("context.x", "true").IsValid);
    }
    [Fact]
    public async Task ConcurrentBuildersNeverShareWorkingDefinitions()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 32).Select(async index =>
        {
            await start.Task;
            string type = index % 2 == 0 ? "BOOLEAN" : "STRING";
            object value = index % 2 == 0 ? true : "exact";
            var result = Builder.BuildCatalog([Json("{\"id\":\"parallel.x\",\"type\":\"" + type + "\"}")]);
            var snapshot = result.Catalog!;
            for (int i = 0; i < 300; i++)
            { Assert.True(snapshot.Validate("parallel.x", value).IsValid); Assert.Equal(type, snapshot.Resolve("parallel.x").Descriptor!.Type.ToString().ToUpperInvariant()); Assert.False(snapshot.Resolve("other.x").IsValid); }
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
    }
    [Fact]
    public async Task ConcurrentReadersObserveWholeSnapshotsDuringPublication()
    {
        var first = Json("{\"id\":\"generation.a\",\"type\":\"STRING\"},{\"id\":\"generation.b\",\"type\":\"STRING\"}");
        var second = Json("{\"id\":\"generation.a\",\"type\":\"BOOLEAN\"},{\"id\":\"generation.b\",\"type\":\"BOOLEAN\"}");
        Catalog active = Builder.BuildCatalog([first]).Catalog!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = Enumerable.Range(0, 8).Select(async _ =>
        {
            await start.Task;
            for (int i = 0; i < 2000; i++)
            { var snapshot = Volatile.Read(ref active); Assert.Equal(2, snapshot.ElementIds.Length); Assert.Equal(snapshot.Resolve("generation.a").Descriptor!.Type, snapshot.Resolve("generation.b").Descriptor!.Type); }
        }).ToArray();
        var writer = Task.Run(async () =>
        {
            await start.Task;
            for (int i = 0; i < 100; i++)
            {
                var source = i % 2 == 0 ? first : second;
                Interlocked.Exchange(ref active, Builder.BuildCatalog([source]).Catalog!);
                var previous = active;
                var failed = Builder.BuildCatalog([source, source]);
                Assert.Null(failed.Catalog);
                Assert.Same(previous, active);
            }
        });
        start.SetResult();
        await Task.WhenAll(readers.Append(writer)).WaitAsync(TimeSpan.FromSeconds(20));
    }
    [Fact]
    public void OneMiBDocumentIsAcceptedAndOverflowIsTyped()
    {
        var source = Json("{\"id\":\"limit.x\",\"type\":\"STRING\",\"label\":\"\"}");
        string document = Encoding.UTF8.GetString(source.GetContent());
        int fill = 1_048_576 - source.GetContent().Length;
        document = document.Replace("\"label\":\"\"", "\"label\":\"" + new string('x', fill) + "\"", StringComparison.Ordinal);
        source = SourceSnapshot.FromUtf8("p", "s", SourceFormat.Json, document);
        Assert.Equal(1_048_576, source.GetContent().Length);
        Assert.True(Builder.BuildCatalog([source]).IsValid);
        Reject(SourceSnapshot.FromUtf8("p", "s", SourceFormat.Json, document + " "), DictionaryErrorCode.LimitExceeded);
    }
    [Fact]
    public void TwoThousandFortyEightElementsAreAccepted()
    {
        var definitions = Enumerable.Range(0, 2048).Select(i => "{\"id\":\"limit.x" + i + "\",\"type\":\"TEXT\"}").ToList();
        Assert.Equal(2048, Builder.BuildCatalog([Json(string.Join(',', definitions))]).Catalog!.ElementIds.Length);
        definitions.Add("{\"id\":\"limit.extra\",\"type\":\"TEXT\"}");
        Reject(Json(string.Join(',', definitions)), DictionaryErrorCode.LimitExceeded);
    }
    [Fact]
    public void CustomBudgetsArePerBuilderAndApplyToBothEncodings()
    {
        var small = new CatalogBuilder(new CatalogLimits(maxDocumentBytes: 512, maxElementsPerSource: 1, maxDepth: 8));
        foreach (var source in new[] { Json("{\"id\":\"limit.a\",\"type\":\"TEXT\"},{\"id\":\"limit.b\",\"type\":\"TEXT\"}"), Yaml("apiVersion: arp.dictionary/v1\nelements: [{id: limit.a, type: TEXT}, {id: limit.b, type: TEXT}]") })
        { Assert.Equal(DictionaryErrorCode.LimitExceeded, small.BuildCatalog([source]).Issues[0].Code); Assert.True(Builder.BuildCatalog([source]).IsValid); }
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogLimits(maxDepth: 0));
    }
    [Fact]
    public void DepthBudgetIsCheckedBeforeRecursiveConversion()
    {
        string nested = new string('[', 129) + "0" + new string(']', 129);
        Reject(SourceSnapshot.FromUtf8("p", "s", SourceFormat.Json, nested), DictionaryErrorCode.LimitExceeded);
        Reject(Yaml(nested), DictionaryErrorCode.LimitExceeded);
    }
    [Fact]
    public void DuplicateNullAndEscapedKeysAreRejectedAtEveryDepth()
    {
        foreach (var source in new[] { SourceSnapshot.FromUtf8("p", "s", SourceFormat.Json, "{\"a\":null,\"\\u0061\":2}"), Yaml("a: null\na: 2"), Yaml("{a: null, a: 2}"), Yaml("a:\n  b: null\n  b: 2"), Yaml("{a: null, \"\\u0061\": 2}") })
            Reject(source);
    }
    [Fact]
    public void AllYamlTagsAnchorsAliasesMergesAndDirectivesAreRejected()
    {
        foreach (string input in new[] { "%UNKNOWN example\n---\n{}", "%YAML 1.2\n---\n{}", "%TAG !e! tag:example.com,2026:\n---\n{}", "a: &scalar value", "a: *missing", "!!System.Diagnostics.Process {}", "!local {}", "a: !!str value", "a: {<<: {b: 1}}", "{}\n---\n{}" })
            Reject(Yaml(input));
    }
    [Fact]
    public void DefaultYamlTagHandlesAreNotExplicitSourceTags()
    {
        var result = Builder.BuildCatalog([Yaml("apiVersion: arp.dictionary/v1\nelements: [{id: yaml.x, type: STRING}]")]);
        Assert.True(result.IsValid);
    }
    [Fact]
    public void LiteralAndFoldedYamlScalarsDoNotExecuteTheirContents()
    {
        string source = "apiVersion: arp.dictionary/v1\nelements:\n  - id: literal.x\n    type: TEXT\n    label: '! & * <<: %YAML ---'\n    description: |\n      %YAML 1.2\n      !!System.Diagnostics.Process\n";
        var result = Builder.BuildCatalog([Yaml(source)]);
        Assert.True(result.IsValid);
        Assert.Contains("!!System.Diagnostics.Process", result.Catalog!.Resolve("literal.x").Descriptor!.Description);
        result = Builder.BuildCatalog([Yaml(source.Replace("description: |", "description: >", StringComparison.Ordinal))]);
        Assert.True(result.IsValid);
    }
    [Fact]
    public void IndentedMultilineQuotedPercentAfterSupplementaryTextIsRejected()
    {
        var source = Yaml("apiVersion: arp.dictionary/v1\nelements:\n  - id: literal.x\n    type: TEXT\n    description: \"😀 text\n      %YAML 1.2\n      end\"\n");
        Reject(source);
    }
    [Fact]
    public void UnsupportedUnindentedMultilineYamlIsRejectedWithoutCoercion()
    {
        Reject(Yaml("apiVersion: arp.dictionary/v1\nelements:\n  - id: literal.x\n    type: TEXT\n    description: \"text\n%YAML 1.2\nend\"\n"));
    }
    [Fact]
    public void YamlPlainDatesAndYamlElevenBooleanWordsStayStrings()
    {
        var result = Builder.BuildCatalog([Yaml("apiVersion: arp.dictionary/v1\nelements: [{id: literal.x, type: ENUM, enumValues: [yes, no, on, off, 2026-11-14]}]")]);
        Assert.True(result.IsValid);
        Assert.Equal(new[] { "yes", "no", "on", "off", "2026-11-14" }, result.Catalog!.Resolve("literal.x").Descriptor!.EnumValues);
    }
    [Fact]
    public void JsonAndYamlHaveTheSameExactMetadataProjection()
    {
        var json = Json("{\"id\":\"exact.x\",\"type\":\"DECIMAL\",\"constraints\":{\"minimum\":\"0.00\",\"multipleOf\":\"0.001\",\"precision\":18,\"scale\":4,\"exclusiveMinimum\":false}}");
        var yaml = Yaml("apiVersion: arp.dictionary/v1\nelements: [{id: exact.x, type: DECIMAL, constraints: {minimum: '0.00', multipleOf: '0.001', precision: 18, scale: 4, exclusiveMinimum: false}}]");
        var first = Builder.BuildCatalog([json]).Catalog!;
        var second = Builder.BuildCatalog([yaml]).Catalog!;
        ContractData.Compare(first.Resolve("exact.x").Descriptor!.ToProjection(), second.Resolve("exact.x").Descriptor!.ToProjection());
        Assert.Equal("0.003", second.Validate("exact.x", "0.00300").NormalizedValue);
    }
    [Fact]
    public void LargeIntegerMetadataRetainsJsonNumberKindAndDigits()
    {
        string integer = "922337203685477580812345678901234567890123456789";
        foreach (var source in new[] { Json("{\"id\":\"exact.x\",\"type\":\"TEXT\",\"constraints\":{\"maximumLength\":" + integer + "}}"), Yaml("apiVersion: arp.dictionary/v1\nelements: [{id: exact.x, type: TEXT, constraints: {maximumLength: " + integer + "}}]") })
        {
            var catalog = Builder.BuildCatalog([source]).Catalog!;
            var descriptor = catalog.Resolve("exact.x").Descriptor!;
            Assert.Equal(JsonValueKind.Number, descriptor.Constraints["maximumLength"].ValueKind);
            Assert.Equal(integer, descriptor.Constraints["maximumLength"].GetRawText());
            using var exported = JsonDocument.Parse(descriptor.ToJson());
            Assert.Equal(integer, exported.RootElement.GetProperty("constraints").GetProperty("maximumLength").GetRawText());
            Assert.True(catalog.Validate("exact.x", "text").IsValid);
        }
    }
    [Fact]
    public void EquivalentIntegralNumberSpellingsAreComparedMathematically()
    {
        foreach (string number in new[] { "1.00", "0.1e1", "1e0", "1.000e+0" })
        {
            var catalog = Catalog("{\"id\":\"exact.x\",\"type\":\"TEXT\",\"constraints\":{\"minimumLength\":" + number + ",\"maximumLength\":1}}");
            Assert.False(catalog.Validate("exact.x", "").IsValid);
            Assert.True(catalog.Validate("exact.x", "a").IsValid);
            Assert.Equal(number, catalog.Resolve("exact.x").Descriptor!.Constraints["minimumLength"].GetRawText());
        }
        Reject(Json("{\"id\":\"exact.x\",\"type\":\"TEXT\",\"constraints\":{\"maximumLength\":1.01}}"), DictionaryErrorCode.ElementInvalid);
    }
    [Fact]
    public void MetadataExponentsAndTokensHavePortableBoundsBeforeConversion()
    {
        foreach (string number in new[] { "1e10000", "1e2147483648", "1e9999999999999999999999999999", "1e-2147483648", "0e129", "1e128", "1e-129", "1e" + new string('0', 255), "1e" + new string('0', 252) + "129" })
            Reject(Json("{\"id\":\"exact.x\",\"type\":\"TEXT\",\"constraints\":{\"maximumLength\":" + number + "}}"), DictionaryErrorCode.LimitExceeded);
        foreach (string number in new[] { "1e127", new string('9', 128), "1e" + new string('0', 253) + "1" })
            Assert.True(Builder.BuildCatalog([Json("{\"id\":\"exact.x\",\"type\":\"TEXT\",\"constraints\":{\"maximumLength\":" + number + "}}")]).IsValid);
    }
    [Fact]
    public void YamlJsonEscapesAndBlockChompingStayPortable()
    {
        foreach (string value in new[] { "\"\\x41\"", "\"\\U0001F600\"", "|2\n      text", "plain\n      continuation", "'quoted\n      continuation'" })
            Reject(Yaml("apiVersion: arp.dictionary/v1\nelements:\n  - id: yaml.x\n    type: TEXT\n    description: " + value + "\n"));
        foreach (var pair in new[] { ("|-", "first\nsecond"), ("|", "first\nsecond\n"), ("|+", "first\nsecond\n\n"), (">-", "first second") })
        {
            var catalog = Builder.BuildCatalog([Yaml("apiVersion: arp.dictionary/v1\nelements:\n  - id: yaml.x\n    type: TEXT\n    description: " + pair.Item1 + "\n      first\n      second\n\n")]).Catalog!;
            Assert.Equal(pair.Item2, catalog.Resolve("yaml.x").Descriptor!.Description);
        }
    }
    [Fact]
    public void CompactSequenceBlockFirstFieldPreservesFollowingScalarKinds()
    {
        var source = Yaml("apiVersion: arp.dictionary/v1\nelements:\n  - description: |\n      first\n      second\n    label: 2025-01-02\n    id: yaml.compact\n    type: TEXT\n    constraints: {minimumLength: 1.00, maximumLength: 0.1e2}\n");
        var built = Builder.BuildCatalog([source]);
        Assert.True(built.IsValid);
        var descriptor = built.Catalog!.Resolve("yaml.compact").Descriptor!;
        Assert.Equal("first\nsecond\n", descriptor.Description);
        Assert.Equal("2025-01-02", descriptor.Label);
        Assert.Equal(JsonValueKind.Number, descriptor.Constraints["minimumLength"].ValueKind);
        Assert.Equal(JsonValueKind.Number, descriptor.Constraints["maximumLength"].ValueKind);
        ContractData.Compare(JsonSerializer.SerializeToElement(new { minimumLength = 1, maximumLength = 10 }),
            JsonSerializer.SerializeToElement(descriptor.Constraints));
    }
    [Fact]
    public void YamlNumericLimitsAlsoApplyToUnknownFieldsAndZeroExponents()
    {
        foreach (string token in new[] { "0e129", "-1e128", "1e-129", "1e" + new string('0', 255) })
            Reject(Yaml("apiVersion: arp.dictionary/v1\nelements: []\nunknown: " + token), DictionaryErrorCode.LimitExceeded);
    }
    [Fact]
    public void DecimalArithmeticExceedsTheNativeDecimalDomainExactly()
    {
        string integer = (BigInteger.Pow(10, 100) + 1).ToString(CultureInfo.InvariantCulture), value = integer + ".123456789012345678901234567890123456789";
        var catalog = Catalog("{\"id\":\"number.x\",\"type\":\"DECIMAL\",\"constraints\":{\"minimum\":\"" + value + "\",\"maximum\":\"" + value + "\",\"multipleOf\":\"0.000000000000000000000000000000000000001\"}}");
        Assert.Equal(value, catalog.Validate("number.x", value + "000").NormalizedValue);
        Assert.False(catalog.Validate("number.x", integer + ".123456789012345678901234567890123456788").IsValid);
    }
    [Fact]
    public void SmallestPortableFractionAnd4096DigitCoefficientsStayExact()
    {
        string fraction = "0." + new string('0', 4094) + "1";
        var catalog = Catalog("{\"id\":\"number.x\",\"type\":\"DECIMAL\",\"constraints\":{\"multipleOf\":\"" + fraction + "\"}}");
        Assert.Equal(fraction, catalog.Validate("number.x", fraction).NormalizedValue);
        var whole = Catalog("{\"id\":\"number.x\",\"type\":\"DECIMAL\",\"constraints\":{\"multipleOf\":\"9\"}}");
        string digits = new('9', 4096);
        Assert.Equal(digits, whole.Validate("number.x", digits).NormalizedValue);
    }
    [Fact]
    public void Utf8AndUtf16AreStrictWithoutReplacementCharacters()
    {
        foreach (var bytes in new[] { new byte[] { 0xc0, 0xaf }, new byte[] { 0xed, 0xa0, 0x80 }, new byte[] { 0xf0, 0x9f } })
            Reject(new("p", "s", SourceFormat.Json, bytes));
        var catalog = Catalog("{\"id\":\"unicode.x\",\"type\":\"STRING\"}");
        foreach (string invalid in new[] { "\ud800", "\udc00", "\ud800X", "X\udc00" })
        { var result = catalog.Validate("unicode.x", invalid); Assert.Equal(DictionaryErrorCode.ValueType, result.Issues[0].Code); Assert.Null(result.NormalizedValue); }
        Assert.Equal("�", catalog.Validate("unicode.x", "�").NormalizedValue);
        Reject(Yaml("a: \"\\uD800\""));
        Reject(SourceSnapshot.FromUtf8("p", "s", SourceFormat.Json, "{\"\\uD800\":1}"));
    }
    [Fact]
    public void JsonEscapedPairsAndUnicodeLengthsAreScalarExact()
    {
        var catalog = Catalog("{\"id\":\"unicode.x\",\"type\":\"ENUM\",\"enumValues\":[\"\\ud83d\\ude00\",\"e\\u0301\"]}");
        Assert.True(catalog.Validate("unicode.x", "😀").IsValid);
        Assert.True(catalog.Validate("unicode.x", "é").IsValid);
        Assert.False(catalog.Validate("unicode.x", "é").IsValid);
        Assert.Equal(1, UnicodeText.Length("😀"));
        Assert.Equal(2, UnicodeText.Length("é"));
    }
    [Fact]
    public void EveryAsciiControlIsRejectedInEveryPatternContext()
    {
        for (int c = 0; c <= 127; c++)
            if (c < 32 || c == 127)
                foreach (string pattern in new[] { "" + (char) c, "[" + (char) c + "]", "\\" + (char) c })
                { var error = Assert.Throws<Rejection>(() => new PortablePattern(pattern)); Assert.Equal(DictionaryErrorCode.ElementInvalid, error.Code); }
    }
    [Fact]
    public void AmbiguousPatternsHaveBoundedExecutionWithoutEngineExtensions()
    {
        var pattern = new PortablePattern(string.Concat(Enumerable.Repeat("a*", 128)));
        var watch = Stopwatch.StartNew();
        Assert.True(pattern.Matches(new string('a', 4096)));
        Assert.False(pattern.Matches(new string('a', 4095) + "\n"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        foreach (string extension in new[] { "(?i)a", "(?<name>a)", "a|b", "\\p{L}", "(?<=a)b" })
            Assert.Throws<Rejection>(() => new PortablePattern(extension));
    }
    [Fact]
    public void TemporalNormalizationPreservesTicksAcrossCalendarAndOffsetBounds()
    {
        var catalog = Catalog("{\"id\":\"instant.x\",\"type\":\"DATETIME\"}");
        Assert.Equal("2026-11-15T01:59:59.7654321Z", catalog.Validate("instant.x", "2026-11-14T23:59:59.7654321-02:00").NormalizedValue);
        Assert.Equal("0001-01-01T00:00:00.0000001Z", catalog.Validate("instant.x", "0001-01-01T14:00:00.0000001+14:00").NormalizedValue);
        Assert.Equal("9999-12-31T23:59:59.9999999Z", catalog.Validate("instant.x", "9999-12-31T09:59:59.9999999-14:00").NormalizedValue);
        Assert.False(catalog.Validate("instant.x", "9999-12-31T23:59:59-00:01").IsValid);
        Assert.False(catalog.Validate("instant.x", "0001-01-01T00:00:00+00:01").IsValid);
    }
    [Fact]
    public void HostCultureDoesNotChangeNumericOrTemporalSemantics()
    {
        var prior = CultureInfo.CurrentCulture;
        var catalog = Catalog("{\"id\":\"culture.instant\",\"type\":\"DATETIME\"},{\"id\":\"culture.decimal\",\"type\":\"DECIMAL\"}");
        try
        {
            foreach (string culture in new[] { "ar-SA", "es-ES", "th-TH" })
            { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture); Assert.Equal("2026-11-14T00:00:00.0000000Z", catalog.Validate("culture.instant", "2026-11-14T01:00:00+01:00").NormalizedValue); Assert.Equal("1.23", catalog.Validate("culture.decimal", "1.2300").NormalizedValue); }
        } finally { CultureInfo.CurrentCulture = prior; }
    }
    [Fact]
    public void ExternalReferenceSyntaxIsNeverResolvedBySourceParsing()
    {
        Reject(Json("{\"id\":\"external.x\",\"type\":\"STRING\",\"$ref\":\"http://127.0.0.1:1/private\"}"), DictionaryErrorCode.ElementInvalid);
        Reject(Yaml("apiVersion: arp.dictionary/v1\nelements: [{id: external.x, type: STRING, label: !include file:///etc/passwd}]"));
        Reject(Yaml("apiVersion: arp.dictionary/v1\nelements: [{id: external.x, type: DECIMAL, constraints: {minimum: 0.1}}]"), DictionaryErrorCode.ElementInvalid);
    }
    [Fact]
    public async Task BareValidationDoesNotReloadSourcesOrOpenNetworkAddresses()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "source.json");
        File.WriteAllBytes(path, Json("{\"id\":\"pure.x\",\"type\":\"STRING\"}").GetContent());
        var source = new SourceSnapshot("provider", path, SourceFormat.Json, File.ReadAllBytes(path));
        var catalog = Builder.BuildCatalog([source]).Catalog!;
        File.Delete(path);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            string url = "http://127.0.0.1:" + ((IPEndPoint) listener.LocalEndpoint).Port + "/must-not-fetch";
            for (int i = 0; i < 100; i++)
                Assert.Equal(url, catalog.Validate("pure.x", url).NormalizedValue);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var unexpected = await listener.AcceptTcpClientAsync(timeout.Token); });
            Assert.False(File.Exists(path));
        } finally { listener.Stop(); }
    }
    [Fact]
    public void EnumerationIsOrdinalAndAnEmptyBuildHasNoDiscovery()
    {
        var catalog = Catalog("{\"id\":\"order.z\",\"type\":\"TEXT\"},{\"id\":\"order.a\",\"type\":\"TEXT\"},{\"id\":\"order.A\",\"type\":\"TEXT\"}");
        Assert.Equal(new[] { "order.A", "order.a", "order.z" }, catalog.ElementIds);
        Assert.Empty(Builder.BuildCatalog([]).Catalog!.ElementIds);
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("arp-dictionary-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}
