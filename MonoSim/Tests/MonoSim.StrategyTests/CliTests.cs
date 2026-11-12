using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MonoSim;
using MonoSim.Cli;
using MonoSim.Logging;
using MonoSim.Models;
using System.Text;
using System.Text.Json.Nodes;

internal static class CliTests
{
    private static readonly SimulationDefinition Definition = SimulationDefinition.Load(new MonoSimEngineOptions().DefinitionPath);

    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("Player range and default round caps", () =>
            {
                for (var count = 2; count <= 6; count++)
                {
                    var options = CliOptions.Parse([count.ToString(), "aggressive"]);
                    Check(options.MaximumRounds == count * 25 && options.Simulations == 1 && options.Delimiter == ',');
                }
                foreach (var value in new[] { "0", "1", "7", "abc", "2147483648" })
                {
                    Expect<ArgumentException>(() => CliOptions.Parse([value, "novice"]));
                }
            }),
            ("Strategy list validation and repeat-last assignment", () =>
            {
                var options = CliOptions.Parse(["4", "Aggressive, conservative"]);
                var players = SimulationBatchRunner.CreatePlayers(options, Definition, 1);
                Check(players.Select(p => p.Strategy).SequenceEqual(["aggressive", "conservative", "conservative", "conservative"]));
                options = CliOptions.Parse(["3", "aggressive,conservative,blue-green"]);
                Check(SimulationBatchRunner.CreatePlayers(options, Definition, 1).Select(p => p.Strategy).SequenceEqual(options.Strategies));
                foreach (var strategies in new[] { "", "novice,", ",novice", "novice,,aggressive", "unknown", "novice,novice,novice" })
                {
                    Expect<ArgumentException>(() => CliOptions.Parse(["2", strategies]));
                }
            }),
            ("Short aliases, equals syntax, negative seeds and logging forwarding", () =>
            {
                var options = CliOptions.Parse(["3", "novice", "-s", "5", "-r=9", "--seed", "-42", "-d", "custom.json", "-o", "custom.tsv", "--delimiter", "tab", "--shuffle-decks=false", "--on-error", "stop", "-l", "custom-logs", "--log-progress=false"]);
                Check(options.Simulations == 5 && options.MaximumRounds == 9 && options.Seed == -42 && options.Delimiter == '\t' && !options.ShuffleDecks && options.StopOnError);
                Check(options.DefinitionPath == Path.GetFullPath("custom.json") && options.OutputPath == Path.GetFullPath("custom.tsv"));
                Check(options.LoggingArguments.SequenceEqual(["--logroot=custom-logs", "--log-progress=false"]));
            }),
            ("Missing, duplicate, unknown and invalid option values fail", () =>
            {
                foreach (var args in new string[][]
                {
                    [], ["2"], ["2", "novice", "extra"], ["2", "novice", "--maxrounds"],
                    ["2", "novice", "--simulations", "0"], ["2", "novice", "-r", "-1"],
                    ["2", "novice", "--simulations=2147483648"], ["2", "novice", "--seed=no"],
                    ["2", "novice", "--shuffle-decks=maybe"], ["2", "novice", "--on-error=ignore"],
                    ["2", "novice", "--delimiter=xx"], ["2", "novice", "--delimiter=\""],
                    ["2", "novice", "--unknown=1"], ["2", "novice", "--log-file=x"],
                    ["2", "novice", "-s", "1", "--simulations", "2"]
                })
                {
                    Expect<ArgumentException>(() => CliOptions.Parse(args));
                }
            }),
            ("Help and version need no positional arguments", () =>
            {
                Check(CliOptions.Parse(["--help"]).ShowHelp && CliOptions.Parse(["-h"]).ShowHelp && CliOptions.Parse(["--version"]).ShowVersion);
            }),
            ("Random strategies are reproducible and resolved independently", () =>
            {
                var options = CliOptions.Parse(["6", "random"]);
                var first = SimulationBatchRunner.CreatePlayers(options, Definition, 42);
                var second = SimulationBatchRunner.CreatePlayers(options, Definition, 42);
                Check(first.Select(p => p.Strategy).SequenceEqual(second.Select(p => p.Strategy)));
                var assignments = Enumerable.Range(1, 30).SelectMany(seed => SimulationBatchRunner.CreatePlayers(options, Definition, seed)).ToArray();
                Check(assignments.All(p => CliOptions.StrategyNames.Contains(p.Strategy)) && assignments.Select(p => p.Strategy).Distinct().Count() == 6);
                Check(first.Select(p => p.Id).Distinct().Count() == 6 && first.All(p => p.CashBalance == Definition.StartingCash));
            }),
            ("Custom definition supplies starting cash and position", () =>
            {
                using var files = new Files();
                var path = Path.Combine(files.Root, "definition.json");
                var json = JsonNode.Parse(File.ReadAllText(new MonoSimEngineOptions().DefinitionPath))!;
                json["initialState"]!["playerCash"] = 1234;
                json["initialState"]!["playerPositionId"] = "baltic-avenue";
                File.WriteAllText(path, json.ToJsonString());
                var custom = SimulationDefinition.Load(path);
                var players = SimulationBatchRunner.CreatePlayers(CliOptions.Parse(["2", "novice"]), custom, 1);
                Check(players.All(p => p.CashBalance == 1234 && p.BoardPosition == 3));
            }),
            ("Delimited fields round-trip quotes, separators, Unicode and newlines", () =>
            {
                foreach (var delimiter in new[] { ',', ';', '\t', '|' })
                {
                    string[] fields = ["plain", "", "quoted \"value\"", "comma,semicolon;tab\tpipe|", "line1\r\nline2", "caf\u00e9"];
                    var text = DelimitedText.Format(fields, delimiter);
                    Check(DelimitedText.Read(new StringReader(text), delimiter).Single().SequenceEqual(fields));
                }
                foreach (var invalid in new[] { "\"unterminated", "\"value\"extra", "val\"ue" })
                {
                    Expect<InvalidDataException>(() => DelimitedText.Read(new StringReader(invalid), ',').ToArray());
                }
            }),
            ("New results and append without a trailing newline preserve one header", () =>
            {
                using var files = new Files();
                using (var writer = files.Open())
                {
                    writer.Write(Outcome());
                }
                File.WriteAllText(files.Output, File.ReadAllText(files.Output).TrimEnd('\r', '\n'));
                using (var writer = files.Open("a\n", interactive: true))
                {
                    writer.Write(Outcome(number: 2));
                }
                var rows = files.Rows();
                Check(rows.Length == 3 && rows[0].SequenceEqual(SimulationOutcome.Headers) && rows.All(r => r.Length == SimulationOutcome.Headers.Count));
            }),
            ("Overwrite and repeated new-name prompts honor the selected path", () =>
            {
                using var files = new Files();
                File.WriteAllText(files.Output, "existing");
                using (var writer = files.Open("o\n", interactive: true))
                {
                    writer.Write(Outcome());
                }
                Check(files.Rows().Length == 2);
                var next = Path.Combine(files.Root, "new.csv");
                using (var writer = files.Open($"n\n{files.Output}\nn\n{next}\n", interactive: true))
                {
                    Check(writer.FilePath == next);
                    writer.Write(Outcome());
                }
                Check(File.Exists(next) && files.Rows().Length == 2);
            }),
            ("Noninteractive conflicts and cancelled prompts preserve existing bytes", () =>
            {
                using var files = new Files();
                File.WriteAllText(files.Output, "keep me");
                Expect<IOException>(() => files.Open());
                Expect<OperationCanceledException>(() => files.Open("", interactive: true));
                Check(File.ReadAllText(files.Output) == "keep me");
            }),
            ("Append rejects wrong delimiters, bad headers and malformed rows before writing", () =>
            {
                using var files = new Files();
                foreach (var contents in new[]
                {
                    "wrong header\n", DelimitedText.Format(SimulationOutcome.Headers, ';') + "\n",
                    DelimitedText.Format(SimulationOutcome.Headers, ',') + "\nwrong,row\n",
                    DelimitedText.Format(SimulationOutcome.Headers, ',') + "\n\"unfinished"
                })
                {
                    File.WriteAllText(files.Output, contents);
                    Expect<InvalidDataException>(() => files.Open("a\n", interactive: true));
                    Check(File.ReadAllText(files.Output) == contents);
                }
            }),
            ("Empty-file append writes a header and UTF-8 BOM append is supported", () =>
            {
                using var files = new Files();
                File.WriteAllText(files.Output, "");
                using (var writer = files.Open("a\n", interactive: true))
                {
                    writer.Write(Outcome());
                }
                var text = File.ReadAllText(files.Output);
                File.WriteAllText(files.Output, text, new UTF8Encoding(true));
                using (var writer = files.Open("a\n", interactive: true))
                {
                    writer.Write(Outcome(number: 2));
                }
                Check(files.Rows().Length == 3);
                File.WriteAllText(files.Output, text, Encoding.Unicode);
                var before = File.ReadAllBytes(files.Output);
                Expect<InvalidDataException>(() => files.Open("a\n", interactive: true));
                Check(before.SequenceEqual(File.ReadAllBytes(files.Output)));
            }),
            ("Parallel batch keeps complete rows and respects hardware worker cap", () =>
            {
                using var files = new Files();
                var active = 0;
                var maximum = 0;
                var sync = new object();
                var runner = new SimulationBatchRunner((players, _, options) =>
                {
                    var current = Interlocked.Increment(ref active);
                    lock (sync)
                    {
                        maximum = Math.Max(maximum, current);
                    }
                    Thread.Sleep(20);
                    Interlocked.Decrement(ref active);
                    return Final(players, options);
                });
                var batch = Run(files, runner, simulations: 12);
                Check(batch.Completed == 12 && batch.Failed == 0 && batch.Workers == Math.Min(4, Environment.ProcessorCount));
                Check(maximum >= 1 && maximum <= batch.Workers);
                var rows = files.Rows();
                Check(rows.Length == 13 && rows.All(r => r.Length == SimulationOutcome.Headers.Count));
                Check(rows.Skip(1).Select(r => r[2]).Distinct().Count() == 12 && rows.Skip(1).All(r => r[4] == "completed"));
            }),
            ("Turn order varies per simulation, reproduces by seed and preserves player identities", () =>
            {
                using var first = new Files();
                using var second = new Files();
                Dictionary<int, string> Capture(Files files)
                {
                    var orders = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
                    var options = CliOptions.Parse(["4", "aggressive,conservative,infrastructure,novice", "-s", "12", "-r", "1", "--seed", "42"]);
                    using var logging = new SimulationLogging(new() { RootDirectory = Path.Combine(files.Root, "logs"), ConsoleEnabled = false });
                    using var results = files.Open();
                    var runner = new SimulationBatchRunner((players, _, engineOptions) =>
                    {
                        Check(players.Select(p => p.Id).Order().SequenceEqual(["player-1", "player-2", "player-3", "player-4"]));
                        Check(players.Single(p => p.Id == "player-1").Strategy == "aggressive" && players.Single(p => p.Id == "player-2").Strategy == "conservative");
                        orders[engineOptions.RandomSeed!.Value] = string.Join(",", players.Select(p => p.Id));
                        return Final(players, engineOptions);
                    });
                    var batch = runner.RunAsync(options, Definition, results, logging).GetAwaiter().GetResult();
                    Check(batch.Completed == 12 && batch.Failed == 0);
                    return orders.ToDictionary();
                }
                var a = Capture(first);
                var b = Capture(second);
                Check(a.Values.Distinct().Count() > 1 && a.All(pair => b[pair.Key] == pair.Value));
                Check(first.Rows().Skip(1).All(row => row[20] == "aggressive" && row[25] == "conservative" && row[30] == "infrastructure" && row[35] == "novice"));
            }),
            ("Real seeded parallel batches reproduce strategies and outcomes", () =>
            {
                using var first = new Files();
                using var second = new Files();
                Run(first, new(), simulations: 4, random: true);
                Run(second, new(), simulations: 4, random: true);
                string[][] Comparable(Files files) => files.Rows().Skip(1).OrderBy(r => int.Parse(r[3])).Select(r => r.Where((_, index) => index is not (1 or 2 or 19)).ToArray()).ToArray();
                var a = Comparable(first);
                var b = Comparable(second);
                Check(a.Zip(b).All(pair => pair.First.SequenceEqual(pair.Second)) && a.Length == 4 && b.Length == 4);
                foreach (var row in first.Rows().Skip(1))
                {
                    Check(File.Exists(row[19]) && Path.GetDirectoryName(row[19])!.EndsWith("monosim-" + row[2]));
                }
            }),
            ("Mixed-strategy batch completes without trade sale and buyback cycles", () =>
            {
                using var files = new Files();
                var options = CliOptions.Parse(["6", "aggressive,conservative,infrastructure,adjacent-group,blue-green,novice", "-r", "10", "--seed", "7961"]);
                {
                    using var logging = new SimulationLogging(new() { RootDirectory = Path.Combine(files.Root, "logs"), ConsoleEnabled = false });
                    using var results = files.Open();
                    using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var batch = new SimulationBatchRunner().RunAsync(options, Definition, results, logging, cancelled.Token).GetAwaiter().GetResult();
                    Check(batch.Completed == 1 && batch.Failed == 0);
                }
                Check(files.Rows().Length == 2 && files.Rows()[1][4] == "completed");
            }),
            ("Continue records failed rows with quoted errors and runs remaining games", () =>
            {
                using var files = new Files();
                var runner = new SimulationBatchRunner((players, _, options) =>
                {
                    if (options.RandomSeed == 42)
                    {
                        throw new InvalidOperationException("failed, \"example\"\nnext line");
                    }
                    return Final(players, options);
                });
                var batch = Run(files, runner, simulations: 6);
                Check(batch.Completed == 5 && batch.Failed == 1 && files.Rows().Length == 7);
                var failure = files.Rows().Skip(1).Single(r => r[4] == "failed");
                Check(failure[18] == "failed, \"example\"\nnext line" && File.Exists(failure[19]));
            }),
            ("Stop-on-error finishes active work and does not start the full batch", () =>
            {
                using var files = new Files();
                var runner = new SimulationBatchRunner((_, _, _) => throw new InvalidOperationException("stop"));
                var batch = Run(files, runner, simulations: 30, stop: true);
                Check(batch.Failed >= 1 && batch.Failed <= batch.Workers && batch.Completed == 0 && files.Rows().Length == batch.Failed + 1);
            }),
            ("Shared winners and unused player slots are explicit in results", () =>
            {
                var fields = Outcome().Fields();
                Check(fields.Length == SimulationOutcome.Headers.Count && fields[16] == "player-1|player-2");
                Check(fields[24] == "True" && fields[29] == "True" && fields.Skip(30).All(string.IsNullOrEmpty));
            }),
            ("Engine honors caller correlation IDs and cancellation", () =>
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var players = SimulationBatchRunner.CreatePlayers(CliOptions.Parse(["2", "novice"]), Definition, 1);
                var engine = new MonoSimEngine(players, NullLogger.Instance, new() { SimulationId = "caller-id", CancellationToken = cancellation.Token });
                Expect<OperationCanceledException>(() => engine.Play());
                Check(engine.GetState().SimulationId == "caller-id");
            })
        };
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS CLI: {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL CLI: {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} CLI checks passed.");
        return failures;
    }

    private static BatchResult Run(Files files, SimulationBatchRunner runner, int simulations, bool random = false, bool stop = false)
    {
        var strategy = "aggressive";
        if (random)
        {
            strategy = "random";
        }
        var options = CliOptions.Parse(["2", strategy, "-s", simulations.ToString(), "-r", "2", "--seed", "42", "-o", files.Output]) with { StopOnError = stop };
        using var logging = new SimulationLogging(new() { RootDirectory = Path.Combine(files.Root, "logs"), ConsoleEnabled = false });
        using var results = files.Open();
        return runner.RunAsync(options, Definition, results, logging).GetAwaiter().GetResult();
    }

    private static GameState Final(Player[] players, MonoSimEngineOptions options) => new()
    {
        SimulationId = options.SimulationId, RandomSeed = options.RandomSeed, Players = players, RoundNumber = 2, TurnNumber = 4,
        EndReason = GameEndReason.RoundLimit, WinnerIds = players.Select(p => p.Id).ToArray(),
        PortfolioValues = players.Select(p => new PlayerPortfolioValue(p.Id, p.CashBalance, 0, 0)).ToArray()
    };
    private static SimulationOutcome Outcome(int number = 1)
    {
        var players = SimulationBatchRunner.CreatePlayers(CliOptions.Parse(["2", "novice"]), Definition, 1);
        return new("run", "simulation", number, 1, 1, Definition, "definition.json", 50, true, "log.clef", players,
            Final(players, new() { SimulationId = "simulation" }), null);
    }
    private static void Check(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Unexpected CLI behavior.");
        }
    }
    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "cli-tests-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, "results.csv");
        public Files() => Directory.CreateDirectory(Root);
        public ResultsWriter Open(string input = "", bool interactive = false) => ResultsWriter.Open(Output, ',', new StringReader(input), TextWriter.Null, interactive);
        public string[][] Rows()
        {
            using var reader = new StreamReader(Output);
            return DelimitedText.Read(reader, ',').ToArray();
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("cli-tests-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the test output folder.");
            }
            Directory.Delete(resolved, recursive: true);
        }
    }
}
