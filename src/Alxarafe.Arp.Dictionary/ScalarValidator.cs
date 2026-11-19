using System.Globalization;
using System.Text.RegularExpressions;

namespace Alxarafe.Arp.Dictionary;

internal static class ScalarValidator
{
    private static readonly Regex DateGrammar = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.NonBacktracking);
    private static readonly Regex TimeGrammar = new(@"\A([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,7}))?\z", RegexOptions.NonBacktracking);
    private static readonly Regex InstantGrammar = new(@"\A([0-9]{4}-[0-9]{2}-[0-9]{2})T([0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?)(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.NonBacktracking);
    private static readonly Regex UuidGrammar = new(@"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z", RegexOptions.NonBacktracking);

    internal static ValidationResult Validate(ElementDescriptor descriptor, object? value)
    {
        string id = descriptor.Id;
        try
        {
            object normalized = Normalize(descriptor.Type, value);
            var issues = new List<DictionaryIssue>();
            var c = descriptor.Constraints;
            void Fail(string constraint) => issues.Add(new(DictionaryErrorCode.ValueConstraint, id, constraint));
            bool Flag(string key) => c.TryGetValue(key, out var flag) && flag.GetBoolean();
            if (Numbers.IsNumeric(descriptor.Type))
            {
                var number = ExactNumber.Parse((string) normalized);
                if (!Numbers.InRange(number, descriptor.Type))
                    Fail("typeRange");
                if (c.TryGetValue("minimum", out var minimum))
                { int compare = number.CompareTo(ExactNumber.Parse(minimum.GetString()!)); if (compare < 0 || compare == 0 && Flag("exclusiveMinimum")) Fail("minimum"); }
                if (c.TryGetValue("maximum", out var maximum))
                { int compare = number.CompareTo(ExactNumber.Parse(maximum.GetString()!)); if (compare > 0 || compare == 0 && Flag("exclusiveMaximum")) Fail("maximum"); }
                if (c.TryGetValue("multipleOf", out var divisor) && !number.IsMultipleOf(ExactNumber.Parse(divisor.GetString()!)))
                    Fail("multipleOf");
                if (c.TryGetValue("precision", out var precision))
                {
                    int p = Numbers.Metadata(precision).ToInt32(), scale = Numbers.Metadata(c["scale"]).ToInt32();
                    if (number.Abs().CompareTo(ExactNumber.PowerOfTen(p - scale)) >= 0)
                        Fail("precision");
                    if (number.Exponent < -scale)
                        Fail("scale");
                }
            }
            if (Numbers.IsText(descriptor.Type) || descriptor.Type == ScalarType.Binary)
            {
                string text = (string) value!;
                int length = descriptor.Type == ScalarType.Binary ? Convert.FromBase64String(text).Length : UnicodeText.Length(text);
                var count = ExactNumber.FromInt(length);
                if (c.TryGetValue("minimumLength", out var min) && count.CompareTo(Numbers.Metadata(min)) < 0)
                    Fail("minimumLength");
                if (c.TryGetValue("maximumLength", out var max) && count.CompareTo(Numbers.Metadata(max)) > 0)
                    Fail("maximumLength");
                if (descriptor.Pattern is not null && !descriptor.Pattern.Matches(text))
                    Fail("pattern");
                if (descriptor.Type == ScalarType.Enum && !descriptor.EnumValues.Contains(text, StringComparer.Ordinal))
                    Fail("enumValues");
            }
            return issues.Count == 0 ? new(normalized, []) : new(null, issues);
        } catch (Rejection rejection) { return new(null, [new(rejection.Code, id)]); }
    }

    private static object Normalize(ScalarType type, object? value)
    {
        if (type == ScalarType.Boolean)
            return value is bool boolean ? boolean : throw Rejection.Type();
        if (value is not string text || !UnicodeText.IsValid(text))
            throw Rejection.Type();
        if (Numbers.IsNumeric(type))
            return Numbers.ParsePortable(text, type).ToPlainString();
        if (Numbers.IsText(type))
            return text;
        switch (type)
        {
            case ScalarType.Uuid:
                if (!UuidGrammar.IsMatch(text))
                    throw Rejection.Type();
                return text.ToLowerInvariant();
            case ScalarType.Binary:
                byte[] decoded;
                try
                { decoded = Convert.FromBase64String(text); } catch (FormatException) { throw Rejection.Type(); }
                if (!string.Equals(Convert.ToBase64String(decoded), text, StringComparison.Ordinal))
                    throw Rejection.Type();
                return text;
            case ScalarType.Date:
                ParseDate(text);
                return text;
            case ScalarType.Time:
                return ParseTime(text).ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            case ScalarType.DateTime:
                var match = InstantGrammar.Match(text);
                if (!match.Success)
                    throw Rejection.Type();
                var date = ParseDate(match.Groups[1].Value);
                var time = ParseTime(match.Groups[2].Value);
                string offset = match.Groups[3].Value;
                int minutes = 0;
                if (offset != "Z")
                {
                    int hour = int.Parse(offset.AsSpan(1, 2), CultureInfo.InvariantCulture), minute = int.Parse(offset.AsSpan(4, 2), CultureInfo.InvariantCulture);
                    if (hour > 14 || minute > 59 || hour == 14 && minute != 0)
                        throw Rejection.Type();
                    minutes = (hour * 60 + minute) * (offset[0] == '-' ? -1 : 1);
                }
                long ticks = date.ToDateTime(time, DateTimeKind.Unspecified).Ticks - minutes * TimeSpan.TicksPerMinute;
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                    throw Rejection.Type();
                return new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
            default:
                throw new InvalidOperationException("Unsupported validated scalar type");
        }
    }
    private static DateOnly ParseDate(string text)
    {
        if (!DateGrammar.IsMatch(text) || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Rejection.Type();
        return date;
    }
    private static TimeOnly ParseTime(string text)
    {
        var match = TimeGrammar.Match(text);
        if (!match.Success)
            throw Rejection.Type();
        int hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), minute = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), second = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59 || second > 59)
            throw Rejection.Type();
        long fraction = long.Parse(match.Groups[4].Value.PadRight(7, '0'), CultureInfo.InvariantCulture);
        return new TimeOnly(hour, minute, second).Add(TimeSpan.FromTicks(fraction));
    }
}
