using System.Text.Json;
using MonoSim.Models;
using MonoSim.Strategies;

internal static class InfrastructureStrategyTests
{
    internal static readonly PropertyState[] Board = LoadBoard();
    private static readonly string[] Infrastructure = Board.Where(p => p.Type != PropertyType.Street).Select(p => p.PropertyId).ToArray();

    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("Infrastructure purchases execute before speculative colors and selection is deferred", () =>
            {
                var e = Engine(600, 5, [], [Buy("oriental-avenue"), Buy("reading-railroad")]);
                var action = new InfrastructureStrategy(e).PostRoll(e.Player, e.State);
                Check(e.Executed.Count == 0, "Selection executed early.");
                action();
                Check(e.Executed.SequenceEqual(["buy-reading-railroad", "buy-oriental-avenue"]), "Did not prioritize infrastructure.");
            }),
            ("A utility ahead sets a 150 reserve rather than a fixed 200 reserve", () =>
            {
                var e = Run(250, 9, [], [Buy("oriental-avenue")]);
                Check(e.Player.CashBalance == 150 && e.Executed.Count == 1, "Incorrect utility reserve.");
            }),
            ("A railroad ahead blocks a purchase that would leave only 150", () =>
            {
                var e = Run(250, 12, [], [Buy("oriental-avenue")]);
                Check(e.Executed.Count == 0, "Spent the railroad reserve.");
            }),
            ("The reserve wraps past GO", () =>
            {
                Check(Run(299, 39, [], [Buy("oriental-avenue")]).Executed.Count == 0, "Failed to reserve across GO.");
                Check(Run(300, 39, [], [Buy("oriental-avenue")]).Player.CashBalance == 200, "Rejected an exact reserve boundary.");
            }),
            ("Owned infrastructure is skipped when calculating the next available target", () =>
            {
                var e = Run(250, 9, [], [Buy("oriental-avenue")], ["electric-company"]);
                Check(e.Executed.Count == 0, "Reserved for an already-owned utility.");
            }),
            ("Buying current infrastructure still protects the next target's reserve", () =>
            {
                Check(Run(200, 5, [], [Buy("reading-railroad")]).Executed.Count == 0, "Spent cash earmarked for the next utility.");
                var e = Run(350, 5, [], [Buy("reading-railroad")]);
                Check(e.Player.CashBalance == 150 && e.Player.OwnedPropertyIds.Contains("reading-railroad"), "Did not acquire affordable infrastructure.");
            }),
            ("A color can be held without an agreed follow-up trade", () =>
            {
                var e = Run(400, 9, [], [Buy("oriental-avenue")]);
                Check(e.Player.OwnedPropertyIds.Contains("oriental-avenue"), "Refused speculative trade leverage.");
            }),
            ("Existing color leverage is exchanged for missing infrastructure promptly", () =>
            {
                var deal = Deal(["oriental-avenue"], ["reading-railroad"], paid: 100);
                var e = Run(400, 9, ["oriental-avenue"], [Buy("vermont-avenue"), Trade(deal)], ["reading-railroad"]);
                Check(e.Executed[0] == "trade" && e.Player.OwnedPropertyIds.Contains("reading-railroad") && !e.Player.OwnedPropertyIds.Contains("oriental-avenue"), "Held leverage instead of taking infrastructure.");
            }),
            ("A mortgage trade's bank fees count against the reserve", () =>
            {
                var deal = Deal(["oriental-avenue"], ["reading-railroad"], paid: 100, fees: 10);
                var e = Run(250, 9, ["oriental-avenue"], [Trade(deal)], ["reading-railroad"]);
                Check(e.Executed.Count == 0, "Ignored mandatory transfer interest.");
            }),
            ("Infrastructure is never surrendered as trade leverage", () =>
            {
                var deal = Deal(["reading-railroad"], ["electric-company"]);
                var e = Run(500, 9, ["reading-railroad"], [Trade(deal)], ["electric-company"]);
                Check(e.Executed.Count == 0, "Traded away infrastructure.");
            }),
            ("Oldest color leverage is sold before buying more", () =>
            {
                var oldSale = Trade(Deal(["oriental-avenue"], [], receivedCash: 100)) with { Id = "old-sale" };
                var newSale = Trade(Deal(["vermont-avenue"], [], receivedCash: 100)) with { Id = "new-sale" };
                var e = Run(500, 9, ["oriental-avenue", "vermont-avenue"], [Buy("connecticut-avenue"), newSale, oldSale]);
                Check(e.Executed.Take(2).SequenceEqual(["old-sale", "new-sale"]), "Did not unload older leverage first.");
                Check(e.Player.FinancialLedger.Take(2).All(t => t.Amount > 0), "Cash sales were not ledger credits.");
            }),
            ("A cash sale can help restore an already depleted reserve", () =>
            {
                var sale = Trade(Deal(["oriental-avenue"], [], receivedCash: 50));
                var e = Run(50, 9, ["oriental-avenue"], [sale, Buy("vermont-avenue")]);
                Check(e.Executed.SequenceEqual(["trade"]) && e.Player.CashBalance == 100, "Did not restore cash without further discretionary spending.");
            }),
            ("No bank reserve is needed when all infrastructure is already owned by someone", () =>
            {
                var e = Run(100, 9, [], [Buy("oriental-avenue")], Infrastructure);
                Check(e.Player.CashBalance == 0 && e.Executed.Count == 1, "Reserved cash for unavailable bank property.");
            }),
            ("An infrastructure monopoly permits mortgage acquisitions of color properties", () =>
            {
                var action = Buy("oriental-avenue") with { Type = StrategyActionType.BuyMortgagedProperty, Cost = 110 };
                var e = Engine(500, 9, Infrastructure, [action], ["oriental-avenue"]);
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId == "oriental-avenue" ? p with { IsMortgaged = true } : p).ToArray() };
                new InfrastructureStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Player.OwnedPropertyIds.Contains("oriental-avenue"), "Did not expand into color groups.");
            }),
            ("Both utility and railroad sets are required before color development", () =>
            {
                foreach (var infra in new[] { Infrastructure.Where(id => id != "water-works").ToArray(), Infrastructure.Where(id => id != "short-line").ToArray() })
                {
                    var e = Run(1500, 9, [.. infra, "oriental-avenue", "vermont-avenue", "connecticut-avenue"], [Build("oriental-avenue")]);
                    Check(e.Executed.Count == 0, "Started development before owning both sets.");
                }
            }),
            ("New ownership immediately unlocks color development in the refreshed state", () =>
            {
                var owned = Infrastructure.Where(id => id != "water-works").Concat(["oriental-avenue", "vermont-avenue", "connecticut-avenue"]).ToArray();
                var e = Engine(1000, 28, owned, [Buy("water-works")]);
                e.AfterAction = a => a.PropertyId == "water-works" ? [Build("oriental-avenue")] : [];
                new InfrastructureStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["buy-water-works", "build-oriental-avenue"]), "Did not switch phases after acquiring the last target.");
            }),
            ("After infrastructure, monopoly-completing trades are preferred", () =>
            {
                var deal = Deal([], ["connecticut-avenue"], paid: 120);
                var e = Run(500, 9, [.. Infrastructure, "oriental-avenue", "vermont-avenue"], [Buy("kentucky-avenue"), Trade(deal)], ["connecticut-avenue"]);
                Check(e.Executed[0] == "trade", "Did not prioritize a new monopoly.");
            }),
            ("Before infrastructure, color mortgage trades and building are deferred", () =>
            {
                var action = Buy("oriental-avenue") with { Type = StrategyActionType.BuyMortgagedProperty };
                var e = Engine(1000, 9, [], [action], ["oriental-avenue"]);
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId == "oriental-avenue" ? p with { IsMortgaged = true } : p).ToArray() };
                new InfrastructureStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0, "Speculated on color mortgage trades before infrastructure.");
            }),
            ("A newly jailed player waits until pre-roll to decide on early release", () =>
            {
                var e = Jailed(500, [Fine(), Card()]);
                new InfrastructureStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0 && e.Player.IsInJail, "Released in the post-roll phase.");
            }),
            ("A held jail card is used before paying when infrastructure is ahead", () =>
            {
                var e = Jailed(500, [Fine(), Card()]);
                new InfrastructureStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["card"]) && e.Player.CashBalance == 500 && !e.Player.IsInJail, "Did not prefer a card.");
            }),
            ("The jail fine is paid when infrastructure is ahead and the reserve survives", () =>
            {
                var e = Jailed(200, [Fine()]);
                new InfrastructureStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["fine"]) && e.Player.CashBalance == 150 && !e.Player.IsInJail, "Did not pay the fine while keeping the utility reserve.");
            }),
            ("The jail fine cannot consume the infrastructure purchase reserve", () =>
            {
                var e = Jailed(199, [Fine()]);
                new InfrastructureStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0 && e.Player.IsInJail, "Used the purchase reserve for a voluntary fine.");
            }),
            ("A free jail card is usable even when cash is below the reserve", () =>
            {
                var e = Jailed(0, [Card()]);
                new InfrastructureStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["card"]) && !e.Player.IsInJail, "Refused free release.");
            }),
            ("If only Reading Railroad is unowned behind Jail the player waits", () =>
            {
                var e = Jailed(500, [Card(), Fine()], Infrastructure.Where(id => id != "reading-railroad").ToArray());
                new InfrastructureStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0 && e.Player.IsInJail, "Treated infrastructure behind Jail as ahead.");
            }),
            ("When all infrastructure is owned by someone the player waits", () =>
            {
                var e = Jailed(500, [Card(), Fine()], Infrastructure);
                new InfrastructureStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0, "Left jail without any unowned target ahead.");
            }),
            ("Equal-progress color swaps are skipped after infrastructure", () =>
            {
                var e = Run(500, 9, [.. Infrastructure, "oriental-avenue"], [Trade(Deal(["oriental-avenue"], ["vermont-avenue"]))], ["vermont-avenue"]);
                Check(e.Executed.Count == 0, "Churned properties without progressing toward a monopoly.");
            })
        };

        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"PASS Infrastructure: {name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL Infrastructure: {name}: {error.Message}"); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} infrastructure strategy checks passed.");
        return failures;
    }

    private static void Check(bool condition, string message) { if (!condition)
        {
            throw new Exception(message);
        }
    }
    internal static StrategyAction Buy(string id) => new() { Id = $"buy-{id}", Type = StrategyActionType.BuyProperty, PropertyId = id, Cost = Board.Single(p => p.PropertyId == id).SalePrice };
    internal static StrategyAction Build(string id) => new() { Id = $"build-{id}", Type = StrategyActionType.BuildHouse, PropertyId = id, Cost = 50, RentIncrease = 24 };
    internal static PropertyTrade Deal(string[] given, string[] received, decimal paid = 0, decimal receivedCash = 0, decimal fees = 0) => new()
    { OtherPlayerId = "other", PropertyIdsGiven = given, PropertyIdsReceived = received, CashPaid = paid, CashReceived = receivedCash, BankFees = fees };
    internal static StrategyAction Trade(PropertyTrade trade) => new() { Id = "trade", Type = StrategyActionType.TradeProperties, Trade = trade, Cost = trade.NetCost };
    internal static StrategyAction Card() => new() { Id = "card", Type = StrategyActionType.UseGetOutOfJailFreeCard, Cost = 0 };
    internal static StrategyAction Fine() => new() { Id = "fine", Type = StrategyActionType.PayJailFine, Cost = 50 };

    private static TestEngine Jailed(decimal cash, StrategyAction[] actions, string[]? otherOwned = null)
    {
        var e = Engine(cash, 10, [], actions, otherOwned);
        e.Player = e.Player with { IsInJail = true };
        e.AfterAction = _ => [];
        return e;
    }

    private static TestEngine Run(decimal cash, int position, string[] owned, StrategyAction[] actions, string[]? otherOwned = null)
    {
        var e = Engine(cash, position, owned, actions, otherOwned);
        new InfrastructureStrategy(e).PostRoll(e.Player, e.State)();
        return e;
    }

    internal static TestEngine Engine(decimal cash, int position, string[] owned, StrategyAction[] actions, string[]? otherOwned = null)
    {
        var player = new Player { Id = "infra", Name = "Infra", Strategy = "infrastructure", CashBalance = cash, BoardPosition = position, OwnedPropertyIds = owned };
        var other = new Player { Id = "other", Name = "Other", Strategy = "other", CashBalance = 5000, OwnedPropertyIds = otherOwned ?? [] };
        return new(player, new GameState { TurnNumber = 1, OtherPlayers = [other], AvailableActions = actions,
            Properties = Board.Select(p => p with { IsOwned = owned.Contains(p.PropertyId) || other.OwnedPropertyIds.Contains(p.PropertyId) }).ToArray() });
    }

    private static PropertyState[] LoadBoard()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "monopoly-us-classic.json")));
        return doc.RootElement.GetProperty("board").EnumerateArray().Where(p => p.TryGetProperty("price", out _)).Select(p => new PropertyState
        {
            PropertyId = p.GetProperty("id").GetString()!, PropertyName = p.GetProperty("name").GetString()!, SalePrice = p.GetProperty("price").GetDecimal(),
            BoardPosition = p.GetProperty("position").GetInt32(), ColorGroup = p.TryGetProperty("colorGroup", out var c) ? c.GetString() : null,
            Type = p.GetProperty("type").GetString() switch { "railroad" => PropertyType.Railroad, "utility" => PropertyType.Utility, _ => PropertyType.Street }
        }).ToArray();
    }

    internal sealed class TestEngine(Player player, GameState state) : IStrategyActionExecutor
    {
        public Player Player { get; set; } = player;
        public GameState State { get; set; } = state;
        public List<string> Executed { get; } = [];
        public Func<StrategyAction, StrategyAction[]>? AfterAction { get; set; }

        public StrategyTurnState Execute(string playerId, StrategyAction action)
        {
            if (playerId != Player.Id || !State.AvailableActions.Contains(action) || action.Cost > Player.CashBalance)
            {
                throw new InvalidOperationException("Invalid option.");
            }

            Executed.Add(action.Id);
            var balance = Player.CashBalance - action.Cost;
            Player = Player with { CashBalance = balance, FinancialLedger = [.. Player.FinancialLedger, new(action.Id, -action.Cost, balance)] };
            if (action.Type is StrategyActionType.UseGetOutOfJailFreeCard or StrategyActionType.PayJailFine)
            {
                Player = Player with { IsInJail = false };
            }

            if (action.Type is StrategyActionType.BuyProperty or StrategyActionType.BuyMortgagedProperty)
            {
                Player = Player with { OwnedPropertyIds = [.. Player.OwnedPropertyIds, action.PropertyId!] };
                State = State with { Properties = State.Properties.Select(p => p.PropertyId == action.PropertyId ? p with { IsOwned = true } : p).ToArray() };
            }
            if (action.Trade is { } trade)
            {
                Player = Player with { OwnedPropertyIds = Player.OwnedPropertyIds.Except(trade.PropertyIdsGiven).Concat(trade.PropertyIdsReceived).ToArray() };
                State = State with { OtherPlayers = State.OtherPlayers.Select(p => p.Id == trade.OtherPlayerId ? p with
                {
                    CashBalance = p.CashBalance + trade.CashPaid - trade.CashReceived,
                    OwnedPropertyIds = p.OwnedPropertyIds.Except(trade.PropertyIdsReceived).Concat(trade.PropertyIdsGiven).ToArray()
                } : p).ToArray() };
            }
            if (action.Type == StrategyActionType.BuildHouse)
            {
                State = State with { Properties = State.Properties.Select(p => p.PropertyId == action.PropertyId ? p with { HouseCount = p.HouseCount + 1 } : p).ToArray() };
            }

            if (action.Type == StrategyActionType.BuildHotel)
            {
                State = State with { Properties = State.Properties.Select(p => p.PropertyId == action.PropertyId ? p with { HouseCount = 0, HotelCount = 1 } : p).ToArray() };
            }

            if (action.Type == StrategyActionType.UnmortgageProperty)
            {
                State = State with { Properties = State.Properties.Select(p => p.PropertyId == action.PropertyId ? p with { IsMortgaged = false } : p).ToArray() };
            }

            State = State with { AvailableActions = AfterAction?.Invoke(action) ?? State.AvailableActions.Where(a => a.Id != action.Id).ToArray() };
            return new(Player, State);
        }
    }
}
