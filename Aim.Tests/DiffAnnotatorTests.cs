using Aim;

namespace Aim.Tests;

public class DiffAnnotatorTests
{
    [Fact]
    public void NumbersTheNewFileSideAndSkipsDeletions()
    {
        var diff = """
            diff --git a/src/App.cs b/src/App.cs
            index 1111111..2222222 100644
            --- a/src/App.cs
            +++ b/src/App.cs
            @@ -1,4 +1,5 @@
             line1
            -line2
            +line2b
             line3
            +line4
             line5
            @@ -20,2 +21,2 @@ context
             old
            -gone
            +fresh
            """;

        var annotated = DiffAnnotator.Annotate(diff);

        Assert.Contains("     1 |  line1", annotated);
        Assert.Contains("       | -line2", annotated);
        Assert.Contains("     2 | +line2b", annotated);
        Assert.Contains("     3 |  line3", annotated);
        Assert.Contains("     4 | +line4", annotated);
        Assert.Contains("     5 |  line5", annotated);
        Assert.Contains("    21 |  old", annotated);
        Assert.Contains("       | -gone", annotated);
        Assert.Contains("    22 | +fresh", annotated);
        Assert.Contains("+++ b/src/App.cs", annotated);
        Assert.DoesNotContain("| +++ b/src/App.cs", annotated);
    }

    [Fact]
    public void AddedLineThatLooksLikeAHeaderStaysInsideTheHunk()
    {
        var diff = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1,2 @@
             kept
            +++ not a header
            """;

        var annotated = DiffAnnotator.Annotate(diff);

        Assert.Contains("     1 |  kept", annotated);
        Assert.Contains("     2 | +++ not a header", annotated);
        Assert.Contains("\n+++ b/a.txt\n", annotated);
    }

    [Fact]
    public void NoNewlineMarkerDoesNotConsumeALineNumber()
    {
        var diff = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -a
            +b
            \ No newline at end of file
            """;

        var annotated = DiffAnnotator.Annotate(diff);

        Assert.Contains("     1 | +b", annotated);
        Assert.Contains("\\ No newline at end of file", annotated);
        Assert.DoesNotContain("| \\ No newline", annotated);
    }
}
