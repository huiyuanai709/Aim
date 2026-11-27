using Aim;

namespace Aim.Tests;

public class SearchCommandTests
{
    [Fact]
    public async Task RootHelpListsSearch()
    {
        using var capture = CliCapture.Hold();
        var code = await CliRoot.Create().Parse(["--help"]).InvokeAsync();
        Assert.Equal(0, code);
        Assert.Contains("search", capture.Stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("ngrep", capture.Stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchHelpDocumentsIndexAndFixedStrings()
    {
        using var capture = CliCapture.Hold();
        var code = await SearchCommand.Invoke(["--help"]);
        Assert.Equal(0, code);
        var text = capture.Stdout.ToString();
        Assert.Contains("aim search index", text, StringComparison.Ordinal);
        Assert.Contains("aim search watch", text, StringComparison.Ordinal);
        Assert.Contains("--files", text, StringComparison.Ordinal);
        Assert.Contains("--fixed-strings", text, StringComparison.Ordinal);
        Assert.Contains(".gitignore", text, StringComparison.Ordinal);
        Assert.Contains(".ngrep/index", text, StringComparison.Ordinal);
        Assert.Contains("AVX2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchHonorsGitignoreAndFixedStrings()
    {
        using var dir = new Tree();
        dir.Write("keep.txt", "visible-token\n");
        dir.Write("skip.log", "hidden-token\n");
        dir.Write(".gitignore", "*.log\n");
        dir.Write("re.txt", "a.b\naxb\n");

        using var capture = CliCapture.Hold();
        var found = await SearchCommand.Invoke(["visible-token", dir.Path, "--no-column", "--no-heading", "--color", "never"]);
        Assert.Equal(0, found);
        var hit = capture.Stdout.ToString();
        Assert.Contains("keep.txt:1:visible-token", hit, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-token", hit, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", hit, StringComparison.Ordinal);

        capture.Clear();
        var ignored = await SearchCommand.Invoke(["hidden-token", dir.Path, "--no-column", "--color", "never"]);
        Assert.Equal(1, ignored);
        Assert.DoesNotContain("hidden-token", capture.Stdout.ToString(), StringComparison.Ordinal);

        capture.Clear();
        var regex = await SearchCommand.Invoke(["a.b", dir.Path, "--no-column", "--no-heading", "--color", "never"]);
        Assert.Equal(0, regex);
        Assert.Contains("axb", capture.Stdout.ToString(), StringComparison.Ordinal);

        capture.Clear();
        var literal = await SearchCommand.Invoke(["-F", "a.b", dir.Path, "--no-column", "--no-heading", "--color", "never"]);
        Assert.Equal(0, literal);
        var literalText = capture.Stdout.ToString();
        Assert.Contains("a.b", literalText, StringComparison.Ordinal);
        Assert.DoesNotContain("axb", literalText, StringComparison.Ordinal);

        capture.Clear();
        var files = await SearchCommand.Invoke(["--files", dir.Path]);
        Assert.Equal(0, files);
        var listed = capture.Stdout.ToString();
        Assert.Contains("keep.txt", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("skip.log", listed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndexIsUsedByALaterSearch()
    {
        using var dir = new Tree();
        dir.Write("note.txt", "indexed-phrase\n");

        using var capture = CliCapture.Hold();
        var scanned = await SearchCommand.Invoke(["indexed-phrase", dir.Path, "--no-column", "--color", "never"]);
        Assert.Equal(0, scanned);
        Assert.Contains("no index found", capture.Stderr.ToString(), StringComparison.Ordinal);

        capture.Clear();
        var built = await SearchCommand.Invoke(["index", dir.Path, "--quiet"]);
        Assert.Equal(0, built);
        Assert.True(File.Exists(Path.Combine(dir.Path, ".ngrep", "index", "manifest.json")));

        capture.Clear();
        var indexed = await SearchCommand.Invoke(["indexed-phrase", dir.Path, "--no-column", "--no-heading", "--color", "never"]);
        Assert.Equal(0, indexed);
        Assert.Contains("note.txt:1:indexed-phrase", capture.Stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("no index found", capture.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndexWithoutAPathFails()
    {
        using var capture = CliCapture.Hold();
        var code = await SearchCommand.Invoke(["index"]);
        Assert.Equal(2, code);
    }

    private sealed class Tree : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "aim-search-" + Guid.NewGuid().ToString("n"));

        public Tree()
        {
            Directory.CreateDirectory(Path);
            Git("init", "-b", "main");
        }

        public void Write(string name, string contents)
        {
            var full = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, contents);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
                return;
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, true);
        }

        private void Git(params string[] args)
        {
            var (ok, stdout, stderr) = GitExecutor.RunGit(args, Path);
            Assert.True(ok, stderr + stdout);
        }
    }
}
