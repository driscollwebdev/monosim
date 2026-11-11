using MonoSim.Models;
using MonoSim.Strategies;
using static InfrastructureStrategyTests;

internal static class BlueGreenStrategyTests
{
    private static readonly string[] Targets = Board.Where(p => p.ColorGroup is "green" or "darkBlue").Select(p => p.PropertyId).ToArray();

    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("Targets are exactly the three greens and two dark blues", () =>
            {
                Check(Targets.SequenceEqual(["pacific-avenue", "north-carolina-avenue", "pennsylvania-avenue", "park-place", "boardwalk"]), "Unexpected fixed property set.");
                var e = Engine(300, 31, ["mediterranean-avenue"], [Buy("pacific-avenue")]);
                var action = new BlueGreenStrategy(e).PostRoll(e.Player, e.State);
                Check(e.Executed.Count == 0, "Selection executed early.");
                action();
                Check(e.Player.OwnedPropertyIds.Contains("pacific-avenue"), "An earlier unrelated purchase changed the fixed targets.");
            }),
            ("Boardwalk can be the first purchase and consume the entire reserve", () =>
            {
                var e = Run(400, 39, [], [Buy("boardwalk")]);
                Check(e.Player.OwnedPropertyIds.SequenceEqual(["boardwalk"]) && e.Player.CashBalance == 0, "Required a green monopoly or retained the reserve after a target purchase.");
            }),
            ("All five properties can be acquired in an interleaved group order", () =>
            {
                var e = Engine(0, 31, [], []);
                var strategy = new BlueGreenStrategy(e);
                var order = new[] { "north-carolina-avenue", "boardwalk", "pacific-avenue", "park-place", "pennsylvania-avenue" };
                foreach (var id in order)
                {
                    var p = Board.Single(p => p.PropertyId == id);
                    e.Player = e.Player with { BoardPosition = p.BoardPosition, CashBalance = p.SalePrice };
                    e.State = e.State with { TurnNumber = e.State.TurnNumber + 1, AvailableActions = [Buy(id)] };
                    strategy.PostRoll(e.Player, e.State)();
                }
                Check(e.Player.OwnedPropertyIds.SequenceEqual(order) && e.Player.CashBalance == 0, "Imposed an acquisition order between groups.");
            }),
            ("Before the final side leverage purchases must leave at least 400", () =>
            {
                Check(Run(459, 1, [], [Buy("mediterranean-avenue")]).Executed.Count == 0, "Spent below the 400 floor.");
                var e = Run(460, 1, [], [Buy("mediterranean-avenue")]);
                Check(e.Player.CashBalance == 400 && e.Executed.Count == 1, "Rejected the exact minimum boundary.");
            }),
            ("A target trade before the final side also preserves the 400 floor", () =>
            {
                var trade = Trade(Deal([], ["pacific-avenue"], paid: 300));
                Check(Run(699, 20, [], [trade], ["pacific-avenue"]).Executed.Count == 0, "Spent the pre-arrival fund on an early trade.");
                Check(Run(700, 20, [], [trade], ["pacific-avenue"]).Player.CashBalance == 400, "Refused a funded target trade.");
            }),
            ("On the final side a target trade may spend the reserve", () =>
            {
                var e = Run(300, 31, [], [Trade(Deal([], ["pacific-avenue"], paid: 300))], ["pacific-avenue"]);
                Check(e.Player.CashBalance == 0 && e.Player.OwnedPropertyIds.Contains("pacific-avenue"), "Did not deploy the target fund in a trade.");
            }),
            ("A target mortgage offer includes its mandatory fees in affordability", () =>
            {
                var option = Buy("pacific-avenue") with { Type = StrategyActionType.BuyMortgagedProperty, Cost = 165 };
                var e = Engine(164, 31, [], [option], ["pacific-avenue"]);
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId == "pacific-avenue" ? p with { IsMortgaged = true } : p).ToArray() };
                var strategy = new BlueGreenStrategy(e);
                strategy.PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0, "Ignored the total mortgage acquisition cost.");
                e.Player = e.Player with { CashBalance = 165 };
                strategy.PostRoll(e.Player, e.State)();
                Check(e.Player.CashBalance == 0 && e.Player.OwnedPropertyIds.Contains("pacific-avenue"), "Refused an exactly affordable mortgage acquisition.");
            }),
            ("Final-side leverage still protects the next target purchase", () =>
            {
                Check(Run(519, 31, ["pacific-avenue"], [Buy("kentucky-avenue")]).Executed.Count == 0, "Depleted the North Carolina fund for leverage.");
                Check(Run(520, 31, ["pacific-avenue"], [Buy("kentucky-avenue")]).Player.CashBalance == 300, "Rejected a reserve-preserving leverage purchase.");
            }),
            ("A cash sale can restore a depleted pre-arrival reserve", () =>
            {
                var sale = Trade(Deal(["oriental-avenue"], [], receivedCash: 100));
                var e = Run(350, 6, ["oriental-avenue"], [sale, Buy("mediterranean-avenue")]);
                Check(e.Executed.SequenceEqual(["trade"]) && e.Player.CashBalance == 450, "Did not restore cash or immediately spent the reserve again.");
            }),
            ("Returning past GO restores the pre-arrival cash rule", () =>
            {
                var e = Engine(400, 39, [], [Buy("boardwalk")]);
                var strategy = new BlueGreenStrategy(e);
                strategy.PostRoll(e.Player, e.State)();
                e.Player = e.Player with { CashBalance = 200, BoardPosition = 1 };
                e.State = e.State with { TurnNumber = e.State.TurnNumber + 1, AvailableActions = [Buy("mediterranean-avenue")] };
                strategy.PostRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["buy-boardwalk"]), "Failed to rebuild the reserve on a later circuit.");
            }),
            ("Owning all five targets releases the acquisition floor for development", () =>
            {
                var e = Run(200, 10, Targets, [Build("pacific-avenue") with { Cost = 200 }]);
                Check(e.Player.CashBalance == 0 && e.State.Properties.Single(p => p.PropertyId == "pacific-avenue").HouseCount == 1, "A stale 400 acquisition floor blocked development.");
            }),
            ("Short Line and a target upgrade retain equal priority", () =>
            {
                foreach (var lineFirst in new[] { true, false })
                {
                    var line = Buy("short-line") with { Id = lineFirst ? "a-line" : "z-line" };
                    var house = Build("pacific-avenue") with { Cost = 200, Id = lineFirst ? "z-house" : "a-house" };
                    var e = Run(200, 35, Targets, [line, house]);
                    Check(e.Executed.SequenceEqual([lineFirst ? "a-line" : "a-house"]), "Changed the inherited secondary-objective priority.");
                }
            }),
            ("Owning Short Line with the targets unlocks later mortgage purchases", () =>
            {
                var option = Buy("oriental-avenue") with { Type = StrategyActionType.BuyMortgagedProperty, Cost = 55 };
                var e = Engine(55, 6, [.. Targets, "short-line"], [option], ["oriental-avenue"]);
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId == "oriental-avenue" ? p with { IsMortgaged = true } : p).ToArray() };
                new BlueGreenStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Player.OwnedPropertyIds.Contains("oriental-avenue"), "Did not inherit expansion after contained ownership.");
            }),
            ("Full target development also unlocks expansion without Short Line", () =>
            {
                var e = Engine(60, 1, Targets, [Buy("mediterranean-avenue")]);
                e.State = e.State with { Properties = e.State.Properties.Select(p => Targets.Contains(p.PropertyId) ? p with { HotelCount = 1 } : p).ToArray() };
                new BlueGreenStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Player.OwnedPropertyIds.Contains("mediterranean-avenue"), "Required Short Line after full development.");
            }),
            ("Early jail release by fine preserves the 400 pre-arrival fund", () =>
            {
                foreach (var cash in new[] { 449m, 450m })
                {
                    var e = Engine(cash, 10, [], [Fine()]);
                    e.Player = e.Player with { IsInJail = true };
                    e.AfterAction = _ => [];
                    new BlueGreenStrategy(e).PreRoll(e.Player, e.State)();
                    Check(e.Executed.Count == (cash == 450 ? 1 : 0), "Did not apply the 400 floor to voluntary release.");
                }
            }),
            ("A held free-release card is preferred even below the cash floor", () =>
            {
                var e = Engine(0, 10, [], [Fine(), Card()]);
                e.Player = e.Player with { IsInJail = true };
                e.AfterAction = _ => [];
                new BlueGreenStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["card"]) && !e.Player.IsInJail, "Did not inherit free card preference.");
            }),
            ("Blue-Green waits in jail when there is no unowned infrastructure ahead", () =>
            {
                var infra = Board.Where(p => p.Type != PropertyType.Street).Select(p => p.PropertyId).ToArray();
                var e = Engine(1000, 10, [], [Fine(), Card()], infra);
                e.Player = e.Player with { IsInJail = true };
                new BlueGreenStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0, "Replaced the inherited infrastructure-based release condition.");
            })
        };

        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"PASS Blue-Green: {name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL Blue-Green: {name}: {error.Message}"); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} blue-green strategy checks passed.");
        return failures;
    }

    private static void Check(bool condition, string message) { if (!condition)
        {
            throw new Exception(message);
        }
    }
    private static TestEngine Run(decimal cash, int position, string[] owned, StrategyAction[] actions, string[]? otherOwned = null)
    {
        var e = Engine(cash, position, owned, actions, otherOwned);
        new BlueGreenStrategy(e).PostRoll(e.Player, e.State)();
        return e;
    }
}
