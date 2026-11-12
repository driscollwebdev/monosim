using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MonoSim;
using MonoSim.Cli;
using MonoSim.Logging;
using MonoSim.Models;
using MonoSim.Strategies;

internal static class AdherenceTests
{
    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("All concrete strategies expose their fixed adherence adjustments", () =>
            {
                var executor = new InertExecutor();
                IPlayingStrategy[] strategies = [new AggressiveStrategy(executor), new ConservativeStrategy(executor), new InfrastructureStrategy(executor), new AdjacentGroupStrategy(executor), new BlueGreenStrategy(executor), new NoviceStrategy(executor)];
                Check(strategies.Select(s => s.AdherenceAdjustment).SequenceEqual([0.02m, 0.02m, 0.01m, -0.02m, -0.02m, -0.04m]));
            }),
            ("Sampling uses inclusive adherence threshold and inverts either binary choice", () =>
            {
                var player = P("a", "aggressive", 0);
                var edge = new AdherenceDecisions(1, _ => 0.87m);
                Check(edge.Decide(player, 0.02m, "buy", true));
                Check(!edge.Decide(player, 0.02m, "pass", false));
                var beyond = new AdherenceDecisions(1, _ => 0.870001m);
                Check(!beyond.Decide(player, 0.02m, "buy", true));
                Check(beyond.Decide(player, 0.02m, "pass", false));
            }),
            ("Mandatory and single-option decisions consume no random values", () =>
            {
                var calls = 0;
                var choices = new AdherenceDecisions(1, _ => { calls++; return 1; });
                Check(choices.Decide(P("a", "novice", 0), -0.04m, "debt", true, mandatory: true));
                Check(!choices.Decide(P("a", "novice", 0), -0.04m, "no-bid", false, hasAlternative: false));
                Check(calls == 0);
            }),
            ("Repeated queries reuse a choice and a new turn permits a new decision", () =>
            {
                var calls = 0;
                var logged = new List<AdherenceDecision>();
                var choices = new AdherenceDecisions(1, _ => { calls++; return 1; }, logged.Add);
                var player = P("a", "novice", 0);
                Check(choices.Decide(player, -0.04m, "buy", false));
                Check(choices.Decide(player, -0.04m, "buy", false) && calls == 1);
                Check(logged.Single().EffectiveProbability == 0.81m && logged.Single().Actual);
                choices.BeginTurn();
                choices.Decide(player, -0.04m, "buy", false);
                Check(calls == 2);
            }),
            ("Per-player random streams do not depend on other players' sampling order", () =>
            {
                var a = new List<AdherenceDecision>();
                var b = new List<AdherenceDecision>();
                var first = new AdherenceDecisions(42, record: a.Add);
                var second = new AdherenceDecisions(42, record: b.Add);
                first.Decide(P("a", "novice", 0), 0, "one", true);
                first.Decide(P("b", "novice", 0), 0, "one", true);
                first.Decide(P("a", "novice", 0), 0, "two", true);
                second.Decide(P("b", "novice", 0), 0, "one", true);
                second.Decide(P("a", "novice", 0), 0, "one", true);
                second.Decide(P("a", "novice", 0), 0, "two", true);
                Check(a.Where(d => d.PlayerId == "a").Select(d => d.Sample).SequenceEqual(b.Where(d => d.PlayerId == "a").Select(d => d.Sample)));
            }),
            ("Profiles are bounded, seeded and shared across different simulation seeds", () =>
            {
                var profile = SimulationBatchRunner.CreateAdherenceProfile(6, 42);
                Check(profile.All(p => p is >= 0.75m and <= 0.95m) && profile.SequenceEqual(SimulationBatchRunner.CreateAdherenceProfile(6, 42)));
                var options = CliOptions.Parse(["6", "random"]);
                var definition = SimulationDefinition.Load(options.DefinitionPath);
                var first = SimulationBatchRunner.CreatePlayers(options, definition, 1, profile);
                var second = SimulationBatchRunner.CreatePlayers(options, definition, 2, profile);
                Check(first.Select(p => p.StrategyAdherence).SequenceEqual(second.Select(p => p.StrategyAdherence)));
                Check(!first.Select(p => p.Strategy).SequenceEqual(second.Select(p => p.Strategy)));
            }),
            ("Invalid base probabilities and invalid sampler values are rejected", () =>
            {
                foreach (var value in new[] { 0.749m, 0.951m })
                {
                    Expect<ArgumentException>(() => new MonoSimEngine([P("a", "novice", 0) with { StrategyAdherence = value }, P("b", "novice", 0)], NullLogger.Instance));
                }
                Expect<InvalidOperationException>(() => new AdherenceDecisions(1, _ => 1.1m).Decide(P("a", "novice", 0), 0, "buy", true));
            }),
            ("A player can unexpectedly decline an affordable strategy purchase", () =>
            {
                var log = new Recorder();
                var final = Play("aggressive", 200, 0, [[1, 2], [1, 3]], _ => 1, log);
                Check(!final.Players.Single(p => p.Id == "a").OwnedPropertyIds.Contains("baltic-avenue"));
                Check(final.Players.Single(p => p.Id == "a").CashBalance == 200);
                Check(log.Entries.Count(e => e.Event == "StrategyDecision") == 1);
            }),
            ("A deviation can buy despite the Infrastructure cash reserve", () =>
            {
                var final = Play("infrastructure", 200, 0, [[1, 2], [1, 3]], _ => 1, new Recorder());
                Check(final.Players.Single(p => p.Id == "a").OwnedPropertyIds.Contains("baltic-avenue"));
                Check(final.Players.Single(p => p.Id == "a").CashBalance == 140);
            }),
            ("No optional choices means no adherence checks", () =>
            {
                var calls = 0;
                Play("aggressive", 200, 0, [[4, 6], [1, 3]], _ => { calls++; return 1; }, new Recorder());
                Check(calls == 0);
            }),
            ("Debt settlement and bankruptcy remain mandatory", () =>
            {
                var calls = 0;
                var final = Play("aggressive", 50, 0, [[1, 3]], _ => { calls++; return 1; }, new Recorder());
                Check(final.Players.Single(p => p.Id == "a").IsBankrupt && final.Players.Single(p => p.Id == "a").CashBalance == 0);
                Check(final.Debts.Single().Remaining == 150 && calls == 0);
            }),
            ("Card movement and GO payments remain mandatory", () =>
            {
                var calls = 0;
                var final = Play("aggressive", 0, 39, [[1, 2], [1, 3]], _ => { calls++; return 1; }, new Recorder());
                var player = final.Players.Single(p => p.Id == "a");
                Check(player.BoardPosition == 0 && player.CashBalance == 400 && player.CompletedLaps == 2 && calls == 0);
            }),
            ("An unexpected trade acceptance can override primary target protection", () =>
            {
                StrategyTurnState? result = null;
                var engine = ProbeTrade(P("a", "aggressive", 500), P("b", "infrastructure", 1000, ["reading-railroad"]),
                    new() { OtherPlayerId = "b", PropertyIdsReceived = ["reading-railroad"], CashPaid = 200 }, _ => 1, updated => result = updated);
                Check(result is { ActionExecuted: true } && engine.GetState().Players.Single(p => p.Id == "a").OwnedPropertyIds.Contains("reading-railroad"));
            }),
            ("Rejected trades return a normal not-executed result without transferring cash", () =>
            {
                StrategyTurnState? result = null;
                var engine = ProbeTrade(P("a", "aggressive", 500), P("b", "conservative", 1000, ["oriental-avenue", "baltic-avenue"]),
                    new() { OtherPlayerId = "b", PropertyIdsReceived = ["baltic-avenue"], CashPaid = 60 }, _ => 0, updated => result = updated);
                Check(result is { ActionExecuted: false } && engine.GetState().Players.Single(p => p.Id == "a").CashBalance == 500);
            }),
            ("Built-in executors handle a different accepted price from the planned counter", () =>
            {
                var bCalls = 0;
                Player[] players = [P("a", "aggressive", 2000, ["mediterranean-avenue"]) with { BoardPosition = 10 }, P("b", "conservative", 20, ["oriental-avenue", "baltic-avenue"]) with { BoardPosition = 5 }];
                var final = new MonoSimEngine(players, NullLogger.Instance, new()
                {
                    MaximumRounds = 1, RandomSeed = 1, ShuffleDecks = false, DiceRoller = () => [1, 3],
                    AdherenceSampler = id => id == "b" && ++bCalls == 1 ? 1 : 0
                }).Play();
                Check(final.Players.Single(p => p.Id == "a").FinancialLedger.Any(t => t.Description == "Trade cash settlement" && t.Amount == -60));
            }),
            ("Auction deviations can pass or bid but never invent an unaffordable bid", () =>
            {
                var passed = ProbeAuction("aggressive", 100, _ => 1);
                Check(passed.Properties.Single(p => p.PropertyId == "mediterranean-avenue").IsAvailable);
                var bought = ProbeAuction("conservative", 100, _ => 1);
                Check(bought.Players.Single(p => p.Id == "b").OwnedPropertyIds.Contains("mediterranean-avenue") && bought.Players.Single(p => p.Id == "b").CashBalance == 75);
                var calls = 0;
                ProbeAuction("aggressive", 20, _ => { calls++; return 1; });
                Check(calls == 0);
            }),
            ("Development deviations override strategy limits while retaining legal even building", () =>
            {
                var log = new Recorder();
                var final = Play("conservative", 500, 20, [[1, 3], [1, 3]], _ => 1, log, ["mediterranean-avenue", "baltic-avenue"]);
                Check(final.Properties.Where(p => p.ColorGroup == "brown").All(p => p.HotelCount == 1));
                Check(final.BankHouses == 32 && final.BankHotels == 10 && final.Players.Single(p => p.Id == "a").CashBalance == 0);
            })
        };
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS Adherence: {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL Adherence: {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} adherence checks passed.");
        return failures;
    }

    private static Player P(string id, string strategy, decimal cash, string[]? owned = null) => new()
    {
        Id = id, Name = id, Strategy = strategy, CashBalance = cash, OwnedPropertyIds = owned ?? [], StrategyAdherence = 0.85m
    };
    private static GameState Play(string strategy, decimal cash, int position, int[][] dice, Func<string, decimal> sample, Recorder log, string[]? owned = null)
    {
        var queue = new Queue<int[]>(dice);
        return new MonoSimEngine([P("a", strategy, cash, owned) with { BoardPosition = position }, P("b", "conservative", 0) with { BoardPosition = 20 }], log,
            new() { MaximumRounds = 1, RandomSeed = 42, ShuffleDecks = false, DiceRoller = () => queue.Dequeue(), AdherenceSampler = sample }).Play();
    }
    private static MonoSimEngine ProbeTrade(Player a, Player b, PropertyTrade trade, Func<string, decimal> sampler, Action<StrategyTurnState> capture)
    {
        var options = new MonoSimEngineOptions
        {
            RandomSeed = 42, MaximumRounds = 1, AdherenceSampler = sampler,
            StrategyFactory = (p, executor) => p.Id == "a" ? new ProbeStrategy((player, state) =>
            {
                capture(executor.Execute(player.Id, new() { Id = "probe", Type = StrategyActionType.TradeProperties, Trade = trade, Cost = trade.NetCost }));
                throw new ProbeComplete();
            }) : Concrete(p.Strategy, executor)
        };
        var engine = new MonoSimEngine([a, b], NullLogger.Instance, options);
        Expect<ProbeComplete>(() => engine.Play());
        return engine;
    }
    private static GameState ProbeAuction(string strategy, decimal cash, Func<string, decimal> sample)
    {
        var options = new MonoSimEngineOptions
        {
            RandomSeed = 42, MaximumRounds = 1, AdherenceSampler = sample,
            StrategyFactory = (p, executor) => p.Id == "s" ? new ProbeStrategy((player, state) =>
            {
                executor.Execute(player.Id, new() { Id = "auction", Type = StrategyActionType.AuctionAssets, Cost = 0, Bundle = new() { PropertyIds = ["mediterranean-avenue"] } });
                throw new ProbeComplete();
            }) : Concrete(p.Strategy, executor)
        };
        var engine = new MonoSimEngine([P("s", "conservative", 0, ["mediterranean-avenue"]), P("b", strategy, cash)], NullLogger.Instance, options);
        Expect<ProbeComplete>(() => engine.Play());
        return engine.GetState();
    }
    private static IPlayingStrategy Concrete(string name, IStrategyActionExecutor executor) => name switch
    {
        "infrastructure" => new InfrastructureStrategy(executor), "aggressive" => new AggressiveStrategy(executor),
        _ => new ConservativeStrategy(executor)
    };
    private static void Check(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Unexpected adherence behavior.");
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
    private sealed class ProbeComplete : Exception;
    private sealed class ProbeStrategy(Action<Player, GameState> run) : IPlayingStrategy
    {
        public Action PreRoll(Player player, GameState state) => () => run(player, state);
        public Action PostRoll(Player player, GameState state) => PreRoll(player, state);
    }
    private sealed class InertExecutor : IStrategyActionExecutor
    {
        public StrategyTurnState Execute(string id, StrategyAction action) => throw new NotSupportedException();
    }
    private sealed class Recorder : ILogger
    {
        public List<SimulationLogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (state is SimulationLogEntry entry)
            {
                Entries.Add(entry);
            }
        }
    }
}
