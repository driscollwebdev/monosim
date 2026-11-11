using MonoSim.Models;
using MonoSim.Strategies;
using MonoSim.Trading;

namespace MonoSim;

public sealed partial class MonoSimEngine
{
    private GameState Snapshot(string? viewer, bool includeActions = true)
    {
        var state = new GameState
        {
            SimulationId = simulationId, RandomSeed = randomSeed, EndReason = endReason,
            WinnerIds = [.. winners], PortfolioValues = order.Select(Portfolio).ToArray(),
            TurnNumber = turn, RoundNumber = round, RollNumber = rollNumber, Phase = phase, CurrentPlayerId = currentPlayer,
            Players = order.Select(id => CopyPlayer(players[id])).ToArray(),
            Properties = properties.Values.OrderBy(p => p.BoardPosition).Select(CloneProperty).ToArray(),
            BankHouses = bankHouses, BankHotels = bankHotels,
            BoardSize = definition.Board.Length, BuildingResaleFraction = definition.Money("buildingResaleFraction"),
            MortgageInterestRate = definition.Money("mortgageInterestRate"), GetOutOfJailFreeCardTradeValue = 50,
            DiceCount = definition.Int("diceCount"), DiceSides = definition.Int("diceSides"),
            CompletedTrades = completedTrades.Select(t => t with { Terms = CopyTrade(t.Terms) }).ToArray(),
            TradeOffers = offers.Select(o => o with { Terms = CopyTrade(o.Terms) }).ToArray(),
            Decks = decks.Select(d => new DeckState(d.Key, d.Value.ToArray())).ToArray(),
            Debts = debts.ToArray(),
            RentObligations = debts.Where(d => d.IsRent && d.Remaining > 0 && !players[d.DebtorId].IsBankrupt)
                .Select(d => new RentObligation(d.DebtorId, d.CreditorId!, d.Remaining)).ToArray(),
            CashObligations = debts.Where(d => !d.IsRent && d.Remaining > 0 && !players[d.DebtorId].IsBankrupt)
                .Select(d => new CashObligation(d.DebtorId, d.CreditorId, d.Remaining, d.Description)).ToArray()
        };
        if (viewer is not null)
        {
            state = state with { OtherPlayers = order.Where(id => id != viewer && !players[id].IsBankrupt).Select(id => CopyPlayer(players[id])).ToArray() };
            if (includeActions && !players[viewer].IsBankrupt)
            {
                state = state with { AvailableActions = AvailableActions(viewer, state) };
            }
        }
        if (phase == TurnPhase.Completed && winners.Length == 1)
        {
            state = state with { WinnerId = winners[0] };
        }
        return state;
    }

    private static PropertyTrade CopyTrade(PropertyTrade trade) => trade with
    {
        PropertyIdsGiven = [.. trade.PropertyIdsGiven], PropertyIdsReceived = [.. trade.PropertyIdsReceived],
        CardIdsGiven = [.. trade.CardIdsGiven], CardIdsReceived = [.. trade.CardIdsReceived]
    };

    private string? Owner(string propertyId) => order.FirstOrDefault(id => players[id].OwnedPropertyIds.Contains(propertyId));

    private decimal Owed(string id) => debts.Where(d => d.DebtorId == id).Sum(d => d.Remaining);

    private void RecalculateMortgageValue(string id)
    {
        players[id] = players[id] with { TotalMortgageValue = players[id].OwnedPropertyIds.Sum(p => properties[p].MortgageValue) };
    }

    private void ChangeCash(string id, decimal amount, string description)
    {
        var balance = players[id].CashBalance + amount;
        if (balance < 0)
        {
            throw new InvalidOperationException("Cash transactions cannot overdraw a player.");
        }
        players[id] = players[id] with
        {
            CashBalance = balance,
            FinancialLedger = [.. players[id].FinancialLedger, new(description, amount, balance)]
        };
        Log("Transaction", id, description, amount);
    }

    private void TransferCash(string? payer, string? payee, decimal amount, string description)
    {
        if (amount < 0 || payer == payee)
        {
            throw new InvalidOperationException("A cash transfer requires distinct parties and a nonnegative amount.");
        }
        if (amount == 0)
        {
            return;
        }
        if (payer is not null)
        {
            ChangeCash(payer, -amount, description);
        }
        else
        {
            Log("BankTransaction", null, description, -amount);
        }
        if (payee is not null)
        {
            ChangeCash(payee, amount, description);
        }
        else
        {
            Log("BankTransaction", null, description, amount);
        }
    }

    private StrategyAction[] AvailableActions(string id, GameState state)
    {
        var player = players[id];
        var actions = new List<StrategyAction>();
        if (player.IsInJail && phase == TurnPhase.PreRoll)
        {
            actions.Add(new() { Id = "jail-fine", Type = StrategyActionType.PayJailFine, Cost = definition.Money("jailReleaseFee") });
            foreach (var card in player.HeldGetOutOfJailFreeCardIds)
            {
                actions.Add(new() { Id = $"use-{card}", Type = StrategyActionType.UseGetOutOfJailFreeCard, CardId = card, Cost = 0 });
            }
        }
        if (Owed(id) == 0 && id == currentPlayer && landingProperty is not null && Owner(landingProperty) is null &&
            phase is TurnPhase.PostRoll or TurnPhase.CombinedPostRollPreRoll)
        {
            var p = properties[landingProperty];
            actions.Add(new() { Id = $"buy-{p.PropertyId}", Type = StrategyActionType.BuyProperty, PropertyId = p.PropertyId, Cost = p.SalePrice });
        }
        foreach (var propertyId in player.OwnedPropertyIds)
        {
            var p = properties[propertyId];
            if (p.IsMortgaged)
            {
                actions.Add(new() { Id = $"redeem-{propertyId}", Type = StrategyActionType.UnmortgageProperty,
                    PropertyId = propertyId, Cost = p.MortgageValue * (1 + definition.Money("mortgageInterestRate")), RentIncrease = NormalRent(p, id, 7) });
            }
            else if (Group(p).Where(g => Owner(g.PropertyId) == id).All(g => g.HouseCount + g.HotelCount == 0))
            {
                actions.Add(new() { Id = $"mortgage-{propertyId}", Type = StrategyActionType.MortgageProperty, PropertyId = propertyId, Cost = -p.MortgageValue });
            }
            if (Owed(id) == 0 && CanBuild(id, p, out var hotel))
            {
                var upgraded = p with { HouseCount = p.HouseCount + 1 };
                var cost = p.HousePrice;
                var type = StrategyActionType.BuildHouse;
                if (hotel)
                {
                    upgraded = p with { HouseCount = 0, HotelCount = 1 };
                    cost = p.HotelPrice;
                    type = StrategyActionType.BuildHotel;
                }
                actions.Add(new() { Id = $"build-{propertyId}", Type = type, PropertyId = propertyId, Cost = cost,
                    RentIncrease = NormalRent(upgraded, id, 7) - NormalRent(p, id, 7) });
            }
        }
        foreach (var group in player.OwnedPropertyIds.Select(p => properties[p]).Where(p => p.ColorGroup is not null).GroupBy(p => p.ColorGroup))
        {
            var value = group.Sum(BuildingValue);
            if (value > 0)
            {
                actions.Add(new() { Id = $"sell-buildings-{group.Key}", Type = StrategyActionType.SellBuildings, Cost = -value,
                    Bundle = new() { PropertyIds = group.Select(p => p.PropertyId).ToArray() } });
            }
        }
        foreach (var card in player.HeldGetOutOfJailFreeCardIds)
        {
            actions.Add(new() { Id = $"redeem-{card}", Type = StrategyActionType.RedeemGetOutOfJailFreeCard, CardId = card, Cost = -50 });
        }
        actions.AddRange(TradeActions(id, state));
        foreach (var propertyId in player.OwnedPropertyIds.Where(p => properties[p].HouseCount + properties[p].HotelCount == 0))
        {
            actions.Add(new() { Id = $"auction-{propertyId}", Type = StrategyActionType.AuctionAssets, Cost = 0, Bundle = new() { PropertyIds = [propertyId] } });
        }
        foreach (var card in player.HeldGetOutOfJailFreeCardIds)
        {
            actions.Add(new() { Id = $"auction-{card}", Type = StrategyActionType.AuctionAssets, Cost = 0, Bundle = new() { CardIds = [card] } });
        }
        return actions.ToArray();
    }

    private PropertyState[] Group(PropertyState p)
    {
        if (p.ColorGroup is null)
        {
            return [p];
        }
        return properties.Values.Where(g => g.ColorGroup == p.ColorGroup).ToArray();
    }

    private bool CanBuild(string id, PropertyState p, out bool hotel)
    {
        hotel = p.HouseCount == definition.Int("maxHousesPerStreet");
        if (p.Type != PropertyType.Street || p.HotelCount > 0 || p.IsMortgaged ||
            Group(p).Any(g => Owner(g.PropertyId) != id || g.IsMortgaged))
        {
            return false;
        }
        var level = p.HouseCount;
        if (Group(p).Any(g => g.HotelCount == 0 && g.HouseCount < level))
        {
            return false;
        }
        if (hotel)
        {
            return bankHotels > 0;
        }
        return bankHouses > 0;
    }

    private decimal NormalRent(PropertyState p, string owner, int diceTotal)
    {
        var rent = p.Rent!;
        if (p.Type == PropertyType.Railroad)
        {
            return rent.ByRailroadsOwned[players[owner].OwnedPropertyIds.Count(id => properties[id].Type == PropertyType.Railroad) - 1];
        }
        if (p.Type == PropertyType.Utility)
        {
            return rent.DiceMultipliersByUtilitiesOwned[players[owner].OwnedPropertyIds.Count(id => properties[id].Type == PropertyType.Utility) - 1] * diceTotal;
        }
        if (p.HotelCount > 0)
        {
            return rent.Hotel;
        }
        if (p.HouseCount > 0)
        {
            return rent.Houses[p.HouseCount - 1];
        }
        if (Group(p).All(g => Owner(g.PropertyId) == owner))
        {
            return rent.CompleteColorGroup;
        }
        return rent.Base;
    }
}
