using System.Text;
using Ngrep.Output;

namespace Ngrep;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Async file reads park work on the pool. Keep at least two workers per
        // CPU so a read completion is not waiting on thread-pool injection.
        ThreadPool.GetMinThreads(out var workers, out var io);
        var min = Math.Max(workers, Environment.ProcessorCount * 2);
        ThreadPool.SetMinThreads(min, Math.Max(io, Environment.ProcessorCount));

        // Set the encoding before installing stdout: the setter rebuilds Console.Out
        // on a terminal. Install flushes a terminal on each write and keeps a 64 KiB
        // block for a pipe or file. It runs here, not in NgrepCli.Run, so a host that
        // calls Run directly keeps its own Console.Out (tests capture a StringWriter).
        Console.OutputEncoding = Encoding.UTF8;
        Stdout.Install();
        try
        {
            return await Cli.NgrepCli.Run(args);
        }
        finally
        {
            Stdout.Flush();
        }
    }
}
