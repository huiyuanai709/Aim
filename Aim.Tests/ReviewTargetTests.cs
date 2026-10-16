using Aim;

namespace Aim.Tests;

public class ReviewTargetTests
{
    [Fact]
    public void DefaultsToStagedText()
    {
        Assert.True(ReviewTarget.TryParse(null, null, null, false, false, null, out var target, out var error), error);
        Assert.Equal(ReviewInputKind.Staged, target.Kind);
        Assert.Equal("text", target.Format);
    }

    [Fact]
    public void BlankRefsAreIgnored()
    {
        Assert.True(ReviewTarget.TryParse("  ", "\t", " ", false, false, "JSON", out var target, out var error), error);
        Assert.Equal(ReviewInputKind.Staged, target.Kind);
        Assert.Equal("json", target.Format);
    }

    [Fact]
    public void FromAndToMustBePaired()
    {
        Assert.False(ReviewTarget.TryParse("main", null, null, false, false, null, out _, out var error));
        Assert.Contains("--from and --to", error);

        Assert.False(ReviewTarget.TryParse(null, "HEAD", null, false, false, null, out _, out error));
        Assert.Contains("--from and --to", error);
    }

    [Fact]
    public void CommitIsMutuallyExclusiveWithRange()
    {
        Assert.False(ReviewTarget.TryParse("main", "HEAD", "HEAD", false, false, null, out _, out var error));
        Assert.Contains("--commit", error);
    }

    [Fact]
    public void BareCommitReviewsLatestHeadCommit()
    {
        Assert.True(ReviewTarget.TryParse(null, null, null, false, false, "text", out var target, out var error, commitSpecified: true), error);
        Assert.Equal(ReviewInputKind.Commit, target.Kind);
        Assert.Equal(ReviewTarget.LatestCommit, target.Commit);
        Assert.Null(target.From);
        Assert.Null(target.To);

        Assert.True(ReviewTarget.TryParse(null, null, "  ", false, false, null, out target, out error, true), error);
        Assert.Equal(ReviewInputKind.Commit, target.Kind);
        Assert.Equal("HEAD", target.Commit);
    }

    [Fact]
    public void ExplicitCommitRevisionIsUnchanged()
    {
        Assert.True(ReviewTarget.TryParse(null, null, "HEAD", false, false, null, out var head, out var error, true), error);
        Assert.Equal(ReviewInputKind.Commit, head.Kind);
        Assert.Equal("HEAD", head.Commit);

        Assert.True(ReviewTarget.TryParse(null, null, "HEAD~0", false, false, "json", out var parent, out error, true), error);
        Assert.Equal("HEAD~0", parent.Commit);
        Assert.Equal("json", parent.Format);

        Assert.True(ReviewTarget.TryParse(null, null, "abc123", false, false, null, out var sha, out error, true), error);
        Assert.Equal("abc123", sha.Commit);
    }

    [Fact]
    public void BareCommitStaysExclusiveWithOtherTargets()
    {
        Assert.False(ReviewTarget.TryParse(null, null, null, true, false, null, out _, out var error, true));
        Assert.Contains("--branch", error);
        Assert.Contains("--commit", error);

        Assert.False(ReviewTarget.TryParse(null, null, " ", false, true, null, out _, out error, true));
        Assert.Contains("--first-commit", error);
        Assert.Contains("--commit", error);

        Assert.False(ReviewTarget.TryParse("main", "HEAD", null, false, false, null, out _, out error, true));
        Assert.Contains("--from/--to", error);

        Assert.False(ReviewTarget.TryParse("main", null, null, false, false, null, out _, out error, true));
        Assert.Contains("--from and --to", error);
    }

    [Fact]
    public void ParsesRangeAndCommit()
    {
        Assert.True(ReviewTarget.TryParse("main", "feature", null, false, false, "text", out var range, out var error), error);
        Assert.Equal(ReviewInputKind.Range, range.Kind);
        Assert.Equal("main", range.From);
        Assert.Equal("feature", range.To);

        Assert.True(ReviewTarget.TryParse(null, null, "abc123", false, false, "json", out var commit, out error), error);
        Assert.Equal(ReviewInputKind.Commit, commit.Kind);
        Assert.Equal("abc123", commit.Commit);
    }

    [Fact]
    public void RejectsUnknownFormatAndFlagLikeRefs()
    {
        Assert.False(ReviewTarget.TryParse(null, null, null, false, false, "sarif", out _, out var error));
        Assert.Contains("--format", error);

        Assert.False(ReviewTarget.TryParse("--cached", "HEAD", null, false, false, null, out _, out error));
        Assert.Contains("--from", error);
    }

    [Fact]
    public void BranchModeReviewsTheDefaultBranchAgainstHead()
    {
        Assert.True(ReviewTarget.TryParse("  ", null, " ", true, false, "json", out var target, out var error), error);
        Assert.Equal(ReviewInputKind.Branch, target.Kind);
        Assert.Null(target.From);
        Assert.Null(target.To);
        Assert.Null(target.Commit);
        Assert.Equal("json", target.Format);
    }

    [Fact]
    public void FirstCommitModeIsSeparateFromBranch()
    {
        Assert.True(ReviewTarget.TryParse(null, null, null, false, true, "text", out var target, out var error), error);
        Assert.Equal(ReviewInputKind.FirstCommit, target.Kind);
    }

    [Fact]
    public void BranchModesRejectExplicitRefs()
    {
        Assert.False(ReviewTarget.TryParse(null, null, null, true, true, null, out _, out var error));
        Assert.Contains("--branch and --first-commit", error);

        Assert.False(ReviewTarget.TryParse("main", null, null, true, false, null, out _, out error));
        Assert.Contains("--branch", error);
        Assert.Contains("--from", error);

        Assert.False(ReviewTarget.TryParse(null, "HEAD", null, true, false, null, out _, out error));
        Assert.Contains("--branch", error);

        Assert.False(ReviewTarget.TryParse("main", "HEAD", null, true, false, null, out _, out error));
        Assert.Contains("--branch", error);

        Assert.False(ReviewTarget.TryParse(null, null, "HEAD", false, true, null, out _, out error));
        Assert.Contains("--first-commit", error);
        Assert.Contains("--commit", error);
    }
}
