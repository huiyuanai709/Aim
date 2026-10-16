namespace Aim;

internal enum ReviewGroupStatus
{
    Succeeded,
    ApiError,
    ParseError,
    TimedOut,
    OverBudget
}

internal sealed class ReviewGroupOutcome
{
    public required ReviewGroup Group { get; init; }
    public required ReviewGroupStatus Status { get; init; }
    public string Error { get; init; } = "";
    public string Raw { get; init; } = "";
    public ReviewParseResult? Parsed { get; init; }
}

internal sealed class ReviewBatchRequest
{
    public int Concurrency { get; init; } = ReviewParallelLimits.DefaultConcurrency;
    public TimeSpan GroupTimeout { get; init; } = TimeSpan.FromMinutes(ReviewParallelLimits.DefaultTimeoutMinutes);
    public int MaxTokensBudget { get; init; }
    public int MaxComments { get; init; } = 20;
}

internal sealed class ReviewBatchResult
{
    public bool AnySuccess { get; init; }
    public List<ReviewComment> Comments { get; init; } = [];
    public int SkippedSchema { get; init; }
    public int OmittedByCap { get; init; }
    public IReadOnlyList<ReviewGroupOutcome> Outcomes { get; init; } = [];
}

internal static class ReviewBatch
{
    public static async Task<ReviewBatchResult> RunAsync(
        IReadOnlyList<ReviewGroup> groups,
        ReviewBatchRequest request,
        Func<ReviewGroup, CancellationToken, Task<string>> reviewGroup,
        CancellationToken cancellationToken)
    {
        var selection = ReviewDiffPlanner.SelectForBudget(groups, request.MaxTokensBudget);
        var outcomes = new ReviewGroupOutcome[groups.Count];
        var position = new Dictionary<ReviewGroup, int>();
        for (var i = 0; i < groups.Count; i++)
            position[groups[i]] = i;

        foreach (var group in selection.Deferred)
        {
            outcomes[position[group]] = new ReviewGroupOutcome
            {
                Group = group,
                Status = ReviewGroupStatus.OverBudget
            };
        }

        var concurrency = request.Concurrency < 1
            ? 1
            : Math.Min(request.Concurrency, ReviewParallelLimits.MaxConcurrency);
        using var gate = new SemaphoreSlim(concurrency);
        var running = new List<Task>(selection.Selected.Count);
        foreach (var group in selection.Selected)
        {
            await gate.WaitAsync(cancellationToken);
            var index = position[group];
            running.Add(ReviewOneAsync(index, group, request, reviewGroup, outcomes, gate, cancellationToken));
        }

        await Task.WhenAll(running);

        var comments = new List<ReviewComment>();
        var skipped = 0;
        var omittedByParser = 0;
        var anySuccess = false;
        foreach (var outcome in outcomes)
        {
            if (outcome is not { Status: ReviewGroupStatus.Succeeded, Parsed: { } parsed })
                continue;

            anySuccess = true;
            comments.AddRange(parsed.Comments);
            skipped += parsed.Skipped;
            omittedByParser += parsed.OmittedByCap;
        }

        var merged = ReviewCommentMerger.Merge(comments, skipped, request.MaxComments);
        return new ReviewBatchResult
        {
            AnySuccess = anySuccess,
            Comments = merged.Comments,
            SkippedSchema = merged.Skipped,
            OmittedByCap = omittedByParser + merged.OmittedByCap,
            Outcomes = outcomes
        };
    }

    private static async Task ReviewOneAsync(
        int index,
        ReviewGroup group,
        ReviewBatchRequest request,
        Func<ReviewGroup, CancellationToken, Task<string>> reviewGroup,
        ReviewGroupOutcome[] outcomes,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        try
        {
            using var groupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            groupCts.CancelAfter(request.GroupTimeout);
            var raw = await reviewGroup(group, groupCts.Token);
            outcomes[index] = Interpret(group, raw);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            outcomes[index] = new ReviewGroupOutcome
            {
                Group = group,
                Status = ReviewGroupStatus.TimedOut
            };
        }
        catch (Exception ex)
        {
            outcomes[index] = new ReviewGroupOutcome
            {
                Group = group,
                Status = ReviewGroupStatus.ApiError,
                Error = ex.Message
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private static ReviewGroupOutcome Interpret(ReviewGroup group, string raw)
    {
        // Cap once, after groups are combined, so a duplicate dropped in one group
        // is not also counted against the global maxreviewcomments budget.
        var parsed = ReviewCommentParser.Parse(raw, int.MaxValue);
        if (!parsed.Success)
        {
            return new ReviewGroupOutcome
            {
                Group = group,
                Status = ReviewGroupStatus.ParseError,
                Error = parsed.Error,
                Raw = raw
            };
        }

        if (parsed.Comments.Count == 0 && parsed.Skipped > 0)
        {
            return new ReviewGroupOutcome
            {
                Group = group,
                Status = ReviewGroupStatus.ParseError,
                Error = $"The model returned {parsed.Skipped} comment(s) but none matched the review schema.",
                Raw = raw,
                Parsed = parsed
            };
        }

        return new ReviewGroupOutcome
        {
            Group = group,
            Status = ReviewGroupStatus.Succeeded,
            Raw = raw,
            Parsed = parsed
        };
    }
}
