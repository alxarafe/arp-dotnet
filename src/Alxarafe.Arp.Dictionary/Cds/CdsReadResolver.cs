using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Alxarafe.Arp.Dictionary;

/// <summary>Pure CDS-READ-001 metadata compiler over explicitly supplied trusted snapshots.</summary>
public sealed class CdsReadResolver
{
    private static readonly string[] Kinds = ["physical", "logical", "projections"];
    private readonly Dictionary<string, JsonObject> schemas = new(StringComparer.Ordinal);
    internal static readonly IComparer<string> Order = Comparer<string>.Create((a, b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b)));
    private static readonly Dictionary<string, (BigInteger Min, BigInteger Max)> Ranges = MakeRanges();

    public CdsReadResolver()
    {
        for (int i = 0; i < Kinds.Length; i++) {
            string name = new[] { "physical", "logical", "projection" }[i];
            using var stream = typeof(CdsReadResolver).Assembly.GetManifestResourceStream(
                "Alxarafe.Arp.Dictionary.Cds.schemas." + name + ".schema.json")
                ?? throw new InvalidOperationException("Missing pinned CDS schema");
            schemas[Kinds[i]] = JsonNode.Parse(stream)!.AsObject();
        }
    }

    private static Dictionary<string, (BigInteger, BigInteger)> MakeRanges()
    {
        var result = new Dictionary<string, (BigInteger, BigInteger)>(StringComparer.Ordinal);
        foreach (var type in System.Enum.GetValues<ScalarType>().Where(Numbers.IsInteger)) {
            var (lo, hi) = Numbers.IntegerRange(type);
            result[type.ToString().ToUpperInvariant()] = (
                BigInteger.Parse(lo.ToPlainString(), CultureInfo.InvariantCulture),
                BigInteger.Parse(hi.ToPlainString(), CultureInfo.InvariantCulture));
        }
        return result;
    }

    internal static string Pointer(params string[] parts) => "/" + string.Join('/', parts.Select(p =>
        p.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));
    private sealed class Failure(string code, string at, string family) : Exception(code)
    {
        internal JsonObject Issue { get; } = new() { ["family"] = family, ["code"] = "CDS_READ_" + code, ["at"] = at };
    }
    private static void Need(bool ok, string code, string at, string family = "definition-invalid")
    {
        if (!ok)
            throw new Failure(code, at, family);
    }
    private static JsonObject Obj(JsonNode? value) => value!.AsObject();
    private static JsonArray Arr(JsonNode? value) => value!.AsArray();
    private static JsonObject Object(JsonObject v, string key) => v[key]?.AsObject() ?? new();
    private static string Text(JsonNode? v) => v!.GetValue<string>();
    private static JsonNode? Copy(JsonNode? v) => v?.DeepClone();
    private static IEnumerable<KeyValuePair<string, JsonNode?>> Sorted(JsonObject v) => v.OrderBy(e => e.Key, Order);
    private static ExactNumber Numeric(JsonNode v) => ExactNumber.Parse(v.ToJsonString());
    private static bool HasRange(string? type) => type is not null && Ranges.ContainsKey(type);

    /// <summary>Independent portable intake; document can be JSON null. No dictionary diagnostics escape.</summary>
    public JsonElement Parse(SourceSnapshot source)
    {
        try {
            var parsed = new SourceParser(CatalogLimits.Default).Parse(source);
            return JsonSerializer.SerializeToElement(new Dictionary<string, object?> {
                ["valid"] = true,
                ["issues"] = Array.Empty<object>(),
                ["document"] = parsed
            });
        } catch (Rejection e) {
            return CdsReadResult.Failure(e.Code == DictionaryErrorCode.LimitExceeded ? "LIMIT" : "SOURCE_INVALID",
                Pointer("sources", source.SourceId)).ToProjection();
        }
    }

    public CdsReadResult ResolveSources(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSnapshot>> sources,
        Catalog dictionary)
    {
        if (sources.Values.Sum(entries => entries.Count) > 256)
            return CdsReadResult.Failure("LIMIT", "/");
        var bundle = new JsonObject();
        foreach (string kind in Kinds) {
            var docs = new JsonObject();
            if (sources.TryGetValue(kind, out var entries)) {
                foreach (var (handle, s) in entries.OrderBy(e => e.Key, Order)) {
                    var parsed = Parse(new SourceSnapshot(s.ProviderId, handle, s.Format, s.GetContent()));
                    if (!parsed.GetProperty("valid").GetBoolean())
                        return new CdsReadResult(parsed);
                    docs[handle] = JsonNode.Parse(parsed.GetProperty("document").GetRawText());
                }
            }
            bundle[kind] = docs;
        }
        return Resolve(JsonSerializer.SerializeToElement(bundle), dictionary);
    }

    /// <summary>Decoded portable snapshot; immutable exact output or failure without a partial catalog.</summary>
    public CdsReadResult Resolve(JsonElement supplied, Catalog dictionary)
    {
        try {
            Need(Kinds.Sum(k => supplied.GetProperty(k).EnumerateObject().Count()) <= 256, "LIMIT", "/");
            var bundle = JsonNode.Parse(supplied.GetRawText(), documentOptions: new() {
                MaxDepth = 128
            })!.AsObject();
            var indexes = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);
            foreach (string kind in Kinds) {
                var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                foreach (var (handle, doc) in Sorted(Obj(bundle[kind]))) {
                    string at = Pointer(kind, handle);
                    Need(Schema(doc, schemas[kind], schemas[kind]), "SCHEMA_INVALID", at);
                    string id = Text(doc!["id"]);
                    Need(!entries.ContainsKey(id), "DUPLICATE_ID", at);
                    entries[id] = new(Obj(doc), at);
                }
                indexes[kind] = entries;
            }
            foreach (var e in indexes["physical"].Values)
                Physical(e.Doc, e.At);
            var models = new SortedDictionary<string, JsonObject>(Order);
            foreach (var (id, e) in indexes["logical"].OrderBy(e => e.Key, Order)) {
                var logical = e.Doc;
                Need(indexes["physical"].ContainsKey(Text(logical["physical"])),
                    "REFERENCE_NOT_FOUND", e.At + "/physical");
                var physical = indexes["physical"][Text(logical["physical"])].Doc;
                var columns = Arr(physical["columns"]).ToDictionary(c => Text(c!["name"]), c => Obj(c),
                    StringComparer.Ordinal);
                var overlay = Obj(logical["fields"]);
                Need(overlay.All(k => columns.ContainsKey(k.Key)), "UNKNOWN_FIELD", e.At + "/fields");
                var fields = new JsonArray();
                foreach (var (name, col) in columns.OrderBy(c => c.Key, Order)) {
                    fields.Add(Field(col, Object(overlay, name), dictionary, e.At + Pointer("fields", name)));
                }
                models[id] = new() {
                    ["id"] = id,
                    ["physical"] = Copy(physical),
                    ["fields"] = fields
                };
            }
            var projected = new SortedDictionary<string, JsonObject>(Order);
            foreach (string id in indexes["projections"].Keys.Order(Order)) {
                Projection(id, [], indexes["projections"], models, projected);
            }
            return new CdsReadResult(JsonSerializer.SerializeToElement(new JsonObject {
                ["valid"] = true,
                ["issues"] = new JsonArray(),
                ["models"] = new JsonArray(models.Values.Select(Copy).ToArray()),
                ["projections"] = new JsonArray(projected.Values.Select(Copy).ToArray())
            }));
        } catch (Failure e) {
            return new CdsReadResult(JsonSerializer.SerializeToElement(new JsonObject {
                ["valid"] = false,
                ["issues"] = new JsonArray(e.Issue)
            }));
        }
    }

    private sealed record Entry(JsonObject Doc, string At);
    private static BigInteger Bound(JsonNode? v, string at)
    {
        Need(v is JsonValue j && j.TryGetValue<string>(out string? s) && s.Length <= 4096, "LIMIT", at);
        string value = Text(v);
        Need(Regex.IsMatch(value, @"\A-?(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant),
            "PROFILE_UNSUPPORTED", at);
        return BigInteger.Parse(value, CultureInfo.InvariantCulture);
    }
    private static bool Interval(JsonObject v) => v.All(e => e.Key is "minimum" or "maximum"
        && e.Value is JsonValue j && j.TryGetValue<string>(out string? s) && s.Length <= 4096
        && Regex.IsMatch(s, @"\A-?(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant));
    private static BigInteger Integer(JsonNode v) => BigInteger.Parse(Text(v), CultureInfo.InvariantCulture);

    private static void Physical(JsonObject doc, string at)
    {
        Need(Text(doc["kind"]) == "table", "PROFILE_UNSUPPORTED", at + "/kind");
        var columns = Arr(doc["columns"]);
        Need(columns.Count <= 2048, "LIMIT", at + "/columns");
        var names = columns.Select(c => Text(c!["name"])).ToArray();
        var ordinals = columns.Where(c => Obj(c).ContainsKey("ordinal"))
            .Select(c => Numeric(c!["ordinal"]!).ToPlainString()).ToArray();
        Need(names.Distinct(StringComparer.Ordinal).Count() == names.Length
            && ordinals.Distinct(StringComparer.Ordinal).Count() == ordinals.Length,
            "PHYSICAL_INVALID", at + "/columns");
        for (int i = 0; i < columns.Count; i++) {
            var col = Obj(columns[i]);
            string point = at + "/columns/" + i;
            var values = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
            var extent = Object(col, "integerRange");
            foreach (string key in new[] { "minimum", "maximum" }) {
                if (extent.ContainsKey(key))
                    values[key] = Bound(extent[key], point + "/integerRange/" + key);
            }
            Need(!values.ContainsKey("minimum") || !values.ContainsKey("maximum")
                || values["minimum"] <= values["maximum"], "PHYSICAL_INVALID", point);
            string? type = col["scalarType"]?.GetValue<string>();
            if (HasRange(type)) {
                var range = Ranges[type!];
                Need(values.Values.All(n => range.Min <= n && n <= range.Max), "PHYSICAL_INVALID", point);
            }
            if (col.ContainsKey("decimalGeometry")) {
                var geometry = Object(col, "decimalGeometry");
                Need(Numeric(geometry["scale"]!).CompareTo(Numeric(geometry["precision"]!)) <= 0,
                    "PHYSICAL_INVALID", point);
            }
        }
        bool Local(JsonArray array) => array.All(v => names.Contains(Text(v), StringComparer.Ordinal));
        Need(Local(doc["primaryKey"]?.AsArray() ?? new()), "PHYSICAL_INVALID", at);
        foreach (var index in doc["indexes"]?.AsArray() ?? new()) {
            Need(Local(Arr(index!["columns"])), "PHYSICAL_INVALID", at);
        }
        foreach (var key in doc["foreignKeys"]?.AsArray() ?? new()) {
            Need(Local(Arr(key!["columns"])) && Arr(key["columns"]).Count == Arr(key["target"]!["columns"]).Count,
                "PHYSICAL_INVALID", at);
        }
        foreach (string key in new[] { "indexes", "foreignKeys" }) {
            var ids = (doc[key]?.AsArray() ?? new()).Select(i => Text(i!["name"])).ToArray();
            Need(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "PHYSICAL_INVALID", at + "/" + key);
        }
    }

    private static JsonObject Field(JsonObject col, JsonObject overlay, Catalog dictionary, string at)
    {
        var result = new JsonObject {
            ["name"] = Copy(col["name"]),
            ["physical"] = Copy(col),
            ["compatibility"] = "not-verified"
        };
        string? type = col["scalarType"]?.GetValue<string>();
        var intrinsic = new JsonObject();
        if (overlay.ContainsKey("dictionary")) {
            string id = Text(overlay["dictionary"]);
            var resolved = dictionary.Resolve(id);
            Need(resolved.Descriptor is not null, "REFERENCE_NOT_FOUND", at + "/dictionary");
            var element = JsonNode.Parse(resolved.Descriptor!.ToJson())!.AsObject();
            type = Text(element["type"]);
            intrinsic = Object(element, "constraints").DeepClone().AsObject();
            result["dictionary"] = id;
            if (element.ContainsKey("enumValues"))
                result["enumValues"] = Copy(element["enumValues"]);
        } else if (HasRange(type)) {
            var range = Ranges[type!];
            foreach (var (key, value) in Object(col, "integerRange")) {
                var n = Integer(value!);
                if (n != (key == "minimum" ? range.Min : range.Max)) {
                    intrinsic[key] = n.ToString(CultureInfo.InvariantCulture);
                }
            }
        }
        var effective = intrinsic.DeepClone().AsObject();
        if (type is not null) {
            result["type"] = type;
            result["intrinsicConstraints"] = intrinsic;
            result["constraints"] = effective;
        }
        var context = Object(overlay, "constraints");
        if (overlay.ContainsKey("constraints"))
            result["contextualConstraints"] = Copy(context);
        if (context.Count > 0) {
            Need(HasRange(type) && Interval(intrinsic), "PROFILE_UNSUPPORTED", at + "/constraints");
            Need(context.All(e => e.Key is "minimum" or "maximum"), "PROFILE_UNSUPPORTED", at + "/constraints");
        }
        if (HasRange(type) && Interval(intrinsic)) {
            var (lo, hi) = Ranges[type!];
            if (intrinsic.ContainsKey("minimum"))
                lo = BigInteger.Max(lo, Integer(intrinsic["minimum"]!));
            if (intrinsic.ContainsKey("maximum"))
                hi = BigInteger.Min(hi, Integer(intrinsic["maximum"]!));
            foreach (string key in new[] { "minimum", "maximum" }) {
                if (!context.ContainsKey(key))
                    continue;
                var value = Bound(context[key], at + "/constraints/" + key);
                Need(key == "minimum" ? value >= lo : value <= hi, "CONSTRAINT_WEAKENING", at + "/constraints/" + key);
                if (key == "minimum")
                    lo = value;
                else
                    hi = value;
                effective[key] = value.ToString(CultureInfo.InvariantCulture);
            }
            Need(lo <= hi, "CONSTRAINT_CONTRADICTION", at + "/constraints");
            string? physicalType = col["scalarType"]?.GetValue<string>();
            if (HasRange(physicalType)) {
                var (plo, phi) = Ranges[physicalType!];
                var extent = Object(col, "integerRange");
                if (extent.ContainsKey("minimum"))
                    plo = BigInteger.Max(plo, Integer(extent["minimum"]!));
                if (extent.ContainsKey("maximum"))
                    phi = BigInteger.Min(phi, Integer(extent["maximum"]!));
                Need(plo <= lo && hi <= phi, "DOMAIN_NOT_REPRESENTABLE", at, "physical-incompatible");
                result["compatibility"] = "compatible";
            }
        }
        foreach (string key in new[] { "label", "description", "hint" }) {
            if (overlay.ContainsKey(key))
                result[key] = Copy(overlay[key]);
        }
        return result;
    }

    private JsonObject Projection(string id, IReadOnlyList<string> stack, Dictionary<string, Entry> index,
        SortedDictionary<string, JsonObject> models, SortedDictionary<string, JsonObject> output)
    {
        var (doc, at) = index[id];
        Need(!stack.Contains(id, StringComparer.Ordinal), "REFERENCE_CYCLE", at + "/source");
        Need(stack.Count < 64, "LIMIT", at + "/source");
        var operations = Arr(doc["operations"]);
        Need(operations.All(v => Text(v) is "LIST" or "GET"), "OPERATION_UNSUPPORTED", at + "/operations",
            "operation-not-allowed");
        var source = Object(doc, "source");
        string entity;
        JsonArray fields;
        if (source.ContainsKey("entity")) {
            entity = Text(source["entity"]);
            Need(models.ContainsKey(entity), "REFERENCE_NOT_FOUND", at + "/source");
            fields = Arr(models[entity]["fields"]);
        } else {
            string parentId = Text(source["projection"]);
            Need(index.ContainsKey(parentId), "REFERENCE_NOT_FOUND", at + "/source");
            var parent = Projection(parentId, [.. stack, id], index, models, output);
            entity = Text(parent["entity"]);
            fields = Arr(parent["fields"]);
            Need(operations.All(op => Arr(parent["operations"]).Any(p => Text(p) == Text(op))),
                "OPERATION_EXPANSION", at + "/operations");
        }
        var available = fields.ToDictionary(f => Text(f!["name"]), f => Obj(f), StringComparer.Ordinal);
        Need(Arr(doc["fields"]).All(name => available.ContainsKey(Text(name))), "FIELD_NOT_EXPOSED", at + "/fields");
        var selected = new JsonArray();
        foreach (var name in Arr(doc["fields"])) {
            var field = available[Text(name)];
            var value = new JsonObject();
            foreach (var (key, v) in field) {
                if (key is not ("physical" or "intrinsicConstraints" or "contextualConstraints"))
                    value[key] = Copy(v);
            }
            if (field["physical"] is JsonObject physical && physical.ContainsKey("nullable")) {
                value["nullable"] = Copy(physical["nullable"]);
            }
            selected.Add(value);
        }
        return output[id] = new() {
            ["id"] = id,
            ["source"] = Copy(source),
            ["entity"] = entity,
            ["fields"] = selected,
            ["operations"] = Copy(operations)
        };
    }

    // Interpreter for keywords used by the pinned authoring schemas, not a scalar data validator.
    private static bool Schema(JsonNode? value, JsonObject schema, JsonObject root)
    {
        if (schema.ContainsKey("$ref")) {
            JsonNode? reference = root;
            foreach (string part in Text(schema["$ref"])[2..].Split('/'))
                reference = reference![part];
            return Schema(value, Obj(reference), root);
        }
        if (schema["oneOf"] is JsonArray options && options.Count(o => Schema(value, Obj(o), root)) != 1)
            return false;
        if (schema.ContainsKey("const") && !JsonNode.DeepEquals(value, schema["const"]))
            return false;
        if (schema["enum"] is JsonArray choices && !choices.Any(o => JsonNode.DeepEquals(o, value)))
            return false;
        JsonValueKind kind = value?.GetValueKind() ?? JsonValueKind.Null;
        if (schema.ContainsKey("type")) {
            bool ok = Text(schema["type"]) switch {
                "object" => kind == JsonValueKind.Object,
                "array" => kind == JsonValueKind.Array,
                "string" => kind == JsonValueKind.String,
                "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
                "integer" => kind == JsonValueKind.Number && Numeric(value!).IsInteger,
                _ => throw new InvalidOperationException("Unknown pinned schema type")
            };
            if (!ok)
                return false;
        }
        if (kind == JsonValueKind.String) {
            string s = Text(value);
            int length = s.EnumerateRunes().Count();
            if (schema.ContainsKey("minLength") && length < schema["minLength"]!.GetValue<int>())
                return false;
            if (schema.ContainsKey("maxLength") && length > schema["maxLength"]!.GetValue<int>())
                return false;
            if (schema.ContainsKey("pattern") && !Regex.IsMatch(s, Text(schema["pattern"]),
                RegexOptions.CultureInvariant))
                return false;
        }
        if (kind == JsonValueKind.Number && schema.ContainsKey("minimum")
            && Numeric(value!).CompareTo(Numeric(schema["minimum"]!)) < 0)
            return false;
        if (value is JsonObject obj) {
            if (schema["required"] is JsonArray required && required.Any(k => !obj.ContainsKey(Text(k))))
                return false;
            if (schema.ContainsKey("minProperties") && obj.Count < schema["minProperties"]!.GetValue<int>())
                return false;
            foreach (var (key, v) in obj) {
                if (schema["propertyNames"] is JsonObject keySchema
                    && !Schema(JsonValue.Create(key), keySchema, root)) {
                    return false;
                }
                var properties = Object(schema, "properties");
                var child = properties.ContainsKey(key) ? properties[key] : schema["additionalProperties"];
                if (child?.GetValueKind() == JsonValueKind.False)
                    return false;
                if (child is JsonObject childSchema && !Schema(v, childSchema, root))
                    return false;
            }
        }
        if (value is JsonArray array) {
            if (schema.ContainsKey("minItems") && array.Count < schema["minItems"]!.GetValue<int>())
                return false;
            if (schema["uniqueItems"]?.GetValue<bool>() == true) {
                for (int i = 0; i < array.Count; i++) {
                    for (int j = 0; j < i; j++)
                        if (JsonNode.DeepEquals(array[i], array[j]))
                            return false;
                }
            }
            if (schema["items"] is JsonObject items && array.Any(v => !Schema(v, items, root)))
                return false;
        }
        return true;
    }
}