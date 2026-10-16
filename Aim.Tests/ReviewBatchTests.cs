using Aim;

namespace Aim.Tests;

public class ReviewBatchTests
{
    [Fact]
    public async Task ReviewsGroupsConcurrentlyAndMergesComments()
    {
        var groups = Enumerable.Range(1, 4).Select(number => Group(number)).ToArray();
        var active = 0;
        var max = 0;
        var sync = new object();

        var result = await ReviewBatch.RunAsync(
            groups,
            new ReviewBatchRequest { Concurrency = 2, GroupTimeout = TimeSpan.FromSeconds(5), MaxComments = 20 },
            async (group, ct) =>
            {
                var now = Interlocked.Increment(ref active);
                lock (sync)
                {
                    if (now > max)
                        max = now;
                }

                await Task.Delay(200, ct);
                Interlocked.Decrement(ref active);
                return CommentJson(group.Label, "high");
            },
            CancellationToken.None);

        Assert.Equal(2, max);
        Assert.True(result.AnySuccess);
        Assert.Equal(["g1", "g2", "g3", "g4"], result.Comments.Select(comment => comment.Path).ToArray());
        Assert.All(result.Outcomes, outcome => Assert.Equal(ReviewGroupStatus.Succeeded, outcome.Status));
    }

    [Fact]
    public async Task TimeoutKeepsCommentsFromTheGroupsThatFinished()
    {
        var groups = new[] { Group(1), Group(2) };
        var result = await ReviewBatch.RunAsync(
            groups,
            new ReviewBatchRequest { Concurrency = 2, GroupTimeout = TimeSpan.FromMilliseconds(100), MaxComments = 20 },
            async (group, ct) =>
            {
                if (group.Number == 2)
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);

                return CommentJson(group.Label, "medium");
            },
            CancellationToken.None);

        Assert.True(result.AnySuccess);
        Assert.Equal("g1", Assert.Single(result.Comments).Path);
        Assert.Equal(ReviewGroupStatus.Succeeded, result.Outcomes[0].Status);
        Assert.Equal(ReviewGroupStatus.TimedOut, result.Outcomes[1].Status);
    }

    [Fact]
    public async Task DoesNotCallGroupsPastTheTokenBudget()
    {
        var calls = 0;
        var groups = new[]
        {
            Group(1, new string('a', 9)),
            Group(2, new string('b', 9)),
            Group(3, "c")
        };

        var result = await ReviewBatch.RunAsync(
            groups,
            new ReviewBatchRequest { Concurrency = 2, GroupTimeout = TimeSpan.FromSeconds(2), MaxTokensBudget = 4, MaxComments = 20 },
            (group, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(CommentJson(group.Label, "low"));
            },
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("g1", Assert.Single(result.Comments).Path);
        Assert.Equal(ReviewGroupStatus.OverBudget, result.Outcomes[1].Status);
        Assert.Equal(ReviewGroupStatus.OverBudget, result.Outcomes[2].Status);
    }

    [Fact]
    public async Task OneBadGroupDoesNotDropTheOthers()
    {
        var groups = new[] { Group(1), Group(2), Group(3) };
        var result = await ReviewBatch.RunAsync(
            groups,
            new ReviewBatchRequest { Concurrency = 3, GroupTimeout = TimeSpan.FromSeconds(2), MaxComments = 20 },
            (group, _) => Task.FromResult(group.Number switch
            {
                1 => CommentJson("a.cs", "low"),
                2 => "not json",
                _ => throw new InvalidOperationException("endpoint down")
            }),
            CancellationToken.None);

        Assert.True(result.AnySuccess);
        Assert.Equal("a.cs", Assert.Single(result.Comments).Path);
        Assert.Equal(ReviewGroupStatus.ParseError, result.Outcomes[1].Status);
        Assert.Equal(ReviewGroupStatus.ApiError, result.Outcomes[2].Status);
        Assert.Contains("endpoint down", result.Outcomes[2].Error);
    }

    [Fact]
    public async Task DedupesCommentsFromSeparateGroups()
    {
        var groups = new[] { Group(1), Group(2) };
        var result = await ReviewBatch.RunAsync(
            groups,
            new ReviewBatchRequest { Concurrency = 2, GroupTimeout = TimeSpan.FromSeconds(2), MaxComments = 1 },
            (group, _) => Task.FromResult(group.Number == 1
                ? CommentJson("a.cs", "low")
                : "[" + CommentObject("a.cs", "low") + "," + CommentObject("b.cs", "critical") + "]"),
            CancellationToken.None);

        Assert.Equal("b.cs", Assert.Single(result.Comments).Path);
        Assert.Equal(1, result.OmittedByCap);
    }

    private static ReviewGroup Group(int number, string? diff = null) => new()
    {
        Number = number,
        Directory = "src",
        Label = "g" + number,
        Files = [],
        Diff = diff ?? "diff --git a/g" + number + " b/g" + number
    };

    private static string CommentJson(string path, string severity) => "[" + CommentObject(path, severity) + "]";

    private static string CommentObject(string path, string severity) =>
        "{\"path\":\"" + path + "\",\"start_line\":1,\"end_line\":1,\"severity\":\"" + severity + "\",\"category\":\"bug\",\"content\":\"problem in " + path + "\"}";
}
