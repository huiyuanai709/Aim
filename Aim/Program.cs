using System.Text;
using Aim;
using Ngrep.Output;

if (args.Length > 0 && args[0] == "search")
{
    var forwarded = args.Length == 1 ? [] : args[1..];
    if (!SearchCommand.WantsHelp(forwarded))
    {
        Console.OutputEncoding = Encoding.UTF8;
        Stdout.Install();
    }

    return await SearchCommand.Invoke(forwarded);
}

return await CliRoot.Create().Parse(args).InvokeAsync();
