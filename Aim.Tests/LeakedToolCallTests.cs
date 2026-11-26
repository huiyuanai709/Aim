using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Aim;

namespace Aim.Tests;

public class LeakedToolCallParserTests
{
    [Fact]
    public void ParsesDoubledPipeMarkupWhenParameterClosersOmitTheBracket()
    {
        var parsed = LeakedToolCallParser.TryParse(DsmlSample.DoubledPipeBareParameterClosers(), out var calls, out var remainder);

        Assert.True(parsed);
        Assert.Equal("", remainder);
        Assert.Equal(3, calls.Count);
        Assert.Equal("report_progress", calls[0].Name);
        Assert.Equal(
            "Checking for another callback entry point that may already handle ICBC pay notifications.",
            calls[0].Arguments["message"]);
        Assert.Equal("code_search", calls[1].Name);
        Assert.Equal("ICBC", calls[1].Arguments["query"]);
        Assert.Equal("src/main/java/cn/yzw/sk/portal/pay", calls[1].Arguments["path"]);
        Assert.Equal("code_search", calls[2].Name);
        Assert.Equal("Notify_Order", calls[2].Arguments["query"]);
        Assert.False(calls[2].Arguments.ContainsKey("path"));
    }

    [Fact]
    public void ParsesCanonicalClosersAndJsonArguments()
    {
        var raw = string.Join('\n',
            DsmlSample.Open("calls", bars: 1),
            DsmlSample.Open("invoke name=\"file_read\"", bars: 1),
            DsmlSample.Open("parameter name=\"path\" string=\"true\"", bars: 1) + "src/App.cs" + DsmlSample.Close("parameter", bars: 1),
            DsmlSample.Open("parameter name=\"start_line\" string=\"false\"", bars: 1) + "2" + DsmlSample.Close("parameter", bars: 1),
            DsmlSample.Open("parameter name=\"end_line\" string=\"false\"", bars: 1) + "3" + DsmlSample.Close("parameter", bars: 1),
            DsmlSample.Close("invoke", bars: 1),
            DsmlSample.Close("calls", bars: 1));

        Assert.True(LeakedToolCallParser.TryParse(raw, out var calls, out var remainder));
        var call = Assert.Single(calls);
        Assert.Equal("file_read", call.Name);
        Assert.Equal("src/App.cs", call.Arguments["path"]);
        Assert.Equal(2, call.Arguments["start_line"]);
        Assert.Equal(3, call.Arguments["end_line"]);
        Assert.Equal("", remainder);
    }

    [Fact]
    public void ParsesAsciiBarsAndTheToolCallsWrapper()
    {
        var raw = string.Join('\n',
            DsmlSample.Open("tool_calls", bars: 1, bar: '|', spaced: false),
            DsmlSample.Open("invoke name=\"code_search\"", bars: 1, bar: '|', spaced: false),
            DsmlSample.Open("parameter name=\"query\" string=\"true\"", bars: 1, bar: '|', spaced: false) + "Notify_Order" +
                DsmlSample.Close("parameter", bars: 1, bar: '|', spaced: false),
            DsmlSample.Close("invoke", bars: 1, bar: '|', spaced: false),
            DsmlSample.Close("tool_calls", bars: 1, bar: '|', spaced: false));

        Assert.True(LeakedToolCallParser.TryParse(raw, out var calls, out var remainder));
        var call = Assert.Single(calls);
        Assert.Equal("code_search", call.Name);
        Assert.Equal("Notify_Order", call.Arguments["query"]);
        Assert.Equal("", remainder);
    }

    [Fact]
    public void KeepsProseOutsideTheToolBlock()
    {
        var raw = "Still checking the callback.\n" + DsmlSample.DoubledPipeBareParameterClosers();

        Assert.True(LeakedToolCallParser.TryParse(raw, out var calls, out var remainder));
        Assert.Equal(3, calls.Count);
        Assert.Equal("Still checking the callback.", remainder);
    }

    [Fact]
    public void IgnoresIncompleteMarkupAndEmptyText()
    {
        Assert.False(LeakedToolCallParser.TryParse(null, out _, out var remainder));
        Assert.Equal("", remainder);
        Assert.False(LeakedToolCallParser.TryParse("   ", out _, out remainder));
        Assert.Equal("   ", remainder);
        Assert.False(LeakedToolCallParser.TryParse(DsmlSample.Open("invoke name=\"file_read\""), out _, out _));
    }

    [Fact]
    public void LeavesReviewJsonThatMentionsDsmlAlone()
    {
        var raw = """
            [{"path":"a.cs","start_line":1,"end_line":1,"severity":"low","category":"style","content":"DSML is only a word here."}]
            """;

        Assert.False(LeakedToolCallParser.TryParse(raw, out var calls, out var remainder));
        Assert.Empty(calls);
        Assert.Equal(raw, remainder);
    }
}

public class LeakedToolCallAgentTests
{
    [Fact]
    public async Task ExecutesLeakedDeepSeekCallsAndContinuesToTheReviewJson()
    {
        using var repo = new TempRepo();
        repo.Write(
            "src/main/java/cn/yzw/sk/portal/pay/NotifyServlet.java",
            "class NotifyServlet { String channel = \"ICBC\"; String kind = \"Notify_Order\"; }\n");
        repo.Git("add", ".");
        repo.Git("commit", "-m", "init");

        var finalJson = """
            [{"path":"src/main/java/cn/yzw/sk/portal/pay/NotifyServlet.java","start_line":1,"end_line":1,"severity":"medium","category":"bug","content":"The notify handler does not reject a mismatched ICBC order id."}]
            """;
        var leaked = new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, DsmlSample.DoubledPipeBareParameterClosers());
        leaked.RawRepresentation = new UserChatMessage(DsmlSample.DoubledPipeBareParameterClosers());
        var script = new ScriptedChatClient(
            leaked,
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, finalJson));
        var progress = new StringWriter();
        var agent = ReviewAgent.Create(Request(repo.Path, progress), script);
        try
        {
            var response = await agent.RunAsync("Review the diff.");
            var parsed = ReviewCommentParser.Parse(response.Text, 20);

            Assert.True(parsed.Success, parsed.Error);
            var comment = Assert.Single(parsed.Comments);
            Assert.Equal("src/main/java/cn/yzw/sk/portal/pay/NotifyServlet.java", comment.Path);
            Assert.Equal(2, script.Requests.Count);

            var log = progress.ToString();
            Assert.Contains(
                "▶ report_progress Checking for another callback entry point that may already handle ICBC pay notifications.",
                log);
            Assert.Contains("▶ code_search ICBC src/main/java/cn/yzw/sk/portal/pay", log);
            Assert.Contains("▶ code_search Notify_Order", log);

            var results = script.Requests[1]
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .Select(result => result.Result?.ToString() ?? "")
                .ToList();
            Assert.Contains(results, result => result.Contains("Noted.", StringComparison.Ordinal));
            Assert.Contains(results, result => result.Contains("NotifyServlet.java", StringComparison.Ordinal));
            Assert.Contains(results, result => result.Contains("Notify_Order", StringComparison.Ordinal));
            Assert.Null(leaked.RawRepresentation);
            Assert.Equal(3, leaked.Contents.OfType<FunctionCallContent>().Count());
            Assert.DoesNotContain("DSML", leaked.Text, StringComparison.Ordinal);
        }
        finally
        {
            ReviewAgent.DisposeProviders(agent);
        }
    }

    [Fact]
    public async Task ExecutesCanonicalFileReadArgumentsFromLeakedMarkup()
    {
        using var repo = new TempRepo();
        repo.Write("src/App.cs", "one\ntwo\nthree\nfour\n");
        repo.Git("add", "src/App.cs");
        repo.Git("commit", "-m", "init");

        var raw = string.Join('\n',
            DsmlSample.Open("calls", bars: 1),
            DsmlSample.Open("invoke name=\"file_read\"", bars: 1),
            DsmlSample.Open("parameter name=\"path\" string=\"true\"", bars: 1) + "src/App.cs" + DsmlSample.Close("parameter", bars: 1),
            DsmlSample.Open("parameter name=\"start_line\" string=\"false\"", bars: 1) + "2" + DsmlSample.Close("parameter", bars: 1),
            DsmlSample.Open("parameter name=\"end_line\" string=\"false\"", bars: 1) + "3" + DsmlSample.Close("parameter", bars: 1),
            DsmlSample.Close("invoke", bars: 1),
            DsmlSample.Close("calls", bars: 1));
        var script = new ScriptedChatClient(
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, raw),
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, "[]"));
        var progress = new StringWriter();
        var agent = ReviewAgent.Create(Request(repo.Path, progress), script);
        try
        {
            var response = await agent.RunAsync("Review the diff.");

            Assert.Equal("[]", response.Text.Trim());
            Assert.Equal(2, script.Requests.Count);
            Assert.Contains("▶ file_read src/App.cs:2-3", progress.ToString());
            var results = script.Requests[1]
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .Select(result => result.Result?.ToString() ?? "");
            Assert.Contains(results, result => result.Contains("2: two", StringComparison.Ordinal) && result.Contains("3: three", StringComparison.Ordinal));
        }
        finally
        {
            ReviewAgent.DisposeProviders(agent);
        }
    }

    [Fact]
    public async Task DoesNotRunLeakedMarkupWhenStructuredToolCallsAlreadyExist()
    {
        var leaked = string.Join('\n',
            DsmlSample.Open("calls"),
            DsmlSample.Open("invoke name=\"report_progress\""),
            DsmlSample.Open("parameter name=\"message\" string=\"true\"") + "Should not run" + DsmlSample.Close("parameter"),
            DsmlSample.Close("invoke"),
            DsmlSample.Close("calls"));
        var first = new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, leaked);
        first.Contents.Add(new FunctionCallContent(
            "call_real",
            "report_progress",
            new Dictionary<string, object?> { ["message"] = "Reading the handler" }));
        first.RawRepresentation = new UserChatMessage("original assistant message");
        var script = new ScriptedChatClient(
            first,
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, "[]"));
        var progress = new StringWriter();
        var agent = ReviewAgent.Create(Request(Path.GetTempPath(), progress), script);
        try
        {
            var response = await agent.RunAsync("Review the diff.");
            var parsed = ReviewCommentParser.Parse(response.Text, 20);

            Assert.True(parsed.Success, parsed.Error);
            Assert.Empty(parsed.Comments);
            Assert.Equal(2, script.Requests.Count);
            Assert.Equal("▶ report_progress Reading the handler" + Environment.NewLine, progress.ToString());
            Assert.DoesNotContain("Should not run", progress.ToString());
            Assert.IsType<UserChatMessage>(first.RawRepresentation);
            Assert.Contains("Should not run", first.Text);
            Assert.Single(first.Contents.OfType<FunctionCallContent>());
        }
        finally
        {
            ReviewAgent.DisposeProviders(agent);
        }
    }

    [Fact]
    public async Task ReturnsReviewJsonWithoutAnotherRound()
    {
        var json = """
            [{"path":"a.cs","start_line":1,"end_line":1,"severity":"low","category":"style","content":"DSML is only a word here."}]
            """;
        var script = new ScriptedChatClient(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, json));
        var progress = new StringWriter();
        var agent = ReviewAgent.Create(Request(Path.GetTempPath(), progress), script);
        try
        {
            var response = await agent.RunAsync("Review the diff.");
            var parsed = ReviewCommentParser.Parse(response.Text, 20);

            Assert.True(parsed.Success, parsed.Error);
            Assert.Single(script.Requests);
            Assert.Equal("", progress.ToString());
        }
        finally
        {
            ReviewAgent.DisposeProviders(agent);
        }
    }

    private static ReviewAgentRequest Request(string directory, TextWriter progress) => new()
    {
        ApiKey = "sk-test",
        Endpoint = "https://example.invalid/v1",
        Model = "deepseek-flash",
        WorkingDirectory = directory,
        MaxRounds = 6,
        MaxToolChars = 12_000,
        NameOnly = false,
        Progress = progress
    };

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<Microsoft.Extensions.AI.ChatMessage> _responses;

        public ScriptedChatClient(params Microsoft.Extensions.AI.ChatMessage[] responses)
        {
            _responses = new Queue<Microsoft.Extensions.AI.ChatMessage>(responses);
        }

        public List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            if (_responses.Count == 0)
                throw new InvalidOperationException("The scripted chat client ran out of responses.");

            return Task.FromResult(new ChatResponse(_responses.Dequeue()));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
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
            "aim-dsml-" + Guid.NewGuid().ToString("n"));

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

internal static class DsmlSample
{
    public static string DoubledPipeBareParameterClosers()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Open("calls"));
        builder.AppendLine(Open("invoke name=\"report_progress\""));
        builder.Append(Open("parameter name=\"message\" string=\"true\""));
        builder.Append("Checking for another callback entry point that may already handle ICBC pay notifications.");
        builder.AppendLine(BareClose("parameter"));
        builder.AppendLine(Close("invoke"));
        builder.AppendLine(Open("invoke name=\"code_search\""));
        builder.Append(Open("parameter name=\"query\" string=\"true\""));
        builder.Append("ICBC");
        builder.AppendLine(BareClose("parameter"));
        builder.Append(Open("parameter name=\"path\" string=\"true\""));
        builder.Append("src/main/java/cn/yzw/sk/portal/pay");
        builder.AppendLine(BareClose("parameter"));
        builder.AppendLine(Close("invoke"));
        builder.AppendLine(Open("invoke name=\"code_search\""));
        builder.Append(Open("parameter name=\"query\" string=\"true\""));
        builder.Append("Notify_Order");
        builder.AppendLine(BareClose("parameter"));
        builder.AppendLine(Close("invoke"));
        builder.Append(Close("calls"));
        return builder.ToString();
    }

    public static string Open(string body, int bars = 2, char bar = '\uFF5C', bool spaced = true) =>
        $"<{Marker(bars, bar)}DSML{Marker(bars, bar)}{(spaced ? " " : "")}{body}>";

    public static string Close(string name, int bars = 2, char bar = '\uFF5C', bool spaced = true) =>
        $"</{Marker(bars, bar)}DSML{Marker(bars, bar)}{(spaced ? " " : "")}{name}>";

    public static string BareClose(string name, int bars = 2, char bar = '\uFF5C') =>
        $"{Marker(bars, bar)}DSML{Marker(bars, bar)} {name}>";

    private static string Marker(int bars, char bar) => new(bar, bars);
}
