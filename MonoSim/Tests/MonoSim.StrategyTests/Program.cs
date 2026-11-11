using MonoSim.Models;
using MonoSim.Strategies;

var tests = new (string Name, Action Run)[]
{
    ("Selection defers execution until the returned Action is invoked", () =>
    {
        var engine = new FakeExecutor(PlayerWith(100), State(Option("buy", StrategyActionType.BuyProperty, 60)));
        var action = new AggressiveStrategy(engine).PostRoll(engine.Player, engine.State);
        Check(engine.Executed.Count == 0 && engine.Player.CashBalance == 100, "Selection mutated state.");
        action();
        Check(engine.Executed.SequenceEqual(["buy"]) && engine.Player.CashBalance == 40, "Purchase did not execute.");
    }),
    ("Purchases take priority over upgrades and retain no cash reserve", () =>
    {
        var engine = Run(100, Option("house", StrategyActionType.BuildHouse, 40, rent: 100), Option("buy", StrategyActionType.BuyProperty, 60));
        Check(engine.Executed.SequenceEqual(["buy", "house"]) && engine.Player.CashBalance == 0, "Incorrect spending sequence.");
        Check(engine.Player.FinancialLedger.Select(t => t.Amount).SequenceEqual([-60m, -40m]), "Incorrect ledger debits.");
        Check(engine.Player.FinancialLedger.Select(t => t.NewCashBalance).SequenceEqual([40m, 0m]), "Incorrect ledger balances.");
    }),
    ("Landing purchases take priority over monopoly-completing mortgage trades", () =>
    {
        var engine = Run(110, Option("ordinary", StrategyActionType.BuyProperty, 60), Option("mortgage-trade", StrategyActionType.BuyMortgagedProperty, 110, completes: true));
        Check(engine.Executed.SequenceEqual(["ordinary"]) && engine.Player.CashBalance == 50, "A mortgage trade displaced the landing purchase.");
    }),
    ("Completing a color group does not change purchase priority", () =>
    {
        var engine = Run(100, Option("completes", StrategyActionType.BuyMortgagedProperty, 100, completes: true), Option("ordinary", StrategyActionType.BuyMortgagedProperty, 60));
        Check(engine.Executed.SequenceEqual(["ordinary"]), "Prioritized a monopoly over an ordinary acquisition.");
    }),
    ("Mortgage trades follow the landing purchase when cash remains", () =>
    {
        var engine = Run(135, Option("mortgage-trade", StrategyActionType.BuyMortgagedProperty, 75, completes: true), Option("landed", StrategyActionType.BuyProperty, 60));
        Check(engine.Executed.SequenceEqual(["landed", "mortgage-trade"]) && engine.Player.CashBalance == 0, "Incorrect acquisition order.");
    }),
    ("Other affordable mortgage offers are purchased", () =>
    {
        var engine = Run(100, Option("mortgage-trade", StrategyActionType.BuyMortgagedProperty, 75));
        Check(engine.Executed.SequenceEqual(["mortgage-trade"]) && engine.Player.CashBalance == 25, "Mortgage trade was skipped.");
    }),
    ("Cheaper secondary mortgage offers maximize the number purchased", () =>
    {
        var engine = Run(120, Option("expensive", StrategyActionType.BuyMortgagedProperty, 110), Option("a", StrategyActionType.BuyMortgagedProperty, 60), Option("b", StrategyActionType.BuyMortgagedProperty, 60));
        Check(engine.Executed.SequenceEqual(["a", "b"]), "Did not select the affordable pair.");
    }),
    ("Development prefers higher rent increase per dollar", () =>
    {
        var engine = Run(100, Option("low", StrategyActionType.BuildHouse, 100, rent: 20), Option("high", StrategyActionType.BuildHotel, 100, rent: 200));
        Check(engine.Executed.SequenceEqual(["high"]), "Incorrect development priority.");
    }),
    ("Mortgage redemption competes with building on rental return", () =>
    {
        var engine = Run(100, Option("house", StrategyActionType.BuildHouse, 100, rent: 20), Option("redeem", StrategyActionType.UnmortgageProperty, 100, rent: 50));
        Check(engine.Executed.SequenceEqual(["redeem"]), "Redemption was not considered.");
    }),
    ("Fresh options after an upgrade enable further legal building", () =>
    {
        var engine = new FakeExecutor(PlayerWith(100), State(Option("first-house", StrategyActionType.BuildHouse, 50)));
        engine.AfterExecute = action => action.Id == "first-house" ? State(Option("second-house", StrategyActionType.BuildHouse, 50)) : State();
        new AggressiveStrategy(engine).PreRoll(engine.Player, engine.State)();
        Check(engine.Executed.SequenceEqual(["first-house", "second-house"]), "Did not refresh options.");
    }),
    ("A held jail card is used before paying the fine", () =>
    {
        var engine = new FakeExecutor(PlayerWith(100, inJail: true), State(Option("fine", StrategyActionType.PayJailFine, 50), Option("card", StrategyActionType.UseGetOutOfJailFreeCard, 0)));
        engine.AfterExecute = _ => State();
        new AggressiveStrategy(engine).PreRoll(engine.Player, engine.State)();
        Check(engine.Executed.SequenceEqual(["card"]) && !engine.Player.IsInJail && engine.Player.CashBalance == 100, "Did not prefer the card.");
    }),
    ("The fine is paid before investments when no card is held", () =>
    {
        var engine = new FakeExecutor(PlayerWith(100, inJail: true), State(Option("buy", StrategyActionType.BuyProperty, 50), Option("fine", StrategyActionType.PayJailFine, 50)));
        new AggressiveStrategy(engine).PreRoll(engine.Player, engine.State)();
        Check(engine.Executed.SequenceEqual(["fine", "buy"]) && !engine.Player.IsInJail, "Did not release before investing.");
    }),
    ("Insufficient cash for jail release is preserved without mortgaging", () =>
    {
        var engine = new FakeExecutor(PlayerWith(40, inJail: true), State(Option("fine", StrategyActionType.PayJailFine, 50), Option("buy", StrategyActionType.BuyProperty, 30)));
        new AggressiveStrategy(engine).PreRoll(engine.Player, engine.State)();
        Check(engine.Executed.Count == 0 && engine.Player.CashBalance == 40, "Spent cash without being able to leave jail.");
    }),
    ("A newly jailed player waits for the next pre-roll", () =>
    {
        var engine = new FakeExecutor(PlayerWith(100, inJail: true), State(Option("fine", StrategyActionType.PayJailFine, 50), Option("buy", StrategyActionType.BuyProperty, 60)));
        new AggressiveStrategy(engine).PostRoll(engine.Player, engine.State)();
        Check(engine.Executed.Count == 0, "Spent money after being jailed.");
    }),
    ("Unaffordable options do not cause overspending", () =>
    {
        var engine = Run(49, Option("house", StrategyActionType.BuildHouse, 50), Option("trade-with-interest", StrategyActionType.BuyMortgagedProperty, 55));
        Check(engine.Executed.Count == 0 && engine.Player.CashBalance == 49, "Overspent cash.");
    }),
    ("Empty legal options produce a no-op", () =>
    {
        var engine = Run(1500);
        Check(engine.Executed.Count == 0, "Invented an unavailable action.");
    }),
    ("An inconsistent executor is rejected rather than spending from stale state", () =>
    {
        var player = PlayerWith(100);
        var state = State(Option("buy", StrategyActionType.BuyProperty, 60));
        var strategy = new AggressiveStrategy(new UnchangedExecutor(player, state));
        try { strategy.PostRoll(player, state)(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected stale executor state to be rejected.");
    })
};

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} strategy checks passed.");
failures += ConservativeStrategyTests.RunAll();
failures += InfrastructureStrategyTests.RunAll();
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static Player PlayerWith(decimal cash, bool inJail = false) => new()
{
    Id = "player-1", Name = "Alice", Strategy = "aggressive", CashBalance = cash, IsInJail = inJail
};

static GameState State(params StrategyAction[] options) => new() { AvailableActions = options };

static StrategyAction Option(string id, StrategyActionType type, decimal cost, bool completes = false, decimal rent = 0) => new()
{
    Id = id, Type = type, PropertyId = id, Cost = cost, CompletesColorGroup = completes, RentIncrease = rent
};

static FakeExecutor Run(decimal cash, params StrategyAction[] options)
{
    var engine = new FakeExecutor(PlayerWith(cash), State(options));
    new AggressiveStrategy(engine).PostRoll(engine.Player, engine.State)();
    return engine;
}

sealed class FakeExecutor(Player player, GameState state) : IStrategyActionExecutor
{
    public Player Player { get; private set; } = player;
    public GameState State { get; private set; } = state;
    public List<string> Executed { get; } = [];
    public Func<StrategyAction, GameState>? AfterExecute { get; set; }

    public StrategyTurnState Execute(string playerId, StrategyAction action)
    {
        if (playerId != Player.Id || !State.AvailableActions.Contains(action) || action.Cost > Player.CashBalance)
            throw new InvalidOperationException("Invalid action.");
        Executed.Add(action.Id);
        var balance = Player.CashBalance - action.Cost;
        Player = Player with
        {
            CashBalance = balance,
            IsInJail = action.Type is StrategyActionType.PayJailFine or StrategyActionType.UseGetOutOfJailFreeCard ? false : Player.IsInJail,
            FinancialLedger = action.Cost == 0 ? Player.FinancialLedger : [.. Player.FinancialLedger, new Transaction(action.Id, -action.Cost, balance)]
        };
        State = AfterExecute?.Invoke(action) ?? State with
        {
            AvailableActions = State.AvailableActions.Where(option => option.Id != action.Id).ToArray()
        };
        return new(Player, State);
    }
}

sealed class UnchangedExecutor(Player player, GameState state) : IStrategyActionExecutor
{
    public StrategyTurnState Execute(string playerId, StrategyAction action) => new(player, state);
}
