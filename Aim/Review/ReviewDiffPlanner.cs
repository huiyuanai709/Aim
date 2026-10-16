using System.Globalization;
using System.Text;

namespace Aim;

internal sealed class ReviewFilePatch
{
    public required string Path { get; init; }
    public required string Text { get; init; }
}

internal sealed class ReviewSkippedFile
{
    public required string Path { get; init; }
    public required int Characters { get; init; }
    public required int Limit { get; init; }
}

internal sealed class ReviewGroup
{
    public required int Number { get; init; }
    public required string Directory { get; init; }
    public required string Label { get; init; }
    public required IReadOnlyList<ReviewFilePatch> Files { get; init; }
    public required string Diff { get; init; }
}

internal sealed class ReviewPlanOptions
{
    public int MaxDiffChars { get; init; } = ReviewParallelLimits.DefaultGroupChars;
    public int MaxFilesPerGroup { get; init; } = ReviewParallelLimits.MaxFilesPerGroup;
}

internal sealed class ReviewPlan
{
    public bool Unsplit { get; init; }
    public bool SingleShot { get; init; }
    public int MaxDiffChars { get; init; }
    public int GroupCharacterBudget { get; init; }
    public int SingleFileLimit { get; init; }
    public int MaxFilesPerGroup { get; init; }
    public IReadOnlyList<ReviewGroup> Groups { get; init; } = [];
    public IReadOnlyList<ReviewSkippedFile> Skipped { get; init; } = [];
}

internal sealed class ReviewBudgetSelection
{
    public required IReadOnlyList<ReviewGroup> Selected { get; init; }
    public required IReadOnlyList<ReviewGroup> Deferred { get; init; }
}

/// <summary>
/// Splits a unified diff into files and packs them into directory groups.
/// A diff that already fits in one model call stays one call.
/// </summary>
internal static class ReviewDiffPlanner
{
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return (text.Length + 3) / 4;
    }

    public static ReviewPlan Plan(string diff, ReviewPlanOptions options)
    {
        var maxFiles = options.MaxFilesPerGroup < 1
            ? ReviewParallelLimits.MaxFilesPerGroup
            : options.MaxFilesPerGroup;
        var maxDiffChars = options.MaxDiffChars;
        var charBudget = maxDiffChars > 0 ? maxDiffChars : int.MaxValue;
        var singleFileLimit = maxDiffChars > 0
            ? (int)((long)maxDiffChars * 4 / 5)
            : int.MaxValue;

        var patches = Split(diff);
        if (patches.Count == 0)
        {
            return new ReviewPlan
            {
                Unsplit = true,
                SingleShot = true,
                MaxDiffChars = maxDiffChars,
                GroupCharacterBudget = charBudget,
                SingleFileLimit = singleFileLimit,
                MaxFilesPerGroup = maxFiles
            };
        }

        var skipped = new List<ReviewSkippedFile>();
        var kept = new List<ReviewFilePatch>();
        foreach (var patch in patches)
        {
            if (patch.Text.Length > singleFileLimit)
            {
                skipped.Add(new ReviewSkippedFile
                {
                    Path = patch.Path,
                    Characters = patch.Text.Length,
                    Limit = singleFileLimit
                });
                continue;
            }

            kept.Add(patch);
        }

        var keptFit = KeptFitsOneCall(diff, kept, skipped.Count, maxDiffChars, maxFiles);
        if (kept.Count > 0 && keptFit)
        {
            return new ReviewPlan
            {
                SingleShot = skipped.Count == 0,
                MaxDiffChars = maxDiffChars,
                GroupCharacterBudget = charBudget,
                SingleFileLimit = singleFileLimit,
                MaxFilesPerGroup = maxFiles,
                Groups = [MakeGroup(1, kept)],
                Skipped = skipped
            };
        }

        return new ReviewPlan
        {
            SingleShot = false,
            MaxDiffChars = maxDiffChars,
            GroupCharacterBudget = charBudget,
            SingleFileLimit = singleFileLimit,
            MaxFilesPerGroup = maxFiles,
            Groups = Pack(kept, charBudget, maxFiles),
            Skipped = skipped
        };
    }

    public static ReviewBudgetSelection SelectForBudget(IReadOnlyList<ReviewGroup> groups, int maxTokensBudget)
    {
        if (maxTokensBudget <= 0 || groups.Count == 0)
            return new ReviewBudgetSelection { Selected = groups.ToList(), Deferred = [] };

        var selected = new List<ReviewGroup>();
        var deferred = new List<ReviewGroup>();
        var spent = 0;
        var stop = false;
        foreach (var group in groups)
        {
            var estimate = EstimateTokens(group.Diff);
            if (stop || (selected.Count > 0 && spent + estimate > maxTokensBudget))
            {
                stop = true;
                deferred.Add(group);
                continue;
            }

            selected.Add(group);
            spent += estimate;
        }

        return new ReviewBudgetSelection { Selected = selected, Deferred = deferred };
    }

    public static string SkipWarning(ReviewSkippedFile file) =>
        $"Warning: Skipping {file.Path} ({file.Characters.ToString(CultureInfo.InvariantCulture)} characters). A single file over {file.Limit.ToString(CultureInfo.InvariantCulture)} characters (80% of maxdiffchars) is left out so the rest of the review can finish.";

    public static string FormatPreview(
        ReviewPlan plan,
        string targetDescription,
        bool agent,
        int concurrency,
        int timeoutMinutes,
        int maxTokensBudget)
    {
        var sb = new StringBuilder();
        sb.Append("Review target: ").Append(targetDescription).Append('\n');

        if (plan.Unsplit)
        {
            AppendMode(sb, parallel: false, agent);
            sb.Append("This diff has no per-file headers, so Aim cannot split it into groups.\n");
            if (plan.MaxDiffChars > 0)
            {
                sb.Append("If it is longer than maxdiffchars (")
                    .Append(plan.MaxDiffChars.ToString(CultureInfo.InvariantCulture))
                    .Append("), Aim truncates it or sends file names instead.\n");
            }

            AppendTimeout(sb, timeoutMinutes);
            return sb.ToString().TrimEnd();
        }

        var fileCount = plan.Groups.Sum(group => group.Files.Count);
        var parallel = plan.Groups.Count > 1;
        AppendMode(sb, parallel, agent);
        sb.Append("Reviewable files: ")
            .Append(fileCount.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        sb.Append("Skipped files: ")
            .Append(plan.Skipped.Count.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        if (parallel)
        {
            sb.Append("Groups: ")
                .Append(plan.Groups.Count.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
            sb.Append("Concurrency: ")
                .Append(concurrency.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
            AppendGroupSize(sb, plan);
        }
        else
        {
            sb.Append("A diff that fits in one call is not split.\n");
        }

        AppendTimeout(sb, timeoutMinutes);
        AppendBudgetHeader(sb, maxTokensBudget);

        if (plan.Skipped.Count > 0)
        {
            sb.Append('\n');
            sb.Append("Skipped (one file over ")
                .Append(plan.SingleFileLimit.ToString(CultureInfo.InvariantCulture))
                .Append(" characters, 80% of maxdiffchars):\n");
            foreach (var file in plan.Skipped)
            {
                sb.Append("  ")
                    .Append(file.Path)
                    .Append(" (")
                    .Append(file.Characters.ToString(CultureInfo.InvariantCulture))
                    .Append(" characters)\n");
            }
        }

        var budget = SelectForBudget(plan.Groups, maxTokensBudget);
        var deferred = new HashSet<ReviewGroup>(budget.Deferred);
        if (parallel && maxTokensBudget > 0 && budget.Selected.Count > 0 &&
            EstimateTokens(budget.Selected[0].Diff) > maxTokensBudget)
        {
            sb.Append('\n');
            sb.Append("The first group is still sent when it alone exceeds the token budget.\n");
        }

        if (!parallel)
        {
            sb.Append('\n');
            foreach (var group in plan.Groups)
            {
                foreach (var file in group.Files)
                    sb.Append(file.Path).Append('\n');
            }
        }
        else
        {
            foreach (var group in plan.Groups)
            {
                sb.Append('\n');
                sb.Append("Group ")
                    .Append(group.Number.ToString(CultureInfo.InvariantCulture))
                    .Append(": ")
                    .Append(group.Label)
                    .Append(" (")
                    .Append(group.Files.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(group.Files.Count == 1 ? " file, " : " files, ")
                    .Append(group.Diff.Length.ToString(CultureInfo.InvariantCulture))
                    .Append(" characters, about ")
                    .Append(EstimateTokens(group.Diff).ToString(CultureInfo.InvariantCulture))
                    .Append(" tokens)");
                if (deferred.Contains(group))
                    sb.Append(" [not sent: token budget]");
                sb.Append('\n');
                foreach (var file in group.Files)
                    sb.Append("  ").Append(file.Path).Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatNameOnlyPreview(string targetDescription, string diff, bool truncated)
    {
        var sb = new StringBuilder();
        sb.Append("Review target: ").Append(targetDescription).Append('\n');
        sb.Append("Mode: single call (file names only)\n");
        sb.Append("File names cannot be grouped into patches. Aim sends them in one call.\n");
        if (truncated)
            sb.Append("The file list was truncated to maxdiffchars.\n");

        var normalized = diff.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var any = false;
        foreach (var line in normalized.Split('\n'))
        {
            if (line.Length == 0)
                continue;

            if (!any)
            {
                sb.Append('\n');
                any = true;
            }

            sb.Append(line).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static bool KeptFitsOneCall(
        string originalDiff,
        List<ReviewFilePatch> kept,
        int skippedCount,
        int maxDiffChars,
        int maxFiles)
    {
        if (kept.Count == 0)
            return false;

        if (maxDiffChars <= 0)
            return kept.Count <= maxFiles;

        if (skippedCount == 0)
            return originalDiff.Length <= maxDiffChars;

        return JoinedLength(kept) <= maxDiffChars;
    }

    private static List<ReviewGroup> Pack(List<ReviewFilePatch> files, int charBudget, int maxFiles)
    {
        var byDirectory = new List<(string Directory, List<ReviewFilePatch> Files)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var directory = DirectoryOf(file.Path);
            if (!index.TryGetValue(directory, out var at))
            {
                at = byDirectory.Count;
                index[directory] = at;
                byDirectory.Add((directory, []));
            }

            byDirectory[at].Files.Add(file);
        }

        var groups = new List<ReviewGroup>();
        foreach (var (directory, dirFiles) in byDirectory)
        {
            var chunks = Chunk(dirFiles, charBudget, maxFiles);
            for (var i = 0; i < chunks.Count; i++)
            {
                var label = DisplayDirectory(directory);
                if (chunks.Count > 1)
                {
                    label += " (part " +
                             (i + 1).ToString(CultureInfo.InvariantCulture) +
                             " of " +
                             chunks.Count.ToString(CultureInfo.InvariantCulture) +
                             ")";
                }

                groups.Add(MakeGroup(groups.Count + 1, chunks[i], label));
            }
        }

        return groups;
    }

    private static List<List<ReviewFilePatch>> Chunk(List<ReviewFilePatch> files, int charBudget, int maxFiles)
    {
        var chunks = new List<List<ReviewFilePatch>>();
        var current = new List<ReviewFilePatch>();
        long currentChars = 0;
        foreach (var file in files)
        {
            var separator = current.Count == 0 ? 0 : 1;
            var nextChars = currentChars + separator + file.Text.Length;
            if (current.Count > 0 && (current.Count >= maxFiles || nextChars > charBudget))
            {
                chunks.Add(current);
                current = [];
                currentChars = 0;
                nextChars = file.Text.Length;
            }

            current.Add(file);
            currentChars = nextChars;
        }

        if (current.Count > 0)
            chunks.Add(current);

        return chunks;
    }

    private static ReviewGroup MakeGroup(int number, IReadOnlyList<ReviewFilePatch> files, string? label = null)
    {
        var directory = files.Count == 0 ? "" : DirectoryOf(files[0].Path);
        var sameDirectory = files.All(file => DirectoryOf(file.Path) == directory);
        var resolved = label ?? (sameDirectory ? DisplayDirectory(directory) : "(all files)");
        return new ReviewGroup
        {
            Number = number,
            Directory = sameDirectory ? directory : "",
            Label = resolved,
            Files = files,
            Diff = Join(files)
        };
    }

    private static string Join(IReadOnlyList<ReviewFilePatch> files)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < files.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(files[i].Text);
        }

        return sb.ToString();
    }

    private static int JoinedLength(IReadOnlyList<ReviewFilePatch> files)
    {
        long total = 0;
        for (var i = 0; i < files.Count; i++)
        {
            if (i > 0)
                total++;
            total += files[i].Text.Length;
            if (total >= int.MaxValue)
                return int.MaxValue;
        }

        return (int)total;
    }

    internal static List<ReviewFilePatch> SplitPatches(string diff) => Split(diff);

    private static List<ReviewFilePatch> Split(string diff)
    {
        var normalized = diff.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
            normalized = normalized[1..];

        var lines = normalized.Split('\n');
        var parts = new List<(string Header, StringBuilder Body)>();
        StringBuilder? current = null;
        var header = "";
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (current is not null)
                    parts.Add((header, current));

                current = new StringBuilder();
                header = line;
            }

            if (current is null)
                continue;

            current.Append(line);
            current.Append('\n');
        }

        if (current is not null)
            parts.Add((header, current));

        var files = new List<ReviewFilePatch>(parts.Count);
        foreach (var (partHeader, body) in parts)
        {
            files.Add(new ReviewFilePatch
            {
                Path = ParseNewPath(partHeader),
                Text = body.ToString().TrimEnd('\n')
            });
        }

        return files;
    }

    private static string ParseNewPath(string header)
    {
        const string prefix = "diff --git ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
            return "unknown";

        var rest = header[prefix.Length..];
        if (!TryReadToken(rest, 0, out var oldToken, out var next))
            return "unknown";

        var oldPath = StripSidePrefix(oldToken);
        if (!TryReadToken(rest, next, out var newToken, out _))
            return oldPath.Length == 0 ? "unknown" : oldPath.Replace('\\', '/');

        var newPath = StripSidePrefix(newToken);
        if (newPath == "dev/null")
            newPath = oldPath == "dev/null" ? "" : oldPath;

        if (newPath.Length == 0)
            return "unknown";

        return newPath.Replace('\\', '/');
    }

    private static string StripSidePrefix(string token)
    {
        if (token.StartsWith("a/", StringComparison.Ordinal) || token.StartsWith("b/", StringComparison.Ordinal))
            return token[2..];

        return token;
    }

    private static bool TryReadToken(string text, int start, out string token, out int next)
    {
        token = "";
        next = start;
        while (start < text.Length && text[start] == ' ')
            start++;

        if (start >= text.Length)
            return false;

        if (text[start] != '"')
        {
            var end = text.IndexOf(' ', start);
            if (end < 0)
                end = text.Length;

            token = text[start..end];
            next = end;
            return token.Length > 0;
        }

        var bytes = new List<byte>();
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                token = Encoding.UTF8.GetString(bytes.ToArray());
                next = i + 1;
                return true;
            }

            if (c == '\\' && i + 1 < text.Length)
            {
                var escaped = text[i + 1];
                if (escaped is >= '0' and <= '7')
                {
                    var value = 0;
                    var count = 0;
                    i++;
                    while (count < 3 && i < text.Length && text[i] is >= '0' and <= '7')
                    {
                        value = (value << 3) + (text[i] - '0');
                        i++;
                        count++;
                    }

                    bytes.Add((byte)value);
                    continue;
                }

                bytes.Add(escaped switch
                {
                    'n' => (byte)'\n',
                    't' => (byte)'\t',
                    'b' => (byte)'\b',
                    'a' => (byte)'\a',
                    _ => (byte)escaped
                });
                i += 2;
                continue;
            }

            if (c <= 127)
                bytes.Add((byte)c);
            else
                bytes.AddRange(Encoding.UTF8.GetBytes([c]));

            i++;
        }

        return false;
    }

    private static string DirectoryOf(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? "" : normalized[..slash];
    }

    private static string DisplayDirectory(string directory) =>
        directory.Length == 0 ? "(root)" : directory;

    private static void AppendMode(StringBuilder sb, bool parallel, bool agent)
    {
        sb.Append(parallel ? "Mode: parallel groups" : "Mode: single call");
        if (agent)
            sb.Append(", with the review agent");
        sb.Append('\n');
    }

    private static void AppendTimeout(StringBuilder sb, int timeoutMinutes)
    {
        sb.Append("Per-group timeout: ")
            .Append(ReviewParallelLimits.FormatMinutes(timeoutMinutes))
            .Append('\n');
    }

    private static void AppendGroupSize(StringBuilder sb, ReviewPlan plan)
    {
        sb.Append("Group size: at most ")
            .Append(plan.MaxFilesPerGroup.ToString(CultureInfo.InvariantCulture))
            .Append(" files");
        if (plan.MaxDiffChars > 0)
        {
            sb.Append(" and ")
                .Append(plan.MaxDiffChars.ToString(CultureInfo.InvariantCulture))
                .Append(" characters");
        }
        else
        {
            sb.Append(" (no character cap, because maxdiffchars is 0)");
        }

        sb.Append(", grouped by directory.\n");
    }

    private static void AppendBudgetHeader(StringBuilder sb, int maxTokensBudget)
    {
        if (maxTokensBudget <= 0)
        {
            sb.Append("Token budget: none\n");
            return;
        }

        sb.Append("Token budget: ")
            .Append(maxTokensBudget.ToString(CultureInfo.InvariantCulture))
            .Append(" (estimated as diff characters / 4)\n");
    }
}
