using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BirkNext.Web.Services.SchemaStudio;

/// <summary>
/// Bounded sample generator for a safe, commonly used subset of XML Schema regular expressions.
/// Unsupported XSD regex constructs fail explicitly; no regular expression is executed without a timeout.
/// </summary>
internal static class XsdPatternValueGenerator
{
    private const int MaxPatternLength = 512;
    private const int MaxRepeat = 256;
    private const int MaxDepth = 24;
    private const int MaxOutputLength = 4096;
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    public static bool TryCreate(string pattern, Random random, out string value)
    {
        value = "";
        if (pattern.Length is 0 or > MaxPatternLength) return false;
        try
        {
            var parser = new Parser(pattern, random);
            value = parser.Parse();
            return Matches(pattern, value);
        }
        catch (PatternNotSupportedException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Anchored match in the XSD dialect. The engine is linear-time (NonBacktracking) and pattern and value are length-bounded, so no
    /// timeout is needed — a timeout would only let CPU load turn a real match into "no match" and make generation non-deterministic.</summary>
    public static bool Matches(string pattern, string value)
    {
        if (pattern.Length is 0 or > MaxPatternLength || value.Length > MaxOutputLength) return false;
        var regex = Translate(pattern);
        return regex is not null && Regex.IsMatch(value, "\\A(?:" + regex + ")\\z", Options, Regex.InfiniteMatchTimeout);
    }

    private static string? Translate(string pattern)
    {
        var result = new StringBuilder(pattern.Length + 8);
        var inClass = false;
        var escaped = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (escaped)
            {
                if (c is 'i' or 'I' or 'c' or 'C' || c == 'p' && (i + 1 >= pattern.Length || pattern[i + 1] != '{')) return null;
                result.Append('\\').Append(c);
                escaped = false;
                continue;
            }
            if (c == '\\') { escaped = true; continue; }
            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (inClass && c == '-' && i + 1 < pattern.Length && pattern[i + 1] == '[') return null;
            result.Append(c);
        }
        return escaped ? null : result.ToString();
    }

    private sealed class Parser(string pattern, Random random)
    {
        private int _position;

        public string Parse()
        {
            var value = Expression(0, terminator: '\0');
            if (_position != pattern.Length || value.Length > MaxOutputLength) throw new PatternNotSupportedException();
            return value;
        }

        private string Expression(int depth, char terminator)
        {
            CheckDepth(depth);
            var choices = new List<string> { Sequence(depth + 1, terminator) };
            while (Peek() == '|')
            {
                _position++;
                choices.Add(Sequence(depth + 1, terminator));
            }
            return choices[random.Next(choices.Count)];
        }

        private string Sequence(int depth, char terminator)
        {
            var result = new StringBuilder();
            while (_position < pattern.Length && Peek() != '|' && Peek() != terminator)
            {
                var atom = Atom(depth + 1);
                if (_position < pattern.Length && Peek() is '?' or '*' or '+' or '{') atom = Repeat(atom);
                if (result.Length + atom.Length > MaxOutputLength) throw new PatternNotSupportedException();
                result.Append(atom);
            }
            return result.ToString();
        }

        private string Atom(int depth)
        {
            CheckDepth(depth);
            if (_position >= pattern.Length) throw new PatternNotSupportedException();
            var c = pattern[_position++];
            return c switch
            {
                '(' => Group(depth),
                '[' => CharacterClass(),
                '.' => "A",
                '^' or '$' => "",
                '\\' => Escape(inClass: false),
                ')' or ']' or '|' or '?' or '*' or '+' or '{' or '}' => throw new PatternNotSupportedException(),
                _ => XmlConvertCharacter(c),
            };
        }

        private string Group(int depth)
        {
            if (Peek() == '?') throw new PatternNotSupportedException();
            var value = Expression(depth + 1, ')');
            if (Peek() != ')') throw new PatternNotSupportedException();
            _position++;
            return value;
        }

        private string CharacterClass()
        {
            var negate = Peek() == '^';
            if (negate) _position++;
            var ranges = new List<(char Start, char End)>();
            var candidates = new List<char>();
            while (_position < pattern.Length && Peek() != ']')
            {
                if (Peek() == '-' && _position + 1 < pattern.Length && pattern[_position + 1] == '[')
                    throw new PatternNotSupportedException();
                var start = ClassCharacter();
                var end = start;
                if (Peek() == '-' && _position + 1 < pattern.Length && pattern[_position + 1] != ']')
                {
                    _position++;
                    end = ClassCharacter();
                    if (end < start) throw new PatternNotSupportedException();
                }
                ranges.Add((start, end));
            }
            if (Peek() != ']' || ranges.Count == 0) throw new PatternNotSupportedException();
            _position++;
            if (!negate)
            {
                foreach (var range in ranges)
                {
                    var width = range.End - range.Start + 1;
                    // Digits are always the lowest allowed digit: generated numbers stay schema-valid but never form a
                    // checksum-valid personal or organisation identifier (fødselsnummer, D-number, DUF, organisation number).
                    candidates.Add(range.Start >= '0' && range.End <= '9' ? range.Start : (char)(range.Start + random.Next(width)));
                }
                return candidates[random.Next(candidates.Count)].ToString();
            }
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 _-";
            candidates.AddRange(alphabet.Where(c => ranges.All(r => c < r.Start || c > r.End)));
            if (candidates.Count == 0) throw new PatternNotSupportedException();
            return candidates[random.Next(candidates.Count)].ToString();
        }

        private char ClassCharacter()
        {
            if (_position >= pattern.Length) throw new PatternNotSupportedException();
            var c = pattern[_position++];
            if (c == '\\')
            {
                var value = Escape(inClass: true);
                if (value.Length != 1) throw new PatternNotSupportedException();
                return value[0];
            }
            return c;
        }

        private string Escape(bool inClass)
        {
            if (_position >= pattern.Length) throw new PatternNotSupportedException();
            var escaped = pattern[_position++];
            return escaped switch
            {
                'd' => "0", // identity-safe: digit runs are zeros (see the class-range rule above)
                'D' when !inClass => "A",
                's' => " ",
                'S' when !inClass => "A",
                'w' => "A",
                'W' when !inClass => "!",
                'p' or 'P' => throw new PatternNotSupportedException(),
                'i' or 'I' or 'c' or 'C' => throw new PatternNotSupportedException(),
                _ when char.IsAsciiLetterOrDigit(escaped) => throw new PatternNotSupportedException(),
                _ => XmlConvertCharacter(escaped),
            };
        }

        private string Repeat(string atom)
        {
            var min = 1;
            var max = 1;
            var marker = pattern[_position++];
            switch (marker)
            {
                case '?': min = 0; max = 1; break;
                case '*': min = 0; max = 2; break;
                case '+': min = 1; max = 2; break;
                case '{':
                    min = Number();
                    if (Peek() == '}') max = min;
                    else
                    {
                        if (Peek() != ',') throw new PatternNotSupportedException();
                        _position++;
                        max = Peek() == '}' ? Math.Max(min, 2) : Number();
                    }
                    if (Peek() != '}' || max < min) throw new PatternNotSupportedException();
                    _position++;
                    break;
            }
            if (min > MaxRepeat || max > MaxRepeat) throw new PatternNotSupportedException();
            var count = max == min ? min : random.Next(min, max + 1);
            if ((long)atom.Length * count > MaxOutputLength) throw new PatternNotSupportedException();
            return string.Concat(Enumerable.Repeat(atom, count));
        }

        private int Number()
        {
            var start = _position;
            while (_position < pattern.Length && char.IsAsciiDigit(Peek())) _position++;
            if (start == _position || !int.TryParse(pattern.AsSpan(start, _position - start), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                throw new PatternNotSupportedException();
            return number;
        }

        private static string XmlConvertCharacter(char c) => System.Xml.XmlConvert.IsXmlChar(c) ? c.ToString() : throw new PatternNotSupportedException();
        private static void CheckDepth(int depth) { if (depth > MaxDepth) throw new PatternNotSupportedException(); }
        private char Peek() => _position < pattern.Length ? pattern[_position] : '\0';
    }

    private sealed class PatternNotSupportedException : Exception { }
}
