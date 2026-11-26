using Microsoft.Extensions.AI;
using Aim;

namespace Aim.Tests;

public class ReviewAgentRecoveryTests
{
    private const string UserPrompt = "Review the diff for ICBC notification handling.";

    private const string PlanningProse =
        "I'll inspect the changed files and the surrounding ICBC notification code before judging.";

    [Fact]
    public async Task PlanningProseThenValidJsonSucceedsOnTheSameThread()
    {
        var json = """
            [{"path":"pay-core/pay-core-service/src/main/java/cn/yzw/sk/pay/core/service/payment/ICBCAggregatePayment.java","start_line":1,"end_line":70,"severity":"medium","category":"bug","content":"The notify handler does not reject a mismatched order id."}]
            """;
        var script = new ScriptedChatClient(
            new ChatMessage(ChatRole.Assistant, PlanningProse),
            new ChatMessage(ChatRole.Assistant, json));
        var progress = new StringWriter();

        var raw = await ReviewAgent.CompleteAsync(Request(progress), UserPrompt, CancellationToken.None, script);
        var parsed = ReviewCommentParser.Parse(raw, 20);

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(json, raw);
        var comment = Assert.Single(parsed.Comments);
        Assert.Equal("pay-core/pay-core-service/src/main/java/cn/yzw/sk/pay/core/service/payment/ICBCAggregatePayment.java", comment.Path);
        Assert.Equal(2, script.Requests.Count);
        Assert.Equal(1, CountText(script.Requests[1], UserPrompt));
        Assert.Equal(1, CountText(script.Requests[1], PlanningProse));
        Assert.Equal(1, CountText(script.Requests[1], ReviewPrompt.JsonContinuation));
        Assert.Equal("▶ " + PlanningProse + Environment.NewLine, progress.ToString());
    }

    [Fact]
    public async Task PlanningProseThenAnEmptyArraySucceeds()
    {
        var script = new ScriptedChatClient(
            new ChatMessage(ChatRole.Assistant, PlanningProse),
            new ChatMessage(ChatRole.Assistant, "[]"));
        var progress = new StringWriter();

        var raw = await ReviewAgent.CompleteAsync(Request(progress), UserPrompt, CancellationToken.None, script);
        var parsed = ReviewCommentParser.Parse(raw, 20);

        Assert.True(parsed.Success, parsed.Error);
        Assert.Empty(parsed.Comments);
        Assert.Equal("[]", raw);
        Assert.Equal(2, script.Requests.Count);
        Assert.Contains(PlanningProse, progress.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanningProseThenAToolCallContinuesTheSameThread()
    {
        using var repo = new TempRepo();
        repo.Write("src/Pay.java", "class Pay { void notifyIcbc() {} }\n");
        repo.Git("add", ".");
        repo.Git("commit", "-m", "init");

        var read = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(
            "call_read",
            "file_read",
            new Dictionary<string, object?> { ["path"] = "src/Pay.java" })]);
        var json = """
            [{"path":"src/Pay.java","start_line":1,"end_line":1,"severity":"low","category":"maintainability","content":"The notify method does not check the order id."}]
            """;
        var script = new ScriptedChatClient(
            new ChatMessage(ChatRole.Assistant, PlanningProse),
            read,
            new ChatMessage(ChatRole.Assistant, json));
        var progress = new StringWriter();

        var raw = await ReviewAgent.CompleteAsync(
            Request(progress, repo.Path),
            UserPrompt,
            CancellationToken.None,
            script);
        var parsed = ReviewCommentParser.Parse(raw, 20);

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(json, raw);
        Assert.Equal(3, script.Requests.Count);
        Assert.Equal(1, CountText(script.Requests[2], PlanningProse));
        Assert.Equal(1, CountText(script.Requests[2], ReviewPrompt.JsonContinuation));
        var results = script.Requests[2]
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Select(result => result.Result?.ToString() ?? "");
        Assert.Contains(results, result => result.Contains("notifyIcbc", StringComparison.Ordinal));
        var log = progress.ToString();
        Assert.Contains("▶ " + PlanningProse, log, StringComparison.Ordinal);
        Assert.Contains("▶ file_read src/Pay.java", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolCallThenPlanningProseContinuesTheSameThread()
    {
        using var repo = new TempRepo();
        repo.Write(
            "pay-core/pay-core-service/src/main/java/cn/yzw/sk/pay/core/service/payment/ICBCAggregatePayment.java",
            "class ICBCAggregatePayment { void notifyOrder() {} }\n");
        repo.Git("add", ".");
        repo.Git("commit", "-m", "init");

        var read = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(
            "call_read",
            "file_read",
            new Dictionary<string, object?>
            {
                ["path"] = "pay-core/pay-core-service/src/main/java/cn/yzw/sk/pay/core/service/payment/ICBCAggregatePayment.java",
                ["start_line"] = 1,
                ["end_line"] = 1
            })]);
        var json = """
            [{"path":"pay-core/pay-core-service/src/main/java/cn/yzw/sk/pay/core/service/payment/ICBCAggregatePayment.java","start_line":1,"end_line":1,"severity":"medium","category":"bug","content":"The notify handler does not reject a mismatched order id."}]
            """;
        var script = new ScriptedChatClient(
            read,
            new ChatMessage(ChatRole.Assistant, PlanningProse),
            new ChatMessage(ChatRole.Assistant, json));
        var progress = new StringWriter();

        var raw = await ReviewAgent.CompleteAsync(
            Request(progress, repo.Path),
            UserPrompt,
            CancellationToken.None,
            script);
        var parsed = ReviewCommentParser.Parse(raw, 20);

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(json, raw);
        Assert.Equal(3, script.Requests.Count);
        Assert.Equal(1, CountText(script.Requests[2], PlanningProse));
        Assert.Equal(1, CountText(script.Requests[2], ReviewPrompt.JsonContinuation));
        var continuedRead = script.Requests[2]
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Select(result => result.Result?.ToString() ?? "");
        Assert.Contains(continuedRead, result => result.Contains("notifyOrder", StringComparison.Ordinal));
        var log = progress.ToString();
        Assert.Contains("▶ " + PlanningProse, log, StringComparison.Ordinal);
        Assert.Contains("▶ file_read ", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanningProseUntilTheRecoveryBudgetFailsWithTheJsonError()
    {
        var longPlan = "I'll inspect the changed files. " + new string('x', 400);
        var last = "Still planning and not returning review JSON. " + new string('y', 2_500);
        var replies = new List<ChatMessage> { new(ChatRole.Assistant, longPlan) };
        for (var i = 1; i < ReviewAgentLimits.MaxJsonRecoveries; i++)
            replies.Add(new ChatMessage(ChatRole.Assistant, $"Planning step {i} before judging the diff."));
        replies.Add(new ChatMessage(ChatRole.Assistant, last));

        var script = new ScriptedChatClient([.. replies]);
        var progress = new StringWriter();

        var raw = await ReviewAgent.CompleteAsync(Request(progress), UserPrompt, CancellationToken.None, script);
        var parsed = ReviewCommentParser.Parse(raw, 20);

        Assert.False(parsed.Success);
        Assert.Equal("The model did not return review comments as JSON.", parsed.Error);
        Assert.Equal(last, raw);
        Assert.Equal(1 + ReviewAgentLimits.MaxJsonRecoveries, script.Requests.Count);

        var lines = progress.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ReviewAgentLimits.MaxJsonRecoveries, lines.Length);
        var excerpt = lines[0]["▶ ".Length..];
        Assert.Equal(ReviewTools.MaxProgressChars, excerpt.Length);
        Assert.EndsWith("...", excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 200), progress.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Still planning and not returning review JSON.", progress.ToString(), StringComparison.Ordinal);
        Assert.Contains("Planning step 1 before judging the diff.", progress.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidJsonOnTheFirstFinalMessageIsUnchanged()
    {
        var json = """
            [{"path":"a.cs","start_line":8,"end_line":8,"severity":"high","category":"bug","content":"The loop increments the index before the bounds check."}]
            """;
        var script = new ScriptedChatClient(new ChatMessage(ChatRole.Assistant, json));
        var progress = new StringWriter();

        var raw = await ReviewAgent.CompleteAsync(Request(progress), UserPrompt, CancellationToken.None, script);
        var parsed = ReviewCommentParser.Parse(raw, 20);

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(json, raw);
        Assert.Single(parsed.Comments);
        Assert.Single(script.Requests);
        Assert.Equal("", progress.ToString());
        Assert.Equal(1, CountText(script.Requests[0], UserPrompt));
        Assert.Equal(0, CountText(script.Requests[0], ReviewPrompt.JsonContinuation));
    }

    [Fact]
    public async Task EmptyCommentArrayOnTheFirstFinalMessageIsUnchanged()
    {
        var script = new ScriptedChatClient(new ChatMessage(ChatRole.Assistant, "[]"));
        var progress = new StringWriter();

        var raw = await ReviewAgent.CompleteAsync(Request(progress), UserPrompt, CancellationToken.None, script);
        var parsed = ReviewCommentParser.Parse(raw, 20);

        Assert.True(parsed.Success, parsed.Error);
        Assert.Empty(parsed.Comments);
        Assert.Equal("[]", raw);
        Assert.Single(script.Requests);
        Assert.Equal("", progress.ToString());
    }

    private static int CountText(IReadOnlyList<ChatMessage> messages, string text) =>
        messages.Count(message => message.Text.Contains(text, StringComparison.Ordinal));

    private static ReviewAgentRequest Request(TextWriter progress, string? directory = null) => new()
    {
        ApiKey = "sk-test",
        Endpoint = "https://example.invalid/v1",
        Model = "deepseek-flash",
        WorkingDirectory = directory ?? Path.GetTempPath(),
        MaxRounds = 6,
        MaxToolChars = 12_000,
        NameOnly = false,
        Progress = progress
    };

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<ChatMessage> _responses;

        public ScriptedChatClient(params ChatMessage[] responses)
        {
            _responses = new Queue<ChatMessage>(responses);
        }

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            if (_responses.Count == 0)
                throw new InvalidOperationException("The scripted chat client ran out of responses.");

            return Task.FromResult(new ChatResponse(_responses.Dequeue()));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class TempRepo : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "aim-prose-" + Guid.NewGuid().ToString("n"));

        public TempRepo()
        {
            Directory.CreateDirectory(Path);
            Git("init", "-b", "main");
            Git("config", "user.email", "aim-tests@example.com");
            Git("config", "user.name", "Aim Tests");
            Git("config", "commit.gpgsign", "false");
        }

        public void Write(string name, string contents)
        {
            var full = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, contents);
        }

        public void Git(params string[] args)
        {
            var (ok, stdout, stderr) = GitExecutor.RunGit(args, Path);
            Assert.True(ok, stderr + stdout);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
                return;

            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(Path, true);
        }
    }
}
