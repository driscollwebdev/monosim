using MonoSim.Models;
using MonoSim.Strategies;
using MonoSim.Trading;
using static InfrastructureStrategyTests;

internal static class TradeSafetyTests
{
    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("A directly reachable hotel can have rent greater than all post-trade wealth", () =>
            {
                var e = HotelSale(27);
                var advice = TradeSafetyAdvisor.Assess(e.Player, e.State, e.State.AvailableActions[0].Trade!);
                Check(advice.PostTradeCash == 900 && advice.PostTradeAssetValue == 0 && advice.ShouldAvoid, "Did not identify a 2000 rent threat against 900 in wealth.");
                Check(advice.Exposures.Single().Rent == 2000 && advice.Exposures.Single().DangerousDiceTotals.SequenceEqual([12]), "Incorrect direct-roll exposure.");
                Check(TradeValidator.Validate(e.Player, e.State, e.State.AvailableActions[0].Trade!).IsValid, "An advisory changed legality.");
                Check(TradeValidator.RecordOffer("risky-but-legal", e.Player, e.State, e.State.AvailableActions[0].Trade!).TradeOffers.Length == 1, "A risky legal offer could not be recorded.");
            }),
            ("Dice totals below two or above twelve do not create direct reachability", () =>
            {
                foreach (var position in new[] { 26, 38, 39 })
                {
                    var e = HotelSale(position);
                    var advice = TradeSafetyAdvisor.Assess(e.Player, e.State, e.State.AvailableActions[0].Trade!);
                    Check(!advice.ShouldAvoid && advice.Exposures.Length == 0, "Flagged a property outside the next direct roll.");
                }
            }),
            ("The minimum two-dice roll is reachable", () =>
            {
                var e = HotelSale(37);
                var advice = TradeSafetyAdvisor.Assess(e.Player, e.State, e.State.AvailableActions[0].Trade!);
                Check(advice.ShouldAvoid && advice.Exposures.Single().DangerousDiceTotals.SequenceEqual([2]), "Ignored a roll of two.");
            }),
            ("Direct movement wraps past GO without assuming future salary", () =>
            {
                var e = Engine(0, 39, ["mediterranean-avenue"], []);
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId == "mediterranean-avenue" ? p with { HotelCount = 1 } : p).ToArray() };
                var advice = TradeSafetyAdvisor.Assess(e.Player, e.State, Deal(["mediterranean-avenue"], [], receivedCash: 185));
                Check(advice.ShouldAvoid && advice.Exposures.Single().Rent == 250, "Failed wrapped reachability or assumed GO salary in post-trade wealth.");
            }),
            ("Rent equal to total post-trade wealth is not greater than it", () =>
            {
                var e = HotelSale(37, cash: 1100);
                var advice = TradeSafetyAdvisor.Assess(e.Player, e.State, e.State.AvailableActions[0].Trade!);
                Check(advice.PostTradeTotalValue == 2000 && !advice.ShouldAvoid, "Used greater-than-or-equal instead of greater-than.");
            }),
            ("Retained property, buildings, mortgage values, and cards all count as assets", () =>
            {
                var e = HotelSale(37);
                e.Player = e.Player with { OwnedPropertyIds = [.. e.Player.OwnedPropertyIds, "pacific-avenue", "north-carolina-avenue"], HeldGetOutOfJailFreeCardIds = ["chance-jail-free"] };
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId switch
                {
                    "pacific-avenue" => p with { IsOwned = true, HouseCount = 2 },
                    "north-carolina-avenue" => p with { IsOwned = true, IsMortgaged = true },
                    _ => p
                }).ToArray() };
                var advice = TradeSafetyAdvisor.Assess(e.Player, e.State, e.State.AvailableActions[0].Trade!);
                Check(advice.PostTradeAssetValue == 700 && advice.PostTradeTotalValue == 1600, "Incorrect retained asset values.");
            }),
            ("Unimproved rent reflects the recipient's newly completed color group", () =>
            {
                var e = Engine(0, 39, ["mediterranean-avenue"], [], ["baltic-avenue"]);
                var advice = TradeSafetyAdvisor.Assess(e.Player, e.State, Deal(["mediterranean-avenue"], [], receivedCash: 60));
                Check(advice.Exposures.Single().Rent == 4, "Did not apply complete-group rent after the exchange.");
                advice = TradeSafetyAdvisor.Assess(e.Player, e.State, Deal(["mediterranean-avenue"], ["baltic-avenue"]));
                Check(advice.Exposures.Single().Rent == 2, "Counted a property the recipient gives away.");
            }),
            ("Railroad rent uses recipient ownership and bank fees reduce post-trade wealth", () =>
            {
                var e = Engine(0, 2, ["reading-railroad"], [], ["pennsylvania-railroad", "b-and-o-railroad", "short-line", "oriental-avenue"]);
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId == "oriental-avenue" ? p with { IsMortgaged = true } : p).ToArray() };
                var deal = Deal(["reading-railroad"], ["oriental-avenue"], receivedCash: 150, fees: 5);
                var advice = TradeSafetyAdvisor.Assess(e.Player, e.State, deal);
                Check(advice.PostTradeTotalValue == 195 && advice.Exposures.Single().Rent == 200 && advice.ShouldAvoid, "Incorrect railroad count or fee treatment.");
            }),
            ("Utility rent uses the reachable roll and post-trade utility ownership", () =>
            {
                var e = Engine(0, 0, ["electric-company"], [], ["water-works"]);
                var advice = TradeSafetyAdvisor.Assess(e.Player, e.State, Deal(["electric-company"], [], receivedCash: 150));
                Check(advice.Exposures.Single().Rent == 120 && !advice.ShouldAvoid, "Incorrect ten-times utility rent for a roll of twelve.");
                advice = TradeSafetyAdvisor.Assess(e.Player, e.State, Deal(["electric-company"], ["water-works"]));
                Check(advice.Exposures.Single().Rent == 48, "Counted the utility the new owner trades away.");
            }),
            ("A currently mortgaged transferred property cannot collect rent", () =>
            {
                var e = Engine(0, 39, ["mediterranean-avenue"], []);
                e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId == "mediterranean-avenue" ? p with { IsMortgaged = true } : p).ToArray() };
                var deal = Deal(["mediterranean-avenue"], [], receivedCash: 30) with { OtherPlayerBankFees = 3 };
                Check(!TradeSafetyAdvisor.Assess(e.Player, e.State, deal).ShouldAvoid, "Assumed a future mortgage redemption.");
            }),
            ("A debt-funded dangerous trade remains a warning but may be accepted", () =>
            {
                var e = HotelSale(37);
                e.State = e.State with { RentObligations = [new(e.Player.Id, "other", 900)] };
                var advice = TradeSafetyAdvisor.Assess(e.Player, e.State, e.State.AvailableActions[0].Trade!);
                Check(advice.HasImminentJeopardy && advice.FundsDueDebt && !advice.ShouldAvoid, "Did not distinguish a warning from the debt exception.");
            }),
            ("A trade that cannot cover due debts does not receive the exception", () =>
            {
                var e = HotelSale(37);
                e.State = e.State with { CashObligations = [new(e.Player.Id, null, 1000, "Tax")] };
                var advice = TradeSafetyAdvisor.Assess(e.Player, e.State, e.State.AvailableActions[0].Trade!);
                Check(advice.ShouldAvoid && !advice.FundsDueDebt, "Applied the exception to insufficient proceeds.");
            }),
            ("Every strategy can fund a debt without spending its payment money afterward", () =>
            {
                foreach (var name in new[] { "aggressive", "conservative", "infrastructure", "adjacent", "blue-green" })
                {
                    var e = HotelSale(37);
                    e.State = e.State with { CashObligations = [new(e.Player.Id, null, 900, "Current debt")] };
                    e.AfterAction = _ => [Buy("oriental-avenue")];
                    IPlayingStrategy strategy = name switch
                    {
                        "aggressive" => new AggressiveStrategy(e),
                        "conservative" => new ConservativeStrategy(e),
                        "infrastructure" => new InfrastructureStrategy(e),
                        "adjacent" => new AdjacentGroupStrategy(e),
                        _ => new BlueGreenStrategy(e)
                    };
                    strategy.PreRoll(e.Player, e.State)();
                    Check(e.Executed.SequenceEqual(["trade"]) && e.Player.CashBalance == 900 && e.State.CashObligations.Single().Amount == 900,
                        $"{name} did not raise cash for the debt or spent it before cash settlement.");
                }
            }),
            ("A debt rescue prefers a safe sufficient offer over a dangerous one", () =>
            {
                var e = HotelSale(37);
                e.Player = e.Player with { OwnedPropertyIds = [.. e.Player.OwnedPropertyIds, "pacific-avenue"] };
                e.State = e.State with
                {
                    Properties = e.State.Properties.Select(p => p.PropertyId == "pacific-avenue" ? p with { IsOwned = true } : p).ToArray(),
                    CashObligations = [new(e.Player.Id, null, 300, "Tax")],
                    AvailableActions = [.. e.State.AvailableActions, Trade(Deal(["pacific-avenue"], [], receivedCash: 300)) with { Id = "safe-sale" }]
                };
                Check(TradeSafetyAdvisor.FindDebtFundingTrade(e.Player, e.State)?.Id == "safe-sale", "Chose the dangerous hotel sale despite a safe debt-funding offer.");
            }),
            ("The infrastructure strategy avoids a dangerous ordinary liquidation", () =>
            {
                var e = HotelSale(37);
                new InfrastructureStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0, "Accepted an unnecessary dangerous liquidation.");
            }),
            ("Already-funded debts stop investment spending until cash settlement", () =>
            {
                var e = Engine(500, 6, [], [Buy("oriental-avenue")]);
                e.State = e.State with { RentObligations = [new(e.Player.Id, "other", 500)] };
                new AggressiveStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0 && e.Player.CashBalance == 500, "Spent cash earmarked for a due rent payment.");
            })
        };
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS Trade safety: {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL Trade safety: {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} trade safety checks passed.");
        return failures;
    }

    private static TestEngine HotelSale(int position, decimal cash = 0)
    {
        var e = Engine(cash, position, ["boardwalk"], [Trade(Deal(["boardwalk"], [], receivedCash: 900))]);
        e.State = e.State with { Properties = e.State.Properties.Select(p => p.PropertyId == "boardwalk" ? p with { HotelCount = 1 } : p).ToArray() };
        return e;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
