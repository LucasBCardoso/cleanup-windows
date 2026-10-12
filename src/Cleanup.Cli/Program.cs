using System.Text;
using Cleanup.Cli;

// cleanup.exe — same commands and flags as the macOS CLI. Everything is a dry run unless --yes.
Console.OutputEncoding = Encoding.UTF8;
try
{
    var command = CommandLine.Parse(args);
    return await new CommandRunner(Console.Out).RunAsync(command);
}
catch (UsageException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    Console.Error.WriteLine("Run `cleanup help` for usage.");
    return 64;
}
catch (Exception e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}
