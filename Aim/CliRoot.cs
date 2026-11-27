using System.CommandLine;

namespace Aim;

internal static class CliRoot
{
    public static RootCommand Create() => new("AIM: AI commit messages, diff review, and code search for Git")
    {
        new ConfigCommand(),
        new CommitCommand(),
        new ReviewCommand(),
        new SearchCommand()
    };
}
