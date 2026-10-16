using Aim;

namespace Aim.Tests;

public class ReviewAgentLimitsTests
{
    [Fact]
    public void CliRoundsOverrideConfigWhenAgentIsOn()
    {
        var ok = ReviewAgentLimits.TryResolveRounds(4, 10, agentEnabled: true, out var rounds, out var note, out var error);

        Assert.True(ok, error);
        Assert.Equal(4, rounds);
        Assert.Null(note);
    }

    [Fact]
    public void CliRoundsWithoutAgentAreNotedAndStillValidated()
    {
        var ok = ReviewAgentLimits.TryResolveRounds(3, 10, agentEnabled: false, out var rounds, out var note, out var error);

        Assert.True(ok, error);
        Assert.Equal(3, rounds);
        Assert.Contains("--agent", note);
    }

    [Fact]
    public void OutOfRangeCliRoundsFailEvenWhenAgentIsOff()
    {
        Assert.False(ReviewAgentLimits.TryResolveRounds(0, 10, false, out _, out _, out var error));
        Assert.Contains("1 to 40", error);

        Assert.False(ReviewAgentLimits.TryResolveRounds(41, 10, true, out _, out _, out error));
        Assert.Contains("1 to 40", error);
    }

    [Fact]
    public void ConfiguredRoundsAreUsedUntilTheyAreOutOfRange()
    {
        Assert.True(ReviewAgentLimits.TryResolveRounds(null, 12, true, out var rounds, out var note, out var error));
        Assert.Equal(12, rounds);
        Assert.Null(note);
        Assert.Equal("", error);

        Assert.True(ReviewAgentLimits.TryResolveRounds(null, 0, true, out rounds, out note, out error));
        Assert.Equal(ReviewAgentLimits.DefaultRounds, rounds);
        Assert.Contains("Using 10", note);
    }

    [Fact]
    public void ToolCharsFallBackToTheDefaultOutsideTheAllowedRange()
    {
        Assert.Equal(4000, ReviewAgentLimits.ResolveToolChars(4000));
        Assert.Equal(ReviewAgentLimits.DefaultToolChars, ReviewAgentLimits.ResolveToolChars(0));
        Assert.Equal(ReviewAgentLimits.DefaultToolChars, ReviewAgentLimits.ResolveToolChars(ReviewAgentLimits.MaxToolChars + 1));
    }

    [Fact]
    public void ToolRevisionFollowsCommitOrRangeHeadAndIsAbsentForStaged()
    {
        Assert.Null(ReviewAgentLimits.ToolRevision(Target(null, null, null)));
        Assert.Equal("feature", ReviewAgentLimits.ToolRevision(Target("main", "feature", null)));
        Assert.Equal("abc123", ReviewAgentLimits.ToolRevision(Target(null, null, "abc123")));
    }

    private static ReviewTarget Target(string? from, string? to, string? commit)
    {
        Assert.True(ReviewTarget.TryParse(from, to, commit, false, false, "text", out var target, out var error), error);
        return target;
    }
}
