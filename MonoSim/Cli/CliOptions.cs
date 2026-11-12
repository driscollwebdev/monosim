using System.Globalization;
using MonoSim.Logging;

namespace MonoSim.Cli;

public sealed record CliOptions
{
    public static IReadOnlyList<string> StrategyNames { get; } = Array.AsReadOnly(new[] { "aggressive", "conservative", "infrastructure", "adjacent-group", "blue-green", "novice" });
    public int PlayerCount { get; init; }
    public string[] Strategies { get; init; } = [];
    public int Simulations { get; init; } = 1;
    public int MaximumRounds { get; init; }
    public int? Seed { get; init; }
    public string DefinitionPath { get; init; } = new MonoSimEngineOptions().DefinitionPath;
    public string OutputPath { get; init; } = "results.csv";
    public char Delimiter { get; init; } = ',';
    public bool ShuffleDecks { get; init; } = true;
    public bool StopOnError { get; init; }
    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }
    public string[] LoggingArguments { get; init; } = [];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-s"] = "--simulations", ["-d"] = "--definition", ["-l"] = "--logroot",
        ["-r"] = "--maxrounds", ["-o"] = "--output"
    };
    private static readonly HashSet<string> MainFlags = ["--simulations", "--definition", "--maxrounds", "--output", "--delimiter", "--seed", "--shuffle-decks", "--on-error"];

    public static CliOptions Parse(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h"))
        {
            return new() { ShowHelp = true };
        }
        if (args.Contains("--version"))
        {
            return new() { ShowVersion = true };
        }
        var positional = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var logging = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith('-'))
            {
                positional.Add(argument);
                continue;
            }
            var pair = argument.Split('=', 2);
            var key = Aliases.GetValueOrDefault(pair[0], pair[0]);
            if (!MainFlags.Contains(key) && !LoggingOptions.IsCommandLineOption(key))
            {
                throw new ArgumentException($"Unknown option: {pair[0]}.");
            }
            string value;
            if (pair.Length == 2)
            {
                value = pair[1];
            }
            else
            {
                if (++i >= args.Length || args[i].StartsWith('-') && !int.TryParse(args[i], out _))
                {
                    throw new ArgumentException($"Option {key} requires a value.");
                }
                value = args[i];
            }
            if (string.IsNullOrWhiteSpace(value) || !values.TryAdd(key, value))
            {
                throw new ArgumentException($"Option {key} must have one nonempty value and cannot be repeated.");
            }
            if (LoggingOptions.IsCommandLineOption(key))
            {
                logging.Add($"{key}={value}");
            }
        }
        if (positional.Count != 2)
        {
            throw new ArgumentException("Specify player count and a comma-delimited strategy list.");
        }
        var count = Positive(positional[0], "players");
        if (count is < 2 or > 6)
        {
            throw new ArgumentException("Player count must be between 2 and 6.");
        }
        var strategies = positional[1].Split(',').Select(s => s.Trim().ToLowerInvariant()).ToArray();
        if (strategies.Length > count || strategies.Any(s => s != "random" && !StrategyNames.Contains(s)))
        {
            throw new ArgumentException("Specify one through player-count strategies: " + string.Join(", ", StrategyNames) + ", random.");
        }
        var delimiter = values.GetValueOrDefault("--delimiter", ",");
        if (delimiter is "tab" or "\\t")
        {
            delimiter = "\t";
        }
        if (delimiter.Length != 1 || delimiter[0] is '\r' or '\n' or '"' or '\0')
        {
            throw new ArgumentException("Delimiter must be one character (excluding quotes, NUL, CR and LF), or 'tab'.");
        }
        var onError = values.GetValueOrDefault("--on-error", "continue").ToLowerInvariant();
        if (onError is not ("stop" or "continue"))
        {
            throw new ArgumentException("--on-error must be stop or continue.");
        }
        int? seed = null;
        if (values.TryGetValue("--seed", out var seedText))
        {
            if (!int.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeed))
            {
                throw new ArgumentException("--seed must be a 32-bit integer.");
            }
            seed = parsedSeed;
        }
        var shuffle = true;
        if (values.TryGetValue("--shuffle-decks", out var shuffleText) && !bool.TryParse(shuffleText, out shuffle))
        {
            throw new ArgumentException("--shuffle-decks must be true or false.");
        }
        return new()
        {
            PlayerCount = count, Strategies = strategies,
            Simulations = Positive(values.GetValueOrDefault("--simulations", "1"), "--simulations"),
            MaximumRounds = Positive(values.GetValueOrDefault("--maxrounds", (25 * count).ToString(CultureInfo.InvariantCulture)), "--maxrounds"),
            Seed = seed, DefinitionPath = Path.GetFullPath(values.GetValueOrDefault("--definition", new MonoSimEngineOptions().DefinitionPath)),
            OutputPath = Path.GetFullPath(values.GetValueOrDefault("--output", "results.csv")), Delimiter = delimiter[0],
            ShuffleDecks = shuffle, StopOnError = onError == "stop", LoggingArguments = logging.ToArray()
        };
    }

    private static int Positive(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            throw new ArgumentException($"{name} must be a positive 32-bit integer.");
        }
        return number;
    }

    public const string Help = """
        Usage: monosim <players> <strategies> [options]
          players                  Required integer, 2 through 6.
          strategies               Comma-delimited names; repeat the last for remaining players.
                                   aggressive, conservative, infrastructure, adjacent-group,
                                   blue-green, novice, random. Random is resolved per player/game.
          -s, --simulations N       Games to run (default 1).
          -r, --maxrounds N         Round cap per game (default 25 * players).
          -d, --definition PATH     Custom game definition (default bundled definition).
          -l, --logroot PATH        Root log folder (default logs).
          -o, --output PATH         Results file (default results.csv).
              --delimiter CHAR     Field separator, or tab (default comma).
              --seed N             Reproducible batch seed (default generated).
              --shuffle-decks BOOL Shuffle decks (default true).
              --on-error MODE      continue (default) or stop; active games finish on stop.
              --log-config PATH    Settings file (default executable's appsettings.json).
              --log-level LEVEL    File minimum level (default Information).
              --log-console-level LEVEL  Console minimum level (default Warning).
              --log-console-enabled BOOL Console enabled (default true).
              --log-progress BOOL  Progress enabled (default true).
              --log-buffer-size N  Shared queue capacity (default 10000).
              --log-file-size-limit N    Bytes per rolled file (default 67108864).
              --log-retained-files N     Files retained per simulation (default all).
              --log-max-open-files N     Maximum open writers (default 8).
          -h, --help               Show help without running simulations.
              --version            Show version without running simulations.
        Up to min(4, available processors) games run concurrently.
        Existing results require interactive append/overwrite/new-name selection.
        Option values accept --name=value or --name value. Booleans require true/false.
        Exit codes: 0 success, 1 runtime/file/simulation failure, 2 invalid arguments, 130 cancelled.
        """;
}
