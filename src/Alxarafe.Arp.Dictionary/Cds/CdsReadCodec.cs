using System.Text.Json;

namespace Alxarafe.Arp.Dictionary;

/// <summary>Read boundaries reuse the portable parser and scalar codecs, without editing constraints.</summary>
public static class CdsReadCodec
{
    public static JsonElement ParseRequest(ReadOnlyMemory<byte> bytes) =>
        new SourceParser(new CatalogLimits(maxDepth: 64)).Parse(
            new SourceSnapshot("host", "request", SourceFormat.Json, bytes.Span));

    public static ValidationResult Decode(ScalarType type, object value) =>
        ScalarValidator.Validate(new ElementDescriptor("stored", type, [], [], null, null, null), value);

    public static int? PageLimit(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
            return null;
        var number = ExactNumber.Parse(value.GetRawText());
        return number.IsInteger && number.CompareTo(ExactNumber.FromInt(1)) >= 0
            && number.CompareTo(ExactNumber.FromInt(100)) <= 0 ? number.ToInt32() : null;
    }
}
