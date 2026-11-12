using Microsoft.Extensions.Logging;
using MonoSim.Logging;
using Serilog.Debugging;

namespace MonoSim.Cli;

public static class CliApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            Console.Error.WriteLine($"Invalid arguments: {error.Message}\nUse --help for usage.");
            return 2;
        }
        if (options.ShowHelp)
        {
            Console.WriteLine(CliOptions.Help);
            return 0;
        }
        if (options.ShowVersion)
        {
            Console.WriteLine($"MonoSim {typeof(CliApplication).Assembly.GetName().Version}");
            return 0;
        }

        SimulationLogging? logging = null;
        ILogger? logger = null;
        var exitCode = 0;
        var loggingFailed = 0;
        var consoleEnabled = false;
        var diagnostics = TextWriter.Synchronized(Console.Error);
        using var cancelled = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cancelled.Cancel();
        };
        Console.CancelKeyPress += handler;
        SelfLog.Enable(message =>
        {
            Interlocked.Exchange(ref loggingFailed, 1);
            diagnostics.WriteLine(message);
        });
        try
        {
            var settings = LoggingOptions.Load(arguments: options.LoggingArguments);
            consoleEnabled = settings.ConsoleEnabled;
            logging = new SimulationLogging(settings);
            logger = logging.Factory.CreateLogger("MonoSim.Application");
            logger.LogInformation("Application started. Run {RunId}; application log {LogPath}.", logging.RunId, logging.FilePath);
            var definition = SimulationDefinition.Load(options.DefinitionPath);
            using var results = ResultsWriter.Open(options.OutputPath, options.Delimiter, Console.In, Console.Out, !Console.IsInputRedirected);
            var batch = await new SimulationBatchRunner().RunAsync(options, definition, results, logging, cancelled.Token);
            if (batch.Failed > 0)
            {
                exitCode = 1;
            }
            logger.LogInformation("Application completed with exit code {ExitCode}.", exitCode);
        }
        catch (OperationCanceledException)
        {
            logger?.LogWarning("Application cancelled; completed results are preserved.");
            Console.Error.WriteLine("MonoSim cancelled; completed results are preserved.");
            exitCode = 130;
        }
        catch (Exception error)
        {
            logger?.LogCritical(error, "Application failed: {ErrorMessage}", error.Message);
            if (logger is null || !consoleEnabled)
            {
                Console.Error.WriteLine($"MonoSim failed: {error.Message}");
            }
            exitCode = 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            try
            {
                if (logging is not null)
                {
                    await logging.DisposeAsync();
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Unable to flush logging: {error.Message}");
                exitCode = 1;
            }
            finally
            {
                SelfLog.Disable();
                if (Volatile.Read(ref loggingFailed) != 0)
                {
                    exitCode = 1;
                }
            }
        }
        return exitCode;
    }
}
