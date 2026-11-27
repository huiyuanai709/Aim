using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ngrep.Search;

namespace Ngrep.Output;

/// <summary>
/// Print-only substitute for <c>-r</c> / <c>--replace</c>. Never writes files.
/// Fixed strings are inserted literally. Regex mode expands captures with
/// ripgrep's <c>$</c> rules on top of a .NET <see cref="Match"/>.
/// </summary>
internal sealed class LineReplacer
{
    private readonly string _replacement;
    private readonly bool _regex;
    private readonly Regex[]? _regexes;

    private LineReplacer(string replacement, bool regex, Regex[]? regexes)
    {
        _replacement = replacement;
        _regex = regex;
        _regexes = regexes;
    }

    public static LineReplacer Create(
        string replacement,
        bool regex,
        IReadOnlyList<string> patterns,
        bool ignoreCase,
        bool backtracking,
        bool lineRegexp,
        bool dotAll,
        bool[]? patternIgnoreCase = null)
    {
        Regex[]? regexes = null;
        if (regex)
        {
            // Same options as MatchPlan. Compiled is not AOT-safe. -P stays on the interpreter.
            var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
            if (!backtracking)
            {
                options |= RegexOptions.NonBacktracking;
            }

            if (dotAll)
            {
                options |= RegexOptions.Singleline;
            }

            regexes = new Regex[patterns.Count];
            for (var i = 0; i < patterns.Count; i++)
            {
                // -x wraps with a non-capturing group, so the caller's $1 is still their first group.
                var source = lineRegexp ? @"\A(?:" + patterns[i] + @")\z" : patterns[i];
                var patternOptions = options;
                var fold = patternIgnoreCase != null && (uint)i < (uint)patternIgnoreCase.Length
                    ? patternIgnoreCase[i]
                    : ignoreCase;
                if (fold)
                {
                    patternOptions |= RegexOptions.IgnoreCase;
                }

                try
                {
                    regexes[i] = new Regex(source, patternOptions, TimeSpan.FromSeconds(5));
                }
                catch (NotSupportedException ex) when (!backtracking)
                {
                    throw new ArgumentException(
                        $"'{patterns[i]}' is not supported by the non-backtracking engine. Use -P/--pcre for backreferences, lookaround, and atomic groups.",
                        ex);
                }
            }
        }

        return new LineReplacer(replacement, regex, regexes);
    }

    /// <summary>The replacement for one match. Used by <c>-o</c>.</summary>
    public string ReplaceOne(SearchMatch match) =>
        ExpandSpan(match.LineText, match.Column - 1, VisibleLength(match.LineText, match.Column - 1, match.Text), match.Text);

    /// <summary>
    /// Every match on the line, left to right. A span that starts inside an earlier one is skipped.
    /// When <paramref name="color"/> is set, each inserted replacement is wrapped in the match color.
    /// </summary>
    public string ReplaceLine(string lineText, IReadOnlyList<SearchMatch> matches, bool color)
    {
        if (matches.Count == 0)
        {
            return lineText;
        }

        var spans = new (int Start, int Length, string Text)[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var start = match.Column - 1;
            spans[i] = (start, VisibleLength(lineText, start, match.Text), match.Text);
        }

        Array.Sort(spans, static (left, right) =>
        {
            var start = left.Start.CompareTo(right.Start);
            return start != 0 ? start : left.Length.CompareTo(right.Length);
        });

        var builder = new StringBuilder(lineText.Length);
        var cursor = 0;
        foreach (var span in spans)
        {
            if (span.Start < cursor || span.Start > lineText.Length)
            {
                continue;
            }

            builder.Append(lineText, cursor, span.Start - cursor);
            var replaced = ExpandSpan(lineText, span.Start, span.Length, span.Text);
            if (color && replaced.Length > 0)
            {
                builder.Append(TextColor.Match);
                builder.Append(replaced);
                builder.Append(TextColor.Reset);
            }
            else
            {
                builder.Append(replaced);
            }

            var next = span.Start + span.Length;
            if (next < cursor)
            {
                next = cursor;
            }

            cursor = next > lineText.Length ? lineText.Length : next;
        }

        if (cursor < lineText.Length)
        {
            builder.Append(lineText, cursor, lineText.Length - cursor);
        }

        return builder.ToString();
    }

    private string ExpandSpan(string lineText, int start, int length, string matched)
    {
        if (!_regex || _regexes == null)
        {
            return _replacement;
        }

        if (start >= 0 && length >= 0 && start <= lineText.Length && start + length <= lineText.Length)
        {
            foreach (var regex in _regexes)
            {
                var found = regex.Match(lineText, Math.Min(start, lineText.Length));
                if (found.Success && found.Index == start && found.Length == length)
                {
                    return Expand(found, regex);
                }
            }
        }

        // A cross-line hit is longer than the printed line. Groups come from the matched slice.
        if (matched.Length > 0 && (length != matched.Length || matched.Contains('\n')))
        {
            foreach (var regex in _regexes)
            {
                var found = regex.Match(matched);
                if (found.Success && found.Index == 0 && found.Length == matched.Length)
                {
                    return Expand(found, regex);
                }
            }
        }

        return ExpandWhole(matched);
    }

    private string Expand(Match match, Regex regex) =>
        Expand(_replacement, name => Lookup(regex, match, name));

    private string ExpandWhole(string matched) =>
        Expand(_replacement, name => name is "0" or "&" ? matched : "");

    /// <summary>
    /// Ripgrep's replacement syntax. <c>$$</c> is <c>$</c>. <c>$0</c>, <c>$&amp;</c>, and <c>${0}</c> are the
    /// whole match. <c>$name</c> and <c>${name}</c> are a group. A digit-only name is a group index
    /// (<c>$1</c>, <c>${1}</c>, <c>$01</c>). Any other name is a .NET group name. A missing group is empty.
    /// <c>$`</c>, <c>$'</c>, <c>$+</c>, and <c>$_</c> are not special.
    /// </summary>
    private static string Expand(string template, Func<string, string> group)
    {
        var builder = new StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '$')
            {
                builder.Append(template[i]);
                continue;
            }

            if (i + 1 >= template.Length)
            {
                builder.Append('$');
                break;
            }

            var next = template[i + 1];
            if (next == '$')
            {
                builder.Append('$');
                i++;
                continue;
            }

            if (next == '&')
            {
                builder.Append(group("0"));
                i++;
                continue;
            }

            if (next == '{')
            {
                var end = template.IndexOf('}', i + 2);
                if (end < 0)
                {
                    builder.Append('$');
                    continue;
                }

                builder.Append(group(template[(i + 2)..end]));
                i = end;
                continue;
            }

            if (IsNameChar(next))
            {
                var end = i + 1;
                while (end < template.Length && IsNameChar(template[end]))
                {
                    end++;
                }

                builder.Append(group(template[(i + 1)..end]));
                i = end - 1;
                continue;
            }

            builder.Append('$');
        }

        return builder.ToString();
    }

    private static string Lookup(Regex regex, Match match, string name)
    {
        if (name.Length == 0)
        {
            return "";
        }

        if (IsAllDigits(name) && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            if ((uint)index >= (uint)match.Groups.Count)
            {
                return "";
            }

            var numbered = match.Groups[index];
            return numbered.Success ? numbered.Value : "";
        }

        var number = regex.GroupNumberFromName(name);
        if (number < 0 || number >= match.Groups.Count)
        {
            return "";
        }

        var found = match.Groups[number];
        return found.Success ? found.Value : "";
    }

    private static int VisibleLength(string lineText, int start, string matchText)
    {
        if (start < 0 || start > lineText.Length)
        {
            return 0;
        }

        var length = matchText.Length;
        var newline = matchText.IndexOf('\n');
        if (newline >= 0)
        {
            length = newline;
            if (length > 0 && matchText[length - 1] == '\r')
            {
                length--;
            }
        }

        if (start + length > lineText.Length)
        {
            length = lineText.Length - start;
        }

        return length < 0 ? 0 : length;
    }

    private static bool IsNameChar(char value) =>
        value is '_' or (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    private static bool IsAllDigits(string name)
    {
        foreach (var value in name)
        {
            if (value is < '0' or > '9')
            {
                return false;
            }
        }

        return name.Length > 0;
    }
}
