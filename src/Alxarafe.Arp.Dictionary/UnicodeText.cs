using System.Globalization;
using System.Text;

namespace Alxarafe.Arp.Dictionary;

internal static class UnicodeText
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static bool IsValid(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            { if (++i == text.Length || !char.IsLowSurrogate(text[i])) return false; } else if (char.IsLowSurrogate(text[i]))
                return false;
        }
        return true;
    }
    internal static int Length(string text)
    {
        int length = 0;
        for (int i = 0; i < text.Length; i++, length++)
            if (char.IsHighSurrogate(text[i]))
                i++;
        return length;
    }

    // Decode raw JSON string contents without replacement fallback. The test boundary
    // may retain an intentionally unpaired surrogate; source parsing always requires valid UTF-16.
    internal static string DecodeJson(ReadOnlySpan<byte> raw, bool requireValid = true)
    {
        string text = Utf8.GetString(raw);
        var decoded = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\')
            { decoded.Append(c); continue; }
            if (++i >= text.Length)
                throw Rejection.Document();
            char escape = text[i];
            if (escape == 'u')
            {
                if (i + 4 >= text.Length || !ushort.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                    throw Rejection.Document();
                decoded.Append((char) code);
                i += 4;
            } else
                decoded.Append(escape switch { '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => throw Rejection.Document() });
        }
        string value = decoded.ToString();
        if (requireValid && !IsValid(value))
            throw Rejection.Document();
        return value;
    }
}
