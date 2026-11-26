using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aim;

internal sealed record LeakedToolCall(string Name, Dictionary<string, object?> Arguments);

/// <summary>
/// DeepSeek writes tool calls as DSML markup in the assistant text when a gateway
/// does not turn them into structured tool_calls. The markers use one or two
/// fullwidth vertical bars (U+FF5C), and sometimes ASCII bars.
/// </summary>
internal static class LeakedToolCallParser
{
    private const char FullwidthBar = '\uFF5C';

    public static bool TryParse(string? text, out IReadOnlyList<LeakedToolCall> calls, out string remainder)
    {
        calls = [];
        remainder = text ?? "";
        if (string.IsNullOrEmpty(text) || text.IndexOf("DSML", StringComparison.Ordinal) < 0)
            return false;

        var found = new List<LeakedToolCall>();
        var remove = new List<(int Start, int End)>();
        var index = 0;
        while (index < text.Length)
        {
            if (!TryFindTag(text, index, out var tag))
                break;

            if (tag.IsClose || tag.Name != "invoke")
            {
                var next = Math.Max(tag.End, index + 1);
                if (next <= index)
                    break;
                index = next;
                continue;
            }

            if (!TryReadInvoke(text, tag, out var call, out var end) || end <= tag.Start)
            {
                var next = Math.Max(tag.End, index + 1);
                if (next <= index)
                    break;
                index = next;
                continue;
            }

            found.Add(call);
            remove.Add((tag.Start, end));
            index = end;
        }

        if (found.Count == 0)
            return false;

        AddWrapperRanges(text, remove);
        calls = found;
        remainder = Excise(text, remove).Trim();
        return true;
    }

    private static bool TryReadInvoke(string text, DsmlTag open, out LeakedToolCall call, out int end)
    {
        call = new LeakedToolCall("", []);
        end = open.End;
        if (!TryAttribute(open.Attributes, "name", out var name))
            return false;

        name = name.Trim();
        if (name.Length == 0 || name.Contains('\n') || name.Contains('\r'))
            return false;

        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        var pos = open.End;
        var closed = false;
        var anyParameter = false;

        while (pos < text.Length)
        {
            if (!TryFindTag(text, pos, out var next))
            {
                end = text.Length;
                break;
            }

            if (!next.IsClose && next.Name == "parameter")
            {
                if (!TryAttribute(next.Attributes, "name", out var paramName) || paramName.Trim().Length == 0)
                    return false;

                if (!TryReadParameterValue(text, next.End, out var raw, out var valueEnd))
                    return false;

                bool? asString = null;
                if (TryAttribute(next.Attributes, "string", out var flag))
                    asString = flag.Equals("true", StringComparison.OrdinalIgnoreCase);

                args[paramName.Trim()] = ConvertArgument(raw, asString);
                anyParameter = true;
                if (valueEnd <= pos)
                    return false;
                pos = valueEnd;
                continue;
            }

            if (next.IsClose && next.Name == "invoke")
            {
                end = next.End;
                closed = true;
                break;
            }

            if (next.IsClose && IsWrapperName(next.Name))
            {
                end = next.Start;
                closed = true;
                break;
            }

            if (!next.IsClose && next.Name == "invoke")
            {
                end = next.Start;
                break;
            }

            end = next.Start;
            break;
        }

        if (!closed && !anyParameter)
            return false;

        if (end < open.End)
            end = open.End;

        call = new LeakedToolCall(name, args);
        return true;
    }

    private static bool TryReadParameterValue(string text, int valueStart, out string raw, out int end)
    {
        raw = "";
        end = valueStart;
        if (!TryFindTag(text, valueStart, out var next))
            return false;

        raw = text.Substring(valueStart, next.Start - valueStart);
        end = next.IsClose && next.Name == "parameter" ? next.End : next.Start;
        return end >= valueStart;
    }

    private static object? ConvertArgument(string raw, bool? asString)
    {
        var value = raw.Trim();
        if (asString == true)
            return value;

        if (TryConvertJson(value, out var parsed))
            return parsed;

        return value;
    }

    private static bool TryConvertJson(string value, out object? parsed)
    {
        parsed = null;
        if (value.Length == 0)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(value);
            switch (doc.RootElement.ValueKind)
            {
                case JsonValueKind.String:
                    parsed = doc.RootElement.GetString();
                    return true;
                case JsonValueKind.True:
                    parsed = true;
                    return true;
                case JsonValueKind.False:
                    parsed = false;
                    return true;
                case JsonValueKind.Null:
                    parsed = null;
                    return true;
                case JsonValueKind.Number:
                    if (doc.RootElement.TryGetInt32(out var i32))
                    {
                        parsed = i32;
                        return true;
                    }

                    if (doc.RootElement.TryGetInt64(out var i64))
                    {
                        parsed = i64;
                        return true;
                    }

                    parsed = doc.RootElement.GetDouble();
                    return true;
                default:
                    parsed = doc.RootElement.Clone();
                    return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AddWrapperRanges(string text, List<(int Start, int End)> remove)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (!TryFindTag(text, index, out var tag))
                break;

            if (IsWrapperName(tag.Name))
                remove.Add((tag.Start, tag.End));

            var next = Math.Max(tag.End, index + 1);
            if (next <= index)
                break;
            index = next;
        }
    }

    private static bool IsWrapperName(string name) =>
        name is "calls" or "tool_calls" or "toolcalls";

    private static string Excise(string text, List<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0)
            return text;

        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var (start, end) in ranges)
        {
            if (end <= cursor)
                continue;

            var from = Math.Max(start, cursor);
            if (from > cursor)
                builder.Append(text, cursor, from - cursor);
            cursor = Math.Max(cursor, end);
        }

        if (cursor < text.Length)
            builder.Append(text, cursor, text.Length - cursor);

        return builder.ToString();
    }

    private static bool TryFindTag(string text, int start, out DsmlTag tag)
    {
        var from = Math.Max(0, start);
        while (from < text.Length)
        {
            var dsml = text.IndexOf("DSML", from, StringComparison.Ordinal);
            if (dsml < 0)
                break;

            if (TryDescribe(text, dsml, out tag) && tag.Start >= start)
                return true;

            from = dsml + 4;
        }

        tag = default;
        return false;
    }

    private static bool TryDescribe(string text, int dsml, out DsmlTag tag)
    {
        tag = default;
        if ((uint)dsml > (uint)text.Length || dsml + 4 > text.Length ||
            !text.AsSpan(dsml, 4).SequenceEqual("DSML"))
            return false;

        var index = dsml - 1;
        if (index < 0 || !IsBar(text[index]))
            return false;

        var bar = text[index];
        var barCount = 1;
        index--;
        if (index >= 0 && text[index] == bar)
        {
            barCount = 2;
            index--;
        }

        if (index >= 0 && text[index] == bar)
            return false;

        var close = false;
        if (index >= 0 && text[index] == '/')
        {
            close = true;
            index--;
        }

        var start = index + 1;
        if (index >= 0 && text[index] == '<')
            start = index;
        else if (!close)
            close = true;

        var cursor = dsml + 4;
        if (!TryConsumeBars(text, ref cursor, bar, barCount))
            return false;

        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            cursor++;

        var nameStart = cursor;
        while (cursor < text.Length && IsNameChar(text[cursor]))
            cursor++;

        if (cursor == nameStart)
            return false;

        var name = text.Substring(nameStart, cursor - nameStart);
        if (!IsKnownName(name))
            return false;

        var attrStart = cursor;
        var end = FindTagEnd(text, cursor);
        if (end < 0)
            return false;

        tag = new DsmlTag(start, end + 1, close, name, text.Substring(attrStart, end - attrStart));
        return true;
    }

    private static bool TryConsumeBars(string text, ref int cursor, char bar, int count)
    {
        for (var n = 0; n < count; n++)
        {
            if (cursor >= text.Length || text[cursor] != bar)
                return false;
            cursor++;
        }

        return cursor >= text.Length || text[cursor] != bar;
    }

    private static int FindTagEnd(string text, int start)
    {
        var quoted = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (c == '>' && !quoted)
                return i;
        }

        return -1;
    }

    private static bool TryAttribute(string attributes, string key, out string value)
    {
        value = "";
        var index = 0;
        while (index < attributes.Length)
        {
            while (index < attributes.Length && char.IsWhiteSpace(attributes[index]))
                index++;
            if (index >= attributes.Length)
                break;

            var keyStart = index;
            while (index < attributes.Length && IsNameChar(attributes[index]))
                index++;
            if (index == keyStart)
            {
                index++;
                continue;
            }

            var found = attributes.AsSpan(keyStart, index - keyStart).Equals(key, StringComparison.Ordinal);
            while (index < attributes.Length && char.IsWhiteSpace(attributes[index]))
                index++;
            if (index >= attributes.Length || attributes[index] != '=')
            {
                while (index < attributes.Length && !char.IsWhiteSpace(attributes[index]))
                    index++;
                continue;
            }

            index++;
            while (index < attributes.Length && char.IsWhiteSpace(attributes[index]))
                index++;
            if (index >= attributes.Length || attributes[index] != '"')
                return false;

            index++;
            var valueStart = index;
            while (index < attributes.Length && attributes[index] != '"')
            {
                if (attributes[index] == '\\' && index + 1 < attributes.Length)
                    index += 2;
                else
                    index++;
            }

            var raw = attributes.Substring(valueStart, Math.Max(0, index - valueStart));
            if (index < attributes.Length && attributes[index] == '"')
                index++;

            if (!found)
                continue;

            value = Unescape(raw);
            return true;
        }

        return false;
    }

    private static string Unescape(string raw)
    {
        if (raw.IndexOf('\\') < 0)
            return raw;

        var builder = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                builder.Append(raw[++i]);
                continue;
            }

            builder.Append(raw[i]);
        }

        return builder.ToString();
    }

    private static bool IsKnownName(string name) =>
        name is "invoke" or "parameter" or "calls" or "tool_calls" or "toolcalls";

    private static bool IsNameChar(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsBar(char c) => c == FullwidthBar || c == '|';

    private readonly struct DsmlTag(
        int start,
        int end,
        bool isClose,
        string name,
        string attributes)
    {
        public int Start { get; } = start;
        public int End { get; } = end;
        public bool IsClose { get; } = isClose;
        public string Name { get; } = name;
        public string Attributes { get; } = attributes;
    }
}
