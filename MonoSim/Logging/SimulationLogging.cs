using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;

namespace MonoSim.Logging;

/// <summary>Owns one Serilog pipeline. Dispose after all producers stop to drain and flush it.</summary>
public sealed class SimulationLogging : IDisposable, IAsyncDisposable
{
    public const string ProgressCategory = "MonoSim.Progress";
    private readonly Logger logger;
    private readonly SerilogLoggerFactory factory;
    private readonly string logDirectory;
    private readonly string baseFileName;
    private int disposed;

    public string RunId { get; } = Guid.NewGuid().ToString("N");
    /// <summary>Application log path. Simulation events use GetSimulationFilePath instead.</summary>
    public string FilePath { get; }
    public ILoggerFactory Factory => factory;

    public SimulationLogging(LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var configuredPath = Path.GetFullPath(options.FilePath);
        if (options.RootDirectory is not null)
        {
            configuredPath = Path.GetFullPath(Path.Combine(options.RootDirectory, "run.clef"));
        }
        logDirectory = Path.GetDirectoryName(configuredPath)!;
        baseFileName = Path.GetFileName(configuredPath);
        FilePath = Path.Combine(logDirectory, $"application-{RunId}", baseFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // Surface ordinary path/access errors before Serilog's sinks can report them through SelfLog.
        using (new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
        {
        }
        var minimum = options.FileMinimumLevel;
        if (options.ConsoleEnabled)
        {
            minimum = (LogEventLevel)Math.Min((int)minimum, (int)options.ConsoleMinimumLevel);
            if (options.ShowProgress)
            {
                minimum = (LogEventLevel)Math.Min((int)minimum, (int)LogEventLevel.Information);
            }
        }
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(minimum)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("RunId", RunId)
            .Enrich.WithProperty("SchemaVersion", 1)
            .WriteTo.Logger(file => file.MinimumLevel.Is(options.FileMinimumLevel)
                .WriteTo.Async(sink => sink.Map(EventFilePath, (path, target) =>
                    target.File(new CompactJsonFormatter(), path,
                        fileSizeLimitBytes: options.FileSizeLimitBytes, rollOnFileSizeLimit: true,
                        retainedFileCountLimit: options.RetainedFileCountLimit),
                    sinkMapCountLimit: options.MaxOpenFiles),
                    bufferSize: options.BufferSize, blockWhenFull: true));
        if (options.ConsoleEnabled)
        {
            configuration.WriteTo.Logger(console => console
                .Filter.ByIncludingOnly(entry => entry.Level >= options.ConsoleMinimumLevel ||
                    options.ShowProgress && entry.Level >= LogEventLevel.Information &&
                    entry.Properties.TryGetValue("SourceContext", out var source) &&
                    source is ScalarValue { Value: ProgressCategory })
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}",
                    standardErrorFromLevel: LogEventLevel.Warning));
        }
        logger = configuration.CreateLogger();
        factory = new SerilogLoggerFactory(logger, dispose: false);
    }

    public string GetSimulationFilePath(string simulationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(simulationId);
        if (simulationId.Length > 128 || simulationId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
        {
            throw new ArgumentException("Simulation IDs must contain at most 128 ASCII letters, digits, hyphens or underscores.", nameof(simulationId));
        }
        return Path.Combine(logDirectory, $"monosim-{simulationId}", baseFileName);
    }

    private string EventFilePath(LogEvent entry)
    {
        if (entry.Properties.TryGetValue("SimulationId", out var value) && value is ScalarValue { Value: string simulationId })
        {
            return GetSimulationFilePath(simulationId);
        }
        return FilePath;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            factory.Dispose();
            logger.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            factory.Dispose();
            await logger.DisposeAsync();
        }
    }
}
