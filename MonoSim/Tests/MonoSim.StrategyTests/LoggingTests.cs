using Microsoft.Extensions.Logging;
using MonoSim;
using MonoSim.Logging;
using MonoSim.Models;
using Serilog.Events;
using System.Text.Json;

internal static class LoggingTests
{
    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("Bounded queue drains every structured event on disposal", () =>
            {
                using var files = new LogFiles();
                using (var session = new SimulationLogging(files.Options with { BufferSize = 1 }))
                {
                    var logger = session.Factory.CreateLogger("test");
                    for (var i = 1; i <= 2000; i++)
                    {
                        var entry = new SimulationLogEntry("simulation", i, 2, 3, 1, TurnPhase.PostRoll, "RentPaid", "a", "Rent", -12.5m, 100.25m);
                        logger.Log(LogLevel.Information, new EventId(1, entry.Event), entry, null, static (e, _) => e.Description);
                    }
                }
                var entries = files.Read();
                Check(entries.Length == 2000, "Lost queued events.");
                Check(entries.Select(e => e.GetProperty("Sequence").GetInt32()).SequenceEqual(Enumerable.Range(1, 2000)), "Event order changed.");
                var first = entries[0];
                Check(first.GetProperty("Amount").GetDecimal() == -12.5m && first.GetProperty("CashBalance").GetDecimal() == 100.25m, "Financial values were not numeric properties.");
                Check(first.GetProperty("Phase").GetString() == "PostRoll" && first.GetProperty("SimulationId").GetString() == "simulation", "Lost simulation fields.");
                Check(first.TryGetProperty("@t", out _) && first.TryGetProperty("@mt", out _) && first.GetProperty("SchemaVersion").GetInt32() == 1, "Missing compact JSON metadata.");
                Check(entries.Select(e => e.GetProperty("RunId").GetString()).Distinct().Count() == 1, "Run correlation changed.");
            }),
            ("Real engine log retains actions, transactions and final outcomes", () =>
            {
                using var files = new LogFiles();
                GameState final;
                using (var session = new SimulationLogging(files.Options))
                {
                    Player[] players = [new() { Id = "a", Name = "A", Strategy = "aggressive", CashBalance = 1500 }, new() { Id = "b", Name = "B", Strategy = "novice", CashBalance = 1500 }];
                    final = new MonoSimEngine(players, session.Factory.CreateLogger<MonoSimEngine>(),
                        new() { MaximumRounds = 2, RandomSeed = 37, DiceRoller = () => [1, 2] }).Play();
                }
                var entries = files.Read();
                Check(entries.Any(e => e.GetProperty("Event").GetString() == "GameCompleted"), "Missing final outcome.");
                Check(entries.Any(e => e.GetProperty("Amount").ValueKind == JsonValueKind.Number), "Missing transactions.");
                Check(entries.All(e => e.GetProperty("SimulationId").GetString() == final.SimulationId), "Wrong simulation ID.");
                Check(entries.Select(e => e.GetProperty("Sequence").GetInt32()).SequenceEqual(Enumerable.Range(1, entries.Length)), "Missing engine events.");
            }),
            ("Interleaved simulations keep separate rolled files across writer eviction", () =>
            {
                using var files = new LogFiles();
                string applicationPath;
                using (var session = new SimulationLogging(files.Options with { MaxOpenFiles = 1, BufferSize = 2, FileSizeLimitBytes = 256 }))
                {
                    applicationPath = session.FilePath;
                    var logger = session.Factory.CreateLogger("test");
                    logger.LogInformation("Application message");
                    for (var sequence = 1; sequence <= 20; sequence++)
                    {
                        foreach (var id in new[] { "first", "second", "third" })
                        {
                            logger.LogInformation("{SimulationId} {Sequence} {Payload}", id, sequence, new string('x', 300));
                            Check(session.GetSimulationFilePath(id) == Path.Combine(files.DirectoryPath, $"monosim-{id}", "events.clef"), "Wrong simulation folder.");
                        }
                    }
                }
                foreach (var id in new[] { "first", "second", "third" })
                {
                    var paths = Directory.GetFiles(Path.Combine(files.DirectoryPath, $"monosim-{id}"), "*.clef").Order(StringComparer.Ordinal).ToArray();
                    Check(paths.Length > 1, "Did not produce rolled files for this simulation.");
                    var entries = ReadPaths(paths);
                    Check(entries.Length == 20 && entries.All(e => e.GetProperty("SimulationId").GetString() == id), "Mixed simulations or lost events while reopening writers.");
                    Check(entries.Select(e => e.GetProperty("Sequence").GetInt32()).SequenceEqual(Enumerable.Range(1, 20)), "Simulation event order changed.");
                }
                Check(ReadPaths([applicationPath]).Single().GetProperty("@mt").GetString() == "Application message", "Application messages mixed with simulations.");
            }),
            ("Two engines in one session use their actual simulation IDs in folder names", () =>
            {
                using var files = new LogFiles();
                var ids = new List<string>();
                using (var session = new SimulationLogging(files.Options))
                {
                    for (var i = 0; i < 2; i++)
                    {
                        Player[] players = [new() { Id = "a", Name = "A", Strategy = "aggressive", CashBalance = 1500 }, new() { Id = "b", Name = "B", Strategy = "novice", CashBalance = 1500 }];
                        var final = new MonoSimEngine(players, session.Factory.CreateLogger<MonoSimEngine>(), new() { MaximumRounds = 1, RandomSeed = 37, DiceRoller = () => [1, 2] }).Play();
                        ids.Add(final.SimulationId!);
                    }
                }
                Check(ids.Distinct().Count() == 2, "Engine IDs were reused.");
                foreach (var id in ids)
                {
                    var entries = ReadPaths(Directory.GetFiles(Path.Combine(files.DirectoryPath, $"monosim-{id}"), "*.clef"));
                    Check(entries.All(e => e.GetProperty("SimulationId").GetString() == id) && entries.Any(e => e.GetProperty("Event").GetString() == "GameCompleted"), "Simulation folder does not match the final state.");
                }
            }),
            ("Retention is independent for each simulation", () =>
            {
                using var files = new LogFiles();
                using (var session = new SimulationLogging(files.Options with { RetainedFileCountLimit = 2, FileSizeLimitBytes = 256, MaxOpenFiles = 1 }))
                {
                    var logger = session.Factory.CreateLogger("test");
                    for (var sequence = 1; sequence <= 5; sequence++)
                    {
                        foreach (var id in new[] { "first", "second" })
                        {
                            logger.LogInformation("{SimulationId} {Sequence} {Payload}", id, sequence, new string('x', 300));
                        }
                    }
                }
                foreach (var id in new[] { "first", "second" })
                {
                    var paths = Directory.GetFiles(Path.Combine(files.DirectoryPath, $"monosim-{id}"), "*.clef").Order(StringComparer.Ordinal).ToArray();
                    Check(paths.Length == 2 && ReadPaths(paths).Select(e => e.GetProperty("Sequence").GetInt32()).SequenceEqual([4, 5]), "Retention affected another simulation or removed the wrong files.");
                }
            }),
            ("Simulation identifiers cannot escape their log directories", () =>
            {
                using var files = new LogFiles();
                using var session = new SimulationLogging(files.Options);
                foreach (var id in new[] { "../escape", "..\\escape", "", new string('a', 129) })
                {
                    ExpectFailure(() => session.GetSimulationFilePath(id));
                }
            }),
            ("Console shows progress and warnings while file retains information", () =>
            {
                using var files = new LogFiles();
                var stdout = Console.Out;
                var stderr = Console.Error;
                using var output = new StringWriter();
                using var errors = new StringWriter();
                try
                {
                    Console.SetOut(output);
                    Console.SetError(errors);
                    using var session = new SimulationLogging(files.Options with { ConsoleEnabled = true });
                    session.Factory.CreateLogger("test").LogInformation("Quiet event");
                    session.Factory.CreateLogger("test").LogWarning("Visible warning");
                    session.Factory.CreateLogger(SimulationLogging.ProgressCategory).LogInformation("Visible progress");
                }
                finally
                {
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                }
                Check(!output.ToString().Contains("Quiet event") && output.ToString().Contains("Visible progress"), "Console filtering failed.");
                Check(errors.ToString().Contains("Visible warning"), "Warning did not reach stderr.");
                Check(files.Read().Length == 3, "Console filtering affected the file.");
            }),
            ("Progress display can be disabled", () =>
            {
                using var files = new LogFiles();
                var stdout = Console.Out;
                using var output = new StringWriter();
                try
                {
                    Console.SetOut(output);
                    using var session = new SimulationLogging(files.Options with { ConsoleEnabled = true, ShowProgress = false });
                    session.Factory.CreateLogger(SimulationLogging.ProgressCategory).LogInformation("Hidden progress");
                }
                finally
                {
                    Console.SetOut(stdout);
                }
                Check(!output.ToString().Contains("Hidden progress") && files.Read().Length == 1, "Disabling progress lost the file record.");
            }),
            ("Size rolling retains more than the sink's default 31 files", () =>
            {
                using var files = new LogFiles();
                using (var session = new SimulationLogging(files.Options with { FileSizeLimitBytes = 256, BufferSize = 2 }))
                {
                    var logger = session.Factory.CreateLogger("test");
                    for (var i = 0; i < 40; i++)
                    {
                        logger.LogInformation("{Sequence} {Payload}", i, new string('x', 300));
                    }
                }
                Check(Directory.GetFiles(files.DirectoryPath, "*.clef", SearchOption.AllDirectories).Length > 31, "Did not exercise retention past 31 files.");
                Check(files.Read().Length == 40, "Rolled records were lost or deleted.");
            }),
            ("Handled exceptions survive async shutdown with stack details", () =>
            {
                using var files = new LogFiles();
                var session = new SimulationLogging(files.Options);
                try
                {
                    throw new InvalidOperationException("Example runtime error");
                }
                catch (Exception error)
                {
                    session.Factory.CreateLogger("test").LogError(error, "Simulation failed {SimulationId}", "failure");
                }
                finally
                {
                    session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    session.Dispose();
                }
                var entry = files.Read().Single();
                Check(entry.GetProperty("@x").GetString()!.Contains("Example runtime error") && entry.GetProperty("SimulationId").GetString() == "failure", "Lost error details during shutdown.");
            }),
            ("Configuration precedence is JSON, environment, then command line", () =>
            {
                using var files = new LogFiles();
                var settings = Path.Combine(files.DirectoryPath, "settings.json");
                File.WriteAllText(settings, """{"Logging":{"BufferSize":17,"FileMinimumLevel":"Debug"}}""");
                const string key = "MONOSIM_Logging__BufferSize";
                var previous = Environment.GetEnvironmentVariable(key);
                try
                {
                    Environment.SetEnvironmentVariable(key, "23");
                    var options = LoggingOptions.Load(settings);
                    Check(options.BufferSize == 23 && options.FileMinimumLevel == LogEventLevel.Debug, "Wrong configuration precedence.");
                    options = LoggingOptions.Load(arguments: ["--log-config", settings, "--log-buffer-size", "31", "--log-progress=false"]);
                    Check(options.BufferSize == 31 && !options.ShowProgress && options.FileMinimumLevel == LogEventLevel.Debug, "Command-line settings did not take precedence.");
                    ExpectFailure(() => LoggingOptions.Load(settings, ["--unknown-log-setting", "true"]));
                }
                finally
                {
                    Environment.SetEnvironmentVariable(key, previous);
                }
            }),
            ("Invalid logging configuration and inaccessible destinations fail at startup", () =>
            {
                using var files = new LogFiles();
                foreach (var options in new[] { files.Options with { BufferSize = 0 }, files.Options with { FileSizeLimitBytes = 0 }, files.Options with { RetainedFileCountLimit = 0 }, files.Options with { FilePath = "" }, files.Options with { ConsoleMinimumLevel = (LogEventLevel)100 }, files.Options with { MaxOpenFiles = 0 } })
                {
                    ExpectFailure(() => new SimulationLogging(options));
                }
                var blocker = Path.Combine(files.DirectoryPath, "blocker");
                File.WriteAllText(blocker, "file");
                ExpectFailure(() => new SimulationLogging(files.Options with { FilePath = Path.Combine(blocker, "events.clef") }));
            })
        };
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS Logging: {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL Logging: {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} logging checks passed.");
        return failures;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
    private static JsonElement[] ReadPaths(IEnumerable<string> paths) => paths.SelectMany(File.ReadLines).Select(line =>
    {
        using var json = JsonDocument.Parse(line);
        return json.RootElement.Clone();
    }).ToArray();
    private static void ExpectFailure(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return;
        }
        throw new Exception("Expected logging setup to fail.");
    }
    private sealed class LogFiles : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "logging-tests-" + Guid.NewGuid().ToString("N"));
        public LoggingOptions Options => new() { FilePath = Path.Combine(DirectoryPath, "events.clef"), ConsoleEnabled = false };
        public LogFiles() => Directory.CreateDirectory(DirectoryPath);
        public JsonElement[] Read() => ReadPaths(Directory.GetFiles(DirectoryPath, "*.clef", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        public void Dispose()
        {
            var resolved = Path.GetFullPath(DirectoryPath);
            var expectedRoot = Path.GetFullPath(AppContext.BaseDirectory);
            if (!resolved.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("logging-tests-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the test output folder.");
            }
            Directory.Delete(resolved, recursive: true);
        }
    }
}
