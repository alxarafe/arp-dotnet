using System.Text;
using System.Collections.Immutable;

namespace Alxarafe.Arp.Dictionary;

/// <summary>Formal ARP parser and bounded state-position matcher; no host regex dialect.</summary>
internal sealed class PortablePattern
{
    private sealed record Atom(ImmutableHashSet<int> Characters, int Minimum, int Maximum);
    private readonly ImmutableArray<Atom> atoms;
    internal PortablePattern(string pattern)
    {
        if (UnicodeText.Length(pattern) > 512) throw Rejection.Limit();
        if (pattern.Length == 0 || pattern.Any(c => c is < ' ' or > '~')) throw Rejection.Element();
        int index = pattern[0] == '^' ? 1 : 0, end = pattern.Length, slashes = 0;
        for (int j = end - 2; j >= 0 && pattern[j] == '\\'; j--) slashes++;
        if (pattern[^1] == '$' && slashes % 2 == 0) end--;
        var parsed = ImmutableArray.CreateBuilder<Atom>();
        while (index < end)
        {
            var chars = ImmutableHashSet.CreateBuilder<int>();
            char c = pattern[index++];
            if (c == '\\') chars.Add(Escape(pattern, ref index, end));
            else if (c == '[')
            {
                var members = new List<(char Character, bool RawHyphen)>();
                while (index < end && pattern[index] != ']')
                {
                    char member = pattern[index++];
                    if (member == '\\') members.Add((Escape(pattern, ref index, end), false));
                    else { if (member is '[' or '^') throw Rejection.Element(); members.Add((member, member == '-')); }
                }
                if (index == end || members.Count == 0) throw Rejection.Element();
                index++;
                if (members[0].RawHyphen) { chars.Add('-'); members.RemoveAt(0); }
                if (members.Count > 0 && members[^1].RawHyphen) { chars.Add('-'); members.RemoveAt(members.Count - 1); }
                for (int j = 0; j < members.Count;)
                {
                    var low = members[j]; if (low.RawHyphen) throw Rejection.Element();
                    if (j + 1 < members.Count && members[j + 1].RawHyphen)
                    {
                        if (j + 2 >= members.Count || members[j + 2].RawHyphen || low.Character > members[j + 2].Character) throw Rejection.Element();
                        for (int code = low.Character; code <= members[j + 2].Character; code++) chars.Add(code);
                        j += 3;
                    }
                    else { chars.Add(low.Character); j++; }
                }
            }
            else { if (".^$*+?{}[]\\()|".Contains(c)) throw Rejection.Element(); chars.Add(c); }
            int minimum = 1, maximum = 1;
            if (index < end && "?*+".Contains(pattern[index]))
            {
                char quantifier = pattern[index++]; minimum = quantifier == '+' ? 1 : 0; maximum = quantifier == '?' ? 1 : 4096;
            }
            else if (index < end && pattern[index] == '{')
            {
                int close = pattern.IndexOf('}', index); if (close < 0 || close >= end) throw Rejection.Element();
                string[] counts = pattern[(index + 1)..close].Split(','); if (counts.Length > 2) throw Rejection.Element();
                minimum = Count(counts[0]); maximum = Count(counts[^1]); if (minimum > maximum) throw Rejection.Element();
                index = close + 1;
            }
            parsed.Add(new(chars.ToImmutable(), minimum, maximum));
        }
        if (parsed.Count == 0) throw Rejection.Element(); atoms = parsed.ToImmutable();
    }
    private static char Escape(string pattern, ref int index, int end)
    {
        if (index >= end || !IsPunctuation(pattern[index])) throw Rejection.Element(); return pattern[index++];
    }
    private static bool IsPunctuation(char c) => c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';
    private static int Count(string text)
    {
        if (text.Length is 0 or > 4 || text.Length > 1 && text[0] == '0' || text.Any(c => c is < '0' or > '9')) throw Rejection.Element();
        int count = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture); if (count > 1024) throw Rejection.Element(); return count;
    }
    internal bool Matches(string value)
    {
        if (!UnicodeText.IsValid(value)) throw Rejection.Type();
        int n = UnicodeText.Length(value); if (n > 4096) throw Rejection.Limit();
        int[] input = value.EnumerateRunes().Select(r => r.Value).ToArray();
        bool[] positions = new bool[n + 1]; positions[0] = true;
        foreach (var atom in atoms)
        {
            int[] prefix = new int[n + 2]; for (int j = 0; j <= n; j++) prefix[j + 1] = prefix[j] + (positions[j] ? 1 : 0);
            bool[] next = new bool[n + 1]; int runStart = 0;
            for (int end = 0; end <= n; end++)
            {
                if (end > 0 && !atom.Characters.Contains(input[end - 1])) runStart = end;
                int low = Math.Max(runStart, end - atom.Maximum), high = end - atom.Minimum;
                if (high >= low && prefix[high + 1] > prefix[low]) next[end] = true;
            }
            positions = next;
        }
        return positions[n];
    }
}
