using System.Collections.Immutable;
using System.Text.Json;

namespace Alxarafe.Arp.Dictionary;

/// <summary>A validated immutable descriptor. JSON numeric metadata retains its exact original numeric kind.</summary>
public sealed class ElementDescriptor
{
    public string Id { get; }
    public ScalarType Type { get; }
    public ImmutableDictionary<string, JsonElement> Constraints { get; }
    public ImmutableArray<string> EnumValues { get; }
    public string? Label { get; }
    public string? Description { get; }
    internal PortablePattern? Pattern { get; }

    internal ElementDescriptor(string id, ScalarType type, ImmutableDictionary<string, JsonElement> constraints,
        ImmutableArray<string> enumValues, string? label, string? description, PortablePattern? pattern)
    { Id = id; Type = type; Constraints = constraints; EnumValues = enumValues; Label = label; Description = description; Pattern = pattern; }

    public JsonElement ToProjection() => JsonProjection.Create(writer =>
    {
        writer.WriteStartObject(); writer.WriteString("id", Id); writer.WriteString("type", Type.ToString().ToUpperInvariant());
        writer.WritePropertyName("constraints"); writer.WriteStartObject();
        foreach (var pair in Constraints) { writer.WritePropertyName(pair.Key); pair.Value.WriteTo(writer); }
        writer.WriteEndObject();
        if (Type == ScalarType.Enum)
        {
            writer.WritePropertyName("enumValues"); writer.WriteStartArray();
            foreach (string value in EnumValues) writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
        if (Label is not null) writer.WriteString("label", Label);
        if (Description is not null) writer.WriteString("description", Description);
        writer.WriteEndObject();
    });
    public string ToJson() => ToProjection().GetRawText();
}
