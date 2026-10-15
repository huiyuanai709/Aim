using System.Text;
using Aim;

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
