using Microsoft.Extensions.Logging;
using MonoSim;
using MonoSim.Models;
using MonoSim.Strategies;
using static InfrastructureStrategyTests;

internal static class AuctionTests
{
    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("Increment tiers include all boundaries", () =>
            {
                foreach (var (bid, increment) in new[] { (0m, 10m), (99.99m, 10m), (100m, 25m), (499.99m, 25m), (500m, 50m), (1000m, 50m), (1000.01m, 100m) })
                {
                    Check(AuctionRules.MinimumIncrement(bid) == increment);
                }
            }),
            ("Auction collateral counts mortgage principal for both mortgage states and cards", () =>
            {
                var e = Engine(500, 0, [], []);
                var bundle = new AssetBundle { PropertyIds = ["mediterranean-avenue", "baltic-avenue"], CardIds = ["chance-jail-free"] };
                Check(AuctionRules.LiquidValue(bundle, e.State) == 110);
                var mortgaged = e.State with { Properties = e.State.Properties.Select(p => bundle.PropertyIds.Contains(p.PropertyId) ? p with { IsMortgaged = true } : p).ToArray() };
                Check(AuctionRules.LiquidValue(bundle, mortgaged) == 110);
            }),
            ("Aggressive doubles increments, caps at cash and passes below minimum", () =>
            {
                var e = Actor("aggressive", 500);
                var policy = new DefaultStrategyMarketPolicy();
                var auction = Context("boardwalk", 400, 100);
                Check(policy.NextBid(e.Player, e.State, auction) == 150);
                Check(policy.NextBid(e.Player with { CashBalance = 140 }, e.State, auction) == 140);
                Check(policy.NextBid(e.Player with { CashBalance = 124 }, e.State, auction) is null);
                Check(policy.NextBid(e.Player, e.State, auction with { CurrentBid = 99 }) == 119);
                Check(policy.NextBid(e.Player with { CashBalance = 2000 }, e.State, auction with { CurrentBid = 1000 }) == 1100);
            }),
            ("All target strategies double bids on their target bundles", () =>
            {
                foreach (var (strategy, property, owned) in new[]
                {
                    ("infrastructure", "reading-railroad", Array.Empty<string>()),
                    ("adjacent-group", "vermont-avenue", new[] { "oriental-avenue" }),
                    ("blue-green", "pacific-avenue", Array.Empty<string>())
                })
                {
                    var e = Actor(strategy, 1000, owned, [property]);
                    var price = e.State.Properties.Single(p => p.PropertyId == property).SalePrice;
                    Check(new DefaultStrategyMarketPolicy().NextBid(e.Player, e.State, Context(property, price, 100)) == 150);
                }
            }),
            ("Target reserves survive overbids and Blue-Green spends reserves on its final side", () =>
            {
                var e = Actor("blue-green", 500, [], ["pacific-avenue"]);
                var auction = Context("pacific-avenue", 300, 100);
                var policy = new DefaultStrategyMarketPolicy();
                Check(policy.NextBid(e.Player, e.State, auction) is null);
                Check(policy.NextBid(e.Player with { BoardPosition = 31 }, e.State, auction) == 150);
            }),
            ("Ordinary genuine bidders use single increments and reference ceilings", () =>
            {
                var e = Actor("conservative", 1000, ["park-place"]);
                var auction = Context("boardwalk", 400, 100);
                var policy = new DefaultStrategyMarketPolicy();
                Check(policy.NextBid(e.Player, e.State, auction) == 125);
                Check(policy.NextBid(e.Player, e.State, auction with { CurrentBid = 400 }) is null);
            }),
            ("Uninterested players bluff only against an active susceptible opponent", () =>
            {
                var e = Actor("conservative", 1000);
                var auction = Context("boardwalk", 400, 100);
                var policy = new DefaultStrategyMarketPolicy();
                Check(policy.MaximumBid(e.Player, e.State, auction.Bundle, 400, 0) == 0);
                Check(policy.NextBid(e.Player, e.State, auction) is null);
                e.State = e.State with { OtherPlayers = e.State.OtherPlayers.Select(p => p with { Strategy = "aggressive" }).ToArray() };
                Check(policy.NextBid(e.Player, e.State, auction) == 125);
                Check(policy.NextBid(e.Player, e.State, auction with { ActiveBidderIds = [e.Player.Id] }) is null);
                Check(policy.NextBid(e.Player, e.State, auction with { CurrentBid = 400 }) is null);
            }),
            ("Bluffs respect target reserves and do not use doubled increments on non-targets", () =>
            {
                var e = Actor("blue-green", 500);
                e.State = e.State with { OtherPlayers = e.State.OtherPlayers.Select(p => p with { Strategy = "aggressive" }).ToArray() };
                var auction = Context("boardwalk", 400, 100) with { Bundle = new() { PropertyIds = ["kentucky-avenue"] }, ReferenceValue = 220 };
                Check(new DefaultStrategyMarketPolicy().NextBid(e.Player, e.State, auction) is null);
                e.Player = e.Player with { CashBalance = 600 };
                Check(new DefaultStrategyMarketPolicy().NextBid(e.Player, e.State, auction) == 125);
            }),
            ("Novice can bluff despite declining an ordinary utility acquisition", () =>
            {
                var e = Actor("novice", 500);
                e.State = e.State with { OtherPlayers = e.State.OtherPlayers.Select(p => p with { Strategy = "aggressive" }).ToArray() };
                var novice = new NoviceStrategy(e, new Random(42));
                var policy = new DefaultStrategyMarketPolicy(playerPolicies: _ => novice);
                var auction = Context("electric-company", 150, 100);
                Check(novice.MaximumBid(e.Player, e.State, auction.Bundle, 150, 0) == 0);
                Check(policy.NextBid(e.Player, e.State, auction) == 125);
            }),
            ("Debt and provisional bankruptcy prevent bidding", () =>
            {
                var e = Actor("aggressive", 500);
                var policy = new DefaultStrategyMarketPolicy();
                var auction = Context("boardwalk", 400, 100);
                Check(policy.NextBid(e.Player with { HasDeclaredBankruptcy = true }, e.State, auction) is null);
                e.State = e.State with { CashObligations = [new(e.Player.Id, null, 50, "tax")] };
                Check(policy.NextBid(e.Player, e.State, auction) is null);
            }),
            ("Live bidding records actual raises and pushes an aggressive buyer above reference", () =>
            {
                var (final, log) = Run([P("s", "conservative", 0, ["mediterranean-avenue"]), P("b", "conservative", 500), P("a", "aggressive", 100)],
                    new() { PropertyIds = ["mediterranean-avenue"] });
                Check(log.Entries.Where(e => e.Event == "AuctionBid").Select(e => e.Amount).SequenceEqual([25m, 45m, 55m, 75m]));
                Check(final.Players.Single(p => p.Id == "a").OwnedPropertyIds.Contains("mediterranean-avenue"));
                Check(final.Players.Single(p => p.Id == "a").CashBalance == 25 && final.Players.Single(p => p.Id == "s").CashBalance == 75);
            }),
            ("A bluffer who wins must complete the sale when a target bidder stops", () =>
            {
                var (final, log) = Run([P("s", "conservative", 0, ["pacific-avenue"]), P("b", "conservative", 500), P("t", "blue-green", 500)],
                    new() { PropertyIds = ["pacific-avenue"] });
                Check(log.Entries.Any(e => e.Event == "AuctionPriceStruck" && e.Amount == 125));
                Check(final.Players.Single(p => p.Id == "b").OwnedPropertyIds.Contains("pacific-avenue") && final.Players.Single(p => p.Id == "b").CashBalance == 375);
                Check(final.Players.Single(p => p.Id == "t").CashBalance == 500);
            }),
            ("Mortgaged bundles open from principal and charge transfer fees separately", () =>
            {
                var bundle = new AssetBundle { PropertyIds = ["mediterranean-avenue", "baltic-avenue"] };
                var (final, log) = Run([P("s", "conservative", 0, bundle.PropertyIds), P("a", "aggressive", 100)], bundle, mortgage: true);
                Check(log.Entries.Any(e => e.Event == "AuctionOpeningAsk" && e.Amount == 30));
                Check(log.Entries.Single(e => e.Event == "AuctionPriceStruck").Amount == 50);
                Check(final.Players.Single(p => p.Id == "a").CashBalance == 44 && final.Players.Single(p => p.Id == "s").CashBalance == 110);
                Check(final.Properties.Where(p => bundle.PropertyIds.Contains(p.PropertyId)).All(p => p.IsMortgaged));
            }),
            ("Odd mortgage collateral rounds the opening ask upward and preserves fractional fees", () =>
            {
                var (final, log) = Run([P("s", "conservative", 0, ["electric-company"]), P("a", "aggressive", 100)],
                    new() { PropertyIds = ["electric-company"] }, mortgage: true);
                Check(log.Entries.Single(e => e.Event == "AuctionOpeningAsk").Amount == 38);
                Check(log.Entries.Single(e => e.Event == "AuctionPriceStruck").Amount == 58);
                Check(final.Players.Single(p => p.Id == "a").CashBalance == 34.5m);
            }),
            ("Card auctions use cash equivalence and return no-bid cards to their original deck", () =>
            {
                var seller = P("s", "conservative", 0) with { HeldGetOutOfJailFreeCardIds = ["chance-jail-free"] };
                var bundle = new AssetBundle { CardIds = ["chance-jail-free"] };
                var (final, log) = Run([seller, P("b", "conservative", 100)], bundle);
                Check(log.Entries.Single(e => e.Event == "AuctionOpeningAsk").Amount == 25);
                Check(final.Players.Single(p => p.Id == "b").HeldGetOutOfJailFreeCardIds.Contains("chance-jail-free"));
                Check(final.Players.Single(p => p.Id == "s").CashBalance == 35);
                (final, log) = Run([seller, P("b", "conservative", 20)], bundle);
                Check(log.Entries.Any(e => e.Event == "AuctionNoBids"));
                Check(final.Decks.Single(d => d.Id == "chance").CardIds[^1] == "chance-jail-free");
            }),
            ("No affordable opening raise returns assets to the bank for zero", () =>
            {
                var (final, log) = Run([P("s", "conservative", 0, ["mediterranean-avenue"]), P("a", "aggressive", 20)],
                    new() { PropertyIds = ["mediterranean-avenue"] });
                Check(log.Entries.Any(e => e.Event == "AuctionNoBids") && final.Properties.Single(p => p.PropertyId == "mediterranean-avenue").IsAvailable);
            }),
            ("Custom live policies can bid above the minimum and cannot exceed cash", () =>
            {
                var policy = new JumpPolicy();
                var (final, log) = Run([P("s", "conservative", 0, ["mediterranean-avenue"]), P("b", "conservative", 200)],
                    new() { PropertyIds = ["mediterranean-avenue"] }, market: policy);
                Check(log.Entries.Single(e => e.Event == "AuctionBid").Amount == 200 && final.Players.Single(p => p.Id == "b").CashBalance == 0);
            })
        };
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS Auction: {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL Auction: {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} auction checks passed.");
        return failures;
    }

    private static TestEngine Actor(string strategy, decimal cash, string[]? owned = null, string[]? other = null)
    {
        var e = Engine(cash, 20, owned ?? [], [], other);
        e.Player = e.Player with { Strategy = strategy };
        return e;
    }
    private static AuctionState Context(string id, decimal reference, decimal current) => new("seller", new() { PropertyIds = [id] }, reference,
        reference / 2, reference / 4, current, "other", ["infra", "other"]);
    private static Player P(string id, string strategy, decimal cash, string[]? owned = null) => new()
    {
        Id = id, Name = id, Strategy = strategy, CashBalance = cash, BoardPosition = 20, OwnedPropertyIds = owned ?? []
    };
    private static (GameState State, Recorder Log) Run(Player[] players, AssetBundle bundle, bool mortgage = false, IStrategyMarketPolicy? market = null)
    {
        var log = new Recorder();
        var options = new MonoSimEngineOptions
        {
            RandomSeed = 42, MaximumRounds = 1, DiceRoller = () => [1, 3], ShuffleDecks = false, MarketPolicy = market,
            StrategyFactory = (player, executor) => new AuctionScript(executor, player.Id == "s", bundle, mortgage)
        };
        return (new MonoSimEngine(players, log, options).Play(), log);
    }
    private static void Check(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Unexpected auction result.");
        }
    }
    private sealed class AuctionScript(IStrategyActionExecutor executor, bool seller, AssetBundle bundle, bool mortgage) : IPlayingStrategy
    {
        public Action PreRoll(Player p, GameState s) => () =>
        {
            if (seller)
            {
                if (mortgage)
                {
                    foreach (var id in bundle.PropertyIds)
                    {
                        executor.Execute(p.Id, s.AvailableActions.Single(a => a.Type == StrategyActionType.MortgageProperty && a.PropertyId == id));
                    }
                }
                executor.Execute(p.Id, new() { Id = "auction", Type = StrategyActionType.AuctionAssets, Bundle = bundle, Cost = 0 });
            }
            ((IPlayerTurnController)executor).DeclareActionsComplete(p.Id);
        };
        public Action PostRoll(Player p, GameState s) => () => ((IPlayerTurnController)executor).DeclareActionsComplete(p.Id);
    }
    private sealed class JumpPolicy : IStrategyMarketPolicy, IStrategyAuctionPolicy
    {
        public decimal? NextBid(Player p, GameState s, AuctionState auction) => 999;
        public decimal MaximumBid(Player p, GameState s, AssetBundle bundle, decimal reference, decimal fees) => 0;
        public bool AcceptTrade(Player p, GameState s, PropertyTrade trade) => false;
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
