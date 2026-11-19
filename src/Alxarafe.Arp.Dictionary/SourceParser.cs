using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Alxarafe.Arp.Dictionary;

internal sealed class SourceParser(CatalogLimits limits)
{
    internal JsonElement Parse(SourceSnapshot source)
    {
        if (source.Bytes.Length > limits.MaxDocumentBytes)
            throw Rejection.Limit();
        try
        {
            string text = UnicodeText.Utf8.GetString(source.Bytes.Span);
            return source.Format == SourceFormat.Json ? ParseJson(source.Bytes.Span) : ParseYaml(text);
        } catch (JsonException) { throw Rejection.Document(); } catch (DecoderFallbackException) { throw Rejection.Document(); } catch (YamlException) { throw Rejection.Document(); }
    }

    private JsonElement ParseJson(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = int.MaxValue });
        var stack = new List<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    if (stack.Count >= limits.MaxDepth)
                        throw Rejection.Limit();
                    stack.Add(reader.TokenType == JsonTokenType.StartObject ? new(StringComparer.Ordinal) : null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case JsonTokenType.PropertyName:
                    string key = UnicodeText.DecodeJson(reader.ValueSpan);
                    if (!stack[^1]!.Add(key))
                        throw Rejection.Document();
                    break;
                case JsonTokenType.String:
                    UnicodeText.DecodeJson(reader.ValueSpan);
                    break;
                case JsonTokenType.Number:
                    CheckNumber(Encoding.ASCII.GetString(reader.ValueSpan));
                    break;
            }
        }
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = limits.MaxDepth });
        return document.RootElement.Clone();
    }

    private JsonElement ParseYaml(string text)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\r(?!\n)|[\u0085\u2028\u2029]"))
            throw Rejection.Document();
        var events = new List<ParsingEvent>();
        var scalarSpans = new List<(long Start, long End)>();
        var parser = new Parser(new StringReader(text));
        int depth = 0, documents = 0;
        while (parser.MoveNext())
        {
            var item = parser.Current!;
            if (item is Comment)
                continue;
            if (item is DocumentStart start)
            {
                if (++documents != 1 || start.Version is not null)
                    throw Rejection.Document();
            }
            if (item is AnchorAlias)
                throw Rejection.Document();
            if (item is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                throw Rejection.Document();
            if (item is MappingStart or SequenceStart)
            { if (++depth > limits.MaxDepth) throw Rejection.Limit(); }
            if (item is MappingEnd or SequenceEnd)
                depth--;
            if (item is Scalar scalar)
            {
                if (!UnicodeText.IsValid(scalar.Value))
                    throw Rejection.Document();
                string raw = text[(int) scalar.Start.Index..(int) scalar.End.Index];
                if (scalar.Style is ScalarStyle.Plain or ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted)
                {
                    if (raw.Contains('\n') || raw.Contains('\r'))
                        throw Rejection.Document();
                    if (scalar.Style == ScalarStyle.DoubleQuoted)
                    {
                        using var quoted = JsonDocument.Parse(raw);
                        UnicodeText.DecodeJson(UnicodeText.Utf8.GetBytes(raw[1..^1]));
                    }
                    if (scalar.Style == ScalarStyle.Plain && ExactNumber.IsJsonLexeme(scalar.Value))
                        CheckNumber(scalar.Value);
                } else if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
                {
                    string header = raw.Split('\n')[0].TrimEnd('\r');
                    if (!System.Text.RegularExpressions.Regex.IsMatch(header, @"\A[|>][+-]?(?:[ \t]+(?:#.*)?)?\z"))
                        throw Rejection.Document();
                }
                scalarSpans.Add((scalar.Start.Index, scalar.End.Index));
            }
            events.Add(item);
        }
        if (documents != 1)
            throw Rejection.Document();
        // Unknown directives can be discarded by a parser. A column-zero '%' is
        // forbidden unless its original bytes belong to an actual scalar token.
        int scalarIndex = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '%' || i > 0 && text[i - 1] is not ('\n' or '\r'))
                continue;
            while (scalarIndex < scalarSpans.Count && scalarSpans[scalarIndex].End <= i)
                scalarIndex++;
            if (scalarIndex == scalarSpans.Count || i < scalarSpans[scalarIndex].Start)
                throw Rejection.Document();
        }
        int index = 0;
        if (events[index++] is not StreamStart || events[index++] is not DocumentStart)
            throw Rejection.Document();
        var projection = JsonProjection.Create(writer => WriteNode(events, ref index, writer));
        if (events[index++] is not DocumentEnd || events[index++] is not StreamEnd || index != events.Count)
            throw Rejection.Document();
        return projection;
    }

    private static void WriteNode(List<ParsingEvent> events, ref int index, Utf8JsonWriter writer)
    {
        switch (events[index++])
        {
            case MappingStart:
                writer.WriteStartObject();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (events[index] is not MappingEnd)
                {
                    if (events[index++] is not Scalar key || key.Value == "<<" || !seen.Add(key.Value))
                        throw Rejection.Document();
                    // The portable object model requires string keys; only quoted
                    // or string-resolving plain scalars qualify.
                    if (key.Style == ScalarStyle.Plain && IsTypedScalar(key.Value))
                        throw Rejection.Document();
                    writer.WritePropertyName(key.Value);
                    WriteNode(events, ref index, writer);
                }
                index++;
                writer.WriteEndObject();
                break;
            case SequenceStart:
                writer.WriteStartArray();
                while (events[index] is not SequenceEnd)
                    WriteNode(events, ref index, writer);
                index++;
                writer.WriteEndArray();
                break;
            case Scalar scalar:
                if (scalar.Style == ScalarStyle.Plain)
                {
                    if (scalar.Value is "null" or "")
                    { writer.WriteNullValue(); break; }
                    if (scalar.Value is "true" or "false")
                    { writer.WriteBooleanValue(scalar.Value == "true"); break; }
                    if (ExactNumber.IsJsonLexeme(scalar.Value))
                    { writer.WriteRawValue(scalar.Value); break; }
                }
                writer.WriteStringValue(scalar.Value);
                break;
            default:
                throw Rejection.Document();
        }
    }

    private static void CheckNumber(string token)
    {
        if (token.Length > 256)
            throw Rejection.Limit();
        int e = token.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            string exponent = token[(e + 1)..].TrimStart('+', '-').TrimStart('0');
            if (exponent.Length > 3 || exponent.Length > 0 && int.Parse(exponent, System.Globalization.CultureInfo.InvariantCulture) > 128)
                throw Rejection.Limit();
        }
        if (ExactNumber.Parse(token).Abs().CompareTo(ExactNumber.PowerOfTen(128)) >= 0)
            throw Rejection.Limit();
    }

    private static bool IsTypedScalar(string value) => value is "true" or "false" or "null" or "" || ExactNumber.IsJsonLexeme(value);
}
