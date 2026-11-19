using System.Text.Json;

namespace Alxarafe.Arp.Dictionary;

/// <summary>Immutable complete outcome with an owned exact JSON representation.</summary>
public sealed class CdsReadResult
{
    private readonly JsonElement projection;
    internal CdsReadResult(JsonElement value)
    {
        projection = value.Clone();
    }
    public bool IsValid => projection.GetProperty("valid").GetBoolean();
    public JsonElement ToProjection() => projection;
    public string ToJson() => projection.GetRawText();
    public static CdsReadResult Failure(string code, string at, string family = "definition-invalid") =>
        new(JsonSerializer.SerializeToElement(new
        {
            valid = false,
            issues = new[] { new { family, code = "CDS_READ_" + code, at } }
        }));
}
