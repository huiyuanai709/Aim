using Aim;

namespace Aim.Tests;

public class ReviewParallelLimitsTests
{
    [Fact]
    public void DefaultTimeoutIsUnlimited()
    {
        Assert.Equal(0, ReviewParallelLimits.DefaultTimeoutMinutes);
        Assert.Equal(0, new ConfigManager.Config().ReviewTimeoutMinutes);

        Assert.True(ReviewParallelLimits.TryResolveTimeout(
            fromCli: null,
            configured: new ConfigManager.Config().ReviewTimeoutMinutes,
            out var minutes,
            out var note,
            out var error));

        Assert.Equal(0, minutes);
        Assert.Null(note);
        Assert.Equal("", error);
        Assert.Equal("no limit", ReviewParallelLimits.FormatTimeout(minutes));
        Assert.Equal(Timeout.InfiniteTimeSpan, ReviewParallelLimits.CancellationTimeout(minutes));
        Assert.Equal(
            Timeout.InfiniteTimeSpan,
            ReviewChat.NetworkTimeoutFor(ReviewParallelLimits.CancellationTimeout(minutes)));
        Assert.Equal(Timeout.InfiniteTimeSpan, new ReviewAgentRequest
        {
            ApiKey = "sk-test",
            Endpoint = "https://example.invalid/v1",
            Model = "gpt-4o",
            WorkingDirectory = Environment.CurrentDirectory,
            MaxRounds = 1,
            MaxToolChars = 100,
            NameOnly = false
        }.NetworkTimeout);
    }

    [Fact]
    public void ExplicitTimeoutStillApplies()
    {
        Assert.True(ReviewParallelLimits.TryResolveTimeout(2, configured: 0, out var fromCli, out var cliNote, out var cliError));
        Assert.Equal(2, fromCli);
        Assert.Null(cliNote);
        Assert.Equal("", cliError);

        Assert.True(ReviewParallelLimits.TryResolveTimeout(null, configured: 9, out var fromConfig, out var configNote, out var configError));
        Assert.Equal(9, fromConfig);
        Assert.Null(configNote);
        Assert.Equal("", configError);

        Assert.True(ReviewParallelLimits.TryResolveTimeout(4, configured: 9, out var flagWins, out _, out _));
        Assert.Equal(4, flagWins);

        Assert.True(ReviewParallelLimits.TryResolveTimeout(0, configured: 9, out var cleared, out _, out _));
        Assert.Equal(0, cleared);

        var limit = ReviewParallelLimits.CancellationTimeout(2);
        Assert.Equal(TimeSpan.FromMinutes(2), limit);
        Assert.Equal(
            TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(ReviewParallelLimits.NetworkTimeoutBufferSeconds),
            ReviewChat.NetworkTimeoutFor(limit));
        Assert.Equal("2 minutes", ReviewParallelLimits.FormatTimeout(2));
        Assert.Equal("1 minute", ReviewParallelLimits.FormatTimeout(1));
    }

    [Fact]
    public void RejectsATimeoutOutsideTheAllowedRange()
    {
        Assert.False(ReviewParallelLimits.TryResolveTimeout(61, configured: 0, out _, out _, out var high));
        Assert.Contains("1 to 60", high);
        Assert.Contains("no limit", high);

        Assert.False(ReviewParallelLimits.TryResolveTimeout(-1, configured: 0, out _, out _, out var low));
        Assert.Contains("--timeout", low);

        Assert.True(ReviewParallelLimits.TryResolveTimeout(null, configured: 61, out var minutes, out var note, out var error));
        Assert.Equal(0, minutes);
        Assert.Equal("", error);
        Assert.NotNull(note);
        Assert.Contains("no limit", note);
    }
}
