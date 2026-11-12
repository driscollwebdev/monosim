using MonoSim.Models;
using MonoSim.Strategies;

namespace MonoSim.Trading;

/// <summary>Validates current two-party exchanges; no rent waiver or future consideration is representable.</summary>
public static class TradeValidator
{
    public static bool IsTradeAction(StrategyAction action) => action.Type is StrategyActionType.TradeProperties or StrategyActionType.BuyMortgagedProperty;

    public static PropertyTrade? TermsForAction(Player player, GameState state, StrategyAction action)
    {
        if (action.Type == StrategyActionType.TradeProperties)
        {
            return action.Trade;
        }
        if (action.Type != StrategyActionType.BuyMortgagedProperty)
        {
            return null;
        }
        var property = state.Properties.FirstOrDefault(p => p.PropertyId == action.PropertyId && p.IsMortgaged && p.IsOwned);
        var owners = state.OtherPlayers.Where(p => p.Id != player.Id && p.OwnedPropertyIds.Contains(action.PropertyId!)).ToArray();
        if (property is null || owners.Length != 1)
        {
            return null;
        }
        return action.Trade ?? new PropertyTrade
        {
            OtherPlayerId = owners[0].Id,
            PropertyIdsReceived = [property.PropertyId],
            CashPaid = property.MortgageValue,
            BankFees = property.MortgageValue * state.MortgageInterestRate
        };
    }

    public static TradeValidationResult ValidateAction(Player player, GameState state, StrategyAction action)
    {
        var terms = TermsForAction(player, state, action);
        if (terms is null || action.Cost != terms.NetCost)
        {
            return new(["A trade action requires two-party terms matching its total cash cost."], 0, 0);
        }
        if (action.Type == StrategyActionType.BuyMortgagedProperty &&
            (!terms.PropertyIdsReceived.SequenceEqual([action.PropertyId!]) || terms.PropertyIdsGiven.Length != 0 ||
             terms.CardIdsGiven.Length + terms.CardIdsReceived.Length != 0 || terms.CashReceived != 0))
        {
            return new(["A mortgage purchase must buy its named property for current cash; other exchanges use TradeProperties."], 0, 0);
        }
        return Validate(player, state, terms);
    }

    public static TradeValidationResult Validate(Player player, GameState state, PropertyTrade trade)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(trade);
        var errors = new List<string>();
        var others = state.OtherPlayers.Where(p => p.Id == trade.OtherPlayerId && p.Id != player.Id).ToArray();
        if (others.Length != 1)
        {
            return new(["A trade must involve the acting player and exactly one distinct counterparty."], 0, 0);
        }

        var other = others[0];
        var properties = state.Properties.ToDictionary(p => p.PropertyId, StringComparer.Ordinal);
        CheckAssets(trade.PropertyIdsGiven, player.OwnedPropertyIds, other.OwnedPropertyIds, "Offered property");
        CheckAssets(trade.PropertyIdsReceived, other.OwnedPropertyIds, player.OwnedPropertyIds, "Requested property");
        CheckAssets(trade.CardIdsGiven, player.HeldGetOutOfJailFreeCardIds, other.HeldGetOutOfJailFreeCardIds, "Offered card");
        CheckAssets(trade.CardIdsReceived, other.HeldGetOutOfJailFreeCardIds, player.HeldGetOutOfJailFreeCardIds, "Requested card");
        if (trade.PropertyIdsGiven.Intersect(trade.PropertyIdsReceived).Any() || trade.CardIdsGiven.Intersect(trade.CardIdsReceived).Any())
        {
            errors.Add("An asset cannot appear on both sides of a trade.");
        }
        if (trade.CashPaid < 0 || trade.CashReceived < 0 || trade.BankFees < 0 || trade.OtherPlayerBankFees < 0)
        {
            errors.Add("Cash transfers and bank fees must be nonnegative.");
        }
        if (trade.PropertyIdsGiven.Length + trade.PropertyIdsReceived.Length + trade.CardIdsGiven.Length + trade.CardIdsReceived.Length == 0 &&
            trade.CashPaid == 0 && trade.CashReceived == 0)
        {
            errors.Add("An empty trade is not an exchange.");
        }

        var given = Value(trade.PropertyIdsGiven) + trade.CardIdsGiven.Length * state.GetOutOfJailFreeCardTradeValue + trade.CashPaid;
        var received = Value(trade.PropertyIdsReceived) + trade.CardIdsReceived.Length * state.GetOutOfJailFreeCardTradeValue + trade.CashReceived;

        var expectedFees = MortgageFees(trade.PropertyIdsReceived);
        var expectedOtherFees = MortgageFees(trade.PropertyIdsGiven);
        if (trade.BankFees != expectedFees || trade.OtherPlayerBankFees != expectedOtherFees)
        {
            errors.Add("Each recipient must pay the mortgage-transfer interest due on the properties they receive.");
        }
        if (player.CashBalance - trade.NetCost < 0 || other.CashBalance + trade.CashPaid - trade.CashReceived - trade.OtherPlayerBankFees < 0)
        {
            errors.Add("Both parties must have sufficient current cash to settle the exchange and bank fees.");
        }

        CheckWashTrades(player.Id, other.Id, trade.PropertyIdsGiven);
        CheckWashTrades(other.Id, player.Id, trade.PropertyIdsReceived);
        if (trade.OriginalOfferId is not null)
        {
            var originals = state.TradeOffers.Where(o => o.Id == trade.OriginalOfferId).ToArray();
            if (originals.Length != 1 || originals[0].Terms.OriginalOfferId is not null)
            {
                errors.Add("A counteroffer must reference its retained root original offer.");
            }
            else
            {
                var original = originals[0];
                if (!new[] { original.PlayerId, original.Terms.OtherPlayerId }.ToHashSet(StringComparer.Ordinal).SetEquals([player.Id, other.Id]))
                {
                    errors.Add("A counteroffer must involve the same two players as the original offer.");
                }
                if (!original.Terms.PropertyIdsGiven.Concat(original.Terms.PropertyIdsReceived)
                    .Intersect(trade.PropertyIdsGiven.Concat(trade.PropertyIdsReceived)).Any())
                {
                    errors.Add("A counteroffer must retain at least one property from the original offer.");
                }
            }
        }
        return new(errors.ToArray(), given, received);

        void CheckAssets(string[] ids, string[] ownerIds, string[] recipientIds, string label)
        {
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
                ids.Any(id => string.IsNullOrWhiteSpace(id) || !ownerIds.Contains(id) || recipientIds.Contains(id)))
            {
                errors.Add($"{label} IDs must be unique, currently owned by their sender, and absent from their recipient.");
            }
        }

        decimal Value(string[] ids)
        {
            decimal total = 0;
            foreach (var id in ids)
            {
                if (!properties.TryGetValue(id, out var property))
                {
                    errors.Add($"Unknown property: {id}.");
                    continue;
                }
                try
                {
                    total += PropertyValue(property, state.BuildingResaleFraction);
                }
                catch (ArgumentException error)
                {
                    errors.Add(error.Message);
                }
            }
            return total;
        }

        decimal MortgageFees(string[] ids) => ids.Where(properties.ContainsKey).Select(id => properties[id])
            .Where(p => p.IsMortgaged).Sum(p => p.MortgageValue * state.MortgageInterestRate);

        void CheckWashTrades(string sender, string recipient, string[] ids)
        {
            foreach (var completed in state.CompletedTrades.Where(t => t.TurnNumber == state.TurnNumber))
            {
                if (completed.PlayerId == sender && completed.Terms.OtherPlayerId != recipient &&
                    completed.Terms.PropertyIdsReceived.Intersect(ids).Any() ||
                    completed.Terms.OtherPlayerId == sender && completed.PlayerId != recipient &&
                    completed.Terms.PropertyIdsGiven.Intersect(ids).Any())
                {
                    errors.Add("A property received in trade cannot be offered to a third player in the same turn.");
                }
            }
        }
    }

    public static decimal PropertyValue(PropertyState property, decimal buildingResaleFraction = 0.5m)
    {
        if (property.SalePrice < 0 || property.MortgageValue < 0 || property.HousePrice < 0 || property.HotelPrice < 0 ||
            property.HouseCount is < 0 or > 4 || property.HotelCount is < 0 or > 1 ||
            property.HotelCount > 0 && property.HouseCount > 0 || buildingResaleFraction is < 0 or > 1)
        {
            throw new ArgumentException($"Invalid financial or building data for {property.PropertyId}.");
        }
        if (property.IsMortgaged)
        {
            if (property.MortgageValue <= 0 || property.HouseCount + property.HotelCount > 0)
            {
                throw new ArgumentException($"Mortgaged property {property.PropertyId} requires a principal and no buildings.");
            }
            return property.MortgageValue;
        }
        if (property.HouseCount > 0 && property.HousePrice <= 0 || property.HotelCount > 0 && (property.HousePrice <= 0 || property.HotelPrice <= 0))
        {
            throw new ArgumentException($"Missing building prices for {property.PropertyId}.");
        }
        var buildingCost = property.HouseCount * property.HousePrice + property.HotelCount * (4 * property.HousePrice + property.HotelPrice);
        return property.SalePrice + buildingCost * buildingResaleFraction;
    }

    /// <summary>Returns a validated snapshot with receipt history recorded; does not transfer assets or settle rent.</summary>
    public static GameState RecordCompletedTrade(Player playerBeforeTrade, GameState stateBeforeTrade, PropertyTrade trade)
    {
        var validation = Validate(playerBeforeTrade, stateBeforeTrade, trade);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(" ", validation.Errors));
        }
        return stateBeforeTrade with
        {
            CompletedTrades = [.. stateBeforeTrade.CompletedTrades.Where(t => t.TurnNumber == stateBeforeTrade.TurnNumber),
                new(stateBeforeTrade.TurnNumber, playerBeforeTrade.Id, SnapshotTerms(trade))]
        };
    }

    /// <summary>Validates an offer before publication and retains its asset IDs for counteroffers.</summary>
    public static GameState RecordOffer(string id, Player player, GameState state, PropertyTrade trade)
    {
        if (string.IsNullOrWhiteSpace(id) || state.TradeOffers.Any(o => o.Id == id))
        {
            throw new ArgumentException("An offer requires a unique, nonempty ID.", nameof(id));
        }
        var validation = Validate(player, state, trade);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(" ", validation.Errors));
        }
        return state with { TradeOffers = [.. state.TradeOffers, new(id, player.Id, SnapshotTerms(trade))] };
    }

    private static PropertyTrade SnapshotTerms(PropertyTrade trade) => trade with
    {
        PropertyIdsGiven = [.. trade.PropertyIdsGiven],
        PropertyIdsReceived = [.. trade.PropertyIdsReceived],
        CardIdsGiven = [.. trade.CardIdsGiven],
        CardIdsReceived = [.. trade.CardIdsReceived]
    };
}
