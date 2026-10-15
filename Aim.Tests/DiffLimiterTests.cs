using Aim;

namespace Aim.Tests;

public class DiffLimiterTests
{
    [Fact]
    public void UnderTheLimitIsUnchanged()
    {
        var diff = "diff --git a/a.txt b/a.txt\n+b\n";
        var limited = DiffLimiter.Limit(diff, 80000);
        Assert.False(limited.Truncated);
        Assert.Equal(diff, limited.Text);
        Assert.Equal(0, limited.OmittedFiles);
    }

    [Fact]
    public void DropsLaterFilesWhenTheBudgetFitsTheFirstPatch()
    {
        var first = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -a
            +b
            """.Replace("\r\n", "\n");
        var second = """
            diff --git a/b.txt b/b.txt
            --- a/b.txt
            +++ b/b.txt
            @@ -1 +1 @@
            -c
            +d
            """.Replace("\r\n", "\n");
        var diff = first + "\n" + second;

        var limited = DiffLimiter.Limit(diff, first.Length);

        Assert.True(limited.Truncated);
        Assert.Equal(1, limited.OmittedFiles);
        Assert.Contains("a.txt", limited.Text);
        Assert.DoesNotContain("b.txt", limited.Text);
    }

    [Fact]
    public void TinyBudgetAsksForANameOnlyFallback()
    {
        var diff = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -a
            +b
            """;

        var fit = DiffLimiter.Fit(diff, 20);

        Assert.True(fit.Truncated);
        Assert.True(fit.NeedsNameOnlyFallback);
    }

    [Fact]
    public void BudgetThatKeepsAHunkDoesNotAskForNames()
    {
        var diff = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -a
            +b
            """;

        var fit = DiffLimiter.Fit(diff, 10_000);

        Assert.False(fit.Truncated);
        Assert.False(fit.NeedsNameOnlyFallback);
        Assert.Contains("@@", fit.Text);
    }
}
