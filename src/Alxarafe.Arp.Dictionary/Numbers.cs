using System.Text.Json;
using System.Text.RegularExpressions;

namespace Alxarafe.Arp.Dictionary;

internal static class Numbers
{
    private static readonly Regex Signed = new(@"\A-?(0|[1-9][0-9]*)\z", RegexOptions.NonBacktracking);
    private static readonly Regex Unsigned = new(@"\A(0|[1-9][0-9]*)\z", RegexOptions.NonBacktracking);
    private static readonly Regex Decimal = new(@"\A-?(0|[1-9][0-9]*)(\.[0-9]+)?\z", RegexOptions.NonBacktracking);
    private static readonly IReadOnlyDictionary<ScalarType, (ExactNumber Min, ExactNumber Max)> Ranges =
        new Dictionary<ScalarType, (ExactNumber, ExactNumber)>
        {
            [ScalarType.Int8] = (ExactNumber.Parse("-128"), ExactNumber.Parse("127")),
            [ScalarType.UInt8] = (ExactNumber.Parse("0"), ExactNumber.Parse("255")),
            [ScalarType.Int16] = (ExactNumber.Parse("-32768"), ExactNumber.Parse("32767")),
            [ScalarType.UInt16] = (ExactNumber.Parse("0"), ExactNumber.Parse("65535")),
            [ScalarType.Int32] = (ExactNumber.Parse("-2147483648"), ExactNumber.Parse("2147483647")),
            [ScalarType.UInt32] = (ExactNumber.Parse("0"), ExactNumber.Parse("4294967295")),
            [ScalarType.Int64] = (ExactNumber.Parse("-9223372036854775808"), ExactNumber.Parse("9223372036854775807"))
        };
    internal static (ExactNumber Min, ExactNumber Max) IntegerRange(ScalarType type) => Ranges[type];
    internal static bool IsInteger(ScalarType type) => Ranges.ContainsKey(type);
    internal static bool IsNumeric(ScalarType type) => type == ScalarType.Decimal || IsInteger(type);
    internal static bool IsText(ScalarType type) => type is ScalarType.String or ScalarType.Text or ScalarType.Enum;
    internal static ExactNumber ParsePortable(object? value, ScalarType type)
    {
        Regex grammar = type == ScalarType.Decimal ? Decimal : type is ScalarType.UInt8 or ScalarType.UInt16 or ScalarType.UInt32 ? Unsigned : Signed;
        if (value is not string text || !grammar.IsMatch(text))
            throw Rejection.Type();
        int digits = 0;
        foreach (char c in text)
            if (c is >= '0' and <= '9')
                digits++;
        if (digits > 4096)
            throw Rejection.Limit();
        return ExactNumber.Parse(text);
    }
    internal static bool InRange(ExactNumber number, ScalarType type) => !Ranges.TryGetValue(type, out var range) || number.CompareTo(range.Min) >= 0 && number.CompareTo(range.Max) <= 0;
    internal static ExactNumber Metadata(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
            throw Rejection.Element();
        var number = ExactNumber.Parse(value.GetRawText());
        if (!number.IsInteger || number.Sign < 0)
            throw Rejection.Element();
        return number;
    }
}
