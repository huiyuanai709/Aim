using Aim;

namespace Aim.Tests;

public class ReviewDiffLoaderTests
{
    [Fact]
    public void StagedDiffIncludesTheCachedPatch()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Git("add", "a.txt");
        repo.Git("commit", "-m", "init");
        repo.Write("a.txt", "one\ntwo\n");
        repo.Git("add", "a.txt");

        var (ok, diff, err) = ReviewDiffLoader.Load(Target(null, null, null), false, repo.Path);

        Assert.True(ok, err);
        Assert.Contains("+two", diff);
    }

    [Fact]
    public void EmptyStagedDiffSucceedsWithNoPatch()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Git("add", "a.txt");
        repo.Git("commit", "-m", "init");

        var (ok, diff, err) = ReviewDiffLoader.Load(Target(null, null, null), false, repo.Path);

        Assert.True(ok, err);
        Assert.True(string.IsNullOrWhiteSpace(diff));
        Assert.Equal(
            "No staged changes to review. Run 'git add' first.",
            ReviewDiffLoader.EmptyMessage(Target(null, null, null)));
    }

    [Fact]
    public void NameOnlyOmitsPatchLines()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Git("add", "a.txt");
        repo.Git("commit", "-m", "init");
        repo.Write("a.txt", "one\ntwo\n");
        repo.Git("add", "a.txt");

        var (ok, diff, err) = ReviewDiffLoader.Load(Target(null, null, null), true, repo.Path);

        Assert.True(ok, err);
        Assert.Contains("a.txt", diff);
        Assert.DoesNotContain("+two", diff);
    }

    [Fact]
    public void TripleDotRangeIgnoresCommitsThatAreOnlyOnTheBase()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "base\n");
        repo.Git("add", "a.txt");
        repo.Git("commit", "-m", "base");
        repo.Git("checkout", "-b", "feature");
        repo.Write("a.txt", "base\nfeature\n");
        repo.Git("add", "a.txt");
        repo.Git("commit", "-m", "feature");
        repo.Git("checkout", "main");
        repo.Write("b.txt", "main-only\n");
        repo.Git("add", "b.txt");
        repo.Git("commit", "-m", "main only");

        var target = Target("main", "feature", null);
        var (ok, diff, err) = ReviewDiffLoader.Load(target, false, repo.Path);

        Assert.True(ok, err);
        Assert.Contains("+feature", diff);
        Assert.DoesNotContain("main-only", diff);
        Assert.Contains("feature", ReviewDiffLoader.Describe(target));
    }

    [Fact]
    public void CommitDiffIsOnlyThatCommit()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "base\n");
        repo.Git("add", "a.txt");
        repo.Git("commit", "-m", "base");
        repo.Write("b.txt", "main-only\n");
        repo.Git("add", "b.txt");
        repo.Git("commit", "-m", "main only");
        var (shaOk, sha, shaErr) = GitExecutor.RunGit(["rev-parse", "HEAD"], repo.Path);
        Assert.True(shaOk, shaErr);

        var target = Target(null, null, sha.Trim());
        var (ok, diff, err) = ReviewDiffLoader.Load(target, false, repo.Path);

        Assert.True(ok, err);
        Assert.Contains("main-only", diff);
        Assert.Contains("b.txt", diff);
        Assert.DoesNotContain("+base", diff);
    }

    [Fact]
    public void UnknownCommitFails()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Git("add", "a.txt");
        repo.Git("commit", "-m", "init");

        var (ok, _, err) = ReviewDiffLoader.Load(
            Target(null, null, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef"),
            false,
            repo.Path);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void OutsideARepositoryFails()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aim-not-git-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var (ok, _, err) = ReviewDiffLoader.Load(Target(null, null, null), false, dir);
            Assert.False(ok);
            Assert.Contains("not a git repository", err.ToLowerInvariant());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static ReviewTarget Target(string? from, string? to, string? commit)
    {
        Assert.True(ReviewTarget.TryParse(from, to, commit, false, false, "text", out var target, out var error), error);
        return target;
    }

    private sealed class TempRepo : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "aim-review-" + Guid.NewGuid().ToString("n"));

        public TempRepo()
        {
            Directory.CreateDirectory(Path);
            Git("init", "-b", "main");
            Git("config", "user.email", "aim-tests@example.com");
            Git("config", "user.name", "Aim Tests");
            Git("config", "commit.gpgsign", "false");
        }

        public void Write(string name, string contents) =>
            File.WriteAllText(System.IO.Path.Combine(Path, name), contents);

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
