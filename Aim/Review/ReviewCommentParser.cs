using System.Globalization;
using System.Text.Json;

namespace Aim;

internal sealed class ReviewParseResult
{
    public bool Success { get; private init; }
    public string Error { get; private init; } = "";
    public List<ReviewComment> Comments { get; private init; } = [];
    public int Skipped { get; private init; }
    public int OmittedByCap { get; private init; }

    public static ReviewParseResult Fail(string error) => new() { Success = false, Error = error };

    public static ReviewParseResult Ok(List<ReviewComment> comments, int skipped, int omitted) => new()
    {
        Success = true,
        Comments = comments,
        Skipped = skipped,
        OmittedByCap = omitted
    };
}

internal static class ReviewCommentParser
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly HashSet<string> Severities = new(StringComparer.OrdinalIgnoreCase)
    {
        "critical", "high", "medium", "low"
    };

    private static readonly HashSet<string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bug", "security", "performance", "maintainability", "style", "other"
    };

    public static ReviewParseResult Parse(string? raw, int maxComments)
    {
        if (maxComments < 1)
            maxComments = 20;

        if (string.IsNullOrWhiteSpace(raw))
            return ReviewParseResult.Fail("The model returned an empty response.");

        var payload = FindBestPayload(raw);
        if (payload is null)
            return ReviewParseResult.Fail("The model did not return review comments as JSON.");

        try
        {
            using var doc = JsonDocument.Parse(payload, DocOptions);
            if (!TryGetCommentArray(doc.RootElement, out var array))
                return ReviewParseResult.Fail("The model did not return review comments as JSON.");

            var parsed = new List<(ReviewComment Comment, int Index)>();
            var skipped = 0;
            var index = 0;
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object || !TryNormalize(element, out var comment))
                {
                    skipped++;
                    index++;
                    continue;
                }

                parsed.Add((comment, index));
                index++;
            }

            var unique = new List<(ReviewComment Comment, int Index)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in parsed)
            {
                var key = string.Join(
                    '\u001f',
                    item.Comment.Path,
                    item.Comment.StartLine.ToString(CultureInfo.InvariantCulture),
                    item.Comment.EndLine.ToString(CultureInfo.InvariantCulture),
                    item.Comment.Severity,
                    item.Comment.Category,
                    item.Comment.Content);
                if (!seen.Add(key))
                    continue;

                unique.Add(item);
            }

            if (unique.Count <= maxComments)
                return ReviewParseResult.Ok(unique.Select(item => item.Comment).ToList(), skipped, 0);

            var kept = unique
                .OrderBy(item => SeverityRank(item.Comment.Severity))
                .ThenBy(item => item.Index)
                .Take(maxComments)
                .OrderBy(item => item.Index)
                .Select(item => item.Comment)
                .ToList();

            return ReviewParseResult.Ok(kept, skipped, unique.Count - kept.Count);
        }
        catch (JsonException)
        {
            return ReviewParseResult.Fail("The model did not return review comments as JSON.");
        }
    }

    private static string? FindBestPayload(string raw)
    {
        string? best = null;
        var bestScore = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] is not ('[' or '{'))
                continue;

            var slice = SliceBalanced(raw, i);
            if (slice is null)
                continue;

            try
            {
                using var doc = JsonDocument.Parse(slice, DocOptions);
                var score = Score(doc.RootElement);
                if (score > bestScore || (score == bestScore && score > 0 && best is not null && slice.Length > best.Length))
                {
                    bestScore = score;
                    best = slice;
                }
            }
            catch (JsonException)
            {
                // Keep scanning. Models often emit brackets that are not JSON.
            }
        }

        return bestScore > 0 ? best : null;
    }

    private static int Score(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return 1000 + CountObjects(root);

        if (root.ValueKind == JsonValueKind.Object &&
            TryGetProperty(root, "comments", out var comments) &&
            comments.ValueKind == JsonValueKind.Array)
            return 1000 + CountObjects(comments);

        return 0;
    }

    private static int CountObjects(JsonElement array)
    {
        var count = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                count++;
        }

        return count;
    }

    private static bool TryGetCommentArray(JsonElement root, out JsonElement array)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            TryGetProperty(root, "comments", out array) &&
            array.ValueKind == JsonValueKind.Array)
            return true;

        array = default;
        return false;
    }

    private static bool TryNormalize(JsonElement element, out ReviewComment comment)
    {
        comment = new ReviewComment();
        if (!TryGetProperty(element, "path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
            return false;

        var path = pathEl.GetString()?.Trim() ?? "";
        if (path.Length == 0 || path.Contains('\n') || path.Contains('\r'))
            return false;

        if (!TryReadInt(element, "start_line", out var start))
            return false;

        if (!TryReadInt(element, "end_line", out var end))
            end = start;

        if (start < 1)
            return false;

        if (end < 1)
            end = start;

        if (end < start)
            (start, end) = (end, start);

        if (!TryGetProperty(element, "severity", out var severityEl) || severityEl.ValueKind != JsonValueKind.String)
            return false;

        var severity = severityEl.GetString()?.Trim().ToLowerInvariant() ?? "";
        if (!Severities.Contains(severity))
            return false;

        if (!TryGetProperty(element, "category", out var categoryEl) || categoryEl.ValueKind != JsonValueKind.String)
            return false;

        var category = categoryEl.GetString()?.Trim().ToLowerInvariant() ?? "";
        if (!Categories.Contains(category))
            return false;

        if (!TryGetProperty(element, "content", out var contentEl) || contentEl.ValueKind != JsonValueKind.String)
            return false;

        var content = contentEl.GetString()?.Trim() ?? "";
        if (content.Length == 0)
            return false;

        string? suggestion = null;
        if (TryGetProperty(element, "suggestion_code", out var suggestionEl) &&
            suggestionEl.ValueKind == JsonValueKind.String)
        {
            var text = suggestionEl.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text))
                suggestion = text;
        }

        comment = new ReviewComment
        {
            Path = path.Replace('\\', '/'),
            StartLine = start,
            EndLine = end,
            Severity = severity,
            Category = category,
            Content = content,
            SuggestionCode = suggestion
        };
        return true;
    }

    private static bool TryReadInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        if (!TryGetProperty(obj, name, out var el))
            return false;

        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetInt32(out value);

        return el.ValueKind == JsonValueKind.String &&
               int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        _ => 4
    };

    private static string? SliceBalanced(string text, int start)
    {
        var open = text[start];
        var close = open == '[' ? ']' : '}';
        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '"')
                    inString = false;

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == open)
                depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                    return text.Substring(start, i - start + 1);
            }
        }

        return null;
    }
}
