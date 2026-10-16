using Aim;

namespace Aim.Tests;

public class DefaultBranchResolverTests
{
    [Fact]
    public void PrepareLeavesExplicitTargetsUntouched()
    {
        var missing = Path.Combine(Path.GetTempPath(), "aim-missing-" + Guid.NewGuid().ToString("n"));
        Assert.True(ReviewTarget.TryParse("main", "feature", null, false, false, "text", out var range, out var error), error);
        Assert.True(DefaultBranchResolver.TryPrepare(range, missing, out var prepared, out var note, out error), error);
        Assert.Equal(range, prepared);
        Assert.Null(note);

        Assert.True(ReviewTarget.TryParse(null, null, "abc123", false, false, "text", out var commit, out error), error);
        Assert.True(DefaultBranchResolver.TryPrepare(commit, missing, out prepared, out note, out error), error);
        Assert.Equal(commit, prepared);
        Assert.Null(note);
    }

    [Fact]
    public void UnresolvedBranchIsNotLoaded()
    {
        var target = new ReviewTarget(ReviewInputKind.Branch, null, null, null, "text");
        var (ok, _, err) = ReviewDiffLoader.Load(target, false, Path.GetTempPath());
        Assert.False(ok);
        Assert.Equal("Error: Branch review was not resolved.", err);
    }

    [Fact]
    public void BranchModeOutsideARepositoryFails()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aim-not-git-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(ReviewTarget.TryParse(null, null, null, true, false, "text", out var target, out var error), error);
            Assert.False(DefaultBranchResolver.TryPrepare(target, dir, out _, out _, out error));
            Assert.Equal(DefaultBranchResolver.NotARepositoryError, error);

            Assert.True(ReviewTarget.TryParse(null, null, null, false, true, "text", out target, out error), error);
            Assert.False(DefaultBranchResolver.TryPrepare(target, dir, out _, out _, out error));
            Assert.Equal(DefaultBranchResolver.NotARepositoryError, error);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EmptyRepositoryCannotResolveHead()
    {
        using var repo = new TempRepo();
        Assert.True(ReviewTarget.TryParse(null, null, null, true, false, "text", out var target, out var error), error);
        Assert.False(DefaultBranchResolver.TryPrepare(target, repo.Path, out _, out _, out error));
        Assert.Equal(DefaultBranchResolver.NoHeadError, error);
    }

    [Fact]
    public void MissingDefaultBranchNamesWhatWasTried()
    {
        using var repo = new TempRepo("develop");
        repo.Write("a.txt", "one\n");
        repo.Commit("init");

        Assert.True(ReviewTarget.TryParse(null, null, null, true, false, "text", out var target, out var error), error);
        Assert.False(DefaultBranchResolver.TryPrepare(target, repo.Path, out _, out _, out error));
        Assert.Equal(DefaultBranchResolver.NoDefaultBranchError, error);
    }

    [Fact]
    public void LocalMainIsTheFallbackAndTheDiffIsTripleDot()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "base\n");
        repo.Commit("base");
        repo.Git("checkout", "-b", "feature");
        repo.Write("a.txt", "base\nfeature\n");
        repo.Commit("feature");
        repo.Git("checkout", "main");
        repo.Write("b.txt", "main-only\n");
        repo.Commit("main only");
        repo.Git("checkout", "feature");

        var prepared = Prepare(repo, branch: true, first: false, out var note);
        Assert.Null(note);
        Assert.Equal(ReviewInputKind.Range, prepared.Kind);
        Assert.Equal("main", prepared.From);
        Assert.Equal("HEAD", prepared.To);
        Assert.Equal("HEAD", ReviewAgentLimits.ToolRevision(prepared));
        Assert.Equal("main...HEAD", ReviewDiffLoader.Describe(prepared));

        var (ok, diff, err) = ReviewDiffLoader.Load(prepared, false, repo.Path);
        Assert.True(ok, err);
        Assert.Contains("+feature", diff);
        Assert.DoesNotContain("main-only", diff);
    }

    [Fact]
    public void OriginHeadWinsOverLocalMainAndMaster()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "base\n");
        repo.Commit("base");
        repo.Git("checkout", "-b", "feature");
        repo.Write("a.txt", "base\nfeature\n");
        repo.Commit("feature");
        repo.Git("checkout", "main");
        repo.Write("b.txt", "main-only\n");
        repo.Commit("main only");
        repo.Git("update-ref", "refs/remotes/origin/main", "HEAD~1");
        repo.Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        repo.Git("branch", "master", "main");
        repo.Git("checkout", "feature");

        var prepared = Prepare(repo, branch: true, first: false, out _);
        Assert.Equal("origin/main", prepared.From);
        Assert.Equal("HEAD", prepared.To);

        var (ok, diff, err) = ReviewDiffLoader.Load(prepared, false, repo.Path);
        Assert.True(ok, err);
        Assert.Contains("+feature", diff);
        Assert.DoesNotContain("main-only", diff);
    }

    [Fact]
    public void DanglingOriginHeadFallsBackToMain()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "base\n");
        repo.Commit("base");
        repo.Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/missing");

        var prepared = Prepare(repo, branch: true, first: false, out _);
        Assert.Equal("main", prepared.From);
    }

    [Fact]
    public void MasterIsUsedWhenMainDoesNotExist()
    {
        using var repo = new TempRepo("master");
        repo.Write("a.txt", "base\n");
        repo.Commit("base");

        var prepared = Prepare(repo, branch: true, first: false, out _);
        Assert.Equal("master", prepared.From);
        Assert.Equal("HEAD", prepared.To);
    }

    [Fact]
    public void OriginMainIsUsedWhenNoLocalMainOrMasterExists()
    {
        using var repo = new TempRepo("develop");
        repo.Write("a.txt", "base\n");
        repo.Commit("base");
        repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");

        var prepared = Prepare(repo, branch: true, first: false, out _);
        Assert.Equal("origin/main", prepared.From);
    }

    [Fact]
    public void LocalMainBeatsOriginMasterWhenOriginHeadIsUnset()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "base\n");
        repo.Commit("base");
        repo.Git("update-ref", "refs/remotes/origin/master", "HEAD");

        var prepared = Prepare(repo, branch: true, first: false, out _);
        Assert.Equal("main", prepared.From);
    }

    [Fact]
    public void DetachedHeadStillComparesAgainstTheDefaultBranch()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "base\n");
        repo.Commit("base");
        repo.Git("checkout", "-b", "feature");
        repo.Write("a.txt", "base\nfeature\n");
        repo.Commit("feature");
        repo.Git("checkout", "--detach");

        var prepared = Prepare(repo, branch: true, first: false, out _);
        Assert.Equal("main", prepared.From);
        var (ok, diff, err) = ReviewDiffLoader.Load(prepared, false, repo.Path);
        Assert.True(ok, err);
        Assert.Contains("+feature", diff);
    }

    [Fact]
    public void FirstCommitIsTheOldestUniqueCommit()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "base\n");
        repo.Commit("base");
        repo.Git("checkout", "-b", "feature");
        repo.Write("first.txt", "first\n");
        repo.Commit("first");
        repo.Write("second.txt", "second\n");
        repo.Commit("second");

        var prepared = Prepare(repo, branch: true, first: false, out _);
        Assert.Equal("main", prepared.From);

        var first = Prepare(repo, branch: false, first: true, out var note);
        var (parentOk, parent, parentErr) = GitExecutor.RunGit(["rev-parse", "HEAD^"], repo.Path);
        Assert.True(parentOk, parentErr);
        Assert.Equal(ReviewInputKind.Commit, first.Kind);
        Assert.Equal(parent.Trim(), first.Commit);
        Assert.Equal(first.Commit, ReviewAgentLimits.ToolRevision(first));
        Assert.NotNull(note);
        Assert.Contains("main", note);
        Assert.Contains(first.Commit!, note);

        var (ok, diff, err) = ReviewDiffLoader.Load(first, false, repo.Path);
        Assert.True(ok, err);
        Assert.Contains("+first", diff);
        Assert.Contains("first.txt", diff);
        Assert.DoesNotContain("second", diff);
    }

    [Fact]
    public void FirstCommitFailsWhenHeadHasNoUniqueCommits()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "base\n");
        repo.Commit("base");

        Assert.True(ReviewTarget.TryParse(null, null, null, false, true, "text", out var target, out var error), error);
        Assert.False(DefaultBranchResolver.TryPrepare(target, repo.Path, out _, out _, out error));
        Assert.Equal("Error: HEAD has no commit that is not already on 'main'.", error);
    }

    [Fact]
    public void FirstCommitFailsWhenHistoriesDoNotMeet()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "base\n");
        repo.Commit("base");
        repo.Git("checkout", "--orphan", "other");
        repo.Write("x.txt", "other\n");
        repo.Commit("other");

        Assert.True(ReviewTarget.TryParse(null, null, null, false, true, "text", out var target, out var error), error);
        Assert.False(DefaultBranchResolver.TryPrepare(target, repo.Path, out _, out _, out error));
        Assert.Equal("Error: No merge base between 'main' and HEAD.", error);
    }

    private static ReviewTarget Prepare(TempRepo repo, bool branch, bool first, out string? note)
    {
        Assert.True(
            ReviewTarget.TryParse(null, null, null, branch, first, "text", out var target, out var error),
            error);
        Assert.True(
            DefaultBranchResolver.TryPrepare(target, repo.Path, out var prepared, out note, out error),
            error);
        return prepared;
    }

    private sealed class TempRepo : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "aim-review-" + Guid.NewGuid().ToString("n"));

        public TempRepo(string branch = "main")
        {
            Directory.CreateDirectory(Path);
            Git("init", "-b", branch);
            Git("config", "user.email", "aim-tests@example.com");
            Git("config", "user.name", "Aim Tests");
            Git("config", "commit.gpgsign", "false");
        }

        public void Write(string name, string contents) =>
            File.WriteAllText(System.IO.Path.Combine(Path, name), contents);

        public void Commit(string message)
        {
            Git("add", ".");
            Git("commit", "-m", message);
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
