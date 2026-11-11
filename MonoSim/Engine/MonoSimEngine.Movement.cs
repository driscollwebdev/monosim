using System.Text.Json;
using MonoSim.Models;

namespace MonoSim;

public sealed partial class MonoSimEngine
{
    private void Move(string id, int steps, bool collectGo)
    {
        var start = players[id].BoardPosition;
        var absolute = start + steps;
        var position = ((absolute % definition.Board.Length) + definition.Board.Length) % definition.Board.Length;
        if (collectGo && steps > 0 && absolute >= definition.Board.Length)
        {
            var circuits = absolute / definition.Board.Length;
            TransferCash(null, id, circuits * definition.Money("goSalary"), "GO salary");
        }
        players[id] = players[id] with { BoardPosition = position };
        Log("PlayerMoved", id, $"{start} -> {position} ({steps} spaces)");
    }

    private void MoveTo(string id, int destination, bool collectGo)
    {
        var steps = (destination - players[id].BoardPosition + definition.Board.Length) % definition.Board.Length;
        if (steps == 0 && destination == 0 && collectGo)
        {
            steps = definition.Board.Length;
        }
        Move(id, steps, collectGo);
    }

    private void ResolveSpace(string id, int diceTotal, decimal rentMultiplier = 1, int? utilityMultiplier = null)
    {
        if (players[id].IsBankrupt || ActiveCount <= 1)
        {
            return;
        }
        var space = definition.Board[players[id].BoardPosition];
        Log("SpaceResolved", id, $"{space.Position}: {space.Name}");
        if (properties.TryGetValue(space.Id, out var property))
        {
            var owner = Owner(property.PropertyId);
            if (owner is null)
            {
                if (id == currentPlayer)
                {
                    landingProperty = property.PropertyId;
                }
                Log("PurchaseOpportunity", id, property.PropertyId);
            }
            else if (owner != id && !property.IsMortgaged)
            {
                var rent = NormalRent(property, owner, diceTotal) * rentMultiplier;
                if (utilityMultiplier is { } multiplier && property.Type == PropertyType.Utility)
                {
                    rent = multiplier * diceTotal;
                }
                Owe(id, owner, rent, true, $"Rent on {property.PropertyId}");
            }
            return;
        }
        switch (space.Type)
        {
            case "drawCard":
                DrawCard(id, space.DeckId!, diceTotal);
                break;
            case "goToJail":
                SendToJail(id, "Go To Jail space");
                break;
            case "tax":
                ResolveEffects(id, space.Effects, diceTotal);
                break;
        }
    }

    private void DrawCard(string id, string deckId, int diceTotal)
    {
        var cardId = decks[deckId].Dequeue();
        var card = definition.Cards[cardId];
        Log("CardDrawn", id, $"{deckId}: {cardId}");
        var retained = card.Effects.EnumerateArray().Any(e => e.GetProperty("type").GetString() == "retain");
        if (retained)
        {
            players[id] = players[id] with { HeldGetOutOfJailFreeCardIds = [.. players[id].HeldGetOutOfJailFreeCardIds, cardId] };
            Log("CardRetained", id, cardId);
        }
        else
        {
            decks[deckId].Enqueue(cardId);
            Log("CardReturned", id, $"{cardId} returned to the bottom of {deckId}.");
            ResolveEffects(id, card.Effects, diceTotal);
        }
    }

    private void ResolveEffects(string id, JsonElement effects, int diceTotal)
    {
        if (effects.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var effect in effects.EnumerateArray())
        {
            if (players[id].IsBankrupt || ActiveCount <= 1)
            {
                break;
            }
            var type = effect.GetProperty("type").GetString();
            switch (type)
            {
                case "transfer":
                    var amount = effect.GetProperty("amount").GetDecimal();
                    var from = effect.GetProperty("from").GetString();
                    var to = effect.GetProperty("to").GetString();
                    if (from == "bank")
                    {
                        TransferCash(null, id, amount, "Card bank payment");
                    }
                    else if (to == "bank")
                    {
                        Owe(id, null, amount, false, "Card bank charge");
                    }
                    else
                    {
                        foreach (var other in order.Where(p => p != id && !players[p].IsBankrupt))
                        {
                            if (from == "eachOtherPlayer")
                            {
                                Owe(other, id, amount, false, "Card payment to player");
                            }
                            else
                            {
                                Owe(id, other, amount, false, "Card payment to player");
                            }
                        }
                    }
                    SettleDebts();
                    break;
                case "repairs":
                    var cost = players[id].OwnedPropertyIds.Sum(p => properties[p].HouseCount * effect.GetProperty("perHouse").GetDecimal() +
                        properties[p].HotelCount * effect.GetProperty("perHotel").GetDecimal());
                    Owe(id, null, cost, false, "Building repair assessment");
                    SettleDebts();
                    break;
                case "goToJail":
                    SendToJail(id, "Card instruction");
                    break;
                case "moveTo":
                    var destination = definition.Board.Single(s => s.Id == effect.GetProperty("destinationId").GetString()).Position;
                    MoveTo(id, destination, effect.GetProperty("collectGo").GetBoolean());
                    ResolveSpace(id, diceTotal);
                    break;
                case "moveRelative":
                    Move(id, effect.GetProperty("spaces").GetInt32(), effect.GetProperty("collectGo").GetBoolean());
                    ResolveSpace(id, diceTotal);
                    break;
                case "moveToNearest":
                    var target = effect.GetProperty("spaceType").GetString();
                    var next = definition.Board.Where(s => s.Type == target)
                        .OrderBy(s => Strategies.InfrastructureStrategy.ForwardDistance(players[id].BoardPosition, s.Position, definition.Board.Length)).First();
                    MoveTo(id, next.Position, effect.GetProperty("collectGo").GetBoolean());
                    var rentOverride = effect.GetProperty("rentOverride");
                    if (rentOverride.GetProperty("type").GetString() == "diceMultiplier")
                    {
                        var owner = Owner(next.Id);
                        var total = diceTotal;
                        if (owner is not null && owner != id && !properties[next.Id].IsMortgaged)
                        {
                            total = RollDice(id).Sum();
                        }
                        ResolveSpace(id, total, utilityMultiplier: rentOverride.GetProperty("multiplier").GetInt32());
                    }
                    else
                    {
                        ResolveSpace(id, diceTotal, rentOverride.GetProperty("multiplier").GetDecimal());
                    }
                    break;
            }
        }
    }

    private void SendToJail(string id, string reason)
    {
        players[id] = players[id] with { IsInJail = true, FailedJailRolls = 0, BoardPosition = definition.Board.Single(s => s.Type == "jail").Position };
        landingProperty = null;
        Log("SentToJail", id, reason);
    }

    private void ReleaseJail(string id, string reason)
    {
        players[id] = players[id] with { IsInJail = false, FailedJailRolls = 0 };
        Log("ReleasedFromJail", id, reason);
    }

    private void FinishLandingOffer(string id)
    {
        if (landingProperty is not null && Owner(landingProperty) is null && definition.Rules.GetProperty("auctionDeclinedProperties").GetBoolean())
        {
            Auction(null, new() { PropertyIds = [landingProperty] });
        }
        landingProperty = null;
    }
}
