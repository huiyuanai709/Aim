using System.CommandLine;
using Aim;

RootCommand rootCommand = new("AIM: AI commit messages and diff review for Git")
{
    new ConfigCommand(),
    new CommitCommand(),
    new ReviewCommand()
};
return await rootCommand.Parse(args).InvokeAsync();