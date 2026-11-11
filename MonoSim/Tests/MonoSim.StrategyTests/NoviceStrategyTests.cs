using Microsoft.Extensions.Logging.Abstractions;
using MonoSim;
using MonoSim.Models;
using MonoSim.Strategies;
using static InfrastructureStrategyTests;

internal static class NoviceStrategyTests
{
    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("Trade distance follows absolute group ordinals without wrapping", () =>
            {
                Check(NoviceStrategy.ColorGroupDistanceProbability("lightBlue", "yellow") == 0.5);
                Check(NoviceStrategy.ColorGroupDistanceProbability("green", "brown") == 0.75);
                Check(NoviceStrategy.ColorGroupDistanceProbability("brown", "darkBlue") == 0.875);
                Check(NoviceStrategy.ColorGroupDistanceProbability("darkBlue", "brown") == 0.875);
            }),
            ("Marvin Gardens trade risk matches railroad, green and red examples", () =>
            {
                var e = Engine(2000, 29, [], []);
                var railroads = e.State.Properties.Where(p => p.Type == PropertyType.Railroad);
                var green = e.State.Properties.Where(p => p.ColorGroup == "green");
                var red = e.State.Properties.Where(p => p.ColorGroup == "red");
                Check(Math.Abs(NoviceStrategy.MonopolyAvoidanceProbability(e.Player, e.State, railroads) - 10.0 / 11) < 1e-12);
                Check(Math.Abs(NoviceStrategy.MonopolyAvoidanceProbability(e.Player, e.State, green) - 8.0 / 11) < 1e-12);
                Check(NoviceStrategy.MonopolyAvoidanceProbability(e.Player, e.State, red) == 1);
            }),
            ("Trade reachability counts distinct destinations across GO without weighting", () =>
            {
                var e = Engine(2000, 39, [], []);
                var brown = e.State.Properties.Where(p => p.ColorGroup == "brown").ToArray();
                Check(Math.Abs(NoviceStrategy.MonopolyAvoidanceProbability(e.Player, e.State, [.. brown, .. brown]) - 9.0 / 11) < 1e-12);
            }),
            ("Initial color probabilities and infrastructure probabilities", () =>
            {
                var e = Engine(2000, 0, [], []);
                var novice = new NoviceStrategy(e);
                var groups = new[] { "brown", "lightBlue", "pink", "orange", "red", "yellow", "green", "darkBlue" };
                for (var i = 0; i < groups.Length; i++)
                {
                    Check(novice.PurchaseProbability(e.Player, e.State, e.State.Properties.First(p => p.ColorGroup == groups[i])) == (i + 1) / 8.0);
                }
                Check(novice.PurchaseProbability(e.Player, e.State, Property(e, "reading-railroad")) == 0.25);
                Check(novice.PurchaseProbability(e.Player, e.State, Property(e, "electric-company")) == 0);
            }),
            ("Existing group ownership makes purchases certain", () =>
            {
                var e = Engine(2000, 0, ["new-york-avenue", "reading-railroad"], []);
                var novice = new NoviceStrategy(e);
                Check(novice.PurchaseProbability(e.Player, e.State, Property(e, "st-james-place")) == 1);
                Check(novice.PurchaseProbability(e.Player, e.State, Property(e, "pennsylvania-railroad")) == 1);
            }),
            ("Blocking applies to streets, railroads and utilities", () =>
            {
                var e = Engine(2000, 0, [], [], ["mediterranean-avenue", "reading-railroad", "b-and-o-railroad", "short-line", "water-works"]);
                var novice = new NoviceStrategy(e);
                foreach (var id in new[] { "baltic-avenue", "pennsylvania-railroad", "electric-company" })
                {
                    Check(novice.PurchaseProbability(e.Player, e.State, Property(e, id)) == 1);
                }
            }),
            ("Probability threshold is strict and sampling is deferred", () =>
            {
                foreach (var sample in new[] { 0.124, 0.125 })
                {
                    var e = Engine(60, 1, [], [Buy("mediterranean-avenue")]);
                    var random = new FixedRandom(sample);
                    var action = new NoviceStrategy(e, random).PostRoll(e.Player, e.State);
                    Check(random.Calls == 0);
                    action();
                    Check(e.Executed.Count == (sample < 0.125 ? 1 : 0) && random.Calls == 1);
                }
            }),
            ("Certain purchases consume no random sample and require affordability", () =>
            {
                var e = Engine(399, 39, [], [Buy("boardwalk")]);
                var random = new FixedRandom(0.999);
                var novice = new NoviceStrategy(e, random);
                novice.PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0 && random.Calls == 0);
                e.Player = e.Player with { CashBalance = 400 };
                novice.PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 1 && e.Player.CashBalance == 0 && random.Calls == 0);
            }),
            ("Declined purchase is sampled once per landing", () =>
            {
                var e = Engine(1000, 1, [], [Buy("mediterranean-avenue")]);
                var random = new FixedRandom(0.9);
                var novice = new NoviceStrategy(e, random);
                novice.PostRoll(e.Player, e.State)();
                novice.PostRoll(e.Player, e.State)();
                Check(random.Calls == 1 && e.Executed.Count == 0);
                e.State = e.State with { RollNumber = e.State.RollNumber + 1 };
                novice.PostRoll(e.Player, e.State)();
                Check(random.Calls == 2);
            }),
            ("Never initiates a trade", () =>
            {
                var e = Engine(500, 0, ["mediterranean-avenue"], [Trade(Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40))], ["oriental-avenue"]);
                var novice = new NoviceStrategy(e);
                novice.PreRoll(e.Player, e.State)();
                novice.PostRoll(e.Player, e.State)();
                Check(!novice.InitiatesTrades && e.Executed.Count == 0);
            }),
            ("Samples qualifying mutual monopolies even when receiving the cheaper group", () =>
            {
                var e = Engine(500, 0, ["mediterranean-avenue", "pennsylvania-avenue"], [], ["baltic-avenue", "pacific-avenue", "north-carolina-avenue"]);
                Check(new NoviceStrategy(e, new FixedRandom(0)).AcceptTrade(e.Player, e.State, Deal(["pennsylvania-avenue"], ["baltic-avenue"], receivedCash: 260)));
            }),
            ("Rejects distance probability below cutoff without drawing randomness", () =>
            {
                var (e, trade) = Mutual(["lightBlue"], ["yellow"], 0);
                var random = new FixedRandom(0);
                var novice = new NoviceStrategy(e, random);
                Check(novice.TradeAcceptanceProbability(e.Player, e.State, trade) == 0.5);
                Check(!novice.AcceptTrade(e.Player, e.State, trade) && random.Calls == 0);
            }),
            ("Exactly five eighths is sampled with a strict random threshold", () =>
            {
                var (e, trade) = Mutual(["brown"], ["yellow"], 0);
                foreach (var sample in new[] { 0.624, 0.625 })
                {
                    var random = new FixedRandom(sample);
                    var novice = new NoviceStrategy(e, random);
                    Check(novice.TradeAcceptanceProbability(e.Player, e.State, trade) == 0.625);
                    Check(novice.AcceptTrade(e.Player, e.State, trade) == (sample < 0.625) && random.Calls == 1);
                }
            }),
            ("Reachability can push a distant mutual monopoly below cutoff", () =>
            {
                var (e, trade) = Mutual(["brown"], ["green"], 29);
                var random = new FixedRandom(0);
                var novice = new NoviceStrategy(e, random);
                Check(Math.Abs(novice.TradeAcceptanceProbability(e.Player, e.State, trade) - 0.75 * 8 / 11) < 1e-12);
                Check(!novice.AcceptTrade(e.Player, e.State, trade) && random.Calls == 0);
            }),
            ("Qualifying risk-adjusted probability is sampled and cached", () =>
            {
                var (e, trade) = Mutual(["brown"], ["darkBlue"], 29);
                var probability = 0.875 * 9 / 11;
                foreach (var sample in new[] { probability - 0.001, probability + 0.001 })
                {
                    var random = new FixedRandom(sample);
                    var novice = new NoviceStrategy(e, random);
                    Check(Math.Abs(novice.TradeAcceptanceProbability(e.Player, e.State, trade) - probability) < 1e-12);
                    Check(novice.AcceptTrade(e.Player, e.State, trade) == (sample < probability));
                    Check(novice.AcceptTrade(e.Player, e.State, trade) == (sample < probability) && random.Calls == 1);
                }
            }),
            ("Infrastructure uses unit distance and still applies reachability", () =>
            {
                var (e, trade) = Mutual(["utility"], ["railroad"], 29);
                var novice = new NoviceStrategy(e, new FixedRandom(0));
                Check(Math.Abs(novice.TradeAcceptanceProbability(e.Player, e.State, trade) - 10.0 / 11) < 1e-12);
                Check(novice.AcceptTrade(e.Player, e.State, trade));
            }),
            ("Trade cache normalizes bundles, refreshes each roll and rechecks legality", () =>
            {
                var (e, trade) = Mutual(["brown", "lightBlue"], ["green", "darkBlue"], 0);
                var random = new FixedRandom(0.7);
                var novice = new NoviceStrategy(e, random);
                Check(novice.AcceptTrade(e.Player, e.State, trade));
                var reordered = trade with { PropertyIdsGiven = trade.PropertyIdsGiven.Reverse().ToArray(), PropertyIdsReceived = trade.PropertyIdsReceived.Reverse().ToArray() };
                Check(novice.AcceptTrade(e.Player, e.State, reordered) && random.Calls == 1);
                e.State = e.State with { RollNumber = e.State.RollNumber + 1 };
                Check(novice.AcceptTrade(e.Player, e.State, trade) && random.Calls == 2);
                Check(!novice.AcceptTrade(e.Player, e.State, trade with { CashPaid = trade.CashPaid + 1 }) && random.Calls == 2);
            }),
            ("Multiple monopolies use highest pair distance and union of opposing targets", () =>
            {
                var (e, trade) = Mutual(["brown", "lightBlue"], ["orange", "green"], 29);
                var novice = new NoviceStrategy(e);
                Check(Math.Abs(novice.TradeAcceptanceProbability(e.Player, e.State, trade) - 0.75 * 8 / 11) < 1e-12);
                (e, trade) = Mutual(["brown"], ["green", "railroad"], 29);
                novice = new NoviceStrategy(e);
                Check(Math.Abs(novice.TradeAcceptanceProbability(e.Player, e.State, trade) - 7.0 / 11) < 1e-12);
            }),
            ("Rejects an opponent monopoly without a new monopoly in return", () =>
            {
                var e = Engine(500, 0, ["pennsylvania-avenue"], [], ["baltic-avenue", "pacific-avenue", "north-carolina-avenue"]);
                Check(!new NoviceStrategy(e).AcceptTrade(e.Player, e.State, Deal(["pennsylvania-avenue"], ["baltic-avenue"], receivedCash: 260)));
            }),
            ("Existing monopoly does not justify giving away a new opposing monopoly", () =>
            {
                var e = Engine(500, 0, ["mediterranean-avenue", "baltic-avenue", "electric-company"], [], ["water-works"]);
                Check(!new NoviceStrategy(e).AcceptTrade(e.Player, e.State, Deal(["electric-company"], [], receivedCash: 150)));
            }),
            ("Accepts balanced trades without monopoly completion and rejects unbalanced ones", () =>
            {
                var e = Engine(500, 0, ["mediterranean-avenue"], [], ["oriental-avenue"]);
                var novice = new NoviceStrategy(e);
                Check(novice.AcceptTrade(e.Player, e.State, Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40)));
                Check(!novice.AcceptTrade(e.Player, e.State, Deal(["mediterranean-avenue"], ["oriental-avenue"])));
            }),
            ("Waits to build until another player is within dice reach", () =>
            {
                var e = Engine(500, 0, ["mediterranean-avenue", "baltic-avenue"], [Build("mediterranean-avenue")]);
                e.State = e.State with { OtherPlayers = e.State.OtherPlayers.Select(p => p with { BoardPosition = 10 }).ToArray() };
                new NoviceStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0);
            }),
            ("Spreads upgrades across threatened groups within a full turn", () =>
            {
                var owned = Board.Where(p => p.ColorGroup is "brown" or "lightBlue").Select(p => p.PropertyId).ToArray();
                var e = Engine(1000, 0, owned, [Build("mediterranean-avenue"), Build("oriental-avenue")]);
                e.State = e.State with { OtherPlayers = e.State.OtherPlayers.Select(p => p with { BoardPosition = 39 }).ToArray() };
                e.AfterAction = a => a.PropertyId == "mediterranean-avenue" ? [Build("baltic-avenue"), Build("oriental-avenue")] : [];
                new NoviceStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["build-mediterranean-avenue", "build-oriental-avenue"]));
            }),
            ("Jail release prefers a card and otherwise preserves purchase affordability", () =>
            {
                var e = Jailed(200, [Fine(), Card()]);
                new NoviceStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["card"]));
                e = Jailed(189, [Fine()]);
                new NoviceStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0);
                e = Jailed(190, [Fine()]);
                new NoviceStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["fine"]));
            }),
            ("Jail risk uses dice weights and permits exactly 50 percent", () =>
            {
                string[] other = ["electric-company", "virginia-avenue", "pennsylvania-railroad", "st-james-place", "tennessee-avenue"];
                var e = Jailed(200, [Fine()], other);
                var novice = new NoviceStrategy(e);
                Check(novice.AssessJail(e.Player, e.State).DebtProbability == 0.5);
                novice.PreRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["fine"]));
                e = Jailed(200, [Card(), Fine()], [.. other, "kentucky-avenue"]);
                novice = new NoviceStrategy(e);
                Check(novice.AssessJail(e.Player, e.State).DebtProbability > 0.5);
                novice.PreRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0);
            }),
            ("Waits in jail without a certain purchase or during post-roll", () =>
            {
                var e = Jailed(500, [Card()]);
                e.Player = e.Player with { OwnedPropertyIds = [] };
                new NoviceStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0);
                e = Jailed(500, [Card()]);
                new NoviceStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0);
            }),
            ("Default market dispatch honors novice trade restrictions", () =>
            {
                var e = Engine(500, 0, ["electric-company"], [], ["water-works"]);
                var novice = new NoviceStrategy(e);
                var policy = new DefaultStrategyMarketPolicy(playerPolicies: _ => novice);
                Check(!policy.AcceptTrade(e.Player, e.State, Deal(["electric-company"], [], receivedCash: 150)));
            }),
            ("Auction decisions are cached and allow cash above valuation after fees", () =>
            {
                var e = Engine(500, 0, [], []);
                var random = new FixedRandom(0.1);
                var novice = new NoviceStrategy(e, random);
                var bundle = new AssetBundle { PropertyIds = ["mediterranean-avenue"] };
                Check(novice.MaximumBid(e.Player, e.State, bundle, 60, 3) == 497);
                Check(novice.MaximumBid(e.Player, e.State, bundle, 60, 3) == 497 && random.Calls == 1);
                Check(novice.MaximumBid(e.Player, e.State, new() { PropertyIds = ["electric-company"] }, 150, 0) == 0 && random.Calls == 1);
                Check(novice.MaximumBid(e.Player with { HasDeclaredBankruptcy = true }, e.State, bundle, 60, 0) == 0);
            }),
            ("Engine offers mutual-monopoly exchanges but filters unilateral ones", () =>
            {
                foreach (var mutual in new[] { false, true })
                {
                    Player[] players =
                    [
                        new() { Id = "a", Name = "A", Strategy = "aggressive", CashBalance = 1000, OwnedPropertyIds = ["pacific-avenue", "north-carolina-avenue", "baltic-avenue"] },
                        new() { Id = "b", Name = "B", Strategy = "novice", CashBalance = 1000, OwnedPropertyIds = mutual ? ["pennsylvania-avenue", "mediterranean-avenue"] : ["pennsylvania-avenue"] }
                    ];
                    var options = new MonoSimEngineOptions
                    {
                        RandomSeed = 1, MaximumRounds = 1, DiceRoller = () => [1, 2],
                        StrategyFactory = (p, executor) => p.Id == "a" ? new SwapScript(executor) : new NoviceStrategy(executor, new FixedRandom(0))
                    };
                    var final = new MonoSimEngine(players, NullLogger.Instance, options).Play();
                    Check(final.Players.Single(p => p.Id == "a").OwnedPropertyIds.Contains("pennsylvania-avenue") == mutual);
                    Check(final.Players.Single(p => p.Id == "b").OwnedPropertyIds.Contains("baltic-avenue") == mutual);
                }
            }),
            ("Engine registers novice strategy with repeatable seeded games", () =>
            {
                Player[] players = [new() { Id = "a", Name = "A", Strategy = "novice", CashBalance = 1500 }, new() { Id = "b", Name = "B", Strategy = "novice", CashBalance = 1500 }];
                var options = new MonoSimEngineOptions { RandomSeed = 37, MaximumRounds = 25 };
                var first = new MonoSimEngine(players, NullLogger.Instance, options).Play();
                var second = new MonoSimEngine(players, NullLogger.Instance, options).Play();
                Check(first.WinnerIds.SequenceEqual(second.WinnerIds));
                Check(first.Players.Zip(second.Players).All(p => p.First.CashBalance == p.Second.CashBalance && p.First.OwnedPropertyIds.SequenceEqual(p.Second.OwnedPropertyIds) && p.First.FinancialLedger.SequenceEqual(p.Second.FinancialLedger)));
            })
        };
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS Novice: {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL Novice: {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} novice strategy checks passed.");
        return failures;
    }

    private static PropertyState Property(TestEngine e, string id) => e.State.Properties.Single(p => p.PropertyId == id);
    private static (TestEngine Engine, PropertyTrade Trade) Mutual(string[] mine, string[] theirs, int position)
    {
        PropertyState[] Group(string name) => Board.Where(p => p.ColorGroup == name ||
            name == "railroad" && p.Type == PropertyType.Railroad || name == "utility" && p.Type == PropertyType.Utility).ToArray();
        var mySets = mine.Select(Group).ToArray();
        var theirSets = theirs.Select(Group).ToArray();
        var received = mySets.Select(g => g[^1]).ToArray();
        var given = theirSets.Select(g => g[^1]).ToArray();
        var myOwned = mySets.SelectMany(g => g.SkipLast(1)).Concat(given).Select(p => p.PropertyId).ToArray();
        var theirOwned = theirSets.SelectMany(g => g.SkipLast(1)).Concat(received).Select(p => p.PropertyId).ToArray();
        var difference = received.Sum(p => p.SalePrice) - given.Sum(p => p.SalePrice);
        var trade = Deal(given.Select(p => p.PropertyId).ToArray(), received.Select(p => p.PropertyId).ToArray(),
            paid: Math.Max(0, difference), receivedCash: Math.Max(0, -difference));
        return (Engine(5000, position, myOwned, [], theirOwned), trade);
    }
    private static TestEngine Jailed(decimal cash, StrategyAction[] actions, string[]? other = null)
    {
        var e = Engine(cash, 10, ["st-charles-place"], actions, other);
        e.Player = e.Player with { IsInJail = true };
        e.AfterAction = _ => [];
        return e;
    }
    private static void Check(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Unexpected novice decision.");
        }
    }
    private sealed class SwapScript(IStrategyActionExecutor executor) : IPlayingStrategy
    {
        public Action PreRoll(Player player, GameState state) => () => Run(player, state);
        public Action PostRoll(Player player, GameState state) => () => Run(player, state);
        private void Run(Player player, GameState state)
        {
            var swap = state.AvailableActions.FirstOrDefault(a => a.Trade is { } t &&
                t.PropertyIdsGiven.SequenceEqual(["baltic-avenue"]) && t.PropertyIdsReceived.SequenceEqual(["pennsylvania-avenue"]));
            if (swap is not null)
            {
                executor.Execute(player.Id, swap);
            }
            ((IPlayerTurnController)executor).DeclareActionsComplete(player.Id);
        }
    }

    private sealed class FixedRandom(double sample) : Random
    {
        public int Calls { get; private set; }
        public override double NextDouble()
        {
            Calls++;
            return sample;
        }
    }
}
