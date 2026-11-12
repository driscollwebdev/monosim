using Microsoft.Extensions.Logging;
using MonoSim;
using MonoSim.Models;
using MonoSim.Strategies;
using MonoSim.Trading;
using static InfrastructureStrategyTests;

internal static class TradingStrategyTests
{
    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("Unequal negotiated prices remain legal while fees and funds remain mandatory", () =>
            {
                var e = Engine(500, 0, [], [], ["baltic-avenue"]);
                Check(TradeValidator.Validate(e.Player, e.State, Deal([], ["baltic-avenue"], paid: 90)).IsValid);
                Check(!TradeValidator.Validate(e.Player, e.State, Deal([], ["baltic-avenue"], paid: 501)).IsValid);
                e.State = Mortgaged(e.State, "baltic-avenue");
                Check(!TradeValidator.Validate(e.Player, e.State, Deal([], ["baltic-avenue"], paid: 45)).IsValid);
                Check(TradeValidator.Validate(e.Player, e.State, Deal([], ["baltic-avenue"], paid: 45, fees: 3)).IsValid);
            }),
            ("Aggressive buyer pays completion premium but rejects excessive pricing", () =>
            {
                var e = Aggressive(500, 10, ["mediterranean-avenue"], ["baltic-avenue"]);
                var policy = new DefaultStrategyMarketPolicy();
                Check(policy.AcceptTrade(e.Player, e.State, Deal([], ["baltic-avenue"], paid: 90)));
                Check(!policy.AcceptTrade(e.Player, e.State, Deal([], ["baltic-avenue"], paid: 91)));
                var offer = policy.CreateOffer(e.Player, e.State, Deal([], ["baltic-avenue"], paid: 60));
                Check(offer?.CashPaid == 60);
                var counter = policy.CounterOffer(e.Player, e.State, Deal([], ["baltic-avenue"], paid: 120));
                Check(counter?.CashPaid == 90);
            }),
            ("A first singleton does not justify buying any ordinary offered property", () =>
            {
                var e = Aggressive(500, 0, [], ["oriental-avenue"]);
                Check(!new DefaultStrategyMarketPolicy().AcceptTrade(e.Player, e.State, Deal([], ["oriental-avenue"], paid: 100)));
            }),
            ("Aggressive swaps a singleton for consolidation in another group", () =>
            {
                var deal = Deal(["kentucky-avenue"], ["vermont-avenue"], receivedCash: 120);
                var e = Aggressive(500, 0, ["oriental-avenue", "kentucky-avenue"], ["vermont-avenue"]);
                Check(new DefaultStrategyMarketPolicy().AcceptTrade(e.Player, e.State, deal));
                e.State = e.State with { AvailableActions = [Trade(deal)] };
                new AggressiveStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["trade"]) && e.Player.OwnedPropertyIds.Contains("vermont-avenue"));
            }),
            ("Aggressive buys unrelated mortgages at principal but not excessive prices", () =>
            {
                var e = Aggressive(500, 0, [], ["kentucky-avenue"]);
                e.State = Mortgaged(e.State, "kentucky-avenue");
                var policy = new DefaultStrategyMarketPolicy();
                Check(policy.AcceptTrade(e.Player, e.State, Deal([], ["kentucky-avenue"], paid: 110, fees: 11)));
                Check(!policy.AcceptTrade(e.Player, e.State, Deal([], ["kentucky-avenue"], paid: 111, fees: 11)));
            }),
            ("Mortgage bargains do not justify unproductive swaps or same-turn buybacks", () =>
            {
                var e = Aggressive(500, 10, ["kentucky-avenue"], ["oriental-avenue"]);
                e.State = Mortgaged(Mortgaged(e.State, "oriental-avenue"), "kentucky-avenue");
                var swap = Deal(["kentucky-avenue"], ["oriental-avenue"], receivedCash: 60, fees: 5) with { OtherPlayerBankFees = 11 };
                Check(!new DefaultStrategyMarketPolicy().AcceptTrade(e.Player, e.State, swap));
                e = Aggressive(500, 10, [], ["oriental-avenue"]);
                e.State = Mortgaged(e.State, "oriental-avenue") with
                {
                    CompletedTrades = [new(e.State.TurnNumber, e.Player.Id, Deal(["oriental-avenue"], [], receivedCash: 50))]
                };
                Check(!new DefaultStrategyMarketPolicy().AcceptTrade(e.Player, e.State, Deal([], ["oriental-avenue"], paid: 50, fees: 5)));
                e = Aggressive(500, 10, ["oriental-avenue"], ["vermont-avenue"]);
                e.State = e.State with { CompletedTrades = [new(e.State.TurnNumber, e.Player.Id, Deal(["vermont-avenue"], [], receivedCash: 100))] };
                Check(!new DefaultStrategyMarketPolicy().AcceptTrade(e.Player, e.State, Deal([], ["vermont-avenue"], paid: 100)));
            }),
            ("Only Conservative uses two-roll pre-roll avoidance", () =>
            {
                var e = Aggressive(500, 0, ["kentucky-avenue"], []);
                var deal = Deal(["kentucky-avenue"], [], receivedCash: 220);
                foreach (var strategy in new[] { "aggressive", "infrastructure", "adjacent-group", "blue-green" })
                {
                    var player = e.Player with { Strategy = strategy };
                    Check(!StrategicTradeAdvisor.AvoidsTransfer(player, e.State with { Phase = TurnPhase.PreRoll }, deal));
                    Check(!StrategicTradeAdvisor.AvoidsTransfer(player, e.State with { Phase = TurnPhase.CombinedPostRollPreRoll }, deal));
                }
                var conservative = e.Player with { Strategy = "conservative" };
                Check(StrategicTradeAdvisor.AvoidsTransfer(conservative, e.State with { Phase = TurnPhase.PreRoll }, deal));
                Check(StrategicTradeAdvisor.AvoidsTransfer(conservative, e.State with { Phase = TurnPhase.CombinedPostRollPreRoll }, deal));
                Check(!StrategicTradeAdvisor.AvoidsTransfer(conservative, e.State with { Phase = TurnPhase.PostRoll }, deal));
            }),
            ("One-roll avoidance includes wrapped destinations and mortgaged properties", () =>
            {
                var e = Aggressive(500, 39, ["mediterranean-avenue"], []);
                var deal = Deal(["mediterranean-avenue"], [], receivedCash: 60);
                Check(StrategicTradeAdvisor.AvoidsTransfer(e.Player, e.State, deal));
                e.State = Mortgaged(e.State, "mediterranean-avenue");
                Check(StrategicTradeAdvisor.AvoidsTransfer(e.Player, e.State, deal with { OtherPlayerBankFees = 3 }));
            }),
            ("Debt rescue keeps its cash exception to transfer avoidance", () =>
            {
                var e = Aggressive(0, 39, ["mediterranean-avenue"], []);
                e.State = e.State with { CashObligations = [new(e.Player.Id, null, 50, "tax")] };
                Check(!StrategicTradeAdvisor.AvoidsTransfer(e.Player, e.State, Deal(["mediterranean-avenue"], [], receivedCash: 60)));
                e = Aggressive(0, 39, ["oriental-avenue", "mediterranean-avenue"], []);
                e.Player = e.Player with { Strategy = "conservative" };
                e.State = e.State with { CashObligations = [new(e.Player.Id, null, 50, "tax")] };
                var rescue = Deal(["mediterranean-avenue"], [], receivedCash: 60);
                Check(new DefaultStrategyMarketPolicy().CreateOffer(e.Player, e.State, rescue) is not null);
            }),
            ("Conservative asks two times value and accepts a one-and-half-times funded sale", () =>
            {
                var e = Aggressive(20, 5, ["oriental-avenue", "baltic-avenue"], []);
                e.Player = e.Player with { Strategy = "conservative" };
                var policy = new DefaultStrategyMarketPolicy();
                var original = Deal(["baltic-avenue"], [], receivedCash: 60);
                Check(!policy.AcceptTrade(e.Player, e.State, original));
                Check(policy.CounterOffer(e.Player, e.State, original)?.CashReceived == 120);
                Check(policy.AcceptTrade(e.Player, e.State, original with { CashReceived = 90 }));
                Check(!policy.AcceptTrade(e.Player, e.State, original with { CashReceived = 89 }));
                Check(!policy.AcceptTrade(e.Player with { CashBalance = 1000 }, e.State, original with { CashReceived = 120 }));
            }),
            ("Conservative mortgage flips need no existing monopoly and develop gradually", () =>
            {
                var action = Buy("mediterranean-avenue") with { Type = StrategyActionType.BuyMortgagedProperty, Cost = 33 };
                var e = Aggressive(1000, 10, ["oriental-avenue"], ["mediterranean-avenue"]);
                e.Player = e.Player with { Strategy = "conservative" };
                e.State = Mortgaged(e.State, "mediterranean-avenue") with { AvailableActions = [action] };
                var strategy = new ConservativeStrategy(e);
                strategy.PreRoll(e.Player, e.State)();
                Check(e.Player.OwnedPropertyIds.Contains("mediterranean-avenue"));
                e.Player = e.Player with { CashBalance = e.Player.CashBalance + 100 };
                e.State = e.State with { AvailableActions = [action with { Id = "redeem", Type = StrategyActionType.UnmortgageProperty }] };
                strategy.PostRoll(e.Player, e.State)();
                Check(!e.State.Properties.Single(p => p.PropertyId == "mediterranean-avenue").IsMortgaged);
                e.Player = e.Player with { CashBalance = e.Player.CashBalance + 200, OwnedPropertyIds = [.. e.Player.OwnedPropertyIds, "baltic-avenue"] };
                e.State = e.State with { AvailableActions = [Build("mediterranean-avenue"), Build("baltic-avenue")] };
                strategy.PostRoll(e.Player, e.State)();
                Check(e.Executed.Count(id => id.StartsWith("build-")) == 1);
            }),
            ("Target strategies never give up primary properties", () =>
            {
                foreach (var strategy in new[] { "infrastructure", "adjacent-group", "blue-green" })
                {
                    var id = "green";
                    if (strategy == "infrastructure")
                    {
                        id = "railroad";
                    }
                    var property = Board.First(p => p.ColorGroup == id || id == "railroad" && p.Type == PropertyType.Railroad);
                    var e = Aggressive(0, 0, [property.PropertyId], []);
                    e.Player = e.Player with { Strategy = strategy };
                    Check(!new DefaultStrategyMarketPolicy().AcceptTrade(e.Player, e.State, Deal([property.PropertyId], [], receivedCash: 1000)));
                }
            }),
            ("Target trading switches on ownership before improvements", () =>
            {
                var targets = Board.Where(p => p.ColorGroup is "green" or "darkBlue").Select(p => p.PropertyId).ToArray();
                var e = Aggressive(500, 0, targets, ["mediterranean-avenue"]);
                e.Player = e.Player with { Strategy = "blue-green" };
                e.State = Mortgaged(e.State, "mediterranean-avenue");
                Check(new DefaultStrategyMarketPolicy().AcceptTrade(e.Player, e.State, Deal([], ["mediterranean-avenue"], paid: 30, fees: 3)));
            }),
            ("Multiples can be configured and invalid options rejected", () =>
            {
                var e = Aggressive(500, 10, ["mediterranean-avenue"], ["baltic-avenue"]);
                var policy = new DefaultStrategyMarketPolicy(negotiationOptions: new() { MonopolyCompletionMultiple = 2 });
                Check(policy.AcceptTrade(e.Player, e.State, Deal([], ["baltic-avenue"], paid: 120)));
                try
                {
                    _ = new DefaultStrategyMarketPolicy(negotiationOptions: new() { ConservativeMinimumMultiple = 3 });
                    throw new Exception("Expected invalid multiple rejection.");
                }
                catch (ArgumentException)
                {
                }
            }),
            ("Engine negotiates opening, seller counter and buyer premium, then settles cash", () =>
            {
                var log = new Recorder();
                Player[] players =
                [
                    new() { Id = "a", Name = "A", Strategy = "aggressive", CashBalance = 2000, BoardPosition = 10, OwnedPropertyIds = ["mediterranean-avenue"] },
                    new() { Id = "b", Name = "B", Strategy = "conservative", CashBalance = 20, BoardPosition = 5, OwnedPropertyIds = ["oriental-avenue", "baltic-avenue"] }
                ];
                var final = new MonoSimEngine(players, log, new() { EnableHumanDecisions = false, RandomSeed = 1, MaximumRounds = 1, DiceRoller = () => [1, 3] }).Play();
                var buyer = final.Players.Single(p => p.Id == "a");
                Check(buyer.OwnedPropertyIds.Contains("baltic-avenue") && buyer.FinancialLedger.Any(t => t.Description == "Trade cash settlement" && t.Amount == -90));
                Check(log.Events.Count(e => e.Event == "CounterOfferMade") == 2 && log.Events.Any(e => e.Event == "TradeCounterDeclined"));
                Check(final.Players.Single(p => p.Id == "b").CashBalance == 110);
            })
        };
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS Trading strategy: {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL Trading strategy: {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} trading strategy checks passed.");
        return failures;
    }

    private static TestEngine Aggressive(decimal cash, int position, string[] owned, string[] other)
    {
        var e = Engine(cash, position, owned, [], other);
        e.Player = e.Player with { Strategy = "aggressive" };
        return e;
    }
    private static GameState Mortgaged(GameState state, string id) => state with
    {
        Properties = state.Properties.Select(p => p.PropertyId == id ? p with { IsOwned = true, IsMortgaged = true } : p).ToArray()
    };
    private static void Check(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Unexpected trading decision.");
        }
    }
    private sealed class Recorder : ILogger
    {
        public List<SimulationLogEntry> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (state is SimulationLogEntry entry)
            {
                Events.Add(entry);
            }
        }
    }
}
