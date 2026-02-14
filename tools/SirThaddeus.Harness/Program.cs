using SirThaddeus.Harness.Cli;
using SirThaddeus.Harness.Execution;

try
{
    var options = CommandLineParser.Parse(args);
    if (options.ShowHelp)
    {
        Console.WriteLine(CommandLineParser.HelpText);
        return;
    }

    var app = new HarnessApplication();
    var exitCode = await app.RunAsync(options, CancellationToken.None);
    Environment.ExitCode = exitCode;
}
catch (CommandLineException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLineParser.HelpText);
    Environment.ExitCode = 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Harness run cancelled.");
    Environment.ExitCode = 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Harness failed: {ex.Message}");
    Environment.ExitCode = 1;
}
