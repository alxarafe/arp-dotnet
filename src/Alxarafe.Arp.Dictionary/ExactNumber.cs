using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Alxarafe.Arp.Dictionary;

/// <summary>Coefficient times ten to an arbitrary integer exponent; never binary floating point.</summary>
internal readonly struct ExactNumber : IComparable<ExactNumber>
{
    private static readonly Regex JsonGrammar = new(@"\A-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?\z", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    private readonly string digits;
    internal BigInteger Coefficient { get; }
    internal BigInteger Exponent { get; }
    internal int Sign => Coefficient.Sign;
    internal bool IsInteger => Sign == 0 || Exponent >= 0;

    private ExactNumber(BigInteger coefficient, BigInteger exponent, string digits)
    { Coefficient = coefficient; Exponent = coefficient.IsZero ? BigInteger.Zero : exponent; this.digits = digits; }

    internal static bool IsJsonLexeme(string text) => JsonGrammar.IsMatch(text);
    internal static ExactNumber Parse(string text)
    {
        // Callers supply grammar-checked decimal/JSON numeric lexemes.
        int expIndex = text.IndexOfAny(['e', 'E']);
        string mantissa = expIndex < 0 ? text : text[..expIndex];
        BigInteger exponent = expIndex < 0 ? BigInteger.Zero : BigInteger.Parse(text.AsSpan(expIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int dot = mantissa.IndexOf('.');
        if (dot >= 0)
            exponent -= mantissa.Length - dot - 1;
        bool negative = mantissa[0] == '-';
        string all = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('-').TrimStart('0');
        if (all.Length == 0)
            return new(BigInteger.Zero, BigInteger.Zero, "0");
        string significant = all.TrimEnd('0');
        exponent += all.Length - significant.Length;
        BigInteger coefficient = BigInteger.Parse(significant, CultureInfo.InvariantCulture);
        return new(negative ? -coefficient : coefficient, exponent, significant);
    }
    internal static ExactNumber FromInt(int value) => Parse(value.ToString(CultureInfo.InvariantCulture));
    internal static ExactNumber PowerOfTen(int exponent) => new(BigInteger.One, exponent, "1");
    internal ExactNumber Abs() => new(BigInteger.Abs(Coefficient), Exponent, digits);
    public int CompareTo(ExactNumber other)
    {
        if (Sign != other.Sign)
            return Sign.CompareTo(other.Sign);
        if (Sign == 0)
            return 0;
        int order = (Exponent + digits.Length).CompareTo(other.Exponent + other.digits.Length);
        if (order != 0)
            return Sign * order;
        int length = Math.Max(digits.Length, other.digits.Length);
        return Sign * Math.Sign(string.CompareOrdinal(digits.PadRight(length, '0'), other.digits.PadRight(length, '0')));
    }
    internal int ToInt32() => checked((int) (Coefficient * BigInteger.Pow(10, checked((int) Exponent))));
    internal bool IsMultipleOf(ExactNumber divisor)
    {
        int difference = checked((int) (Exponent - divisor.Exponent)); // Both operands are portable <=4096-digit lexemes.
        BigInteger numerator = Coefficient, denominator = divisor.Coefficient;
        if (difference >= 0)
            numerator *= BigInteger.Pow(10, difference);
        else
            denominator *= BigInteger.Pow(10, -difference);
        return numerator % denominator == 0;
    }
    internal string ToPlainString()
    {
        if (Sign == 0)
            return "0";
        int point = checked(digits.Length + (int) Exponent); // Only bounded portable scalar values are expanded.
        string magnitude = point <= 0 ? "0." + new string('0', -point) + digits
            : point >= digits.Length ? digits + new string('0', point - digits.Length)
            : digits.Insert(point, ".");
        return Sign < 0 ? "-" + magnitude : magnitude;
    }
}
