using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Aim;
using Ngrep.Search;

namespace Aim.Tests;

public class ReviewToolsTests
{
    [Fact]
    public void FileReadReturnsAWorktreeWindowAndRejectsEscape()
    {
        using var repo = new TempRepo();
        var lines = Enumerable.Range(1, 10).Select(i => $"line-{i}").ToArray();
        repo.Write("src/App.cs", string.Join('\n', lines) + "\n");
        repo.Git("add", "src/App.cs");
        repo.Git("commit", "-m", "init");

        var progress = new StringWriter();
        var tools = new ReviewTools(repo.Path, revision: null, maxChars: 12_000, progress);

        var window = tools.FileRead("src/App.cs", start_line: 2, end_line: 4);
        Assert.Contains("src/App.cs:2-4 of 10", window);
        Assert.Contains("2: line-2", window);
        Assert.Contains("4: line-4", window);
        Assert.DoesNotContain("line-5", window);
        Assert.Contains("▶ file_read src/App.cs:2-4", progress.ToString());

        progress.GetStringBuilder().Clear();
        var rejected = tools.FileRead("../outside.txt");
        Assert.Contains("inside the repository", rejected);
        Assert.Contains("▶ file_read (rejected)", progress.ToString());
    }

    [Fact]
    public void FileReadCapsTheLineWindow()
    {
        using var repo = new TempRepo();
        var body = new StringBuilder();
        for (var i = 1; i <= 250; i++)
            body.Append("row-").Append(i).Append('\n');
        repo.Write("big.txt", body.ToString());
        repo.Git("add", "big.txt");
        repo.Git("commit", "-m", "init");

        var tools = new ReviewTools(repo.Path, revision: null, maxChars: 200_000, progress: TextWriter.Null);
        var text = tools.FileRead("big.txt");

        Assert.Contains("1-200 of 250", text);
        Assert.Contains("window capped at 200 lines", text);
        Assert.Contains("200: row-200", text);
        Assert.DoesNotContain("201: row-201", text);
    }

    [Fact]
    public void CommitRevisionReadsGitShowRatherThanTheWorktree()
    {
        using var repo = new TempRepo();
        repo.Write("note.txt", "committed-text\n");
        repo.Git("add", "note.txt");
        repo.Git("commit", "-m", "init");
        var (ok, sha, err) = GitExecutor.RunGit(["rev-parse", "HEAD"], repo.Path);
        Assert.True(ok, err);
        repo.Write("note.txt", "worktree-text\n");

        var tools = new ReviewTools(repo.Path, sha.Trim(), maxChars: 4000, progress: TextWriter.Null);
        var fromCommit = tools.FileRead("note.txt");
        Assert.Contains("committed-text", fromCommit);
        Assert.DoesNotContain("worktree-text", fromCommit);

        var worktree = new ReviewTools(repo.Path, revision: null, maxChars: 4000, progress: TextWriter.Null);
        Assert.Contains("worktree-text", worktree.FileRead("note.txt"));
    }

    [Fact]
    public void SymlinkOutsideTheRepositoryIsNotRead()
    {
        using var repo = new TempRepo();
        var outside = Path.Combine(Path.GetTempPath(), "aim-secret-" + Guid.NewGuid().ToString("n") + ".txt");
        File.WriteAllText(outside, "secret-payload");
        try
        {
            File.CreateSymbolicLink(Path.Combine(repo.Path, "leak.txt"), outside);
            var tools = new ReviewTools(repo.Path, revision: null, maxChars: 4000, progress: TextWriter.Null);
            var result = tools.FileRead("leak.txt");
            Assert.Contains("inside the repository", result);
            Assert.DoesNotContain("secret-payload", result);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void CodeSearchTreatsTheQueryAsALiteralAndCapsLines()
    {
        using var repo = new TempRepo();
        var marker = Path.Combine(Path.GetTempPath(), "aim-review-pwn-" + Guid.NewGuid().ToString("n"));
        var query = $"hello; touch {marker}";
        var body = new StringBuilder();
        for (var i = 0; i < 100; i++)
            body.Append(query).Append('\n');
        repo.Write("notes.txt", body.ToString());
        repo.Git("add", "notes.txt");
        repo.Git("commit", "-m", "init");

        var progress = new StringWriter();
        var tools = new ReviewTools(repo.Path, revision: null, maxChars: 200_000, progress);
        var result = tools.CodeSearch(query, "notes.txt");

        Assert.False(File.Exists(marker));
        Assert.Contains(query, result);
        Assert.Contains("more lines omitted", result);
        Assert.Contains("▶ code_search", progress.ToString());
        Assert.Equal("No matches.", tools.CodeSearch("this-string-is-absent", "notes.txt"));
        Assert.False(Directory.Exists(Path.Combine(repo.Path, ".ngrep")));
    }

    [Fact]
    public void WorktreeSearchUsesNgrepAndRevisionSearchUsesGitGrep()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "committed-token\n");
        repo.Git("add", "tracked.txt");
        repo.Git("commit", "-m", "init");
        var (ok, sha, err) = GitExecutor.RunGit(["rev-parse", "HEAD"], repo.Path);
        Assert.True(ok, err);
        repo.Write("tracked.txt", "worktree-token\n");
        repo.Write("untracked.txt", "untracked-token\n");
        repo.Write(".dot.txt", "dotfile-token\n");
        repo.Write(".gitignore", "ignored.txt\n");
        repo.Write("ignored.txt", "ignored-token\n");
        repo.Write("src/App.cs", "nested-token\n");
        repo.Write("lit.txt", "a.b\naxb\n");
        repo.Write("bin.dat", "before\0needle-bin\n");

        var work = new ReviewTools(repo.Path, revision: null, maxChars: 20_000, TextWriter.Null);
        var atCommit = new ReviewTools(repo.Path, sha.Trim(), maxChars: 20_000, TextWriter.Null);

        Assert.Contains("tracked.txt:", work.CodeSearch("worktree-token"));
        Assert.Contains("worktree-token", work.CodeSearch("worktree-token"));
        Assert.Equal("No matches.", work.CodeSearch("committed-token"));
        Assert.Contains("untracked.txt:", work.CodeSearch("untracked-token"));
        Assert.Contains(".dot.txt:", work.CodeSearch("dotfile-token"));
        Assert.Equal("No matches.", work.CodeSearch("ignored-token"));
        Assert.Contains("src/App.cs:", work.CodeSearch("nested-token", "src"));
        var literal = work.CodeSearch("a.b", "lit.txt");
        Assert.Contains("lit.txt:", literal);
        Assert.Contains("a.b", literal);
        Assert.DoesNotContain("axb", literal);
        Assert.Equal("No matches.", work.CodeSearch("needle-bin"));

        Assert.Contains("committed-token", atCommit.CodeSearch("committed-token"));
        Assert.DoesNotContain("worktree-token", atCommit.CodeSearch("committed-token"));
        Assert.Equal("No matches.", atCommit.CodeSearch("worktree-token"));
        Assert.Equal("No matches.", atCommit.CodeSearch("untracked-token"));
        Assert.Contains("tracked.txt:", atCommit.CodeSearch("committed-token", "tracked.txt"));
    }

    [Fact]
    public async Task WorktreeSearchUsesAnExistingIndexAndDoesNotBuildOne()
    {
        using var repo = new TempRepo();
        repo.Write("old.txt", "indexed-token\n");
        repo.Git("add", "old.txt");
        repo.Git("commit", "-m", "init");

        var tools = new ReviewTools(repo.Path, revision: null, maxChars: 12_000, TextWriter.Null);
        Assert.Contains("old.txt:1:indexed-token", tools.CodeSearch("indexed-token"));
        Assert.False(Directory.Exists(Path.Combine(repo.Path, ".ngrep")));

        using (CliCapture.Hold())
        {
            Assert.Equal(0, await SearchCommand.Invoke(["index", repo.Path, "--quiet"]));
        }

        Assert.True(File.Exists(Path.Combine(repo.Path, ".ngrep", "index", "manifest.json")));
        repo.Write("new.txt", "indexed-token\n");

        var indexed = tools.CodeSearch("indexed-token");
        Assert.Contains("old.txt:", indexed);
        Assert.DoesNotContain("new.txt", indexed);

        Directory.Delete(Path.Combine(repo.Path, ".ngrep"), recursive: true);
        var scanned = tools.CodeSearch("indexed-token");
        Assert.Contains("old.txt:", scanned);
        Assert.Contains("new.txt:", scanned);
    }

    [Fact]
    public void CodeSearchStopsBeforeReadingEveryMatchingFile()
    {
        using var repo = new TempRepo();
        const int files = 400;
        for (var i = 0; i < files; i++)
            repo.Write($"early/f{i:000}.txt", "needle-line\n");
        repo.Git("add", "early");
        repo.Git("commit", "-m", "early");

        var confirmed = 0;
        var previous = Searcher.BeforeConfirm;
        Searcher.BeforeConfirm = path =>
        {
            if (path.StartsWith("early/", StringComparison.Ordinal))
                Interlocked.Increment(ref confirmed);
            previous?.Invoke(path);
        };
        try
        {
            var tools = new ReviewTools(repo.Path, revision: null, maxChars: 200_000, TextWriter.Null);
            var result = tools.CodeSearch("needle-line");
            var hits = result.Split('\n').Count(line => line.Contains("needle-line", StringComparison.Ordinal));
            Assert.Equal(ReviewTools.MaxSearchLines, hits);
            Assert.Contains("more lines omitted", result, StringComparison.Ordinal);
            Assert.InRange(confirmed, 1, files - 1);
        }
        finally
        {
            Searcher.BeforeConfirm = previous;
        }
    }

    [Fact]
    public void CreateFunctionsRegistersReportProgress()
    {
        var tools = new ReviewTools(Environment.CurrentDirectory, revision: null, maxChars: 4_000, TextWriter.Null);
        var functions = tools.CreateFunctions();
        var names = functions.Select(function => function.Name).ToList();

        Assert.Contains("file_read", names);
        Assert.Contains("code_search", names);
        Assert.Contains("report_progress", names);
        Assert.Equal(3, names.Count);

        var progress = Assert.Single(functions, function => function.Name == "report_progress");
        Assert.Contains("does not submit a review comment", progress.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("message", progress.JsonSchema.ToString());
    }

    [Fact]
    public async Task ReportProgressAcceptsTheJsonArgumentTheAgentSends()
    {
        var progress = new StringWriter();
        var tools = new ReviewTools(Environment.CurrentDirectory, revision: null, maxChars: 4_000, progress);
        var function = Assert.Single(tools.CreateFunctions(), candidate => candidate.Name == "report_progress");
        using var document = JsonDocument.Parse("""{"message":"Reading the handler"}""");
        var arguments = new AIFunctionArguments();
        foreach (var property in document.RootElement.EnumerateObject())
            arguments[property.Name] = property.Value.Clone();

        var result = await function.InvokeAsync(arguments);

        var returned = Assert.IsType<JsonElement>(result);
        Assert.Equal("Noted. This does not submit a review comment.", returned.GetString());
        Assert.Equal("▶ report_progress Reading the handler" + Environment.NewLine, progress.ToString());
    }

    [Fact]
    public void ReportProgressPrintsOneStatusLine()
    {
        var progress = new StringWriter();
        var tools = new ReviewTools(Environment.CurrentDirectory, revision: null, maxChars: 4_000, progress);

        var result = tools.ReportProgress("Reading the payment handler");

        Assert.Equal("▶ report_progress Reading the payment handler" + Environment.NewLine, progress.ToString());
        Assert.Equal("Noted. This does not submit a review comment.", result);
        Assert.DoesNotContain("start_line", result, StringComparison.Ordinal);

        progress.GetStringBuilder().Clear();
        tools.ReportProgress("正在阅读支付处理");
        Assert.Equal("▶ report_progress 正在阅读支付处理" + Environment.NewLine, progress.ToString());
    }

    [Fact]
    public void ReportProgressCollapsesNewlinesStripsControlsAndCapsLength()
    {
        var progress = new StringWriter();
        var tools = new ReviewTools(Environment.CurrentDirectory, revision: null, maxChars: 4_000, progress);

        var collapsed = tools.ReportProgress("Reading files\r\n\r\nsearching\tcallers");
        Assert.Equal("▶ report_progress Reading files searching callers" + Environment.NewLine, progress.ToString());
        Assert.Equal("Noted. This does not submit a review comment.", collapsed);

        progress.GetStringBuilder().Clear();
        var noisy = tools.ReportProgress("Status\u001b[31m ok\u0000\u202e");
        var noisyLine = progress.ToString();
        Assert.DoesNotContain("\u001b", noisyLine, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", noisyLine, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202e", noisyLine, StringComparison.Ordinal);
        Assert.Equal("▶ report_progress Status [31m ok" + Environment.NewLine, noisyLine);
        Assert.Equal("Noted. This does not submit a review comment.", noisy);

        progress.GetStringBuilder().Clear();
        var longMessage = "Reading " + new string('a', 400);
        tools.ReportProgress(longMessage);
        var written = progress.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        var line = Assert.Single(written.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("▶ report_progress Reading ", line, StringComparison.Ordinal);
        var status = line["▶ report_progress ".Length..];
        Assert.Equal(ReviewTools.MaxProgressChars, status.Length);
        Assert.EndsWith("...", status, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", status, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportProgressRejectsABlankStatus()
    {
        var progress = new StringWriter();
        var tools = new ReviewTools(Environment.CurrentDirectory, revision: null, maxChars: 4_000, progress);

        var result = tools.ReportProgress(" \r\n\t");

        Assert.Equal("▶ report_progress (rejected)" + Environment.NewLine, progress.ToString());
        Assert.Equal("Status must be one short line of plain text. This does not submit a review comment.", result);
    }

    [Fact]
    public void ParallelReportProgressDoesNotInterleaveLines()
    {
        var inner = new StringWriter();
        var progress = new LockedTextWriter(inner);
        var tools = new ReviewTools(Environment.CurrentDirectory, revision: null, maxChars: 4_000, progress);
        var messages = Enumerable.Range(0, 32)
            .Select(i => new string((char)('A' + (i % 26)), 40) + i.ToString("00"))
            .ToArray();

        Parallel.For(0, messages.Length, i => tools.ReportProgress(messages[i]));

        var lines = inner.ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(messages.Length, lines.Length);
        foreach (var line in lines)
        {
            Assert.StartsWith("▶ report_progress ", line, StringComparison.Ordinal);
            var body = line["▶ report_progress ".Length..];
            Assert.Contains(body, messages);
        }
    }

    [Fact]
    public async Task ConcurrentReadsSearchesAndStatusStayIntact()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "alpha-token\n");
        repo.Write("b.txt", "beta-token\n");
        repo.Git("add", ".");
        repo.Git("commit", "-m", "init");
        repo.Write("c.txt", "worktree-token\n");

        var progress = new StringWriter();
        var work = new ReviewTools(repo.Path, revision: null, maxChars: 12_000, progress);
        var at = new ReviewTools(repo.Path, "HEAD", 12_000, progress);
        var errors = new List<string>();
        var gate = new object();

        void Check(bool ok, string detail)
        {
            if (ok)
                return;
            lock (gate)
                errors.Add(detail);
        }

        Parallel.For(0, 8, _ =>
        {
            var read = work.FileRead("a.txt");
            Check(read.Contains("alpha-token", StringComparison.Ordinal), "read " + read);
            var other = work.FileRead("b.txt");
            Check(other.Contains("beta-token", StringComparison.Ordinal) && !other.Contains("alpha-token", StringComparison.Ordinal), "other " + other);

            var alpha = work.CodeSearch("alpha-token");
            Check(alpha.Contains("a.txt:", StringComparison.Ordinal) && !alpha.Contains("worktree-token", StringComparison.Ordinal), "alpha " + alpha);
            var fresh = work.CodeSearch("worktree-token");
            Check(fresh.Contains("c.txt:", StringComparison.Ordinal) && !fresh.Contains("alpha-token", StringComparison.Ordinal), "fresh " + fresh);

            var rev = at.CodeSearch("beta-token");
            Check(rev.Contains("b.txt:", StringComparison.Ordinal) && !rev.Contains("worktree-token", StringComparison.Ordinal), "rev " + rev);
            var missed = at.CodeSearch("worktree-token");
            Check(missed == "No matches.", "missed " + missed);

            work.ReportProgress("Reading the worktree");
            at.ReportProgress("Searching the commit");
        });

        Assert.Empty(errors);

        using (CliCapture.Hold())
            Assert.Equal(0, await SearchCommand.Invoke(["index", repo.Path, "--quiet"]));
        repo.Write("d.txt", "alpha-token\n");

        Parallel.For(0, 8, _ =>
        {
            var indexed = work.CodeSearch("alpha-token");
            Check(indexed.Contains("a.txt:", StringComparison.Ordinal) && !indexed.Contains("d.txt", StringComparison.Ordinal), "indexed " + indexed);
            var missed = work.CodeSearch("not-in-the-tree");
            Check(missed == "No matches.", "indexed miss " + missed);
        });

        Assert.Empty(errors);
        var lines = progress.ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            Assert.StartsWith("▶ ", line, StringComparison.Ordinal);
            Assert.Equal(1, line.Split("▶ ", StringSplitOptions.None).Length - 1);
        }
    }

    private sealed class TempRepo : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "aim-tools-" + Guid.NewGuid().ToString("n"));

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
