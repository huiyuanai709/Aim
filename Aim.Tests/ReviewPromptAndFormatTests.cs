using System.Text.Json;
using Aim;

namespace Aim.Tests;

public class ReviewPromptAndFormatTests
{
    [Fact]
    public void PromptCarriesChecklistContextAndDoesNotForceALanguage()
    {
        var prompt = ReviewPrompt.Build(new ReviewPromptInput
        {
            Diff = "diff --git a/a.cs b/a.cs\n+var x = 1;\n",
            DiffLabel = "staged changes (git diff --cached)",
            NameOnly = false,
            Truncated = true,
            MaxComments = 7,
            Background = "Billing service. Prefer explicit errors.",
            ExtraRules = "Flag swallowed exceptions."
        });

        Assert.Contains("Correctness:", prompt);
        Assert.Contains("Security:", prompt);
        Assert.Contains("Performance:", prompt);
        Assert.Contains("Maintainability:", prompt);
        Assert.Contains("Do not assume Chinese or English", prompt);
        Assert.Contains("will not be relocated", prompt);
        Assert.Contains("at most 7 comments", prompt);
        Assert.Contains("<background>", prompt);
        Assert.Contains("Billing service. Prefer explicit errors.", prompt);
        Assert.Contains("<rules>", prompt);
        Assert.Contains("Flag swallowed exceptions.", prompt);
        Assert.Contains("was truncated", prompt);
        Assert.Contains("suggestion_code", prompt);
        Assert.DoesNotContain("file_read", prompt);
    }

    [Fact]
    public void AgentPromptMentionsToolsAndKeepsTheJsonContract()
    {
        var prompt = ReviewPrompt.Build(new ReviewPromptInput
        {
            Diff = "diff --git a/a.cs b/a.cs\n+var x = 1;\n",
            DiffLabel = "staged changes (git diff --cached)",
            NameOnly = false,
            Truncated = false,
            MaxComments = 20,
            AgentTools = true
        });

        Assert.Contains("file_read", prompt);
        Assert.Contains("code_search", prompt);
        Assert.Contains("only the JSON array", prompt);

        var instructions = ReviewPrompt.AgentInstructions(nameOnly: false);
        Assert.Contains("file_read", instructions);
        Assert.Contains("code_search", instructions);
        Assert.Contains("JSON array", instructions);
        Assert.DoesNotContain("Only file names", instructions);

        var nameOnly = ReviewPrompt.AgentInstructions(nameOnly: true);
        Assert.Contains("file_read", nameOnly);
        Assert.Contains("file names only", nameOnly);
        Assert.DoesNotContain("load_skill", nameOnly);

        var withSkills = ReviewPrompt.AgentInstructions(nameOnly: false, skills: true);
        Assert.Contains("load_skill", withSkills);
        Assert.Contains("read_skill_resource", withSkills);
        Assert.Contains("run_skill_script", withSkills);
    }

    [Fact]
    public void NameOnlyPromptSaysLinesAreUnavailable()
    {
        var prompt = ReviewPrompt.Build(new ReviewPromptInput
        {
            Diff = "src/Pay.cs\n",
            DiffLabel = "staged changes (git diff --cached), file names only",
            NameOnly = true,
            Truncated = false,
            MaxComments = 20
        });

        Assert.Contains("Only file names are available", prompt);
        Assert.DoesNotContain("was truncated", prompt);
    }

    [Fact]
    public void TextFooterDocumentsBestEffortLines()
    {
        var text = ReviewFormatter.ToText(
        [
            new ReviewComment
            {
                Path = "src/App.cs",
                StartLine = 12,
                EndLine = 18,
                Severity = "high",
                Category = "bug",
                Content = "The last item is skipped.",
                SuggestionCode = "while (index < items.Count)\n{\n    index++;\n}"
            }
        ], diffTruncated: false, nameOnly: false);

        Assert.Contains("src/App.cs:12-18  high  bug", text);
        Assert.Contains("  The last item is skipped.", text);
        Assert.Contains("    while (index < items.Count)", text);
        Assert.Contains("best-effort (not relocated)", text);
        Assert.Contains("1 comment.", text);
    }

    [Fact]
    public void JsonOmitsEmptySuggestions()
    {
        var json = ReviewFormatter.ToJson(
        [
            new ReviewComment
            {
                Path = "a.cs",
                StartLine = 3,
                EndLine = 3,
                Severity = "low",
                Category = "style",
                Content = "Name hides the unit."
            }
        ]);

        using var doc = JsonDocument.Parse(json);
        var item = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("a.cs", item.GetProperty("path").GetString());
        Assert.Equal(3, item.GetProperty("start_line").GetInt32());
        Assert.Equal("low", item.GetProperty("severity").GetString());
        Assert.Equal("style", item.GetProperty("category").GetString());
        Assert.False(item.TryGetProperty("suggestion_code", out _));
    }

    [Fact]
    public void EmptyTextDependsOnTruncation()
    {
        Assert.Equal("No review comments.", ReviewFormatter.ToText([], false, false));
        Assert.Equal(
            "No review comments for the portion of the diff that was sent.",
            ReviewFormatter.ToText([], true, false));
    }
}
