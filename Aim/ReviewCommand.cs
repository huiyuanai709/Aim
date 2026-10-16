using System.CommandLine;
using System.Globalization;
using OpenAI;

namespace Aim;

public class ReviewCommand : Command
{
    public ReviewCommand() : base(
        "review",
        "Review a git diff and print AI line comments. Defaults to staged changes. Use --branch for default-branch...HEAD. Large diffs are split into directory groups and reviewed together. Line numbers refer to the new file and are best-effort.")
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
        Option<bool> branchOption = new("--branch")
        {
            Description = "Review HEAD against the default branch (triple-dot). Default branch is origin/HEAD, or else main, master, origin/main, origin/master. Not with --from, --to, or --commit."
        };
        Option<bool> firstCommitOption = new("--first-commit")
        {
            Description = "Review the oldest commit on HEAD that is not on the default branch. Not with --from, --to, --commit, or --branch."
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
        Option<bool> agentOption = new("--agent")
        {
            Description = "Opt in to a review agent that can read files and search the repository before answering. Off by default. Large diffs run one agent per group."
        };
        Option<string?> maxRoundsOption = new("--max-rounds")
        {
            Description = "Maximum agent tool rounds (1-40). Overrides maxreviewrounds. Used only with --agent.",
            HelpName = "n"
        };
        Option<int?> concurrencyOption = new("--concurrency")
        {
            Description = "How many file groups to review at the same time (1-32). Default: 8, or reviewconcurrency. Used when a diff is split.",
            HelpName = "n"
        };
        Option<int?> timeoutOption = new("--timeout")
        {
            Description = "Minutes to wait for each review call (1-60). Default: 5, or reviewtimeoutminutes. Also raises the HTTP timeout, which otherwise stops a call at about 100 seconds.",
            HelpName = "minutes"
        };
        Option<int?> budgetOption = new("--max-tokens-budget")
        {
            Description = "Approximate token budget for all groups (diff characters / 4). The first group is always sent. Later groups are not started when the estimate would exceed this. Omit for no limit.",
            HelpName = "n"
        };
        Option<bool> previewOption = new("--preview")
        {
            Description = "Print the files and groups that would be reviewed, then exit. Does not call the model and does not need an API key."
        };

        Options.Add(fromOption);
        Options.Add(toOption);
        Options.Add(commitOption);
        Options.Add(branchOption);
        Options.Add(firstCommitOption);
        Options.Add(ruleOption);
        Options.Add(backgroundOption);
        Options.Add(formatOption);
        Options.Add(agentOption);
        Options.Add(maxRoundsOption);
        Options.Add(concurrencyOption);
        Options.Add(timeoutOption);
        Options.Add(budgetOption);
        Options.Add(previewOption);

        SetAction(async (parseResult, cancellationToken) =>
        {
            if (!ReviewTarget.TryParse(
                    parseResult.GetValue(fromOption),
                    parseResult.GetValue(toOption),
                    parseResult.GetValue(commitOption),
                    parseResult.GetValue(branchOption),
                    parseResult.GetValue(firstCommitOption),
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
            var agentEnabled = parseResult.GetValue(agentOption);
            var preview = parseResult.GetValue(previewOption);
            if (!TryParseMaxRounds(parseResult.GetValue(maxRoundsOption), out var maxRoundsCli, out var maxRoundsError))
            {
                Console.Error.WriteLine(maxRoundsError);
                return 1;
            }

            var config = ConfigManager.LoadConfig();
            if (!ReviewParallelLimits.TryResolveConcurrency(
                    parseResult.GetValue(concurrencyOption),
                    config.ReviewConcurrency,
                    out var concurrency,
                    out var concurrencyNote,
                    out var concurrencyError))
            {
                Console.Error.WriteLine(concurrencyError);
                return 1;
            }

            if (!ReviewParallelLimits.TryResolveTimeout(
                    parseResult.GetValue(timeoutOption),
                    config.ReviewTimeoutMinutes,
                    out var timeoutMinutes,
                    out var timeoutNote,
                    out var timeoutError))
            {
                Console.Error.WriteLine(timeoutError);
                return 1;
            }

            if (!ReviewParallelLimits.TryResolveBudget(parseResult.GetValue(budgetOption), out var tokenBudget, out var budgetError))
            {
                Console.Error.WriteLine(budgetError);
                return 1;
            }

            var maxRounds = config.MaxReviewRounds;
            if (agentEnabled || maxRoundsCli is not null)
            {
                if (!ReviewAgentLimits.TryResolveRounds(
                        maxRoundsCli,
                        config.MaxReviewRounds,
                        agentEnabled,
                        out maxRounds,
                        out var roundsNote,
                        out var roundsError))
                {
                    Console.Error.WriteLine(roundsError);
                    return 1;
                }

                if (!string.IsNullOrEmpty(roundsNote))
                    Console.Error.WriteLine(roundsNote);
            }

            if (!string.IsNullOrEmpty(concurrencyNote))
                Console.Error.WriteLine(concurrencyNote);
            if (!string.IsNullOrEmpty(timeoutNote))
                Console.Error.WriteLine(timeoutNote);

            var nameOnly = config.DiffNameOnly;
            if (!DefaultBranchResolver.TryPrepare(target, null, out var prepared, out var branchNote, out var prepareError))
            {
                Console.Error.WriteLine(prepareError);
                return 1;
            }

            target = prepared;
            if (!string.IsNullOrEmpty(branchNote))
                Console.Error.WriteLine(branchNote);

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

            var truncated = false;
            ReviewPlan? plan = null;
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

                if (preview)
                {
                    Console.WriteLine(ReviewDiffPlanner.FormatNameOnlyPreview(
                        ReviewDiffLoader.Describe(target),
                        diff,
                        truncated));
                    return 0;
                }
            }
            else
            {
                plan = ReviewDiffPlanner.Plan(diff, new ReviewPlanOptions { MaxDiffChars = config.MaxDiffChars });
                if (preview)
                {
                    Console.WriteLine(ReviewDiffPlanner.FormatPreview(
                        plan,
                        ReviewDiffLoader.Describe(target),
                        agentEnabled,
                        concurrency,
                        timeoutMinutes,
                        tokenBudget));
                    return plan.Unsplit || plan.Groups.Count > 0 ? 0 : 1;
                }

                if (!plan.Unsplit && plan.Groups.Count == 0)
                {
                    foreach (var skipped in plan.Skipped)
                        Console.Error.WriteLine(ReviewDiffPlanner.SkipWarning(skipped));
                    Console.Error.WriteLine("Error: Every file exceeds the single-file size limit, so there is nothing to review.");
                    return 1;
                }
            }

            if (string.IsNullOrWhiteSpace(config.ApiKey))
            {
                Console.Error.WriteLine("Error: OpenAI API key is required.");
                Console.Error.WriteLine("Run 'aim config --set apikey=YOUR_KEY' to save it.");
                return 1;
            }

            if (!nameOnly && plan is { Unsplit: true } && config.MaxDiffChars > 0 && diff.Length > config.MaxDiffChars)
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

            var timeout = TimeSpan.FromMinutes(timeoutMinutes);
            var networkTimeout = ReviewChat.NetworkTimeoutFor(timeout);
            var endpoint = string.IsNullOrWhiteSpace(config.ApiEndpoint)
                ? "https://api.openai.com/v1"
                : config.ApiEndpoint.Trim();
            var description = ReviewDiffLoader.Describe(target);
            var grouped = plan is { Unsplit: false } && !nameOnly;

            string BuildPrompt(string source, bool promptTruncated, bool annotate, string? groupLabel)
            {
                var body = annotate && !nameOnly ? DiffAnnotator.Annotate(source) : source;
                return ReviewPrompt.Build(new ReviewPromptInput
                {
                    Diff = body,
                    DiffLabel = ReviewDiffLoader.DiffLabel(target, nameOnly),
                    NameOnly = nameOnly,
                    Truncated = promptTruncated,
                    MaxComments = maxComments,
                    Background = background,
                    ExtraRules = rules,
                    AgentTools = agentEnabled,
                    GroupLabel = groupLabel
                });
            }

            int PrintParsed(ReviewParseResult parsed, bool diffTruncated, bool partial, string? rawOnFailure)
            {
                if (!parsed.Success)
                {
                    Console.Error.WriteLine($"Error: {parsed.Error}");
                    if (!string.IsNullOrEmpty(rawOnFailure))
                    {
                        Console.Error.WriteLine("Raw response:");
                        Console.Error.WriteLine(Excerpt(rawOnFailure));
                    }

                    return 1;
                }

                if (parsed.Comments.Count == 0 && parsed.Skipped > 0)
                {
                    Console.Error.WriteLine(
                        $"Error: The model returned {parsed.Skipped} comment(s) but none matched the review schema.");
                    if (!string.IsNullOrEmpty(rawOnFailure))
                    {
                        Console.Error.WriteLine("Raw response:");
                        Console.Error.WriteLine(Excerpt(rawOnFailure));
                    }

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
                    : ReviewFormatter.ToText(parsed.Comments, diffTruncated, nameOnly, partial);
                Console.WriteLine(rendered);
                return 0;
            }

            try
            {
                var log = grouped && plan!.Groups.Count > 1
                    ? new LockedTextWriter(Console.Error)
                    : Console.Error;
                var skills = agentEnabled
                    ? ReviewSkillPaths.Resolve(Environment.CurrentDirectory, config.SkillsPaths)
                    : null;
                if (skills is not null)
                {
                    foreach (var note in skills.Notes)
                        Console.Error.WriteLine(note);
                    if (skills.Directories.Count > 0)
                    {
                        Console.Error.WriteLine("Skills: " + string.Join(", ", skills.Labels));
                        Console.Error.WriteLine(
                            $"Note: Skill scripts (.py, .sh, .js, .cs, .csx) run as your user in the skill directory, with a {SkillScriptRunner.DefaultTimeout.TotalSeconds:0} second timeout. A shebang selects the interpreter. Only use skills you trust.");
                    }
                }

                var toolChars = ReviewAgentLimits.ResolveToolChars(config.MaxToolResultChars);
                if (agentEnabled && toolChars != config.MaxToolResultChars)
                {
                    Console.Error.WriteLine(
                        $"Warning: maxtoolresultchars must be from 1 to {ReviewAgentLimits.MaxToolChars}. Using {toolChars}.");
                }

                OpenAIClient? client = null;
                ReviewAgentRequest? agentRequest = null;
                if (agentEnabled)
                {
                    agentRequest = new ReviewAgentRequest
                    {
                        ApiKey = config.ApiKey,
                        Endpoint = endpoint,
                        Model = model,
                        WorkingDirectory = Environment.CurrentDirectory,
                        MaxRounds = maxRounds,
                        MaxToolChars = toolChars,
                        NameOnly = nameOnly,
                        Revision = ReviewAgentLimits.ToolRevision(target),
                        Progress = log,
                        SkillDirectories = skills?.Directories ?? [],
                        NetworkTimeout = networkTimeout
                    };
                }
                else
                {
                    client = ReviewChat.CreateClient(config.ApiKey!, endpoint, networkTimeout);
                }

                async Task<string> Invoke(string prompt, CancellationToken ct)
                {
                    if (agentEnabled)
                        return await ReviewAgent.CompleteAsync(agentRequest!, prompt, ct);

                    return await ReviewChat.CompleteAsync(client!, model, prompt, ct);
                }

                async Task<string> InvokeWithTimeout(string prompt, CancellationToken ct)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(timeout);
                    try
                    {
                        return await Invoke(prompt, cts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"The review timed out after {ReviewParallelLimits.FormatMinutes(timeoutMinutes)}. Raise --timeout (minutes) for a large diff.");
                    }
                }

                void WriteStart(int groups)
                {
                    if (agentEnabled && groups > 1)
                    {
                        Console.Error.WriteLine(
                            $"Reviewing {description} with {model} (agent, up to {maxRounds} rounds, {groups} groups, concurrency {concurrency}, {ReviewParallelLimits.FormatMinutes(timeoutMinutes)} timeout per group)...");
                        return;
                    }

                    if (agentEnabled)
                    {
                        Console.Error.WriteLine(
                            $"Reviewing {description} with {model} (agent, up to {maxRounds} rounds)...");
                        return;
                    }

                    if (groups > 1)
                    {
                        Console.Error.WriteLine(
                            $"Reviewing {description} with {model} ({groups} groups, concurrency {concurrency}, {ReviewParallelLimits.FormatMinutes(timeoutMinutes)} timeout per group)...");
                        return;
                    }

                    Console.Error.WriteLine($"Reviewing {description} with {model}...");
                }

                if (!grouped)
                {
                    var body = nameOnly ? diff : DiffAnnotator.Annotate(diff);
                    if (truncated)
                        body = body.TrimEnd('\r', '\n') + "\n\n[truncated: additional diff content was omitted]\n";

                    var prompt = BuildPrompt(body, truncated, annotate: false, groupLabel: null);
                    WriteStart(1);
                    var raw = await InvokeWithTimeout(prompt, cancellationToken);
                    var parsed = ReviewCommentParser.Parse(raw, maxComments);
                    return PrintParsed(parsed, truncated, partial: false, raw);
                }

                foreach (var skipped in plan!.Skipped)
                    Console.Error.WriteLine(ReviewDiffPlanner.SkipWarning(skipped));

                WriteStart(plan.Groups.Count);
                var batch = await ReviewBatch.RunAsync(
                    plan.Groups,
                    new ReviewBatchRequest
                    {
                        Concurrency = concurrency,
                        GroupTimeout = timeout,
                        MaxTokensBudget = tokenBudget,
                        MaxComments = maxComments
                    },
                    async (group, ct) =>
                    {
                        var source = plan.SingleShot ? diff : group.Diff;
                        var groupLabel = plan.Groups.Count > 1
                            ? $"{group.Label} ({group.Files.Count} {(group.Files.Count == 1 ? "file" : "files")})"
                            : null;
                        var prompt = BuildPrompt(
                            source,
                            promptTruncated: plan.Skipped.Count > 0 && plan.Groups.Count == 1,
                            annotate: true,
                            groupLabel);
                        var raw = await Invoke(prompt, ct);
                        if (plan.Groups.Count > 1)
                        {
                            log.WriteLine(
                                $"Group {group.Number.ToString(CultureInfo.InvariantCulture)}/{plan.Groups.Count.ToString(CultureInfo.InvariantCulture)} ({group.Label}) finished.");
                        }

                        return raw;
                    },
                    cancellationToken);

                if (!batch.AnySuccess)
                {
                    if (plan.Groups.Count == 1)
                        return WriteSingleGroupFailure(batch.Outcomes[0], timeoutMinutes);

                    Console.Error.WriteLine("Error: No review group finished successfully.");
                    foreach (var outcome in batch.Outcomes)
                        Console.Error.WriteLine($"Error: Group {outcome.Group.Number} ({outcome.Group.Label}): {DescribeFailure(outcome, timeoutMinutes)}");
                    return 1;
                }

                var deferred = 0;
                foreach (var outcome in batch.Outcomes)
                {
                    if (outcome.Status == ReviewGroupStatus.Succeeded)
                        continue;

                    if (outcome.Status == ReviewGroupStatus.OverBudget)
                    {
                        deferred++;
                        continue;
                    }

                    var detail = DescribeFailure(outcome, timeoutMinutes);
                    if (!detail.EndsWith('.'))
                        detail += ".";
                    Console.Error.WriteLine(
                        $"Warning: Group {outcome.Group.Number} ({outcome.Group.Label}) {detail}");
                }

                if (deferred > 0)
                {
                    Console.Error.WriteLine(
                        $"Note: {deferred} group(s) were not sent because they would exceed --max-tokens-budget {tokenBudget.ToString(CultureInfo.InvariantCulture)}. Showing comments from the groups that finished.");
                }

                var partial = plan.Skipped.Count > 0 || batch.Outcomes.Any(outcome => outcome.Status != ReviewGroupStatus.Succeeded);
                var combined = ReviewParseResult.Ok(batch.Comments, batch.SkippedSchema, batch.OmittedByCap);
                return PrintParsed(combined, diffTruncated: false, partial, rawOnFailure: null);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("\nOperation was cancelled");
                return 1;
            }
            catch (TimeoutException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error calling AI API: {ex.Message}");
                return 1;
            }
        });
    }

    private static int WriteSingleGroupFailure(ReviewGroupOutcome outcome, int timeoutMinutes)
    {
        switch (outcome.Status)
        {
            case ReviewGroupStatus.TimedOut:
                Console.Error.WriteLine(
                    $"Error: The review timed out after {ReviewParallelLimits.FormatMinutes(timeoutMinutes)}. Raise --timeout (minutes) for a large diff.");
                return 1;
            case ReviewGroupStatus.ApiError:
                Console.Error.WriteLine($"Error calling AI API: {outcome.Error}");
                return 1;
            default:
                Console.Error.WriteLine($"Error: {outcome.Error}");
                if (outcome.Raw.Length > 0)
                {
                    Console.Error.WriteLine("Raw response:");
                    Console.Error.WriteLine(Excerpt(outcome.Raw));
                }

                return 1;
        }
    }

    private static string DescribeFailure(ReviewGroupOutcome outcome, int timeoutMinutes) => outcome.Status switch
    {
        ReviewGroupStatus.TimedOut => $"timed out after {ReviewParallelLimits.FormatMinutes(timeoutMinutes)}",
        ReviewGroupStatus.OverBudget => "was not sent because the token budget was reached",
        ReviewGroupStatus.ApiError => $"failed: {outcome.Error}",
        ReviewGroupStatus.ParseError => $"was skipped: {outcome.Error}",
        _ => outcome.Error
    };

    private static bool TryParseMaxRounds(string? text, out int? rounds, out string error)
    {
        rounds = null;
        error = "";
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"Error: --max-rounds must be an integer from 1 to {ReviewAgentLimits.MaxRounds}.";
            return false;
        }

        rounds = parsed;
        return true;
    }

    private static void WriteGitError(string diffError)
    {
        var detail = diffError.Trim();
        if (detail.Length == 0)
            detail = "git exited with a non-zero status.";

        if (detail.StartsWith("Error:", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(detail);
            return;
        }

        Console.Error.WriteLine($"Error running git diff: {detail}");
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
