using Aim;

namespace Aim.Tests;

public class ReviewCommentMergerTests
{
    [Fact]
    public void DropsCrossGroupDuplicatesAndCapsBySeverity()
    {
        var low = Comment("a.cs", "low", "nit");
        var critical = Comment("b.cs", "critical", "secret");
        var duplicate = Comment("a.cs", "low", "nit");
        var high = Comment("c.cs", "high", "bug");

        var merged = ReviewCommentMerger.Merge([low, critical, duplicate, high], skippedSchema: 2, maxComments: 2);

        Assert.Equal(["b.cs", "c.cs"], merged.Comments.Select(comment => comment.Path).ToArray());
        Assert.Equal(2, merged.Skipped);
        Assert.Equal(1, merged.OmittedByCap);
    }

    [Fact]
    public void KeepsModelOrderWhenNothingIsCapped()
    {
        var merged = ReviewCommentMerger.Merge(
            [Comment("a.cs", "low", "one"), Comment("b.cs", "critical", "two")],
            skippedSchema: 0,
            maxComments: 20);

        Assert.Equal(["a.cs", "b.cs"], merged.Comments.Select(comment => comment.Path).ToArray());
        Assert.Equal(0, merged.OmittedByCap);
    }

    private static ReviewComment Comment(string path, string severity, string content) => new()
    {
        Path = path,
        StartLine = 1,
        EndLine = 1,
        Severity = severity,
        Category = "bug",
        Content = content
    };
}
