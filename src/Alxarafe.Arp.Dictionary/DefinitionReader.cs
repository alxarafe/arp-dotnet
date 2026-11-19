using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Alxarafe.Arp.Dictionary;

internal static class DefinitionReader
{
    private static readonly HashSet<string> Keys = ["id", "type", "constraints", "enumValues", "label", "description"];
    private static readonly IReadOnlyDictionary<string, ScalarType> Types = System.Enum.GetValues<ScalarType>().ToDictionary(t => t.ToString().ToUpperInvariant(), StringComparer.Ordinal);
    private static readonly Regex Identity = new(@"\A[A-Za-z][A-Za-z0-9_-]*(\.[A-Za-z][A-Za-z0-9_-]*)+\z", RegexOptions.NonBacktracking);

    internal static ElementDescriptor Read(JsonElement element)
    {
        if (element.EnumerateObject().Any(p => !Keys.Contains(p.Name)))
            throw Rejection.Element();
        if (!element.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String)
            throw Rejection.Element();
        string id = idValue.GetString()!;
        if (id.Length > 128 || !Identity.IsMatch(id))
            throw Rejection.Element();
        if (!element.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String || !Types.TryGetValue(typeValue.GetString()!, out var type))
            throw Rejection.Element();
        string? label = OptionalText(element, "label"), description = OptionalText(element, "description");
        var constraints = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        PortablePattern? pattern = null;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (Numbers.IsNumeric(type))
            allowed.UnionWith(["minimum", "maximum", "multipleOf", "exclusiveMinimum", "exclusiveMaximum"]);
        if (type == ScalarType.Decimal)
            allowed.UnionWith(["precision", "scale"]);
        if (Numbers.IsText(type) || type == ScalarType.Binary)
            allowed.UnionWith(["minimumLength", "maximumLength"]);
        if (Numbers.IsText(type))
            allowed.Add("pattern");
        if (element.TryGetProperty("constraints", out var container))
        {
            if (container.ValueKind != JsonValueKind.Object)
                throw Rejection.Element();
            foreach (var property in container.EnumerateObject())
            {
                string key = property.Name;
                var value = property.Value;
                if (!allowed.Contains(key))
                    throw Rejection.Element();
                switch (key)
                {
                    case "minimum":
                    case "maximum":
                    case "multipleOf":
                        ExactNumber number;
                        try
                        { number = Numbers.ParsePortable(value.ValueKind == JsonValueKind.String ? value.GetString() : null, type); } catch (Rejection e) { if (e.Code == DictionaryErrorCode.LimitExceeded) throw; throw Rejection.Element(); }
                        if (!Numbers.InRange(number, type) || key == "multipleOf" && number.Sign <= 0)
                            throw Rejection.Element();
                        break;
                    case "exclusiveMinimum":
                    case "exclusiveMaximum":
                        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw Rejection.Element();
                        break;
                    case "pattern":
                        if (value.ValueKind != JsonValueKind.String)
                            throw Rejection.Element();
                        pattern = new(value.GetString()!);
                        break;
                    default:
                        Numbers.Metadata(value);
                        break;
                }
                constraints.Add(key, value.Clone());
            }
        }
        if (constraints.ContainsKey("precision") != constraints.ContainsKey("scale"))
            throw Rejection.Element();
        if (constraints.TryGetValue("precision", out var precision))
        {
            var p = Numbers.Metadata(precision);
            var s = Numbers.Metadata(constraints["scale"]);
            if (p.CompareTo(ExactNumber.FromInt(1)) < 0 || p.CompareTo(ExactNumber.FromInt(4096)) > 0 || s.CompareTo(p) > 0)
                throw Rejection.Element();
        }
        if (constraints.TryGetValue("minimumLength", out var minLength) && constraints.TryGetValue("maximumLength", out var maxLength)
            && Numbers.Metadata(minLength).CompareTo(Numbers.Metadata(maxLength)) > 0)
            throw Rejection.Element();
        foreach (string endpoint in new[] { "Minimum", "Maximum" })
            if (constraints.TryGetValue("exclusive" + endpoint, out var flag) && flag.GetBoolean() && !constraints.ContainsKey(endpoint.ToLowerInvariant()))
                throw Rejection.Element();
        if (constraints.TryGetValue("minimum", out var min) && constraints.TryGetValue("maximum", out var max))
        {
            int compare = ExactNumber.Parse(min.GetString()!).CompareTo(ExactNumber.Parse(max.GetString()!));
            if (compare > 0 || compare == 0 && (IsTrue(constraints, "exclusiveMinimum") || IsTrue(constraints, "exclusiveMaximum")))
                throw Rejection.Element();
        }
        var literals = ImmutableArray.CreateBuilder<string>();
        if (type == ScalarType.Enum)
        {
            if (!element.TryGetProperty("enumValues", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
                throw Rejection.Element();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } literal || !seen.Add(literal))
                    throw Rejection.Element();
                literals.Add(literal);
            }
        } else if (element.TryGetProperty("enumValues", out _))
            throw Rejection.Element();
        var descriptor = new ElementDescriptor(id, type, constraints.ToImmutable(), literals.ToImmutable(), label, description, pattern);
        foreach (string literal in descriptor.EnumValues)
        {
            var result = ScalarValidator.Validate(descriptor, literal);
            if (!result.IsValid)
            {
                if (result.Issues.Any(i => i.Code == DictionaryErrorCode.LimitExceeded))
                    throw Rejection.Limit();
                throw Rejection.Element();
            }
        }
        return descriptor;
    }
    private static bool IsTrue(ImmutableDictionary<string, JsonElement>.Builder values, string key) => values.TryGetValue(key, out var value) && value.GetBoolean();
    private static string? OptionalText(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text)
            throw Rejection.Element();
        return text;
    }
}
