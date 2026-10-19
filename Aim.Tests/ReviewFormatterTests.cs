using System.Text;
using System.Text.Json;
using Aim;

namespace Aim.Tests;

public class ReviewFormatterTests
{
    [Fact]
    public void PlainTextKeepsSeverityUncolored()
    {
        var text = Render(color: false, diff: SampleDiff());

        Assert.Contains("src/App.cs:11-12  high  bug", text);
        Assert.Contains("src/Other.cs:3  low  style", text);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorTintsSeverityAndLeavesThePathReadable()
    {
        var text = Render(color: true, diff: SampleDiff());

        Assert.Contains("\u001b[1;36msrc/App.cs\u001b[0m:\u001b[1m11-12\u001b[0m  \u001b[1;91mhigh\u001b[0m  \u001b[35mbug\u001b[0m", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[1;36msrc/Other.cs\u001b[0m:\u001b[1m3\u001b[0m  \u001b[2;36mlow\u001b[0m  \u001b[35mstyle\u001b[0m", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[1;36mlib/Mid.cs\u001b[0m:\u001b[1m4\u001b[0m  \u001b[33mmedium\u001b[0m  \u001b[35mperformance\u001b[0m", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[1;36mlib/Mid.cs\u001b[0m:\u001b[1m8\u001b[0m  \u001b[1;91mcritical\u001b[0m  \u001b[35msecurity\u001b[0m", text, StringComparison.Ordinal);
        Assert.Contains("src/App.cs", text, StringComparison.Ordinal);
        Assert.Contains("    \u001b[90m11 | \u001b[0m\u001b[1;95mnew\u001b[0m line", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[1;36mCode:\u001b[0m", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[1;91mbug", text, StringComparison.Ordinal);

        var prose = Assert.Single(text.Split('\n'), line => line.Contains("skipped", StringComparison.Ordinal));
        Assert.Equal("  The last item is skipped.", prose);
    }

    [Fact]
    public void ColorHighlightsCitedCodeAndSuggestion()
    {
        var diff = string.Join('\n',
        [
            "diff --git a/src/App.cs b/src/App.cs",
            "--- a/src/App.cs",
            "+++ b/src/App.cs",
            "@@ -1,2 +1,2 @@",
            "+if (count > 0) // ready",
            "+    return \"ok\";"
        ]);
        var text = Render(color: true, diff: diff, comments:
        [
            Comment("src/App.cs", 1, 2, "high", "bug", "Check the count.", "return count;")
        ]);

        Assert.Contains("  \u001b[1;36mCode:\u001b[0m", text, StringComparison.Ordinal);
        Assert.Contains("    \u001b[90m1 | \u001b[0m\u001b[1;95mif\u001b[0m (count > \u001b[93m0\u001b[0m) \u001b[3;90m// ready\u001b[0m", text, StringComparison.Ordinal);
        Assert.Contains("    \u001b[90m2 | \u001b[0m    \u001b[1;95mreturn\u001b[0m \u001b[92m\"ok\"\u001b[0m;", text, StringComparison.Ordinal);
        Assert.Contains("  \u001b[1;36mSuggestion:\u001b[0m", text, StringComparison.Ordinal);
        Assert.Contains("    \u001b[1;95mreturn\u001b[0m count;", text, StringComparison.Ordinal);
        Assert.Contains("\n1 comment. Line numbers refer to the new file and are best-effort (not relocated).", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextLeavesKeywordsUncolored()
    {
        var diff = string.Join('\n',
        [
            "diff --git a/src/App.cs b/src/App.cs",
            "--- a/src/App.cs",
            "+++ b/src/App.cs",
            "@@ -1,1 +1,1 @@",
            "+if (count > 0) // ready"
        ]);
        var text = Render(color: false, diff: diff, comments:
        [
            Comment("src/App.cs", 1, 1, "high", "bug", "Check the count.", "return count;")
        ]);

        Assert.Contains("src/App.cs:1  high  bug", text, StringComparison.Ordinal);
        Assert.Contains("  Code:\n    1 | if (count > 0) // ready\n  Suggestion:\n    return count;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownLanguageDoesNotTintWholeCodeLines()
    {
        var diff = string.Join('\n',
        [
            "diff --git a/notes/readme.txt b/notes/readme.txt",
            "--- a/notes/readme.txt",
            "+++ b/notes/readme.txt",
            "@@ -1,1 +1,1 @@",
            "+if (ready)"
        ]);
        var text = Render(color: true, diff: diff, comments:
        [
            Comment("notes/readme.txt", 1, 1, "low", "style", "Say what is ready.", "return true;")
        ]);

        Assert.Contains("    \u001b[90m1 | \u001b[0mif (ready)\n", text, StringComparison.Ordinal);
        Assert.Contains("    return true;\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[36mif (ready)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[36mreturn true;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[1;95m", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SuggestionColorDoesNotInheritAnOpenComment()
    {
        var diff = string.Join('\n',
        [
            "diff --git a/src/App.cs b/src/App.cs",
            "--- a/src/App.cs",
            "+++ b/src/App.cs",
            "@@ -1,1 +1,1 @@",
            "+/* unfinished"
        ]);
        var text = Render(color: true, diff: diff, comments:
        [
            Comment("src/App.cs", 1, 1, "medium", "maintainability", "Close the comment.", "int x = 1;")
        ]);

        Assert.Contains("    \u001b[90m1 | \u001b[0m\u001b[3;90m/* unfinished\u001b[0m", text, StringComparison.Ordinal);
        Assert.Contains("    \u001b[1;95mint\u001b[0m x = \u001b[93m1\u001b[0m;", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorDimsTheOmittedLineCount()
    {
        var body = new List<string>
        {
            "diff --git a/src/Long.cs b/src/Long.cs",
            "--- a/src/Long.cs",
            "+++ b/src/Long.cs",
            "@@ -0,0 +1,25 @@"
        };
        for (var i = 1; i <= 25; i++)
            body.Add($"+line-{i}");

        var text = Render(color: true, diff: string.Join('\n', body), comments:
        [
            Comment("src/Long.cs", 1, 25, "medium", "maintainability", "This function is too long.")
        ]);

        Assert.Contains("\u001b[90m... 5 more lines\u001b[0m", text, StringComparison.Ordinal);
        Assert.Contains("    \u001b[90m 1 | \u001b[0mline-\u001b[93m1\u001b[0m", text, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonStaysPlain()
    {
        var json = ReviewFormatter.ToJson(
        [
            Comment("src/App.cs", 11, 12, "high", "bug", "The last item is skipped.")
        ]);

        using var doc = JsonDocument.Parse(json);
        var item = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("src/App.cs", item.GetProperty("path").GetString());
        Assert.Equal(11, item.GetProperty("start_line").GetInt32());
        Assert.Equal(12, item.GetProperty("end_line").GetInt32());
        Assert.Equal("high", item.GetProperty("severity").GetString());
        Assert.DoesNotContain("\u001b", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SnippetShowsNewFileLinesFromTheDiff()
    {
        var text = Render(color: false, diff: SampleDiff(), comments:
        [
            Comment("src/App.cs", 11, 12, "high", "bug", "The last item is skipped.", "while (index < items.Count)")
        ]);

        Assert.Contains("  The last item is skipped.", text);
        Assert.Contains("  Code:\n    11 | new line\n    12 | kept-after\n  Suggestion:\n    while (index < items.Count)", text);
        Assert.DoesNotContain("old", text);
        Assert.DoesNotContain("+new line", text);
        Assert.DoesNotContain("kept-before", text);
    }

    [Fact]
    public void SnippetSkipsLinesThatCannotBeResolved()
    {
        var text = Render(color: false, diff: SampleDiff(), comments:
        [
            Comment("src/Missing.cs", 4, 4, "low", "style", "Nothing to show.")
        ]);

        Assert.Contains("src/Missing.cs:4  low  style", text);
        Assert.DoesNotContain("Code:", text);
    }

    [Fact]
    public void SnippetCapsALongCitedRange()
    {
        var body = new List<string>
        {
            "diff --git a/src/Long.cs b/src/Long.cs",
            "--- a/src/Long.cs",
            "+++ b/src/Long.cs",
            "@@ -0,0 +1,25 @@"
        };
        for (var i = 1; i <= 25; i++)
            body.Add($"+line-{i}");

        var text = Render(color: false, diff: string.Join('\n', body), comments:
        [
            Comment("src/Long.cs", 1, 25, "medium", "maintainability", "This function is too long.")
        ]);

        Assert.Contains("     1 | line-1", text);
        Assert.Contains("    20 | line-20", text);
        Assert.DoesNotContain("line-21", text);
        Assert.Contains("... 5 more lines", text);
    }

    [Fact]
    public void SnippetClipsAVeryLongLine()
    {
        var huge = new string('x', ReviewCodeIndex.MaxLineChars + 40);
        var diff = string.Join('\n',
        [
            "diff --git a/src/Wide.cs b/src/Wide.cs",
            "--- a/src/Wide.cs",
            "+++ b/src/Wide.cs",
            "@@ -0,0 +1 @@",
            "+" + huge
        ]);

        var text = Render(color: false, diff: diff, comments:
        [
            Comment("src/Wide.cs", 1, 1, "low", "style", "Line is too wide.")
        ]);

        Assert.Contains(new string('x', ReviewCodeIndex.MaxLineChars) + "...", text);
        Assert.DoesNotContain(huge, text);
    }

    [Fact]
    public void SnippetReadsQuotedPaths()
    {
        var diff = string.Join('\n',
        [
            """diff --git "a/my file.cs" "b/my file.cs" """,
            """--- "a/my file.cs" """,
            """+++ "b/my file.cs" """,
            "@@ -0,0 +1,2 @@",
            "+hello",
            "+world"
        ]);

        var text = Render(color: false, diff: diff, comments:
        [
            Comment("my file.cs", 2, 2, "low", "style", "Name the concept.")
        ]);

        Assert.Contains("    2 | world", text);
        Assert.DoesNotContain("hello", text);
    }

    [Fact]
    public void SnippetNumbersALaterHunk()
    {
        var diff = string.Join('\n',
        [
            "diff --git a/src/App.cs b/src/App.cs",
            "--- a/src/App.cs",
            "+++ b/src/App.cs",
            "@@ -1,2 +1,2 @@",
            " kept",
            "-old",
            "+fresh",
            "@@ -20,1 +21,1 @@",
            "-gone",
            "+after"
        ]);

        var text = Render(color: false, diff: diff, comments:
        [
            Comment("src/App.cs", 21, 21, "high", "bug", "Wrong value.")
        ]);

        Assert.Contains("    21 | after", text);
        Assert.DoesNotContain("gone", text);
        Assert.DoesNotContain("fresh", text);
    }

    [Fact]
    public void WorkingTreeFillsLinesTheDiffDoesNotContain()
    {
        var dir = Directory.CreateTempSubdirectory("aim-fmt-");
        try
        {
            var file = Path.Combine(dir.FullName, "src", "App.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "alpha\nbeta\ngamma\n");

            var text = ReviewFormatter.ToText(
                [Comment("src/App.cs", 2, 3, "low", "style", "Rename this.")],
                diffTruncated: false,
                nameOnly: false,
                workingDirectory: dir.FullName);

            Assert.Contains("    2 | beta", text);
            Assert.Contains("    3 | gamma", text);
            Assert.DoesNotContain("alpha", text);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void DiffLinesWinOverTheWorkingTree()
    {
        var dir = Directory.CreateTempSubdirectory("aim-fmt-");
        try
        {
            var file = Path.Combine(dir.FullName, "src", "App.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "from-worktree\n");

            var text = ReviewFormatter.ToText(
                [Comment("src/App.cs", 11, 11, "high", "bug", "Use the diff.")],
                diffTruncated: false,
                nameOnly: false,
                diff: SampleDiff(),
                workingDirectory: dir.FullName);

            Assert.Contains("    11 | new line", text);
            Assert.DoesNotContain("from-worktree", text);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void PartialDiffHitDoesNotFillTheRestFromTheWorkingTree()
    {
        var dir = Directory.CreateTempSubdirectory("aim-fmt-");
        try
        {
            var file = Path.Combine(dir.FullName, "src", "App.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var body = new StringBuilder();
            for (var i = 1; i <= 12; i++)
                body.Append("worktree-").Append(i).Append('\n');
            File.WriteAllText(file, body.ToString());

            var diff = string.Join('\n',
            [
                "diff --git a/src/App.cs b/src/App.cs",
                "--- a/src/App.cs",
                "+++ b/src/App.cs",
                "@@ -11,1 +11,1 @@",
                "-old",
                "+from-diff"
            ]);
            var text = ReviewFormatter.ToText(
                [Comment("src/App.cs", 10, 12, "high", "bug", "Only the changed line is in the diff.")],
                diffTruncated: false,
                nameOnly: false,
                diff: diff,
                workingDirectory: dir.FullName);

            Assert.Contains("    11 | from-diff", text);
            Assert.DoesNotContain("worktree-", text);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void NameOnlyDoesNotInventLinesFromTheWorkingTree()
    {
        var dir = Directory.CreateTempSubdirectory("aim-fmt-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "App.cs"), "secret-body\n");
            var text = ReviewFormatter.ToText(
                [Comment("App.cs", 1, 1, "low", "style", "Placeholder line.")],
                diffTruncated: false,
                nameOnly: true,
                workingDirectory: dir.FullName);

            Assert.Contains("Only file names were sent", text);
            Assert.DoesNotContain("Code:", text);
            Assert.DoesNotContain("secret-body", text);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void WorkingTreeFallbackRejectsPathsOutsideTheDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("aim-fmt-");
        var outside = Path.Combine(Path.GetTempPath(), "aim-secret-" + Guid.NewGuid().ToString("n") + ".txt");
        File.WriteAllText(outside, "secret-payload");
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir.FullName, "leak.txt"), outside);
            var escaped = ReviewFormatter.ToText(
                [Comment("../" + Path.GetFileName(outside), 1, 1, "high", "security", "Do not read this.")],
                diffTruncated: false,
                nameOnly: false,
                workingDirectory: dir.FullName);
            var linked = ReviewFormatter.ToText(
                [Comment("leak.txt", 1, 1, "high", "security", "Do not follow this.")],
                diffTruncated: false,
                nameOnly: false,
                workingDirectory: dir.FullName);

            Assert.DoesNotContain("secret-payload", escaped);
            Assert.DoesNotContain("secret-payload", linked);
            Assert.DoesNotContain("Code:", escaped + linked);
        }
        finally
        {
            File.Delete(outside);
            dir.Delete(true);
        }
    }

    [Fact]
    public void RevisionFallbackReadsTheCommitInsteadOfTheWorkingTree()
    {
        using var repo = new TempRepo();
        repo.Write("src/App.cs", "committed-text\n");
        repo.Git("add", "src/App.cs");
        repo.Git("commit", "-m", "init");
        var (ok, sha, err) = GitExecutor.RunGit(["rev-parse", "HEAD"], repo.Path);
        Assert.True(ok, err);
        repo.Write("src/App.cs", "worktree-text\n");

        var nested = Path.Combine(repo.Path, "nested");
        Directory.CreateDirectory(nested);
        var text = ReviewFormatter.ToText(
            [Comment("src/App.cs", 1, 1, "high", "bug", "Stale copy.")],
            diffTruncated: false,
            nameOnly: false,
            workingDirectory: nested,
            revision: sha.Trim());

        Assert.Contains("    1 | committed-text", text);
        Assert.DoesNotContain("worktree-text", text);
    }

    [Theory]
    [InlineData(null, null, false, null, true)]
    [InlineData(null, null, true, null, false)]
    [InlineData("1", null, false, null, false)]
    [InlineData("", null, false, null, true)]
    [InlineData("1", "1", false, null, false)]
    [InlineData(null, "1", true, null, true)]
    [InlineData(null, "0", false, null, false)]
    [InlineData(null, "false", false, null, false)]
    [InlineData(null, "", true, "dumb", true)]
    [InlineData(null, null, false, "dumb", false)]
    [InlineData(null, "2", true, "dumb", true)]
    [InlineData(null, "off", false, null, false)]
    public void ColorFollowsNoColorForceColorAndRedirect(
        string? noColor,
        string? forceColor,
        bool redirected,
        string? term,
        bool expected)
    {
        Assert.Equal(expected, TerminalColor.Decide(noColor, forceColor, redirected, term));
    }

    private static string Render(bool color, string? diff, IReadOnlyList<ReviewComment>? comments = null)
    {
        comments ??=
        [
            Comment("src/App.cs", 11, 12, "high", "bug", "The last item is skipped."),
            Comment("src/Other.cs", 3, 3, "low", "style", "Name hides the unit."),
            Comment("lib/Mid.cs", 4, 4, "medium", "performance", "This allocates on every call."),
            Comment("lib/Mid.cs", 8, 8, "critical", "security", "The token is logged.")
        ];
        return ReviewFormatter.ToText(comments, diffTruncated: false, nameOnly: false, color: color, diff: diff);
    }

    private static string SampleDiff() => string.Join('\n',
    [
        "diff --git a/src/App.cs b/src/App.cs",
        "index 1111111..2222222 100644",
        "--- a/src/App.cs",
        "+++ b/src/App.cs",
        "@@ -10,4 +10,4 @@",
        " kept-before",
        "-old",
        "+new line",
        " kept-after",
        "diff --git a/src/Other.cs b/src/Other.cs",
        "--- a/src/Other.cs",
        "+++ b/src/Other.cs",
        "@@ -1,3 +1,3 @@",
        " one",
        "-two",
        "+three",
        "diff --git a/lib/Mid.cs b/lib/Mid.cs",
        "--- a/lib/Mid.cs",
        "+++ b/lib/Mid.cs",
        "@@ -1,8 +1,8 @@",
        " a",
        " b",
        " c",
        "+alloc",
        " e",
        " f",
        " g",
        "+token"
    ]);

    private static ReviewComment Comment(
        string path,
        int start,
        int end,
        string severity,
        string category,
        string content,
        string? suggestion = null) => new()
    {
        Path = path,
        StartLine = start,
        EndLine = end,
        Severity = severity,
        Category = category,
        Content = content,
        SuggestionCode = suggestion
    };

    private sealed class TempRepo : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "aim-fmt-" + Guid.NewGuid().ToString("n"));

        public TempRepo()
        {
            Directory.CreateDirectory(Path);
            Git("init", "-b", "main");
            Git("config", "user.email", "aim-tests@example.com");
            Git("config", "user.name", "Aim Tests");
            Git("config", "commit.gpgsign", "false");
        }

        public void Write(string name, string contents)
        {
            var full = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, contents);
        }

        public void Git(params string[] args)
        {
            var (ok, stdout, stderr) = GitExecutor.RunGit(args, Path);
            Assert.True(ok, stderr + stdout);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
                return;

            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(Path, true);
        }
    }
}
