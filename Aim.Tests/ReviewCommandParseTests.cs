using System.CommandLine;
using Aim;

namespace Aim.Tests;

public class ReviewCommandParseTests
{
    [Fact]
    public void BareCommitSelectsHead()
    {
        var parsed = Parse("review", "--commit");
        Assert.Empty(parsed.Errors);

        Assert.True(ReviewCommand.TryParseTarget(parsed, out var target, out var error), error);
        Assert.Equal(ReviewInputKind.Commit, target.Kind);
        Assert.Equal("HEAD", target.Commit);
    }

    [Fact]
    public void ExplicitCommitRevisionIsPreserved()
    {
        var parsed = Parse("review", "--commit", "abc123");
        Assert.Empty(parsed.Errors);

        Assert.True(ReviewCommand.TryParseTarget(parsed, out var target, out var error), error);
        Assert.Equal(ReviewInputKind.Commit, target.Kind);
        Assert.Equal("abc123", target.Commit);
    }

    [Fact]
    public void HeadAndHeadTildeZeroStayExplicit()
    {
        var head = Parse("review", "--commit", "HEAD");
        Assert.True(ReviewCommand.TryParseTarget(head, out var headTarget, out var error), error);
        Assert.Equal("HEAD", headTarget.Commit);

        var parent = Parse("review", "--commit", "HEAD~0");
        Assert.True(ReviewCommand.TryParseTarget(parent, out var parentTarget, out error), error);
        Assert.Equal("HEAD~0", parentTarget.Commit);
    }

    [Fact]
    public void OmittingCommitReviewsStagedChanges()
    {
        var parsed = Parse("review");
        Assert.Empty(parsed.Errors);

        Assert.True(ReviewCommand.TryParseTarget(parsed, out var target, out var error), error);
        Assert.Equal(ReviewInputKind.Staged, target.Kind);
        Assert.Null(target.Commit);
    }

    [Fact]
    public void BareCommitDoesNotConsumeTheNextOption()
    {
        var parsed = Parse("review", "--commit", "--format", "json");
        Assert.Empty(parsed.Errors);

        Assert.True(ReviewCommand.TryParseTarget(parsed, out var target, out var error), error);
        Assert.Equal(ReviewInputKind.Commit, target.Kind);
        Assert.Equal("HEAD", target.Commit);
        Assert.Equal("json", target.Format);
    }

    [Fact]
    public void EmptyCommitRevisionReviewsHead()
    {
        var parsed = Parse("review", "--commit", "");
        Assert.Empty(parsed.Errors);

        Assert.True(ReviewCommand.TryParseTarget(parsed, out var target, out var error), error);
        Assert.Equal(ReviewInputKind.Commit, target.Kind);
        Assert.Equal("HEAD", target.Commit);
    }

    [Fact]
    public void BareCommitStaysMutuallyExclusive()
    {
        Assert.False(ReviewCommand.TryParseTarget(Parse("review", "--commit", "--branch"), out _, out var error));
        Assert.Contains("--branch", error);
        Assert.Contains("--commit", error);

        Assert.False(ReviewCommand.TryParseTarget(Parse("review", "--commit", "--first-commit"), out _, out error));
        Assert.Contains("--first-commit", error);
        Assert.Contains("--commit", error);

        Assert.False(ReviewCommand.TryParseTarget(
            Parse("review", "--commit", "--from", "main", "--to", "HEAD"),
            out _,
            out error));
        Assert.Contains("--from/--to", error);
    }

    [Fact]
    public void HelpDescribesBareCommitAsLatestHead()
    {
        var review = new ReviewCommand();
        var commit = Assert.Single(review.Options, option => option.Name == "--commit");
        Assert.Equal(ArgumentArity.ZeroOrOne, commit.Arity);
        Assert.Equal("rev", commit.HelpName);
        Assert.Contains("latest commit", commit.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HEAD", commit.Description, StringComparison.Ordinal);
    }

    private static ParseResult Parse(params string[] args)
    {
        RootCommand root = new()
        {
            new ReviewCommand()
        };
        return root.Parse(args);
    }
}
