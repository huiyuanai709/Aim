using System.ClientModel;
using System.CommandLine;
using System.Text;
using OpenAI;
using OpenAI.Chat;

namespace Aim;

public class ReviewCommand : Command
{
    public ReviewCommand() : base(
        "review",
        "Review a git diff and print AI line comments. Defaults to staged changes. Line numbers refer to the new file and are best-effort.")
    {
        Option<string?> fromOption = new("--from")
        {
            Description = "Base ref for a triple-dot range review (changes on --to since it diverged from this ref). Requires --to.",
            HelpName = "ref"
        };
        Option<string?> toOption = new("--to")
        {
            Description = "Head ref for a triple-dot range review. Requires --from.",
            HelpName = "ref"
        };
        Option<string?> commitOption = new("--commit")
        {
            Description = "Review the patch of one commit (SHA or ref). Cannot be combined with --from/--to.",
            HelpName = "rev"
        };
        Option<string?> ruleOption = new("--rule")
        {
            Description = "Path to a text file of extra review rules, appended to the built-in checklist.",
            HelpName = "file"
        };
        Option<string?> backgroundOption = new("--background")
        {
            Description = "Extra product context included in the review prompt.",
            HelpName = "text"
        };
        Option<string> formatOption = new("--format")
        {
            Description = "Output format: text or json.",
            HelpName = "text|json",
            DefaultValueFactory = _ => "text"
        };

        Options.Add(fromOption);
        Options.Add(toOption);
        Options.Add(commitOption);
        Options.Add(ruleOption);
        Options.Add(backgroundOption);
        Options.Add(formatOption);

        SetAction(async (parseResult, cancellationToken) =>
        {
            if (!ReviewTarget.TryParse(
                    parseResult.GetValue(fromOption),
                    parseResult.GetValue(toOption),
                    parseResult.GetValue(commitOption),
                    parseResult.GetValue(formatOption),
                    out var target,
                    out var error))
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            string? rules = null;
            var rulePath = parseResult.GetValue(ruleOption);
            if (!string.IsNullOrWhiteSpace(rulePath))
            {
                if (!File.Exists(rulePath))
                {
                    Console.Error.WriteLine($"Error: Rules file not found: {rulePath}");
                    return 1;
                }

                try
                {
                    rules = File.ReadAllText(rulePath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: Could not read rules file: {ex.Message}");
                    return 1;
                }
            }

            var background = parseResult.GetValue(backgroundOption);
            var config = ConfigManager.LoadConfig();
            var nameOnly = config.DiffNameOnly;
            var (success, diff, diffError) = ReviewDiffLoader.Load(target, nameOnly);
            if (!success)
            {
                WriteGitError(diffError);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(diff))
            {
                Console.Error.WriteLine(ReviewDiffLoader.EmptyMessage(target));
                return 1;
            }

            if (string.IsNullOrWhiteSpace(config.ApiKey))
            {
                Console.Error.WriteLine("Error: OpenAI API key is required.");
                Console.Error.WriteLine("Run 'aim config --set apikey=YOUR_KEY' to save it.");
                return 1;
            }

            var truncated = false;
            if (nameOnly)
            {
                Console.Error.WriteLine("Note: diffnameonly is enabled. Review comments cannot cite real line numbers.");
                if (config.MaxDiffChars > 0 && diff.Length > config.MaxDiffChars)
                {
                    Console.Error.WriteLine(
                        $"Warning: Diff ({diff.Length} chars) exceeds maxdiffchars ({config.MaxDiffChars}). Truncating the file list.");
                    diff = DiffLimiter.Limit(diff, config.MaxDiffChars).Text;
                    truncated = true;
                }
            }
            else if (config.MaxDiffChars > 0 && diff.Length > config.MaxDiffChars)
            {
                var rawLength = diff.Length;
                var fit = DiffLimiter.Fit(diff, config.MaxDiffChars);
                if (fit.NeedsNameOnlyFallback)
                {
                    Console.Error.WriteLine(
                        $"Warning: Diff ({rawLength} chars) exceeds maxdiffchars ({config.MaxDiffChars}). Falling back to file names only.");
                    (success, diff, diffError) = ReviewDiffLoader.Load(target, nameOnly: true);
                    if (!success)
                    {
                        WriteGitError(diffError);
                        return 1;
                    }

                    nameOnly = true;
                    if (config.MaxDiffChars > 0 && diff.Length > config.MaxDiffChars)
                    {
                        diff = DiffLimiter.Limit(diff, config.MaxDiffChars).Text;
                        truncated = true;
                    }
                }
                else
                {
                    var detail = fit.OmittedFiles > 0
                        ? $"Sending the leading patch content and omitting {fit.OmittedFiles} file patch(es)."
                        : "Sending a truncated prefix of the patch.";
                    Console.Error.WriteLine(
                        $"Warning: Diff ({rawLength} chars) exceeds maxdiffchars ({config.MaxDiffChars}). {detail}");
                    diff = fit.Text;
                    truncated = true;
                }
            }

            if (string.IsNullOrWhiteSpace(diff))
            {
                Console.Error.WriteLine(ReviewDiffLoader.EmptyMessage(target));
                return 1;
            }

            if (!string.IsNullOrWhiteSpace(rules) && rules.Length > ReviewPrompt.MaxRuleChars)
            {
                Console.Error.WriteLine(
                    $"Warning: Rules file exceeds {ReviewPrompt.MaxRuleChars} characters. Using the first {ReviewPrompt.MaxRuleChars}.");
                rules = rules[..ReviewPrompt.MaxRuleChars];
            }

            if (!string.IsNullOrWhiteSpace(background) && background.Length > ReviewPrompt.MaxBackgroundChars)
            {
                Console.Error.WriteLine(
                    $"Warning: --background exceeds {ReviewPrompt.MaxBackgroundChars} characters. Using the first {ReviewPrompt.MaxBackgroundChars}.");
                background = background[..ReviewPrompt.MaxBackgroundChars];
            }

            if (string.IsNullOrWhiteSpace(rules))
                rules = null;
            if (string.IsNullOrWhiteSpace(background))
                background = null;

            var maxComments = config.MaxReviewComments;
            if (maxComments < 1)
            {
                Console.Error.WriteLine("Warning: maxreviewcomments must be positive. Using 20.");
                maxComments = 20;
            }

            var model = string.IsNullOrWhiteSpace(config.ReviewModel) ? config.Model : config.ReviewModel.Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                Console.Error.WriteLine("Error: No model configured. Set model or reviewmodel.");
                return 1;
            }

            if (!nameOnly)
                diff = DiffAnnotator.Annotate(diff);

            if (truncated)
                diff = diff.TrimEnd('\r', '\n') + "\n\n[truncated: additional diff content was omitted]\n";

            var prompt = ReviewPrompt.Build(new ReviewPromptInput
            {
                Diff = diff,
                DiffLabel = ReviewDiffLoader.DiffLabel(target, nameOnly),
                NameOnly = nameOnly,
                Truncated = truncated,
                MaxComments = maxComments,
                Background = background,
                ExtraRules = rules
            });

            var endpoint = string.IsNullOrWhiteSpace(config.ApiEndpoint)
                ? "https://api.openai.com/v1"
                : config.ApiEndpoint.Trim();

            try
            {
                Console.Error.WriteLine($"Reviewing {ReviewDiffLoader.Describe(target)} with {model}...");
                var openAI = new OpenAIClient(
                    new ApiKeyCredential(config.ApiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
                var chatClient = openAI.GetChatClient(model);
                var response = await chatClient.CompleteChatAsync(
                    [new UserChatMessage(prompt)],
                    null,
                    cancellationToken);

                var raw = ReadContent(response.Value);
                var parsed = ReviewCommentParser.Parse(raw, maxComments);
                if (!parsed.Success)
                {
                    Console.Error.WriteLine($"Error: {parsed.Error}");
                    Console.Error.WriteLine("Raw response:");
                    Console.Error.WriteLine(Excerpt(raw));
                    return 1;
                }

                if (parsed.Comments.Count == 0 && parsed.Skipped > 0)
                {
                    Console.Error.WriteLine(
                        $"Error: The model returned {parsed.Skipped} comment(s) but none matched the review schema.");
                    Console.Error.WriteLine("Raw response:");
                    Console.Error.WriteLine(Excerpt(raw));
                    return 1;
                }

                if (parsed.Skipped > 0)
                {
                    Console.Error.WriteLine(
                        $"Warning: Skipped {parsed.Skipped} model comment(s) that did not match the review schema.");
                }

                if (parsed.OmittedByCap > 0)
                {
                    var total = parsed.Comments.Count + parsed.OmittedByCap;
                    Console.Error.WriteLine(
                        $"Note: The model returned {total} comments. Showing {parsed.Comments.Count} (maxreviewcomments).");
                }

                var rendered = target.Format == "json"
                    ? ReviewFormatter.ToJson(parsed.Comments)
                    : ReviewFormatter.ToText(parsed.Comments, truncated, nameOnly);
                Console.WriteLine(rendered);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("\nOperation was cancelled");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error calling AI API: {ex.Message}");
                return 1;
            }
        });
    }

    private static void WriteGitError(string diffError)
    {
        var detail = diffError.Trim();
        if (detail.Length == 0)
            detail = "git exited with a non-zero status.";

        Console.Error.WriteLine($"Error running git diff: {detail}");
    }

    private static string ReadContent(ChatCompletion completion)
    {
        var sb = new StringBuilder();
        foreach (var part in completion.Content)
        {
            if (!string.IsNullOrEmpty(part.Text))
                sb.Append(part.Text);
        }

        return sb.ToString();
    }

    private static string Excerpt(string raw)
    {
        var trimmed = raw.Trim();
        const int max = 2000;
        if (trimmed.Length <= max)
            return trimmed;

        return trimmed[..max] + "\n...";
    }
}
