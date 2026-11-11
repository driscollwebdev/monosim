using MonoSim.Models;
using MonoSim.Strategies;
using MonoSim.Trading;

internal static class ConservativeStrategyTests
{
    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("First landed street establishes the focus without side effects during selection", () =>
            {
                var engine = Engine([], [Buy("blue1")]);
                var strategy = new ConservativeStrategy(engine);
                var action = strategy.PostRoll(engine.Player, engine.State);
                Check(engine.Executed.Count == 0, "Selected action executed early.");
                action();
                engine.NextTurn([Buy("red3")]);
                strategy.PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.SequenceEqual(["buy-blue1"]), "Bought an unrelated property.");
                engine.NextTurn([Buy("blue2")]);
                strategy.PostRoll(engine.Player, engine.State)();
                Check(engine.Player.OwnedPropertyIds.SequenceEqual(["blue1", "blue2"]), "Did not keep pursuing the initial group.");
            }),
            ("A railroad does not establish a color monopoly", () =>
            {
                var engine = Engine([], [Buy("railroad", 200)]);
                new ConservativeStrategy(engine).PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.Count == 0, "Acquired a non-color property without a plan.");
            }),
            ("Unaffordable focus purchases are skipped", () =>
            {
                var engine = Engine(["blue1"], [Buy("blue2")], cash: 99);
                new ConservativeStrategy(engine).PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.Count == 0, "Overspent on the target group.");
            }),
            ("A fair cash trade can acquire another target property", () =>
            {
                var trade = new PropertyTrade { OtherPlayerId = "other", PropertyIdsReceived = ["blue2"], CashPaid = 100 };
                var engine = Engine(["blue1"], [Trade(trade)], ["blue2"]);
                new ConservativeStrategy(engine).PostRoll(engine.Player, engine.State)();
                Check(engine.Player.OwnedPropertyIds.Contains("blue2") && engine.Other.CashBalance == 1100, "Target trade did not transfer ownership and cash.");
            }),
            ("An unfavorable trade price is rejected", () =>
            {
                var trade = new PropertyTrade { OtherPlayerId = "other", PropertyIdsReceived = ["blue2"], CashPaid = 150 };
                var engine = Engine(["blue1"], [Trade(trade)], ["blue2"]);
                new ConservativeStrategy(engine).PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.Count == 0, "Accepted an unbalanced trade.");
            }),
            ("A last available property is bought only for a concrete mutually helpful trade", () =>
            {
                var deal = CompletionDeal();
                var engine = Engine(["blue1", "blue2"], [Buy("red3", 240) with { FollowUpTrade = deal }], ["blue3", "red1", "red2"]);
                engine.AfterAction = a => a.Type == StrategyActionType.BuyProperty ? [Trade(deal)] : [];
                new ConservativeStrategy(engine).PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.SequenceEqual(["buy-red3", "trade"]), "Did not execute the planned exchange.");
                Check(new[] { "blue1", "blue2", "blue3" }.All(engine.Player.OwnedPropertyIds.Contains), "Did not complete the focus group.");
                Check(new[] { "red1", "red2", "red3" }.All(engine.Other.OwnedPropertyIds.Contains), "Trade did not help the counterpart.");
                Check(engine.Player.CashBalance == 880 && engine.Other.CashBalance == 880, "Property-and-cash settlement is incorrect.");
            }),
            ("A speculative unrelated purchase is declined without an approved deal", () =>
            {
                var engine = Engine(["blue1", "blue2"], [Buy("red3", 240)], ["blue3", "red1", "red2"]);
                new ConservativeStrategy(engine).PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.Count == 0, "Bought speculative trade material.");
            }),
            ("The trade setup must be the last available property in its group", () =>
            {
                var engine = Engine(["blue1", "blue2"], [Buy("red3", 240) with { FollowUpTrade = CompletionDeal() }], ["blue3", "red1"]);
                new ConservativeStrategy(engine).PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.Count == 0, "Bought a setup while another group member was still available.");
            }),
            ("A setup must complete the player's own target monopoly", () =>
            {
                var engine = Engine(["blue1"], [Buy("red3", 240) with { FollowUpTrade = CompletionDeal() }], ["blue3", "red1", "red2"]);
                new ConservativeStrategy(engine).PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.Count == 0, "Bought a setup that cannot finish the target.");
            }),
            ("The focus group is never given away in a trade", () =>
            {
                var deal = new PropertyTrade { OtherPlayerId = "other", PropertyIdsGiven = ["blue1"], PropertyIdsReceived = ["blue2"] };
                var engine = Engine(["blue1"], [Trade(deal)], ["blue2"]);
                new ConservativeStrategy(engine).PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.Count == 0, "Traded away the focus group.");
            }),
            ("Cash conservation has no fixed reserve and development spends only net turn gains", () =>
            {
                var engine = Engine(["blue1", "blue2", "blue3"], [Build("blue1")], cash: 100);
                var strategy = new ConservativeStrategy(engine);
                strategy.PreRoll(engine.Player, engine.State)();
                Check(engine.Executed.Count == 0, "Drew from opening cash to build.");
                engine.Player = engine.Player with { CashBalance = 150 };
                strategy.PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.Count == 1 && engine.Player.CashBalance == 100, "Failed to spend the exact net gain without an arbitrary reserve.");
            }),
            ("One upgrade per full turn spans both phases and repeated calls", () =>
            {
                var engine = Engine(["blue1", "blue2", "blue3"], [Build("blue1")]);
                engine.AfterAction = _ => [Build("blue2")];
                var strategy = new ConservativeStrategy(engine);
                strategy.PreRoll(engine.Player, engine.State)();
                engine.Player = engine.Player with { CashBalance = 1200 };
                strategy.PostRoll(engine.Player, engine.State)();
                strategy.PreRoll(engine.Player, engine.State)();
                strategy.PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.Count == 1, "Built more than once in the same full turn.");
                engine.NextTurn([Build("blue2")]);
                strategy.PreRoll(engine.Player, engine.State)();
                engine.Player = engine.Player with { CashBalance = engine.Player.CashBalance + 50 };
                strategy.PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.Count == 2, "Upgrade allowance did not reset for the next turn.");
            }),
            ("Incomplete and mortgaged groups cannot be developed", () =>
            {
                foreach (var mortgaged in new[] { false, true })
                {
                    var engine = Engine(mortgaged ? ["blue1", "blue2", "blue3"] : ["blue1"], [Build("blue1")]);
                    if (mortgaged) { engine.State = engine.State with { Properties = engine.State.Properties.Select(p => p.PropertyId == "blue2" ? p with { IsMortgaged = true } : p).ToArray() }; } var strategy = new ConservativeStrategy(engine);
                    strategy.PreRoll(engine.Player, engine.State)();
                    engine.Player = engine.Player with { CashBalance = 1200 };
                    strategy.PostRoll(engine.Player, engine.State)();
                    Check(engine.Executed.Count == 0, "Developed an incomplete or mortgaged group.");
                }
            }),
            ("A completed but undeveloped monopoly does not trigger buying a second group", () =>
            {
                var engine = Engine(["blue1", "blue2", "blue3"], [Buy("red3", 240)]);
                new ConservativeStrategy(engine).PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.Count == 0, "Started a second monopoly.");
            }),
            ("Only a fully developed group allows starting the next monopoly", () =>
            {
                var engine = Engine(["blue1", "blue2", "blue3"], [Buy("red3", 240)]);
                engine.State = engine.State with { Properties = engine.State.Properties.Select(p => p.ColorGroup == "lightBlue" ? p with { HotelCount = 1 } : p).ToArray() };
                var strategy = new ConservativeStrategy(engine);
                strategy.PostRoll(engine.Player, engine.State)();
                engine.NextTurn([Buy("red1", 220)]);
                strategy.PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.SequenceEqual(["buy-red3", "buy-red1"]), "Did not transition to the next focus after full development.");
            }),
            ("A hotel upgrade also consumes the single full-turn upgrade allowance", () =>
            {
                var hotel = new StrategyAction { Id = "hotel", Type = StrategyActionType.BuildHotel, PropertyId = "blue1", Cost = 50 };
                var engine = Engine(["blue1", "blue2", "blue3"], [hotel]);
                engine.State = engine.State with { Properties = engine.State.Properties.Select(p => p.ColorGroup == "lightBlue" ? p with { HouseCount = 4 } : p).ToArray() };
                engine.AfterAction = _ => [hotel with { Id = "second-hotel", PropertyId = "blue2" }];
                var strategy = new ConservativeStrategy(engine);
                strategy.PreRoll(engine.Player, engine.State)();
                engine.Player = engine.Player with { CashBalance = 1200 };
                strategy.PostRoll(engine.Player, engine.State)();
                Check(engine.Executed.SequenceEqual(["hotel"]) && engine.State.Properties.Single(p => p.PropertyId == "blue1").HotelCount == 1, "Built more than one hotel or failed to upgrade.");
            }),
            ("Jail decisions leave doubles attempts and mandatory release to the engine", () =>
            {
                var engine = Engine(["blue1"], [Buy("blue2"), new() { Id = "card", Type = StrategyActionType.UseGetOutOfJailFreeCard, Cost = 0 }, new() { Id = "fine", Type = StrategyActionType.PayJailFine, Cost = 50 }]);
                engine.Player = engine.Player with { IsInJail = true };
                var strategy = new ConservativeStrategy(engine);
                for (var turn = 0; turn < 3; turn++)
                {
                    strategy.PreRoll(engine.Player, engine.State)();
                    strategy.PostRoll(engine.Player, engine.State)();
                    engine.State = engine.State with { TurnNumber = engine.State.TurnNumber + 1 };
                }
                Check(engine.Executed.Count == 0, "Voluntarily left jail or invested while jailed.");
            })
        };

        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"PASS Conservative: {name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL Conservative: {name}: {error.Message}"); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} conservative strategy checks passed.");
        return failures;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private static StrategyAction Buy(string id, decimal cost = 100) => new() { Id = $"buy-{id}", Type = StrategyActionType.BuyProperty, PropertyId = id, Cost = cost };
    private static StrategyAction Build(string id) => new() { Id = $"build-{id}", Type = StrategyActionType.BuildHouse, PropertyId = id, Cost = 50 };
    private static StrategyAction Trade(PropertyTrade trade) => new() { Id = "trade", Type = StrategyActionType.TradeProperties, Trade = trade, Cost = trade.NetCost };
    private static PropertyTrade CompletionDeal() => new() { OtherPlayerId = "other", PropertyIdsGiven = ["red3"], PropertyIdsReceived = ["blue3"], CashReceived = 120 };

    private static ScenarioExecutor Engine(string[] owned, StrategyAction[] actions, string[]? otherOwned = null, decimal cash = 1000)
    {
        var player = new Player { Id = "conservative", Name = "Careful", Strategy = "conservative", CashBalance = cash, OwnedPropertyIds = owned };
        var other = new Player { Id = "other", Name = "Other", Strategy = "other", CashBalance = 1000, OwnedPropertyIds = otherOwned ?? [] };
        var properties = new[]
        {
            Property("blue1", "lightBlue", 100), Property("blue2", "lightBlue", 100), Property("blue3", "lightBlue", 120),
            Property("red1", "red", 220), Property("red2", "red", 220), Property("red3", "red", 240), Property("railroad", null, 200)
        }.Select(p => p with { IsOwned = owned.Contains(p.PropertyId) || other.OwnedPropertyIds.Contains(p.PropertyId) }).ToArray();
        return new(player, new GameState { TurnNumber = 1, Properties = properties, OtherPlayers = [other], AvailableActions = actions });
    }

    private static PropertyState Property(string id, string? group, decimal price) => new()
    {
        PropertyId = id, PropertyName = id, ColorGroup = group, SalePrice = price,
        MortgageValue = price / 2, HousePrice = 50, HotelPrice = 50
    };

    private sealed class ScenarioExecutor(Player player, GameState state) : IStrategyActionExecutor
    {
        public Player Player { get; set; } = player;
        public GameState State { get; set; } = state;
        public Player Other => State.OtherPlayers[0];
        public List<string> Executed { get; } = [];
        public Func<StrategyAction, StrategyAction[]>? AfterAction { get; set; }

        public void NextTurn(StrategyAction[] options) => State = State with { TurnNumber = State.TurnNumber + 1, AvailableActions = options };

        public StrategyTurnState Execute(string playerId, StrategyAction action)
        {
            if (playerId != Player.Id || !State.AvailableActions.Contains(action) || action.Cost > Player.CashBalance)
            {
                throw new InvalidOperationException("Invalid option.");
            }

            Executed.Add(action.Id);
            var terms = TradeValidator.TermsForAction(Player, State, action);
            if (terms is not null)
            {
                State = TradeValidator.RecordCompletedTrade(Player, State, terms);
            }
            var balance = Player.CashBalance - action.Cost;
            Player = Player with { CashBalance = balance, FinancialLedger = [.. Player.FinancialLedger, new(action.Id, -action.Cost, balance)] };
            if (action.Type == StrategyActionType.BuyProperty)
            {
                Player = Player with { OwnedPropertyIds = [.. Player.OwnedPropertyIds, action.PropertyId!] };
                State = State with { Properties = State.Properties.Select(p => p.PropertyId == action.PropertyId ? p with { IsOwned = true } : p).ToArray() };
            }
            if (action.Trade is { } trade)
            {
                Player = Player with { OwnedPropertyIds = Player.OwnedPropertyIds.Except(trade.PropertyIdsGiven).Concat(trade.PropertyIdsReceived).ToArray() };
                var otherBalance = Other.CashBalance + trade.CashPaid - trade.CashReceived;
                State = State with { OtherPlayers = [Other with
                {
                    CashBalance = otherBalance,
                    OwnedPropertyIds = Other.OwnedPropertyIds.Except(trade.PropertyIdsReceived).Concat(trade.PropertyIdsGiven).ToArray(),
                    FinancialLedger = [.. Other.FinancialLedger, new(action.Id, trade.CashPaid - trade.CashReceived, otherBalance)]
                }] };
            }
            if (action.Type == StrategyActionType.BuildHouse)
            {
                State = State with { Properties = State.Properties.Select(p => p.PropertyId == action.PropertyId ? p with { HouseCount = p.HouseCount + 1 } : p).ToArray() };
            }

            if (action.Type == StrategyActionType.BuildHotel)
            {
                State = State with { Properties = State.Properties.Select(p => p.PropertyId == action.PropertyId ? p with { HouseCount = 0, HotelCount = 1 } : p).ToArray() };
            }

            State = State with { AvailableActions = AfterAction?.Invoke(action) ?? State.AvailableActions.Where(a => a.Id != action.Id).ToArray() };
            return new(Player, State);
        }
    }
}
