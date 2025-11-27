using System.CommandLine;
using Aim;

RootCommand rootCommand = new("AIM: Git Intelligence Message - Automatically generate commit messages using AI")
{
    new ConfigCommand(),
    new CommitCommand()
};
return await rootCommand.Parse(args).InvokeAsync();