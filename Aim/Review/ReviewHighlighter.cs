using System.Text;

namespace Aim;

/// <summary>
/// Tiny line tokenizer for review snippets. Keyword sets and a scan are enough;
/// there is no regex compilation and no reflection, so it stays trim/AOT friendly.
/// Unknown and extension-less paths are not wrapped in one color. Strings and
/// numbers are still tinted; keywords and comments stay plain unless the path
/// resolves to a language.
/// </summary>
internal static class ReviewHighlighter
{
    private const string Reset = "\u001b[0m";

    // Bright hues plus weight, so the four token classes stay apart from each
    // other, from plain identifiers, and from the formatter chrome (gray gutter,
    // dim yellow "medium", cyan paths).
    private const string KeywordColor = "\u001b[1;95m";
    private const string StringColor = "\u001b[92m";
    private const string CommentColor = "\u001b[3;90m";
    private const string NumberColor = "\u001b[93m";
    private const string MemberColor = "\u001b[96m";

    [Flags]
    private enum Features
    {
        None = 0,
        SlashSlash = 1 << 0,
        HashComment = 1 << 1,
        DashDash = 1 << 2,
        BlockComment = 1 << 3,
        HtmlComment = 1 << 4,
        DoubleQuote = 1 << 5,
        SingleQuote = 1 << 6,
        Backtick = 1 << 7,
        VerbatimAt = 1 << 8,
        TripleQuote = 1 << 9,
        InterpolatedDollar = 1 << 10,
        MarkupTags = 1 << 11,
        AtRules = 1 << 12,
        Preprocessor = 1 << 13
    }

    internal enum SpanKind
    {
        None,
        BlockComment,
        HtmlComment,
        TripleDouble,
        TripleSingle,
        Verbatim,
        Template
    }

    internal struct State
    {
        internal SpanKind Open;
    }

    private sealed class Language
    {
        public required Features Features { get; init; }
        public HashSet<string>? Keywords { get; init; }
        public bool ColorMembers { get; init; }
    }

    private const Features CFamily =
        Features.SlashSlash | Features.BlockComment | Features.DoubleQuote | Features.SingleQuote;

    private const Features CSharpFeatures =
        CFamily | Features.VerbatimAt | Features.TripleQuote | Features.InterpolatedDollar | Features.Preprocessor;

    private static readonly Language CSharp = Make(CSharpFeatures, Words(
        "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while var record required init when yield async await nint nuint"));

    private static readonly Language JavaScript = Make(
        Features.SlashSlash | Features.BlockComment | Features.DoubleQuote | Features.SingleQuote | Features.Backtick,
        Words("async await break case catch class const continue debugger default delete do else export extends false finally for from function if import in instanceof let new null of return static super switch this throw true try typeof var void while with yield interface type enum implements public private protected readonly as satisfies keyof is"));

    private static readonly Language Python = Make(
        Features.HashComment | Features.DoubleQuote | Features.SingleQuote | Features.TripleQuote,
        Words("and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield match case"));

    private static readonly Language Go = Make(
        CFamily | Features.Backtick,
        Words("break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var true false nil"));

    private static readonly Language Rust = Make(
        CFamily,
        Words("as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while"));

    private static readonly Language Java = Make(
        CFamily,
        Words("abstract assert boolean break byte case catch char class const continue default do double else enum extends final finally float for goto if implements import instanceof int interface long native new null package private protected public return short static strictfp super switch synchronized this throw throws transient true try void volatile while var record yield"));

    private static readonly Language Cxx = Make(
        CFamily,
        Words("auto break case char const continue default do double else enum extern float for goto if inline int long register return short signed sizeof static struct switch typedef union unsigned void volatile while bool class namespace template typename public private protected virtual override new delete this true false nullptr using try catch throw"));

    private static readonly Language Php = Make(
        CFamily | Features.HashComment,
        Words("abstract and array as break case catch class clone const continue declare default do echo else elseif empty extends final finally fn for foreach function global goto if implements include include_once instanceof interface isset list match namespace new or print private protected public readonly require require_once return static switch throw trait try unset use var while xor yield true false null"));

    private static readonly Language Sql = Make(
        Features.DashDash | Features.BlockComment | Features.DoubleQuote | Features.SingleQuote,
        WordsIgnoreCase("select from where and or not insert into values update set delete create table view index join left right inner outer on as group by order having limit offset union all distinct null is in like between exists case when then else end begin commit rollback primary key foreign references constraint default"));

    private static readonly Language Shell = Make(
        Features.HashComment | Features.DoubleQuote | Features.SingleQuote,
        Words("if then else elif fi for while do done case esac function return exit local export readonly declare unset source echo foreach"));

    private static readonly Language Json = Make(Features.DoubleQuote, Words("true false null"));

    private static readonly Language Jsonc = Make(
        Features.DoubleQuote | Features.SlashSlash | Features.BlockComment,
        Words("true false null"));

    private static readonly Language Yaml = Make(
        Features.HashComment | Features.DoubleQuote | Features.SingleQuote,
        Words("true false null yes no"));

    private static readonly Language Dockerfile = Make(
        Features.HashComment | Features.DoubleQuote | Features.SingleQuote,
        WordsIgnoreCase("from as run cmd entrypoint copy add env arg workdir expose volume user label healthcheck shell stopsignal onbuild maintainer"));

    // No keyword or comment rules: a whole line must not become one tint.
    private static readonly Language Generic = Make(
        Features.DoubleQuote,
        keywords: null,
        colorMembers: false);

    private static readonly Language Css = Make(
        Features.BlockComment | Features.DoubleQuote | Features.SingleQuote | Features.AtRules,
        keywords: null);

    private static readonly Language Markup = Make(
        Features.HtmlComment | Features.DoubleQuote | Features.SingleQuote | Features.MarkupTags,
        keywords: null);

    private static readonly Language Razor = Make(CSharpFeatures | Features.HtmlComment | Features.MarkupTags, CSharp.Keywords);

    private static readonly Dictionary<string, Language> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = CSharp,
        [".csx"] = CSharp,
        [".js"] = JavaScript,
        [".jsx"] = JavaScript,
        [".mjs"] = JavaScript,
        [".cjs"] = JavaScript,
        [".ts"] = JavaScript,
        [".tsx"] = JavaScript,
        [".mts"] = JavaScript,
        [".cts"] = JavaScript,
        [".json"] = Json,
        [".jsonc"] = Jsonc,
        [".html"] = Markup,
        [".htm"] = Markup,
        [".xhtml"] = Markup,
        [".xml"] = Markup,
        [".svg"] = Markup,
        [".vue"] = Markup,
        [".svelte"] = Markup,
        [".cshtml"] = Razor,
        [".razor"] = Razor,
        [".css"] = Css,
        [".scss"] = Css,
        [".less"] = Css,
        [".py"] = Python,
        [".pyw"] = Python,
        [".go"] = Go,
        [".rs"] = Rust,
        [".java"] = Java,
        [".c"] = Cxx,
        [".h"] = Cxx,
        [".cc"] = Cxx,
        [".cpp"] = Cxx,
        [".cxx"] = Cxx,
        [".hh"] = Cxx,
        [".hpp"] = Cxx,
        [".php"] = Php,
        [".sql"] = Sql,
        [".sh"] = Shell,
        [".bash"] = Shell,
        [".zsh"] = Shell,
        [".ps1"] = Shell,
        [".yml"] = Yaml,
        [".yaml"] = Yaml
    };

    internal static string Colorize(string snippet, string path)
    {
        var sb = new StringBuilder();
        var state = default(State);
        var normalized = snippet.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                sb.Append('\n');
            Append(sb, lines[i], path, ref state);
        }

        return sb.ToString();
    }

    internal static void Append(StringBuilder sb, string text, string path, ref State state)
    {
        var language = LanguageFor(path);
        var i = 0;
        if (state.Open != SpanKind.None)
        {
            if (!TryClose(text, 0, state.Open, out var closed))
            {
                Emit(sb, StyleFor(state.Open), text);
                return;
            }

            Emit(sb, StyleFor(state.Open), text.AsSpan(0, closed));
            state.Open = SpanKind.None;
            i = closed;
        }

        while (i < text.Length)
        {
            var features = language.Features;
            if (IsLineComment(text, i, features))
            {
                Emit(sb, CommentColor, text.AsSpan(i));
                return;
            }

            if (Has(features, Features.BlockComment) && StartsWith(text, i, "/*"))
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    Emit(sb, CommentColor, text.AsSpan(i));
                    state.Open = SpanKind.BlockComment;
                    return;
                }

                var next = end + 2;
                Emit(sb, CommentColor, text.AsSpan(i, next - i));
                i = next;
                continue;
            }

            if (Has(features, Features.HtmlComment) && StartsWith(text, i, "<!--"))
            {
                var end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end < 0)
                {
                    Emit(sb, CommentColor, text.AsSpan(i));
                    state.Open = SpanKind.HtmlComment;
                    return;
                }

                var next = end + 3;
                Emit(sb, CommentColor, text.AsSpan(i, next - i));
                i = next;
                continue;
            }

            if (TryString(text, i, features, out var stringEnd, out var carry) && stringEnd > i)
            {
                Emit(sb, StringColor, text.AsSpan(i, stringEnd - i));
                if (carry != SpanKind.None)
                    state.Open = carry;
                i = stringEnd;
                continue;
            }

            if (IsNumberStart(text, i))
            {
                var next = ScanNumber(text, i);
                Emit(sb, NumberColor, text.AsSpan(i, next - i));
                i = next;
                continue;
            }

            if (Has(features, Features.AtRules) && text[i] == '@' && i + 1 < text.Length && IsIdentStart(text[i + 1]))
            {
                var start = i;
                i += 2;
                while (i < text.Length && IsIdentPart(text[i]))
                    i++;
                Emit(sb, KeywordColor, text.AsSpan(start, i - start));
                continue;
            }

            if (Has(features, Features.MarkupTags) && TryMarkupTag(text, i, out var tagEnd, out var nameStart, out var nameEnd))
            {
                sb.Append(text, i, nameStart - i);
                Emit(sb, KeywordColor, text.AsSpan(nameStart, nameEnd - nameStart));
                i = tagEnd;
                continue;
            }

            if (language.ColorMembers &&
                text[i] == '.' &&
                (i == 0 || text[i - 1] != '.') &&
                i + 1 < text.Length &&
                IsIdentStart(text[i + 1]))
            {
                var start = i;
                i += 2;
                while (i < text.Length && IsIdentPart(text[i]))
                    i++;
                Emit(sb, MemberColor, text.AsSpan(start, i - start));
                continue;
            }

            if (IsIdentStart(text[i]))
            {
                var start = i++;
                while (i < text.Length && IsIdentPart(text[i]))
                    i++;

                var precededByDot = start > 0 && text[start - 1] == '.';
                var word = text.Substring(start, i - start);
                if (!precededByDot && language.Keywords is not null && language.Keywords.Contains(word))
                    Emit(sb, KeywordColor, word);
                else
                    sb.Append(word);
                continue;
            }

            sb.Append(text[i]);
            i++;
        }
    }

    private static bool TryClose(string text, int start, SpanKind open, out int end)
    {
        var markerEnd = open switch
        {
            SpanKind.BlockComment => After(text, start, "*/"),
            SpanKind.HtmlComment => After(text, start, "-->"),
            SpanKind.TripleDouble => After(text, start, "\"\"\""),
            SpanKind.TripleSingle => After(text, start, "'''"),
            SpanKind.Verbatim => VerbatimClose(text, start),
            SpanKind.Template => UnescapedClose(text, start, '`'),
            _ => -1
        };
        if (markerEnd < 0)
        {
            end = 0;
            return false;
        }

        end = markerEnd;
        return true;
    }

    private static int After(string text, int start, string marker)
    {
        var found = text.IndexOf(marker, start, StringComparison.Ordinal);
        return found < 0 ? -1 : found + marker.Length;
    }

    private static int VerbatimClose(string text, int start)
    {
        var end = FindVerbatimEnd(text, start);
        return end < 0 ? -1 : end + 1;
    }

    private static int UnescapedClose(string text, int start, char quote)
    {
        var end = FindUnescaped(text, start, quote);
        return end < 0 ? -1 : end + 1;
    }

    private static bool IsLineComment(string text, int i, Features features)
    {
        if (Has(features, Features.SlashSlash) && StartsWith(text, i, "//"))
            return true;
        if (Has(features, Features.HashComment) && text[i] == '#')
            return true;
        if (Has(features, Features.DashDash) && StartsWith(text, i, "--"))
            return true;
        if (Has(features, Features.Preprocessor) && text[i] == '#' && IsLineStart(text, i))
            return true;
        return false;
    }

    private static bool IsLineStart(string text, int i)
    {
        for (var j = 0; j < i; j++)
        {
            if (!char.IsWhiteSpace(text[j]))
                return false;
        }

        return true;
    }

    private static bool TryString(string text, int i, Features features, out int end, out SpanKind carry)
    {
        end = i;
        carry = SpanKind.None;
        var j = i;
        var verbatim = false;
        if (Has(features, Features.VerbatimAt) && j < text.Length && text[j] == '@')
        {
            if (j + 1 < text.Length && (text[j + 1] == '"' || text[j + 1] == '$'))
            {
                verbatim = true;
                j++;
            }
            else
            {
                return false;
            }
        }

        if (Has(features, Features.InterpolatedDollar) && j < text.Length && text[j] == '$')
        {
            var k = j;
            while (k < text.Length && text[k] == '$')
                k++;
            if (k < text.Length && text[k] == '"')
                j = k;
            else
                return false;
        }

        if (j >= text.Length)
            return false;

        if (Has(features, Features.Backtick) && text[j] == '`')
        {
            var close = FindUnescaped(text, j + 1, '`');
            if (close < 0)
            {
                carry = SpanKind.Template;
                end = text.Length;
                return true;
            }

            end = close + 1;
            return true;
        }

        var triple = Has(features, Features.TripleQuote);
        if (Has(features, Features.DoubleQuote) && text[j] == '"')
        {
            if (triple && j + 2 < text.Length && text[j + 1] == '"' && text[j + 2] == '"')
            {
                var close = text.IndexOf("\"\"\"", j + 3, StringComparison.Ordinal);
                if (close < 0)
                {
                    carry = SpanKind.TripleDouble;
                    end = text.Length;
                    return true;
                }

                end = close + 3;
                return true;
            }

            if (verbatim)
            {
                var close = FindVerbatimEnd(text, j + 1);
                if (close < 0)
                {
                    carry = SpanKind.Verbatim;
                    end = text.Length;
                    return true;
                }

                end = close + 1;
                return true;
            }

            var plain = FindUnescaped(text, j + 1, '"');
            end = plain < 0 ? text.Length : plain + 1;
            return true;
        }

        if (Has(features, Features.SingleQuote) && text[j] == '\'')
        {
            if (triple && j + 2 < text.Length && text[j + 1] == '\'' && text[j + 2] == '\'')
            {
                var close = text.IndexOf("'''", j + 3, StringComparison.Ordinal);
                if (close < 0)
                {
                    carry = SpanKind.TripleSingle;
                    end = text.Length;
                    return true;
                }

                end = close + 3;
                return true;
            }

            var plain = FindUnescaped(text, j + 1, '\'');
            end = plain < 0 ? text.Length : plain + 1;
            return true;
        }

        return false;
    }

    private static int FindUnescaped(string text, int start, char quote)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == quote)
                return i;
        }

        return -1;
    }

    private static int FindVerbatimEnd(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;
            if (i + 1 < text.Length && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static bool TryMarkupTag(string text, int i, out int end, out int nameStart, out int nameEnd)
    {
        end = i;
        nameStart = i;
        nameEnd = i;
        if (text[i] != '<')
            return false;

        var j = i + 1;
        if (j < text.Length && text[j] == '/')
            j++;
        if (j >= text.Length || !IsTagStart(text[j]))
            return false;

        nameStart = j;
        j++;
        while (j < text.Length && IsTagPart(text[j]))
            j++;

        nameEnd = j;
        end = j;
        return nameEnd > nameStart;
    }

    private static bool IsNumberStart(string text, int i)
    {
        if (char.IsAsciiDigit(text[i]))
            return true;

        // `..` is a range operator. `.5` is a number.
        return text[i] == '.'
            && (i == 0 || text[i - 1] != '.')
            && i + 1 < text.Length
            && text[i + 1] != '.'
            && char.IsAsciiDigit(text[i + 1]);
    }

    private static int ScanNumber(string text, int i)
    {
        if (text[i] == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X' or 'b' or 'B')
        {
            var hex = text[i + 1] is 'x' or 'X';
            i += 2;
            while (i < text.Length && (text[i] == '_' || (hex ? IsHex(text[i]) : text[i] is '0' or '1')))
                i++;
            return ConsumeSuffix(text, i);
        }

        if (text[i] == '.')
            i++;

        while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '_'))
            i++;

        if (i < text.Length && text[i] == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
        {
            i++;
            while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '_'))
                i++;
        }

        if (i < text.Length && text[i] is 'e' or 'E')
        {
            var e = i + 1;
            if (e < text.Length && text[e] is '+' or '-')
                e++;
            if (e < text.Length && char.IsAsciiDigit(text[e]))
            {
                i = e + 1;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '_'))
                    i++;
            }
        }

        return ConsumeSuffix(text, i);
    }

    private static int ConsumeSuffix(string text, int i)
    {
        var t = i;
        var count = 0;
        while (count < 2 && t < text.Length && IsNumberSuffix(text[t]))
        {
            t++;
            count++;
        }

        if (count == 0)
            return i;
        if (t < text.Length && (char.IsLetterOrDigit(text[t]) || text[t] == '_'))
            return i;
        return t;
    }

    private static bool IsHex(char c) =>
        char.IsAsciiDigit(c) || c is >= 'a' and <= 'f' || c is >= 'A' and <= 'F';

    private static bool IsNumberSuffix(char c) => c is 'f' or 'F' or 'd' or 'D' or 'm' or 'M' or 'u' or 'U' or 'l' or 'L';

    private static bool IsIdentStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsIdentPart(char c) => c == '_' || char.IsLetterOrDigit(c);

    private static bool IsTagStart(char c) => char.IsAsciiLetter(c);

    private static bool IsTagPart(char c) => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_';

    private static bool StartsWith(string text, int i, string marker) =>
        i + marker.Length <= text.Length && text.AsSpan(i, marker.Length).SequenceEqual(marker);

    private static bool Has(Features features, Features flag) => (features & flag) != 0;

    private static string StyleFor(SpanKind kind) => kind switch
    {
        SpanKind.BlockComment or SpanKind.HtmlComment => CommentColor,
        _ => StringColor
    };

    private static void Emit(StringBuilder sb, string color, ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
            return;

        sb.Append(color);
        sb.Append(text);
        sb.Append(Reset);
    }

    private static Language LanguageFor(string path)
    {
        var cleaned = CleanPath(path);
        var extension = Path.GetExtension(cleaned);
        if (extension.Length != 0 && ByExtension.TryGetValue(extension, out var byExtension))
            return byExtension;

        if (IsDockerfileName(Path.GetFileName(cleaned)))
            return Dockerfile;

        return Generic;
    }

    private static bool IsDockerfileName(string name) =>
        name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Containerfile", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Containerfile.", StringComparison.OrdinalIgnoreCase);

    private static string CleanPath(string path)
    {
        var text = path.Trim();
        if (text.Length >= 2)
        {
            var quote = text[0];
            if (quote is '"' or '\'' or '`' && text[^1] == quote)
                text = text[1..^1].Trim();
        }

        while (TryStripLocation(text, out var stripped))
            text = stripped;

        return text;
    }

    private static bool TryStripLocation(string text, out string stripped)
    {
        if (TryStripMarker(text, '#', out stripped) || TryStripMarker(text, ':', out stripped))
            return true;

        if (text.Length > 2 && text[^1] == ')')
        {
            var open = text.LastIndexOf('(');
            if (open > 0 && IsLineSpan(text.AsSpan(open + 1, text.Length - open - 2)))
            {
                stripped = text[..open].TrimEnd();
                return stripped.Length > 0;
            }
        }

        return false;
    }

    private static bool TryStripMarker(string text, char marker, out string stripped)
    {
        stripped = text;
        var index = text.LastIndexOf(marker);
        if (index <= 0)
            return false;

        var tail = text.AsSpan(index + 1);
        if (tail.Length > 1 && tail[0] is 'L' or 'l' && char.IsAsciiDigit(tail[1]))
            tail = tail[1..];

        if (!IsLineSpan(tail))
            return false;

        stripped = text[..index].TrimEnd();
        return stripped.Length > 0;
    }

    private static bool IsLineSpan(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
            return false;

        var i = 0;
        if (!ConsumeDigits(text, ref i))
            return false;

        if (i == text.Length)
            return true;

        if (text[i] != '-')
            return false;

        i++;
        return ConsumeDigits(text, ref i) && i == text.Length;
    }

    private static bool ConsumeDigits(ReadOnlySpan<char> text, ref int i)
    {
        var start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
            i++;

        return i > start;
    }

    private static Language Make(Features features, HashSet<string>? keywords, bool colorMembers = true) => new()
    {
        Features = features,
        Keywords = keywords,
        ColorMembers = colorMembers
    };

    private static HashSet<string> Words(string text) =>
        new(text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);

    private static HashSet<string> WordsIgnoreCase(string text) =>
        new(text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
}
