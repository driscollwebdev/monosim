using MonoSim.Models;
using MonoSim.Strategies;
using static InfrastructureStrategyTests;

internal static class AdjacentGroupStrategyTests
{
    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("A successful first acquisition anchors the clockwise pair across a corner", () =>
            {
                var e = Engine(1000, 6, [], [Buy("oriental-avenue")]);
                var s = new AdjacentGroupStrategy(e);
                var action = s.PostRoll(e.Player, e.State);
                Check(e.Executed.Count == 0, "Selection mutated the engine.");
                action();
                Next(e, [Buy("st-charles-place")]);
                s.PostRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["buy-oriental-avenue", "buy-st-charles-place"]), "Did not pursue light blue and pink across Jail.");
            }),
            ("Only a successful purchase commits a focus pair", () =>
            {
                var e = Engine(199, 6, [], [Buy("oriental-avenue")]);
                var s = new AdjacentGroupStrategy(e);
                s.PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0, "Spent the reserve when choosing the initial pair.");
                e.Player = e.Player with { CashBalance = 1500 };
                Next(e, [Buy("kentucky-avenue")]);
                s.PostRoll(e.Player, e.State)();
                Next(e, [Buy("atlantic-avenue")]);
                s.PostRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["buy-kentucky-avenue", "buy-atlantic-avenue"]), "An unsuccessful blue purchase locked the target.");
            }),
            ("The last color group pairs with the first across GO", () =>
            {
                var e = Run(1000, 1, ["park-place"], [Buy("mediterranean-avenue")]);
                Check(e.Player.OwnedPropertyIds.Contains("mediterranean-avenue"), "Did not wrap dark blue to brown.");
            }),
            ("The reserve uses the next target street rather than the next infrastructure", () =>
            {
                Check(Run(359, 9, ["oriental-avenue"], [Buy("kentucky-avenue")]).Executed.Count == 0, "Spent the pink street reserve.");
                var e = Run(360, 9, ["oriental-avenue"], [Buy("kentucky-avenue")]);
                Check(e.Player.CashBalance == 140, "Did not allow an exact target-street reserve.");
            }),
            ("The target reserve wraps clockwise and skips already owned streets", () =>
            {
                Check(Run(319, 39, ["oriental-avenue"], [Buy("kentucky-avenue")]).Executed.Count == 0, "Failed to reserve for Vermont past GO.");
                Check(Run(320, 39, ["oriental-avenue"], [Buy("kentucky-avenue")]).Player.CashBalance == 100, "Incorrect wrapped reserve.");
            }),
            ("A target purchase protects the following target property's reserve", () =>
            {
                Check(Run(100, 8, ["oriental-avenue"], [Buy("vermont-avenue")]).Executed.Count == 0, "Spent the next target property's reserve.");
                Check(Run(220, 8, ["oriental-avenue"], [Buy("vermont-avenue")]).Player.CashBalance == 120, "Did not buy while reserving for Connecticut.");
            }),
            ("Unrelated property can be held as leverage without a prearranged deal", () =>
            {
                var e = Run(500, 21, ["oriental-avenue"], [Buy("kentucky-avenue")]);
                Check(e.Player.OwnedPropertyIds.Contains("kentucky-avenue"), "Refused speculative leverage.");
            }),
            ("Leverage trades can acquire a target street", () =>
            {
                var e = Run(500, 0, ["oriental-avenue", "kentucky-avenue"], [Trade(Deal(["kentucky-avenue"], ["st-charles-place"], receivedCash: 80))], ["st-charles-place"]);
                Check(e.Player.OwnedPropertyIds.Contains("st-charles-place") && !e.Player.OwnedPropertyIds.Contains("kentucky-avenue"), "Did not use leverage for the target.");
            }),
            ("Neither target group can be traded away", () =>
            {
                var e = Run(1000, 9, ["oriental-avenue"], [Trade(Deal(["oriental-avenue"], ["vermont-avenue"]))], ["vermont-avenue"]);
                Check(e.Executed.Count == 0, "Gave away a target street.");
            }),
            ("Both groups must be owned before building or acquiring contained infrastructure", () =>
            {
                var owned = Group("lightBlue");
                var e = Run(1000, 9, owned, [Build("oriental-avenue"), Buy("electric-company")]);
                Check(e.Executed.Count == 0, "Started secondary objectives before owning both groups.");
            }),
            ("An embedded utility is contained even though it is not in the gap", () =>
            {
                var e = Run(500, 12, Pair("lightBlue", "pink"), [Buy("electric-company")]);
                Check(e.Player.OwnedPropertyIds.Contains("electric-company"), "Ignored Electric Company within the pair's span.");
            }),
            ("Infrastructure beyond the span is not a secondary objective", () =>
            {
                var option = Buy("pennsylvania-railroad") with { Type = StrategyActionType.BuyMortgagedProperty };
                var e = Engine(500, 15, Pair("lightBlue", "pink"), [option], ["pennsylvania-railroad"]);
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId == "pennsylvania-railroad" ? p with { IsMortgaged = true } : p).ToArray() };
                new AdjacentGroupStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0, "Treated Pennsylvania Railroad beyond pink as contained.");
            }),
            ("Leverage purchases remain possible during the secondary objective", () =>
            {
                var e = Run(500, 21, Pair("lightBlue", "pink"), [Buy("kentucky-avenue")]);
                Check(e.Player.OwnedPropertyIds.Contains("kentucky-avenue"), "Blocked leverage needed to negotiate a contained asset.");
            }),
            ("Equal-cost infrastructure and upgrades have no category preference", () =>
            {
                foreach (var infrastructureFirst in new[] { true, false })
                {
                    var water = Buy("water-works") with { Id = infrastructureFirst ? "a-water" : "z-water" };
                    var house = Build("kentucky-avenue") with { Cost = 150, Id = infrastructureFirst ? "z-house" : "a-house" };
                    var e = Run(150, 28, Pair("red", "yellow"), [water, house]);
                    Check(e.Executed.SequenceEqual([infrastructureFirst ? "a-water" : "a-house"]), "A category displaced the equal-priority tie rule.");
                }
            }),
            ("A cheaper upgrade can precede contained infrastructure", () =>
            {
                var e = Run(50, 12, Pair("lightBlue", "pink"), [Buy("electric-company"), Build("oriental-avenue")]);
                Check(e.Executed.SequenceEqual(["build-oriental-avenue"]), "Blocked an affordable upgrade behind infrastructure.");
            }),
            ("A cheaper contained utility can precede an upgrade", () =>
            {
                var hotel = new StrategyAction { Id = "hotel", Type = StrategyActionType.BuildHotel, PropertyId = "pennsylvania-avenue", Cost = 200 };
                var e = Engine(150, 28, Pair("yellow", "green"), [hotel, Buy("water-works")]);
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.ColorGroup == "green" ? p with { HouseCount = 4 } : p).ToArray() };
                new AdjacentGroupStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["buy-water-works"]), "Blocked an affordable contained utility behind development.");
            }),
            ("Owning any contained infrastructure unlocks additional monopolies", () =>
            {
                var e = Run(1000, 1, [.. Pair("pink", "orange"), "electric-company"], [Buy("mediterranean-avenue")]);
                Check(e.Player.OwnedPropertyIds.Contains("mediterranean-avenue"), "Required both contained assets or full development despite the either-objective rule.");
            }),
            ("Buying contained infrastructure unlocks expansion within the same returned action", () =>
            {
                var e = Engine(500, 12, Pair("lightBlue", "pink"), [Buy("electric-company")]);
                e.AfterAction = a => a.PropertyId == "electric-company" ? [Buy("mediterranean-avenue")] : [];
                new AdjacentGroupStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["buy-electric-company", "buy-mediterranean-avenue"]), "Did not re-evaluate primary completion.");
            }),
            ("Later contained acquisitions do not sacrifice an additional complete monopoly", () =>
            {
                var deal = Deal(Group("brown"), ["pennsylvania-railroad"], paid: 80);
                var e = Run(1000, 15, [.. Pair("pink", "orange"), "electric-company", .. Group("brown")], [Trade(deal)], ["pennsylvania-railroad"]);
                Check(e.Executed.Count == 0, "Broke an existing monopoly for an optional contained asset.");
            }),
            ("Full improvement of both groups unlocks expansion without infrastructure", () =>
            {
                var e = Engine(500, 1, Pair("lightBlue", "pink"), [Buy("mediterranean-avenue")]);
                Hotels(e, "lightBlue", "pink");
                new AdjacentGroupStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 1, "Required contained infrastructure after fully improving both groups.");
            }),
            ("Owning both target groups unlocks conservative mortgage trades before full development", () =>
            {
                var option = Buy("mediterranean-avenue") with { Type = StrategyActionType.BuyMortgagedProperty, Cost = 33 };
                var e = Engine(500, 1, Pair("lightBlue", "pink"), [option], ["mediterranean-avenue"]);
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId == "mediterranean-avenue" ? p with { IsMortgaged = true } : p).ToArray() };
                Hotels(e, "lightBlue");
                new AdjacentGroupStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 1, "Did not switch trading modes after owning both target groups.");
            }),
            ("A pair without contained infrastructure permits bargain trades before hotel development", () =>
            {
                var option = Buy("mediterranean-avenue") with { Type = StrategyActionType.BuyMortgagedProperty, Cost = 33 };
                var e = Engine(500, 1, Pair("orange", "red"), [option], ["mediterranean-avenue"]);
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId == "mediterranean-avenue" ? p with { IsMortgaged = true } : p).ToArray() };
                new AdjacentGroupStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 1, "Did not allow a bargain after acquiring the target groups.");
                Hotels(e, "orange", "red");
                new AdjacentGroupStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 1, "Could not complete a pair with no contained infrastructure.");
            }),
            ("Mortgaged target properties can be acquired through accepted offers", () =>
            {
                var option = Buy("st-charles-place") with { Type = StrategyActionType.BuyMortgagedProperty, Cost = 77 };
                var e = Engine(500, 9, ["oriental-avenue"], [option], ["st-charles-place"]);
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId == "st-charles-place" ? p with { IsMortgaged = true } : p).ToArray() };
                new AdjacentGroupStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Player.OwnedPropertyIds.Contains("st-charles-place"), "Skipped a target mortgage offer.");
            }),
            ("Jail release uses the Infrastructure condition and the target-street reserve", () =>
            {
                var e = Engine(190, 10, ["oriental-avenue"], [Fine()]);
                e.Player = e.Player with { IsInJail = true };
                e.AfterAction = _ => [];
                new AdjacentGroupStrategy(e).PreRoll(e.Player, e.State)();
                Check(!e.Player.IsInJail && e.Player.CashBalance == 140, "Did not pay the fine while preserving the target-street reserve.");
            }),
            ("A free jail card is preferred when infrastructure is ahead", () =>
            {
                var e = Engine(0, 10, ["oriental-avenue"], [Fine(), Card()]);
                e.Player = e.Player with { IsInJail = true };
                e.AfterAction = _ => [];
                new AdjacentGroupStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.SequenceEqual(["card"]), "Did not prefer free release.");
            }),
            ("Jail waiting is unchanged when no infrastructure is ahead", () =>
            {
                var infra = Board.Where(p => p.Type != PropertyType.Street && p.PropertyId != "reading-railroad").Select(p => p.PropertyId).ToArray();
                var e = Engine(1000, 10, ["oriental-avenue"], [Fine(), Card()], infra);
                e.Player = e.Player with { IsInJail = true };
                new AdjacentGroupStrategy(e).PreRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0, "Left jail when only infrastructure behind Jail was available.");
            }),
            ("Newly jailed players wait until pre-roll", () =>
            {
                var e = Engine(1000, 10, ["oriental-avenue"], [Fine(), Card()]);
                e.Player = e.Player with { IsInJail = true };
                new AdjacentGroupStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0, "Released in the post-roll phase.");
            })
        };

        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"PASS Adjacent: {name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL Adjacent: {name}: {error.Message}"); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} adjacent group strategy checks passed.");
        return failures;
    }

    private static void Check(bool condition, string message) { if (!condition)
        {
            throw new Exception(message);
        }
    }
    private static string[] Group(string group) => Board.Where(p => p.ColorGroup == group).Select(p => p.PropertyId).ToArray();
    private static string[] Pair(string first, string second) => [.. Group(first), .. Group(second)];
    private static void Hotels(TestEngine e, params string[] groups) => e.State = e.State with
    { Properties = e.State.Properties.Select(p => groups.Contains(p.ColorGroup) ? p with { HotelCount = 1, HouseCount = 0 } : p).ToArray() };
    private static void Next(TestEngine e, StrategyAction[] actions) => e.State = e.State with { TurnNumber = e.State.TurnNumber + 1, AvailableActions = actions };
    private static TestEngine Run(decimal cash, int position, string[] owned, StrategyAction[] actions, string[]? otherOwned = null)
    {
        var e = Engine(cash, position, owned, actions, otherOwned);
        new AdjacentGroupStrategy(e).PostRoll(e.Player, e.State)();
        return e;
    }
}
