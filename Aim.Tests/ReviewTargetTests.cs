using Aim;

namespace Aim.Tests;

public class ReviewTargetTests
{
    [Fact]
    public void DefaultsToStagedText()
    {
        Assert.True(ReviewTarget.TryParse(null, null, null, null, out var target, out var error), error);
        Assert.Equal(ReviewInputKind.Staged, target.Kind);
        Assert.Equal("text", target.Format);
    }

    [Fact]
    public void BlankRefsAreIgnored()
    {
        Assert.True(ReviewTarget.TryParse("  ", "\t", " ", "JSON", out var target, out var error), error);
        Assert.Equal(ReviewInputKind.Staged, target.Kind);
        Assert.Equal("json", target.Format);
    }

    [Fact]
    public void FromAndToMustBePaired()
    {
        Assert.False(ReviewTarget.TryParse("main", null, null, null, out _, out var error));
        Assert.Contains("--from and --to", error);

        Assert.False(ReviewTarget.TryParse(null, "HEAD", null, null, out _, out error));
        Assert.Contains("--from and --to", error);
    }

    [Fact]
    public void CommitIsMutuallyExclusiveWithRange()
    {
        Assert.False(ReviewTarget.TryParse("main", "HEAD", "HEAD", null, out _, out var error));
        Assert.Contains("--commit", error);
    }

    [Fact]
    public void ParsesRangeAndCommit()
    {
        Assert.True(ReviewTarget.TryParse("main", "feature", null, "text", out var range, out var error), error);
        Assert.Equal(ReviewInputKind.Range, range.Kind);
        Assert.Equal("main", range.From);
        Assert.Equal("feature", range.To);

        Assert.True(ReviewTarget.TryParse(null, null, "abc123", "json", out var commit, out error), error);
        Assert.Equal(ReviewInputKind.Commit, commit.Kind);
        Assert.Equal("abc123", commit.Commit);
    }

    [Fact]
    public void RejectsUnknownFormatAndFlagLikeRefs()
    {
        Assert.False(ReviewTarget.TryParse(null, null, null, "sarif", out _, out var error));
        Assert.Contains("--format", error);

        Assert.False(ReviewTarget.TryParse("--cached", "HEAD", null, null, out _, out error));
        Assert.Contains("--from", error);
    }
}
