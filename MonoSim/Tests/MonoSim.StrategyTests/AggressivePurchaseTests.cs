using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MonoSim;
using MonoSim.Models;
using MonoSim.Strategies;
using static InfrastructureStrategyTests;

internal static class AggressivePurchaseTests
{
    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("After lap one Connecticut is passed to preserve an affordable pink purchase", () =>
            {
                var e = Scenario(200);
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0 && e.Player.CashBalance == 200);
                Check(AggressivePurchaseAdvisor.PurchaseReserve(e.Player, e.State, Property(e, "connecticut-avenue")) == 140);
            }),
            ("Cash sufficient for Connecticut and the cheapest pink allows the purchase", () =>
            {
                var e = Scenario(260);
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["buy-connecticut-avenue"]) && e.Player.CashBalance == 140);
                e = Scenario(259);
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0);
            }),
            ("First-lap aggressive purchases remain unconditional when affordable", () =>
            {
                var e = Scenario(200);
                e.Player = e.Player with { CompletedLaps = 0 };
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["buy-connecticut-avenue"]) && e.Player.CashBalance == 80);
            }),
            ("No available stronger group means no speculative cash reserve", () =>
            {
                var e = Scenario(200, available: ["connecticut-avenue"]);
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 1);
            }),
            ("Unreachable or unaffordable alternatives do not block a purchase", () =>
            {
                var e = Scenario(150, available: ["connecticut-avenue", "virginia-avenue"]);
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 1);
                e = Scenario(200, owned: ["reading-railroad", "states-avenue", "pacific-avenue"], available: ["connecticut-avenue", "north-carolina-avenue"]);
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 1);
            }),
            ("A reachable last utility protects its reserve even against another partial-group purchase", () =>
            {
                var e = Scenario(200, owned: ["tennessee-avenue", "electric-company"], available: ["st-james-place", "water-works"]);
                e.Player = e.Player with { BoardPosition = 16 };
                e.State = e.State with { AvailableActions = [Buy("st-james-place")] };
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0);
            }),
            ("An actual landing that strengthens the existing group is purchased", () =>
            {
                var e = Scenario(200);
                e.Player = e.Player with { BoardPosition = 11 };
                e.State = e.State with { AvailableActions = [Buy("st-charles-place")] };
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["buy-st-charles-place"]) && e.Player.CashBalance == 60);
            }),
            ("A certain monopoly acquisition takes precedence over a hoped-for opportunity", () =>
            {
                var e = Scenario(200, owned: ["oriental-avenue", "vermont-avenue", "states-avenue", "reading-railroad"]);
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["buy-connecticut-avenue"]));
            }),
            ("Mortgage purchases protect even a partial-group opportunity", () =>
            {
                foreach (var cash in new[] { 190m, 195m })
                {
                    var e = Scenario(cash);
                    Mortgage(e, "oriental-avenue");
                    e.State = e.State with { AvailableActions = [Buy("oriental-avenue") with { Type = StrategyActionType.BuyMortgagedProperty, Cost = 55 }] };
                    new AggressiveStrategy(e).PreRoll(e.Player, e.State)();
                    Check(e.Executed.Count == (cash == 195 ? 1 : 0));
                }
            }),
            ("Monopoly-completing mortgage purchases still protect the acquisition reserve", () =>
            {
                var e = Scenario(200, owned: ["oriental-avenue", "vermont-avenue", "states-avenue", "reading-railroad"], available: ["st-charles-place", "virginia-avenue"]);
                Mortgage(e, "connecticut-avenue");
                e.State = e.State with { AvailableActions = [Buy("connecticut-avenue") with { Type = StrategyActionType.BuyMortgagedProperty, Cost = 66 }] };
                new AggressiveStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0);
                Check(!new DefaultStrategyMarketPolicy().AcceptTrade(e.Player, e.State, Deal([], ["connecticut-avenue"], paid: 60, fees: 6)));
            }),
            ("Trades protect even a partial-group opportunity", () =>
            {
                foreach (var cash in new[] { 200m, 240m })
                {
                    var e = Scenario(cash, owned: ["oriental-avenue", "states-avenue", "reading-railroad"]);
                    e.State = e.State with { AvailableActions = [Trade(Deal([], ["vermont-avenue"], paid: 100))] };
                    new AggressiveStrategy(e).PreRoll(e.Player, e.State)();
                    Check(e.Executed.Count == (cash == 240 ? 1 : 0));
                }
            }),
            ("Building can spend through a partial-group reserve", () =>
            {
                var e = Scenario(200, owned: ["states-avenue", "reading-railroad", "mediterranean-avenue", "baltic-avenue"]);
                e.State = e.State with { AvailableActions = [Buy("connecticut-avenue"), Build("mediterranean-avenue"), Build("baltic-avenue")] };
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 2 && e.Executed.All(id => id.StartsWith("build-")) && e.Player.CashBalance == 100);
            }),
            ("Building protects the opportunity to complete a monopoly", () =>
            {
                var e = Scenario(200, owned: ["states-avenue", "virginia-avenue", "reading-railroad", "mediterranean-avenue", "baltic-avenue"], available: ["connecticut-avenue", "st-charles-place"]);
                e.State = e.State with { AvailableActions = [Buy("connecticut-avenue"), Build("mediterranean-avenue"), Build("baltic-avenue")] };
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 1 && e.Executed[0].StartsWith("build-") && e.Player.CashBalance == 150);
            }),
            ("Mortgage redemptions share the development rule", () =>
            {
                foreach (var complete in new[] { false, true })
                {
                    string[] owned = ["reading-railroad", "states-avenue", "mediterranean-avenue"];
                    if (complete)
                    {
                        owned = [.. owned, "virginia-avenue"];
                    }
                    var e = Scenario(150, owned, ["connecticut-avenue", "st-charles-place"]);
                    Mortgage(e, "mediterranean-avenue");
                    e.State = e.State with { AvailableActions = [new() { Id = "redeem", Type = StrategyActionType.UnmortgageProperty, PropertyId = "mediterranean-avenue", Cost = 33 }] };
                    new AggressiveStrategy(e).PreRoll(e.Player, e.State)();
                    Check(e.Executed.Count == (complete ? 0 : 1));
                }
            }),
            ("Auction ceilings and trade acceptance honor the same acquisition reserve", () =>
            {
                var e = Scenario(200, owned: ["states-avenue", "reading-railroad", "oriental-avenue"]);
                var policy = new DefaultStrategyMarketPolicy();
                Check(policy.MaximumBid(e.Player, e.State, new() { PropertyIds = ["mediterranean-avenue"] }, 60, 0) == 60);
                Check(!policy.AcceptTrade(e.Player, e.State, Deal([], ["vermont-avenue"], paid: 100)));
                Check(policy.MaximumBid(e.Player, e.State, new() { PropertyIds = ["st-charles-place"] }, 140, 0) == 200);
                var weak = Scenario(200);
                Check(policy.MaximumBid(weak.Player, weak.State, new() { PropertyIds = ["connecticut-avenue"] }, 120, 0) == 60);
            }),
            ("Railroad consolidation also creates a reachable purchase opportunity", () =>
            {
                var e = Scenario(200, available: ["connecticut-avenue", "pennsylvania-railroad"]);
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0 && AggressivePurchaseAdvisor.AcquisitionReserve(e.Player, e.State) == 200);
            }),
            ("Available properties are reevaluated instead of keeping a stale reserve", () =>
            {
                var e = Scenario(200);
                var strategy = new AggressiveStrategy(e);
                strategy.PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0);
                var changed = Scenario(200, available: ["connecticut-avenue"]);
                e.State = changed.State;
                strategy.PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 1);
            }),
            ("The engine reproduces both cash boundaries in the user's scenario", () =>
            {
                foreach (var cash in new[] { 200m, 260m })
                {
                    var e = Scenario(cash);
                    e.Player = e.Player with { BoardPosition = 2 };
                    var other = e.State.OtherPlayers.Single() with { BoardPosition = 20 };
                    var rolls = new Queue<int[]>([[3, 4], [1, 3]]);
                    var options = new MonoSimEngineOptions
                    {
                        EnableHumanDecisions = false, MaximumRounds = 1, RandomSeed = 42, ShuffleDecks = false, DiceRoller = () => rolls.Dequeue(),
                        StrategyFactory = (player, executor) => player.Id == e.Player.Id ? new AggressiveStrategy(executor) : new Pass(executor)
                    };
                    var final = new MonoSimEngine([e.Player, other], NullLogger.Instance, options).Play();
                    var player = final.Players.Single(p => p.Id == e.Player.Id);
                    Check(player.OwnedPropertyIds.Contains("connecticut-avenue") == (cash == 260), $"Starting cash {cash}; final cash {player.CashBalance}; holdings {string.Join(',', player.OwnedPropertyIds)}.");
                    Check(player.CashBalance == (cash == 260 ? 140 : 200), $"Starting cash {cash}; final cash {player.CashBalance}; holdings {string.Join(',', player.OwnedPropertyIds)}.");
                }
            })
        };
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS Aggressive purchasing: {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL Aggressive purchasing: {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} aggressive purchase checks passed.");
        return failures;
    }

    private static TestEngine Scenario(decimal cash, string[]? owned = null, string[]? available = null)
    {
        owned ??= ["reading-railroad", "states-avenue"];
        available ??= ["connecticut-avenue", "st-charles-place", "virginia-avenue"];
        var otherOwned = Board.Select(p => p.PropertyId).Except(owned).Except(available).ToArray();
        var e = Engine(cash, 9, owned, [Buy("connecticut-avenue")], otherOwned);
        e.Player = e.Player with { Strategy = "aggressive", CompletedLaps = 1 };
        return e;
    }
    private static PropertyState Property(TestEngine e, string id) => e.State.Properties.Single(p => p.PropertyId == id);
    private static void Mortgage(TestEngine e, string id) => e.State = e.State with
    {
        Properties = e.State.Properties.Select(p => p.PropertyId == id ? p with { IsMortgaged = true } : p).ToArray()
    };
    private static void Check(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new Exception(message ?? "Unexpected aggressive purchase decision.");
        }
    }
    private sealed class Pass(IStrategyActionExecutor executor) : IPlayingStrategy
    {
        public Action PreRoll(Player player, GameState state) => () => ((IPlayerTurnController)executor).DeclareActionsComplete(player.Id);
        public Action PostRoll(Player player, GameState state) => PreRoll(player, state);
    }
}
