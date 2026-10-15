using Aim;

namespace Aim.Tests;

public class ReviewCommentParserTests
{
    [Fact]
    public void ParsesAFencedArrayAndNormalizesFields()
    {
        var raw = """
            Here is the review:
            ```json
            [
              {
                "path": "src\\App.cs",
                "start_line": "4",
                "end_line": "2",
                "severity": "High",
                "category": "Bug",
                "content": " Bounds check is inverted. ",
                "suggestion_code": "  if (index < items.Count)  "
              }
            ]
            ```
            """;

        var parsed = ReviewCommentParser.Parse(raw, 20);

        Assert.True(parsed.Success, parsed.Error);
        var comment = Assert.Single(parsed.Comments);
        Assert.Equal("src/App.cs", comment.Path);
        Assert.Equal(2, comment.StartLine);
        Assert.Equal(4, comment.EndLine);
        Assert.Equal("high", comment.Severity);
        Assert.Equal("bug", comment.Category);
        Assert.Equal("Bounds check is inverted.", comment.Content);
        Assert.Equal("if (index < items.Count)", comment.SuggestionCode);
    }

    [Fact]
    public void AcceptsACommentsWrapperAndDropsInvalidItems()
    {
        var raw = """
            {"comments":[
              {"path":"a.cs","start_line":1,"end_line":1,"severity":"low","category":"style","content":"fine"},
              {"path":"b.cs","start_line":1,"end_line":1,"severity":"blocker","category":"bug","content":"nope"},
              {"path":"a.cs","start_line":1,"end_line":1,"severity":"low","category":"style","content":"fine"}
            ]}
            """;

        var parsed = ReviewCommentParser.Parse(raw, 20);

        Assert.True(parsed.Success, parsed.Error);
        var comment = Assert.Single(parsed.Comments);
        Assert.Equal("a.cs", comment.Path);
        Assert.Equal(1, parsed.Skipped);
        Assert.Null(comment.SuggestionCode);
    }

    [Fact]
    public void CapsBySeverityWhileKeepingModelOrder()
    {
        var raw = """
            [
              {"path":"a.cs","start_line":1,"end_line":1,"severity":"low","category":"style","content":"nit"},
              {"path":"b.cs","start_line":2,"end_line":2,"severity":"critical","category":"security","content":"secret"},
              {"path":"c.cs","start_line":3,"end_line":3,"severity":"high","category":"bug","content":"bug"}
            ]
            """;

        var parsed = ReviewCommentParser.Parse(raw, 2);

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(2, parsed.Comments.Count);
        Assert.Equal("b.cs", parsed.Comments[0].Path);
        Assert.Equal("c.cs", parsed.Comments[1].Path);
        Assert.Equal(1, parsed.OmittedByCap);
    }

    [Fact]
    public void EmptyArrayIsASuccessfulReview()
    {
        var parsed = ReviewCommentParser.Parse("[]", 20);

        Assert.True(parsed.Success, parsed.Error);
        Assert.Empty(parsed.Comments);
        Assert.Equal(0, parsed.Skipped);
    }

    [Fact]
    public void MissingEndLineUsesTheStartLine()
    {
        var raw = """
            [{"path":"a.cs","start_line":9,"severity":"medium","category":"performance","content":"Loops the file twice."}]
            """;

        var parsed = ReviewCommentParser.Parse(raw, 20);

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(9, Assert.Single(parsed.Comments).EndLine);
    }

    [Fact]
    public void ProseWithoutJsonFails()
    {
        var parsed = ReviewCommentParser.Parse("No issues, looks good.", 20);

        Assert.False(parsed.Success);
        Assert.Contains("JSON", parsed.Error);
    }
}
