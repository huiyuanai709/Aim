namespace Aim;

internal static class ReviewCommentMerger
{
    public static ReviewParseResult Merge(IReadOnlyList<ReviewComment> comments, int skippedSchema, int maxComments)
    {
        if (maxComments < 1)
            maxComments = 20;

        var unique = new List<(ReviewComment Comment, int Index)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < comments.Count; i++)
        {
            var comment = comments[i];
            var key = string.Join(
                '\u001f',
                comment.Path,
                comment.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
                comment.EndLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
                comment.Severity,
                comment.Category,
                comment.Content);
            if (!seen.Add(key))
                continue;

            unique.Add((comment, i));
        }

        if (unique.Count <= maxComments)
            return ReviewParseResult.Ok(unique.Select(item => item.Comment).ToList(), skippedSchema, 0);

        var kept = unique
            .OrderBy(item => SeverityRank(item.Comment.Severity))
            .ThenBy(item => item.Index)
            .Take(maxComments)
            .OrderBy(item => item.Index)
            .Select(item => item.Comment)
            .ToList();

        return ReviewParseResult.Ok(kept, skippedSchema, unique.Count - kept.Count);
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        _ => 4
    };
}
