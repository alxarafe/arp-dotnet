using System.Buffers;
using System.Text.Json;

namespace Alxarafe.Arp.Dictionary;

internal static class JsonProjection
{
    internal static JsonElement Create(Action<Utf8JsonWriter> write)
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes, new JsonWriterOptions { MaxDepth = int.MaxValue }))
        { write(writer); writer.Flush(); }
        using var document = JsonDocument.Parse(bytes.WrittenMemory, new JsonDocumentOptions { MaxDepth = int.MaxValue });
        return document.RootElement.Clone();
    }
    internal static JsonElement Outcome(bool valid, IEnumerable<DictionaryIssue> issues) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["valid"] = valid, ["issues"] = issues.Select(i => i.ToProjection()).ToArray() });
}
