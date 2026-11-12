using MonoSim.Models;
using MonoSim.Strategies;
using MonoSim.Trading;

namespace MonoSim;

public sealed partial class MonoSimEngine
{
    public StrategyTurnState Execute(string playerId, StrategyAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!playing || marketDecision || decisionPlayer != playerId || players[playerId].IsBankrupt)
        {
            throw new InvalidOperationException("Actions may execute only in the acting player's decision window.");
        }
        options.CancellationToken.ThrowIfCancellationRequested();
        if (declared && phase != TurnPhase.RollAndMovement)
        {
            throw new InvalidOperationException("No actions may follow a completion declaration.");
        }
        var state = Snapshot(playerId, false);
        var executed = action;
        if (TradeValidator.IsTradeAction(action))
        {
            var trade = ExecuteTrade(playerId, action, state);
            if (trade is null)
            {
                decisionRevision++;
                return new(CopyPlayer(players[playerId]), Snapshot(playerId)) { ActionExecuted = false };
            }
            executed = trade;
        }
        else if (action.Type == StrategyActionType.AuctionAssets)
        {
            Auction(playerId, action.Bundle ?? throw new ArgumentException("An auction requires a bundle."));
        }
        else if (action.Type == StrategyActionType.DeclareBankruptcy)
        {
            DeclareBankruptcy(playerId);
        }
        else
        {
            var legal = AvailableActions(playerId, state).FirstOrDefault(a => a.Id == action.Id && a.Type == action.Type && a.Cost == action.Cost && a.PropertyId == action.PropertyId && a.CardId == action.CardId);
            if (legal is null)
            {
                throw new InvalidOperationException("The action is not currently available.");
            }
            if (players[playerId].CashBalance < action.Cost)
            {
                throw new InvalidOperationException("The player cannot afford the action.");
            }
            switch (action.Type)
            {
                case StrategyActionType.BuyProperty:
                    TransferCash(playerId, null, action.Cost, $"Purchase {action.PropertyId}");
                    GiveProperty(null, playerId, action.PropertyId!);
                    landingProperty = null;
                    break;
                case StrategyActionType.BuildHouse:
                case StrategyActionType.BuildHotel:
                    Build(playerId, properties[action.PropertyId!], action.Type == StrategyActionType.BuildHotel);
                    break;
                case StrategyActionType.MortgageProperty:
                    Mortgage(playerId, action.PropertyId!);
                    break;
                case StrategyActionType.UnmortgageProperty:
                    TransferCash(playerId, null, action.Cost, $"Redeem mortgage {action.PropertyId}");
                    properties[action.PropertyId!] = properties[action.PropertyId!] with { IsMortgaged = false };
                    break;
                case StrategyActionType.SellBuildings:
                    SellBuildings(playerId, legal.Bundle!.PropertyIds);
                    break;
                case StrategyActionType.RedeemGetOutOfJailFreeCard:
                    RedeemCard(playerId, action.CardId!);
                    break;
                case StrategyActionType.UseGetOutOfJailFreeCard:
                    ReturnCard(playerId, action.CardId!);
                    ReleaseJail(playerId, "Used Get Out of Jail Free card.");
                    break;
                case StrategyActionType.PayJailFine:
                    TransferCash(playerId, null, action.Cost, "Voluntary jail release fine");
                    ReleaseJail(playerId, "Paid voluntary jail release fine.");
                    break;
                default:
                    throw new InvalidOperationException("Unsupported action.");
            }
        }
        Log("ActionExecuted", playerId, $"{action.Type}: {action.Id}");
        decisionRevision++;
        return new(CopyPlayer(players[playerId]), Snapshot(playerId)) { ExecutedAction = executed };
    }

    private void GiveProperty(string? from, string? to, string id)
    {
        if (from is not null)
        {
            players[from] = players[from] with { OwnedPropertyIds = players[from].OwnedPropertyIds.Where(p => p != id).ToArray() };
            RecalculateMortgageValue(from);
        }
        if (to is not null)
        {
            players[to] = players[to] with { OwnedPropertyIds = [.. players[to].OwnedPropertyIds, id] };
            properties[id] = properties[id] with { IsOwned = true };
            RecalculateMortgageValue(to);
        }
        else
        {
            properties[id] = properties[id] with { IsOwned = false, IsMortgaged = false, HouseCount = 0, HotelCount = 0 };
        }
        Log("PropertyTransferred", to, $"{id}: {from ?? "bank"} -> {to ?? "bank"}");
    }

    private void Build(string id, PropertyState p, bool hotel)
    {
        if (!CanBuild(id, p, out var expectedHotel) || expectedHotel != hotel)
        {
            throw new InvalidOperationException("Construction must be even, unmortgaged, and backed by bank stock.");
        }
        var cost = p.HousePrice;
        if (hotel)
        {
            cost = p.HotelPrice;
            bankHotels--;
            bankHouses += p.HouseCount;
            properties[p.PropertyId] = p with { HouseCount = 0, HotelCount = 1 };
        }
        else
        {
            bankHouses--;
            properties[p.PropertyId] = p with { HouseCount = p.HouseCount + 1 };
        }
        TransferCash(id, null, cost, $"Build on {p.PropertyId}");
        Log("BuildingPurchased", id, $"{p.PropertyId}: hotel={hotel}");
    }

    private decimal BuildingValue(PropertyState p) =>
        (p.HouseCount * p.HousePrice + p.HotelCount * (definition.Int("housesReturnedForHotel") * p.HousePrice + p.HotelPrice)) * definition.Money("buildingResaleFraction");

    private void SellBuildings(string id, string[] ids)
    {
        if (ids.Any(p => Owner(p) != id))
        {
            throw new InvalidOperationException("Only owned buildings may be sold.");
        }
        var value = ids.Sum(p => BuildingValue(properties[p]));
        foreach (var propertyId in ids)
        {
            var p = properties[propertyId];
            bankHouses += p.HouseCount;
            bankHotels += p.HotelCount;
            properties[propertyId] = p with { HouseCount = 0, HotelCount = 0 };
            Log("BuildingsReturned", id, $"{propertyId}: {p.HouseCount} houses, {p.HotelCount} hotels returned to bank.");
        }
        TransferCash(null, id, value, "Bank building resale");
    }

    private void Mortgage(string id, string propertyId)
    {
        var p = properties[propertyId];
        if (Owner(propertyId) != id || p.IsMortgaged || Group(p).Any(g => Owner(g.PropertyId) == id && g.HouseCount + g.HotelCount > 0))
        {
            throw new InvalidOperationException("A mortgage requires an unimproved color group and an unmortgaged owned property.");
        }
        properties[propertyId] = p with { IsMortgaged = true };
        TransferCash(null, id, p.MortgageValue, $"Mortgage {propertyId}");
        Log("PropertyMortgaged", id, propertyId);
    }

    private void ReturnCard(string id, string cardId)
    {
        if (!players[id].HeldGetOutOfJailFreeCardIds.Contains(cardId))
        {
            throw new InvalidOperationException("The player does not hold this card.");
        }
        players[id] = players[id] with { HeldGetOutOfJailFreeCardIds = players[id].HeldGetOutOfJailFreeCardIds.Where(c => c != cardId).ToArray() };
        decks[definition.Cards[cardId].DeckId].Enqueue(cardId);
        Log("CardReturned", id, $"{cardId} returned to its original deck's bottom.");
    }

    private void RedeemCard(string id, string cardId)
    {
        ReturnCard(id, cardId);
        TransferCash(null, id, 50, "Bank redemption of Get Out of Jail Free card");
    }
}
