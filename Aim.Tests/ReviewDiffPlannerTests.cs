using Aim;

namespace Aim.Tests;

public class ReviewDiffPlannerTests
{
    [Fact]
    public void ReadsAddedRenamedDeletedAndQuotedPaths()
    {
        var diff = """
            diff --git a/dev/null b/src/New.cs
            new file mode 100644
            diff --git a/src/Old.cs b/src/Renamed.cs
            similarity index 90%
            rename from src/Old.cs
            rename to src/Renamed.cs
            diff --git a/src/Gone.cs b/dev/null
            deleted file mode 100644
            diff --git "a/my file.cs" "b/my file.cs"
            diff --git "a/caf\303\251.txt" "b/caf\303\251.txt"
            """;

        var plan = ReviewDiffPlanner.Plan(diff, new ReviewPlanOptions { MaxDiffChars = 80_000 });

        Assert.True(plan.SingleShot);
        Assert.Equal(
            ["src/New.cs", "src/Renamed.cs", "src/Gone.cs", "my file.cs", "café.txt"],
            plan.Groups[0].Files.Select(file => file.Path).ToArray());
    }

    [Fact]
    public void KeepsOneCallWhenTheDiffFits()
    {
        var diff = Patch("src/a/A.cs") + "\n" + Patch("src/b/B.cs");
        var plan = ReviewDiffPlanner.Plan(diff, new ReviewPlanOptions { MaxDiffChars = diff.Length });

        Assert.True(plan.SingleShot);
        Assert.Single(plan.Groups);
        Assert.Equal(["src/a/A.cs", "src/b/B.cs"], plan.Groups[0].Files.Select(file => file.Path).ToArray());
        Assert.Empty(plan.Skipped);

        var preview = ReviewDiffPlanner.FormatPreview(plan, "main...HEAD", agent: false, concurrency: 8, timeoutMinutes: 5, maxTokensBudget: 0);
        Assert.Contains("Mode: single call", preview);
        Assert.Contains("A diff that fits in one call is not split.", preview);
        Assert.Contains("src/a/A.cs", preview);
        Assert.DoesNotContain("Group 1:", preview);
    }

    [Fact]
    public void GroupsByDirectoryWhenTheDiffExceedsTheBudget()
    {
        var diff = Patch("src/a/A.cs") + "\n" + Patch("src/a/B.cs") + "\n" + Patch("src/b/C.cs");
        var plan = ReviewDiffPlanner.Plan(diff, new ReviewPlanOptions { MaxDiffChars = diff.Length - 1 });

        Assert.False(plan.SingleShot);
        Assert.Equal(2, plan.Groups.Count);
        Assert.Equal("src/a", plan.Groups[0].Label);
        Assert.Equal(["src/a/A.cs", "src/a/B.cs"], plan.Groups[0].Files.Select(file => file.Path).ToArray());
        Assert.Equal("src/b", plan.Groups[1].Label);
        Assert.Equal("src/b/C.cs", Assert.Single(plan.Groups[1].Files).Path);

        var preview = ReviewDiffPlanner.FormatPreview(plan, "main...HEAD", agent: true, concurrency: 4, timeoutMinutes: 5, maxTokensBudget: 0);
        Assert.Contains("Mode: parallel groups, with the review agent", preview);
        Assert.Contains("Concurrency: 4", preview);
        Assert.Contains("grouped by directory", preview);
        Assert.True(preview.IndexOf("src/a/A.cs", StringComparison.Ordinal) < preview.IndexOf("src/b/C.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void SkipsAFileOverEightyPercentAndReviewsTheRest()
    {
        var small = PatchOfLength("src/a/A.cs", 50);
        var exact = PatchOfLength("src/a/Exact.cs", 80);
        var over = PatchOfLength("src/b/Huge.cs", 81);
        var diff = string.Join('\n', small, exact, over);
        var plan = ReviewDiffPlanner.Plan(diff, new ReviewPlanOptions { MaxDiffChars = 100 });

        var skipped = Assert.Single(plan.Skipped);
        Assert.Equal("src/b/Huge.cs", skipped.Path);
        Assert.Equal(81, skipped.Characters);
        Assert.Equal(80, skipped.Limit);
        Assert.Equal(["src/a/A.cs", "src/a/Exact.cs"], plan.Groups.SelectMany(group => group.Files).Select(file => file.Path).ToArray());
        Assert.False(plan.SingleShot);
        Assert.Contains("80% of maxdiffchars", ReviewDiffPlanner.SkipWarning(skipped));
    }

    [Fact]
    public void CapsFilesPerGroupInsideADirectory()
    {
        var files = Enumerable.Range(0, 3)
            .Select(index => PatchOfLength($"src/F{index}.cs", 80))
            .ToArray();
        var other = PatchOfLength("lib/Big.cs", 200);
        var diff = string.Join('\n', files.Append(other));
        var plan = ReviewDiffPlanner.Plan(diff, new ReviewPlanOptions
        {
            MaxDiffChars = diff.Length - 1,
            MaxFilesPerGroup = 2
        });

        Assert.Equal(3, plan.Groups.Count);
        Assert.Equal("src (part 1 of 2)", plan.Groups[0].Label);
        Assert.Equal(2, plan.Groups[0].Files.Count);
        Assert.Equal("src (part 2 of 2)", plan.Groups[1].Label);
        Assert.Equal("src/F2.cs", Assert.Single(plan.Groups[1].Files).Path);
        Assert.Equal("lib", plan.Groups[2].Label);
    }

    [Fact]
    public void UnlimitedCharacterBudgetStillSplitsAfterTenFiles()
    {
        var ten = string.Join('\n', Enumerable.Range(0, 10).Select(index => Patch($"src/F{index}.cs")));
        var tenPlan = ReviewDiffPlanner.Plan(ten, new ReviewPlanOptions { MaxDiffChars = 0 });
        Assert.True(tenPlan.SingleShot);
        Assert.Empty(tenPlan.Skipped);

        var eleven = ten + "\n" + Patch("src/F10.cs");
        var elevenPlan = ReviewDiffPlanner.Plan(eleven, new ReviewPlanOptions { MaxDiffChars = 0, MaxFilesPerGroup = 10 });
        Assert.False(elevenPlan.SingleShot);
        Assert.Equal(2, elevenPlan.Groups.Count);
        Assert.Equal(10, elevenPlan.Groups[0].Files.Count);
        Assert.Single(elevenPlan.Groups[1].Files);
        Assert.Empty(elevenPlan.Skipped);
    }

    [Fact]
    public void StopsTheTokenBudgetInsteadOfSkippingAhead()
    {
        var groups = new[]
        {
            Group(1, new string('a', 9)),
            Group(2, new string('b', 9)),
            Group(3, "c")
        };

        var selection = ReviewDiffPlanner.SelectForBudget(groups, maxTokensBudget: 4);

        Assert.Equal([1], selection.Selected.Select(group => group.Number).ToArray());
        Assert.Equal([2, 3], selection.Deferred.Select(group => group.Number).ToArray());
        Assert.Equal(3, ReviewDiffPlanner.EstimateTokens(groups[0].Diff));
        Assert.Equal(0, ReviewDiffPlanner.EstimateTokens(""));
    }

    [Fact]
    public void PreviewMarksGroupsTheBudgetWouldNotSend()
    {
        var diff = Patch("src/a/A.cs") + "\n" + Patch("src/b/B.cs");
        var plan = ReviewDiffPlanner.Plan(diff, new ReviewPlanOptions { MaxDiffChars = diff.Length - 1 });
        var budget = ReviewDiffPlanner.EstimateTokens(plan.Groups[0].Diff);
        var preview = ReviewDiffPlanner.FormatPreview(plan, "staged changes", false, 8, 5, budget);

        Assert.DoesNotContain("[not sent: token budget]", preview.Split("Group 2:")[0]);
        Assert.Contains("Group 2:", preview);
        Assert.Contains("[not sent: token budget]", preview[preview.IndexOf("Group 2:", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void DiffWithoutFileHeadersStaysOneCall()
    {
        var plan = ReviewDiffPlanner.Plan("not a unified diff\n", new ReviewPlanOptions());
        Assert.True(plan.Unsplit);
        Assert.Empty(plan.Groups);

        var preview = ReviewDiffPlanner.FormatPreview(plan, "staged changes", false, 8, 1, 0);
        Assert.Contains("no per-file headers", preview);
        Assert.Contains("Per-group timeout: 1 minute", preview);
    }

    [Fact]
    public void NameOnlyPreviewListsPaths()
    {
        var preview = ReviewDiffPlanner.FormatNameOnlyPreview("staged changes", "src/A.cs\nsrc/B.cs\n", truncated: true);
        Assert.Contains("file names only", preview);
        Assert.Contains("truncated", preview);
        Assert.Contains("src/A.cs", preview);
        Assert.Contains("src/B.cs", preview);
    }

    private static ReviewGroup Group(int number, string diff) => new()
    {
        Number = number,
        Directory = "",
        Label = "g" + number,
        Files = [],
        Diff = diff
    };

    private static string Patch(string path) =>
        $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -0,0 +1 @@\n+x";

    private static string PatchOfLength(string path, int length)
    {
        var head = $"diff --git a/{path} b/{path}\n";
        if (head.Length >= length)
            throw new InvalidOperationException($"Header is already {head.Length} characters.");

        return head + new string('x', length - head.Length);
    }
}
