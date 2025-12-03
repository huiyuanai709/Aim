using System.ClientModel;
using System.CommandLine;
using OpenAI;
using OpenAI.Chat;

namespace Aim;

public class CommitCommand : Command
{
    public CommitCommand() : base("commit", "Git commit with AI generated messages")
    {
        Option<bool?> addAllOption = new("--all", "-a")
        {
            Description = "Run git add . then commit all with AI generated messages"
        };

        Option<bool?> amendOption = new("--amend")
        {
            Description = "Generate message for amending the last commit"
        };

        Options.Add(addAllOption);
        Options.Add(amendOption);

        SetAction(async (parseResult, cancellationToken) =>
        {
            var addAll = parseResult.GetValue(addAllOption) == true;
            if (addAll)
            {
                GitExecutor.RunGitCommand("add .");
            }

            var config = ConfigManager.LoadConfig();

            var apiKey = config.ApiKey;

            if (string.IsNullOrEmpty(apiKey))
            {
                Console.Error.WriteLine("Error: OpenAI API key is required.");
                Console.Error.WriteLine("Provide via: --api-key, config file.");
                Console.Error.WriteLine("Run 'aim config --set apikey=YOUR_KEY' to save it.");
                return 1;
            }

            var isAmend = parseResult.GetValue(amendOption) == true;
            var model = config.Model;
            var autoCommit = config.AutoCommit;
            var diffNameOnly = config.DiffNameOnly;

            var gitArgs = "diff --cached";

            if (diffNameOnly)
            {
                gitArgs += " --name-only";
            }


            var (success, diff, error) = GitExecutor.RunGitCommand(gitArgs);

            if (isAmend)
            {
                gitArgs = "show --pretty=format:";
                gitArgs += config.DiffNameOnly ? " --name-only" : "";
                diff += GitExecutor.RunGitCommand(gitArgs).output;
            }

            if (!success)
            {
                Console.Error.WriteLine($"Error running git {gitArgs}: {error}");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(diff))
            {
                Console.Error.WriteLine("No changes to commit. Run 'git add' first.");
                return 1;
            }

            var prompt =
                $@"You are a helpful assistant that generates concise, semantic commit messages based on Git diff.
Rules:
- Keep it under {config.MaxSubjectLength} characters for the subject line.
- Use imperative mood (e.g., 'Fix bug' not 'Fixed bug').
- Reference issues if mentioned in diff.
- Structure: Subject line Body if needed.

Git diff:
{diff}

Generate only the commit message, nothing else.";

            var endpoint = config.ApiEndpoint ?? "https://api.openai.com/v1";
            var openAI = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) }
            );

            var chatClient = openAI.GetChatClient(model);

            try
            {
                Console.WriteLine("Generating commit message...");
                var response = await chatClient.CompleteChatAsync([
                        new UserChatMessage(prompt)
                    ],
                    null,
                    cancellationToken);
                var commitMessage = response.Value.Content[0].Text;

                if (string.IsNullOrEmpty(commitMessage))
                {
                    Console.Error.WriteLine("Failed to generate commit message");
                    return 1;
                }

                Console.WriteLine("\nGenerated commit message:");
                Console.WriteLine(new string('-', 60));
                Console.WriteLine(commitMessage.Trim());
                Console.WriteLine(new string('-', 60));

                if (autoCommit)
                {
                    if (!GitExecutor.GitCommit(commitMessage, isAmend))
                        return 1;
                }
                else
                {
                    if (isAmend)
                        Console.WriteLine(
                            $"\nTo use: git commit --amend -m \"{commitMessage}\"");
                    else
                        Console.WriteLine($"\nTo use: git commit -m \"{commitMessage}\"");

                    Console.WriteLine("Or run: aim --auto-commit");
                }
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

            return 0;
        });
    }
}