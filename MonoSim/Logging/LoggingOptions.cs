using Microsoft.Extensions.Configuration;
using Serilog.Events;

namespace MonoSim.Logging;

/// <summary>Application-owned logging settings, ready for later command-line overrides.</summary>
public sealed record LoggingOptions
{
    private static readonly Dictionary<string, string> Switches = new(StringComparer.Ordinal)
    {
        ["--log-config"] = "LoggingConfigPath",
        ["--logroot"] = "Logging:RootDirectory",
        ["--log-level"] = "Logging:FileMinimumLevel",
        ["--log-console-level"] = "Logging:ConsoleMinimumLevel",
        ["--log-console-enabled"] = "Logging:ConsoleEnabled",
        ["--log-progress"] = "Logging:ShowProgress",
        ["--log-buffer-size"] = "Logging:BufferSize",
        ["--log-file-size-limit"] = "Logging:FileSizeLimitBytes",
        ["--log-retained-files"] = "Logging:RetainedFileCountLimit",
        ["--log-max-open-files"] = "Logging:MaxOpenFiles"
    };
    /// <summary>Parent directory and base filename; each simulation gets an ID subdirectory.</summary>
    public string FilePath { get; init; } = "logs/run.clef";
    public string? RootDirectory { get; init; }
    public LogEventLevel FileMinimumLevel { get; init; } = LogEventLevel.Information;
    public LogEventLevel ConsoleMinimumLevel { get; init; } = LogEventLevel.Warning;
    public bool ConsoleEnabled { get; init; } = true;
    public bool ShowProgress { get; init; } = true;
    public int BufferSize { get; init; } = 10000;
    public long FileSizeLimitBytes { get; init; } = 64 * 1024 * 1024;
    public int? RetainedFileCountLimit { get; init; }
    public int MaxOpenFiles { get; init; } = 8;

    public static bool IsCommandLineOption(string option) => Switches.ContainsKey(option);

    public static LoggingOptions Load(string? settingsPath = null, string[]? arguments = null)
    {
        arguments ??= [];
        foreach (var argument in arguments.Where(a => a.StartsWith('-')))
        {
            var key = argument.Split('=', 2)[0];
            if (!Switches.ContainsKey(key) && !int.TryParse(argument, out _))
            {
                throw new ArgumentException($"Unknown logging option: {key}.");
            }
        }
        var commandLine = new ConfigurationBuilder().AddCommandLine(arguments, Switches).Build();
        using var commandLineLifetime = commandLine as IDisposable;
        settingsPath = commandLine["LoggingConfigPath"] ?? settingsPath;
        settingsPath ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(settingsPath), optional: false, reloadOnChange: false)
            .AddEnvironmentVariables("MONOSIM_")
            .AddConfiguration(commandLine)
            .Build();
        using var configurationLifetime = configuration as IDisposable;
        var options = configuration.GetSection("Logging").Get<LoggingOptions>() ?? new();
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (RootDirectory is not null && string.IsNullOrWhiteSpace(RootDirectory))
        {
            throw new ArgumentException("Logging:RootDirectory cannot be empty.");
        }
        if (string.IsNullOrWhiteSpace(FilePath) || string.IsNullOrEmpty(Path.GetFileName(FilePath)))
        {
            throw new ArgumentException("Logging:FilePath must name a log file.");
        }
        if (BufferSize <= 0 || FileSizeLimitBytes <= 0 || RetainedFileCountLimit is <= 0 || MaxOpenFiles <= 0)
        {
            throw new ArgumentException("Logging buffer size, file size limit, open file limit, and any retention count must be positive.");
        }
        if (!Enum.IsDefined(FileMinimumLevel) || !Enum.IsDefined(ConsoleMinimumLevel))
        {
            throw new ArgumentException("Logging levels must be valid Serilog levels.");
        }
    }
}
