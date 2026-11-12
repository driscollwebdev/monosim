using MonoSim.Models;
using MonoSim.Strategies;

namespace MonoSim.Trading;

/// <summary>A strategy recommendation only; it never changes trade legality.</summary>
public static class TradeSafetyAdvisor
{
    public static TradeSafetyAssessment Assess(Player player, GameState state, PropertyTrade trade)
    {
        var validity = TradeValidator.Validate(player, state, trade);
        if (!validity.IsValid)
        {
            throw new ArgumentException(string.Join(" ", validity.Errors), nameof(trade));
        }
        if (state.BoardSize <= 0 || player.BoardPosition < 0 || player.BoardPosition >= state.BoardSize ||
            state.DiceCount <= 0 || state.DiceSides <= 0 ||
            state.Properties.Any(p => p.BoardPosition < 0 || p.BoardPosition >= state.BoardSize))
        {
            throw new ArgumentException("The advisory requires a valid board, player position, and dice configuration.", nameof(state));
        }

        var properties = state.Properties.ToDictionary(p => p.PropertyId, StringComparer.Ordinal);
        var ownedAfter = player.OwnedPropertyIds.Except(trade.PropertyIdsGiven).Concat(trade.PropertyIdsReceived).ToHashSet(StringComparer.Ordinal);
        var other = state.OtherPlayers.Single(p => p.Id == trade.OtherPlayerId);
        var otherAfter = other.OwnedPropertyIds.Except(trade.PropertyIdsReceived).Concat(trade.PropertyIdsGiven).ToHashSet(StringComparer.Ordinal);
        var cardsAfter = player.HeldGetOutOfJailFreeCardIds.Except(trade.CardIdsGiven).Concat(trade.CardIdsReceived).Count();
        var postCash = player.CashBalance - trade.NetCost;
        var assets = ownedAfter.Sum(id => TradeValidator.PropertyValue(properties[id], state.BuildingResaleFraction)) +
            cardsAfter * state.GetOutOfJailFreeCardTradeValue;
        var totalValue = postCash + assets;
        var threats = new List<TradeRentExposure>();

        foreach (var id in trade.PropertyIdsGiven)
        {
            var property = properties[id];
            if (property.IsMortgaged)
            {
                continue;
            }
            var outcomes = Enumerable.Range(state.DiceCount, state.DiceCount * state.DiceSides - state.DiceCount + 1)
                .Where(total => (player.BoardPosition + total) % state.BoardSize == property.BoardPosition)
                .Select(total => (Total: total, Rent: RentAt(property, state, otherAfter, total))).ToArray();
            var dangerous = outcomes.Where(o => o.Rent > totalValue).ToArray();
            if (outcomes.Length > 0)
            {
                threats.Add(new(property.PropertyId, property.PropertyName, outcomes.Max(o => o.Rent), dangerous.Select(o => o.Total).ToArray()));
            }
        }

        var due = DebtDue(player, state);
        var fundsDebt = due > player.CashBalance && postCash >= due && postCash > player.CashBalance;
        return new(postCash, assets, threats.ToArray(), fundsDebt);
    }

    public static bool ShouldConsiderAction(Player player, GameState state, StrategyAction action)
    {
        if (!TradeValidator.IsTradeAction(action))
        {
            return true;
        }
        if (!TradeValidator.ValidateAction(player, state, action).IsValid)
        {
            return false;
        }
        var trade = TradeValidator.TermsForAction(player, state, action)!;
        return !Assess(player, state, trade).ShouldAvoid && !StrategicTradeAdvisor.AvoidsTransfer(player, state, trade);
    }

    public static decimal DebtDue(Player player, GameState state) =>
        state.RentObligations.Where(o => o.PayerId == player.Id && o.Amount > 0).Sum(o => o.Amount) +
        state.CashObligations.Where(o => o.PayerId == player.Id && o.Amount > 0).Sum(o => o.Amount);

    /// <summary>Debt rescue bypasses discretionary investment preferences, but never trade legality.</summary>
    public static StrategyAction? FindDebtFundingTrade(Player player, GameState state)
    {
        if (DebtDue(player, state) <= player.CashBalance)
        {
            return null;
        }
        return state.AvailableActions.Where(a => TradeValidator.IsTradeAction(a) && TradeValidator.ValidateAction(player, state, a).IsValid)
            .Select(a => (Action: a, Assessment: Assess(player, state, TradeValidator.TermsForAction(player, state, a)!)))
            .Where(c => c.Assessment.FundsDueDebt)
            .OrderBy(c => c.Assessment.HasImminentJeopardy)
            .ThenBy(c => c.Assessment.PostTradeCash)
            .ThenBy(c => c.Action.Id, StringComparer.Ordinal)
            .Select(c => c.Action).FirstOrDefault();
    }

    private static decimal RentAt(PropertyState property, GameState state, HashSet<string> ownerIds, int diceTotal)
    {
        var rent = property.Rent ?? throw new ArgumentException($"Missing rent schedule for {property.PropertyId}.");
        if (property.Type == PropertyType.Railroad)
        {
            var count = state.Properties.Count(p => p.Type == PropertyType.Railroad && ownerIds.Contains(p.PropertyId));
            return rent.ByRailroadsOwned[count - 1];
        }
        if (property.Type == PropertyType.Utility)
        {
            var count = state.Properties.Count(p => p.Type == PropertyType.Utility && ownerIds.Contains(p.PropertyId));
            return rent.DiceMultipliersByUtilitiesOwned[count - 1] * diceTotal;
        }
        if (property.HotelCount > 0)
        {
            return rent.Hotel;
        }
        if (property.HouseCount > 0)
        {
            return rent.Houses[property.HouseCount - 1];
        }
        var group = state.Properties.Where(p => p.ColorGroup == property.ColorGroup).ToArray();
        if (property.ColorGroup is not null && group.All(p => ownerIds.Contains(p.PropertyId)))
        {
            return rent.CompleteColorGroup;
        }
        return rent.Base;
    }
}
