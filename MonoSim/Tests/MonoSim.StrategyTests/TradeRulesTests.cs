using System.Text.Json;
using MonoSim.Models;
using MonoSim.Strategies;
using MonoSim.Trading;
using static InfrastructureStrategyTests;

internal static class TradeRulesTests
{
    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("The user's Mediterranean plus 40 for Oriental offer balances", () =>
            {
                var e = Engine(1000, 1, ["mediterranean-avenue"], [], ["oriental-avenue"]);
                Valid(e.Player, e.State, Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40));
                Invalid(e.Player, e.State, Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 39));
            }),
            ("Unimproved streets, railroads, and utilities use their face prices", () =>
            {
                Check(TradeValidator.PropertyValue(Property("mediterranean-avenue")) == 60, "Incorrect street value.");
                Check(TradeValidator.PropertyValue(Property("reading-railroad")) == 200, "Incorrect railroad value.");
                Check(TradeValidator.PropertyValue(Property("electric-company")) == 150, "Incorrect utility value.");
            }),
            ("Improved property includes house resale values", () =>
            {
                var p = Property("mediterranean-avenue") with { HouseCount = 2 };
                Check(TradeValidator.PropertyValue(p) == 110, "Two houses should add 50 in resale value.");
                var e = Engine(1000, 1, [p.PropertyId], []);
                e.State = Replace(e.State, p);
                Valid(e.Player, e.State, Deal([p.PropertyId], [], receivedCash: 110));
                Invalid(e.Player, e.State, Deal([p.PropertyId], [], receivedCash: 60));
            }),
            ("Hotel value includes four exchanged houses and the hotel upgrade", () =>
            {
                Check(TradeValidator.PropertyValue(Property("mediterranean-avenue") with { HotelCount = 1 }) == 185, "Incorrect hotel resale valuation.");
                Check(TradeValidator.PropertyValue(Property("boardwalk") with { HotelCount = 1 }) == 900, "Incorrect expensive hotel valuation.");
            }),
            ("Mortgaged property value excludes interest and both recipients pay their own fees", () =>
            {
                var e = Engine(1000, 1, ["mediterranean-avenue"], [], ["baltic-avenue"]);
                e.State = Replace(Replace(e.State, Property("mediterranean-avenue") with { IsOwned = true, IsMortgaged = true }),
                    Property("baltic-avenue") with { IsOwned = true, IsMortgaged = true });
                var deal = Deal(["mediterranean-avenue"], ["baltic-avenue"], fees: 3) with { OtherPlayerBankFees = 3 };
                var result = TradeValidator.Validate(e.Player, e.State, deal);
                Check(result.IsValid && result.ValueGiven == 30 && result.ValueReceived == 30, "Principal should balance separately from interest.");
                Invalid(e.Player, e.State, deal with { OtherPlayerBankFees = 0 });
                Invalid(e.Player, e.State, deal with { BankFees = 0 });
            }),
            ("Mortgage purchase actions cannot bypass equal-value pricing", () =>
            {
                var e = Engine(1000, 6, [], [], ["oriental-avenue"]);
                e.State = Replace(e.State, Property("oriental-avenue") with { IsOwned = true, IsMortgaged = true });
                var action = Buy("oriental-avenue") with { Type = StrategyActionType.BuyMortgagedProperty, Cost = 55 };
                Check(TradeValidator.ValidateAction(e.Player, e.State, action).IsValid, "Expected 50 principal plus 5 bank interest.");
                Check(!TradeValidator.ValidateAction(e.Player, e.State, action with { Cost = 100 }).IsValid, "An overpriced mortgage purchase bypassed the rule.");
            }),
            ("A jail-free card is worth 50 and may be traded for cash alone", () =>
            {
                var e = Engine(1000, 1, [], []);
                e.Player = e.Player with { HeldGetOutOfJailFreeCardIds = ["chance-jail-free"] };
                var deal = Deal([], [], receivedCash: 50) with { CardIdsGiven = ["chance-jail-free"] };
                Valid(e.Player, e.State, deal);
                Invalid(e.Player, e.State, deal with { CashReceived = 49 });
            }),
            ("A mixed card property and cash exchange transfers held cards", () =>
            {
                var deal = Deal([], ["mediterranean-avenue"], paid: 10) with { CardIdsGiven = ["chance-jail-free"] };
                var action = Trade(deal);
                var e = Engine(1000, 1, [], [action], ["mediterranean-avenue"]);
                e.Player = e.Player with { HeldGetOutOfJailFreeCardIds = ["chance-jail-free"] };
                e.Execute(e.Player.Id, action);
                Check(e.Player.HeldGetOutOfJailFreeCardIds.Length == 0 && e.State.OtherPlayers[0].HeldGetOutOfJailFreeCardIds.Contains("chance-jail-free"), "Card ownership did not transfer.");
                Check(e.Player.OwnedPropertyIds.Contains("mediterranean-avenue") && e.Player.CashBalance == 990, "Property or cash did not transfer.");
            }),
            ("Both decks' cards can be exchanged and duplicate cards are rejected", () =>
            {
                var e = Engine(1000, 1, [], []);
                e.Player = e.Player with { HeldGetOutOfJailFreeCardIds = ["chance-jail-free"] };
                e.State = e.State with { OtherPlayers = [e.State.OtherPlayers[0] with { HeldGetOutOfJailFreeCardIds = ["chest-jail-free"] }] };
                var deal = Deal([], []) with { CardIdsGiven = ["chance-jail-free"], CardIdsReceived = ["chest-jail-free"] };
                Valid(e.Player, e.State, deal);
                Invalid(e.Player, e.State, deal with { CardIdsGiven = ["chance-jail-free", "chance-jail-free"], CashReceived = 50 });
            }),
            ("One exchange can combine cash improvements mortgages and a jail card", () =>
            {
                var e = Engine(1000, 1, ["mediterranean-avenue", "reading-railroad"], [], ["pacific-avenue"]);
                e.Player = e.Player with { HeldGetOutOfJailFreeCardIds = ["chance-jail-free"] };
                e.State = Replace(Replace(e.State, Property("mediterranean-avenue") with { IsOwned = true, HouseCount = 2 }),
                    Property("reading-railroad") with { IsOwned = true, IsMortgaged = true });
                var deal = Deal(["mediterranean-avenue", "reading-railroad"], ["pacific-avenue"], paid: 40) with
                { CardIdsGiven = ["chance-jail-free"], OtherPlayerBankFees = 10 };
                Valid(e.Player, e.State, deal);
            }),
            ("A third player's property or card cannot fund a two-party exchange", () =>
            {
                var e = Engine(1000, 1, [], [], ["oriental-avenue"]);
                e.State = e.State with { OtherPlayers = [.. e.State.OtherPlayers, Third(["mediterranean-avenue"]) with { HeldGetOutOfJailFreeCardIds = ["chance-jail-free"] }] };
                Invalid(e.Player, e.State, Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40));
                Invalid(e.Player, e.State, Deal([], ["oriental-avenue"], paid: 50) with { CardIdsGiven = ["chance-jail-free"] });
            }),
            ("Self trades and unknown counterparties are rejected", () =>
            {
                var e = Engine(1000, 1, ["mediterranean-avenue"], []);
                Invalid(e.Player, e.State, Deal(["mediterranean-avenue"], [], receivedCash: 60) with { OtherPlayerId = e.Player.Id });
                Invalid(e.Player, e.State, Deal(["mediterranean-avenue"], [], receivedCash: 60) with { OtherPlayerId = "missing" });
            }),
            ("The acting player cannot pass a same-turn receipt to a third party", () =>
            {
                var e = WashScenario();
                Invalid(e.Player, e.State, Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40));
                var action = Trade(Deal(["mediterranean-avenue"], ["reading-railroad"], paid: 140));
                e.State = e.State with { AvailableActions = [action], OtherPlayers = [e.State.OtherPlayers[0] with { OwnedPropertyIds = ["reading-railroad"] }, Third([])] };
                new InfrastructureStrategy(e).PostRoll(e.Player, e.State)();
                Check(e.Executed.Count == 0, "A strategy selected a wash trade.");
            }),
            ("The counterparty cannot pass its same-turn receipt to the actor", () =>
            {
                var e = Engine(1000, 1, ["mediterranean-avenue"], [], ["oriental-avenue"]);
                e.State = e.State with { CompletedTrades = [new(e.State.TurnNumber, "third", Deal(["oriental-avenue"], []) with { OtherPlayerId = "other", CashReceived = 100 })] };
                Invalid(e.Player, e.State, Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40));
            }),
            ("Wash protection covers the opposite perspective of the original trade", () =>
            {
                var e = WashScenario();
                e.State = e.State with { CompletedTrades = [new(e.State.TurnNumber, e.Player.Id, Deal([], ["mediterranean-avenue"], paid: 60) with { OtherPlayerId = "third" })] };
                Invalid(e.Player, e.State, Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40));
            }),
            ("A receipt from an earlier full turn may be traded", () =>
            {
                var e = WashScenario();
                e.State = e.State with { TurnNumber = e.State.TurnNumber + 1 };
                Valid(e.Player, e.State, Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40));
            }),
            ("Returning a receipt to the same counterparty is not a three-party pass-through", () =>
            {
                var e = Engine(1000, 1, ["mediterranean-avenue"], []);
                e.State = e.State with { CompletedTrades = [new(e.State.TurnNumber, "other", Deal(["mediterranean-avenue"], [], receivedCash: 60) with { OtherPlayerId = e.Player.Id })] };
                Valid(e.Player, e.State, Deal(["mediterranean-avenue"], [], receivedCash: 60));
            }),
            ("A bank purchase may be used as leverage in the same turn", () =>
            {
                var e = Engine(1000, 1, ["mediterranean-avenue"], [], ["oriental-avenue"]);
                Valid(e.Player, e.State, Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40));
            }),
            ("Executed receipt history blocks a later pass-through from the recipient", () =>
            {
                var action = Trade(Deal(["mediterranean-avenue"], [], receivedCash: 60));
                var e = Engine(1000, 1, ["mediterranean-avenue"], [action]);
                e.Execute(e.Player.Id, action);
                var recipient = e.State.OtherPlayers[0];
                var state = e.State with { OtherPlayers = [e.Player, Third([])] };
                var passThrough = Deal(["mediterranean-avenue"], [], receivedCash: 60) with { OtherPlayerId = "third" };
                Invalid(recipient, state, passThrough);
                Valid(recipient, state with { TurnNumber = state.TurnNumber + 1 }, passThrough);
            }),
            ("Counteroffers may keep either or both original properties", () =>
            {
                var e = OriginalScenario();
                var other = e.State.OtherPlayers[0];
                var state = e.State with { OtherPlayers = [e.Player] };
                Valid(other, state, Deal(["oriental-avenue"], ["mediterranean-avenue"], receivedCash: 40) with { OtherPlayerId = e.Player.Id, OriginalOfferId = "root" });
                Valid(other, state, Deal([], ["mediterranean-avenue"], paid: 60) with { OtherPlayerId = e.Player.Id, OriginalOfferId = "root" });
                Valid(other, state, Deal(["oriental-avenue"], [], receivedCash: 100) with { OtherPlayerId = e.Player.Id, OriginalOfferId = "root" });
            }),
            ("Counteroffers cannot discard all original properties", () =>
            {
                var e = OriginalScenario();
                var other = e.State.OtherPlayers[0];
                Invalid(other, e.State with { OtherPlayers = [e.Player] }, Deal([], ["baltic-avenue"], paid: 60) with { OtherPlayerId = e.Player.Id, OriginalOfferId = "root" });
            }),
            ("Counteroffers cannot switch parties or reference a missing original", () =>
            {
                var e = OriginalScenario();
                var deal = Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40) with { OriginalOfferId = "root" };
                Invalid(e.Player, e.State, deal with { OriginalOfferId = "missing" });
                e.State = e.State with { TradeOffers = [new("root", "third", deal with { OriginalOfferId = null })] };
                Invalid(e.Player, e.State, deal);
            }),
            ("Counteroffer chains must reference the root rather than a later counter", () =>
            {
                var e = OriginalScenario();
                var deal = Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40) with { OriginalOfferId = "root" };
                e.State = e.State with { TradeOffers = [.. e.State.TradeOffers, new("counter", e.Player.Id, deal)] };
                Invalid(e.Player, e.State, deal with { OriginalOfferId = "counter" });
            }),
            ("Recorded offers and receipts copy asset lists from caller-owned terms", () =>
            {
                var e = Engine(1000, 1, ["mediterranean-avenue"], [], ["oriental-avenue"]);
                var deal = Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40);
                var offered = TradeValidator.RecordOffer("root", e.Player, e.State, deal);
                var recorded = TradeValidator.RecordCompletedTrade(e.Player, e.State, deal);
                deal.PropertyIdsGiven[0] = "baltic-avenue";
                Check(offered.TradeOffers[0].Terms.PropertyIdsGiven[0] == "mediterranean-avenue" &&
                    recorded.CompletedTrades[0].Terms.PropertyIdsGiven[0] == "mediterranean-avenue", "Caller mutation rewrote the original asset history.");
            }),
            ("Either party's current cash must cover settlement and fees", () =>
            {
                var e = Engine(39, 1, ["mediterranean-avenue"], [], ["oriental-avenue"]);
                Invalid(e.Player, e.State, Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40));
                e.State = e.State with { OtherPlayers = [e.State.OtherPlayers[0] with { CashBalance = 59 }] };
                Invalid(e.Player, e.State, Deal(["mediterranean-avenue"], [], receivedCash: 60));
            }),
            ("Rent can be funded by an asset sale but remains a separate cash obligation", () =>
            {
                var e = Engine(0, 1, ["mediterranean-avenue"], []);
                e.State = e.State with { RentObligations = [new(e.Player.Id, "other", 60)] };
                var recorded = TradeValidator.RecordCompletedTrade(e.Player, e.State, Deal(["mediterranean-avenue"], [], receivedCash: 60));
                Check(recorded.RentObligations.Single().Amount == 60 && recorded.CompletedTrades.Length == 1 && e.Player.CashBalance == 0,
                    "Recording a trade settled rent or performed cash mutation.");
            }),
            ("Future payments, rent waivers, and third-party fields cannot be deserialized as terms", () =>
            {
                foreach (var field in new[] { "futureGoPayment", "rentWaiver", "thirdPlayerId" })
                {
                    var json = "{\"OtherPlayerId\":\"other\",\"" + field + "\":50}";
                    var rejected = false;
                    try
                    {
                        JsonSerializer.Deserialize<PropertyTrade>(json);
                    }
                    catch (JsonException)
                    {
                        rejected = true;
                    }
                    Check(rejected, $"Accepted unsupported consideration: {field}.");
                }
            }),
            ("Negative money, repeated assets, and unknown property IDs are rejected", () =>
            {
                var e = Engine(1000, 1, ["mediterranean-avenue"], []);
                Invalid(e.Player, e.State, Deal(["mediterranean-avenue"], [], receivedCash: -60));
                Invalid(e.Player, e.State, Deal(["mediterranean-avenue", "mediterranean-avenue"], [], receivedCash: 120));
                Invalid(e.Player, e.State, Deal(["unknown"], []));
            })
        };
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS Trades: {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL Trades: {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} trade rule checks passed.");
        return failures;
    }

    private static PropertyState Property(string id) => Board.Single(p => p.PropertyId == id);
    private static GameState Replace(GameState state, PropertyState property) => state with { Properties = state.Properties.Select(p => p.PropertyId == property.PropertyId ? property : p).ToArray() };
    private static Player Third(string[] owned) => new() { Id = "third", Name = "Third", Strategy = "other", CashBalance = 1000, OwnedPropertyIds = owned };
    private static TestEngine WashScenario()
    {
        var e = Engine(1000, 1, ["mediterranean-avenue"], [], ["oriental-avenue"]);
        e.State = e.State with
        {
            OtherPlayers = [.. e.State.OtherPlayers, Third([])],
            CompletedTrades = [new(e.State.TurnNumber, "third", Deal(["mediterranean-avenue"], [], receivedCash: 60) with { OtherPlayerId = e.Player.Id })]
        };
        return e;
    }
    private static TestEngine OriginalScenario()
    {
        var e = Engine(1000, 1, ["mediterranean-avenue", "baltic-avenue"], [], ["oriental-avenue"]);
        e.State = e.State with { TradeOffers = [new("root", e.Player.Id, Deal(["mediterranean-avenue"], ["oriental-avenue"], paid: 40))] };
        return e;
    }
    private static void Valid(Player player, GameState state, PropertyTrade trade)
    {
        var result = TradeValidator.Validate(player, state, trade);
        Check(result.IsValid, string.Join(" ", result.Errors));
    }
    private static void Invalid(Player player, GameState state, PropertyTrade trade)
    {
        Check(!TradeValidator.Validate(player, state, trade).IsValid, "Expected the trade to be rejected.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
