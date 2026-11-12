using MonoSim.Models;
using MonoSim.Strategies;

namespace MonoSim.Trading;

public static class StrategicTradeAdvisor
{
    public static bool AvoidsTransfer(Player player, GameState state, PropertyTrade trade)
    {
        if (DefaultStrategyMarketPolicy.Kind(player) == "novice" || TradeSafetyAdvisor.Assess(player, state, trade).FundsDueDebt)
        {
            return false;
        }
        var rolls = 1;
        if (DefaultStrategyMarketPolicy.Kind(player) == "conservative" && state.Phase is TurnPhase.PreRoll or TurnPhase.CombinedPostRollPreRoll)
        {
            rolls = 2;
        }
        return state.Properties.Where(p => trade.PropertyIdsGiven.Contains(p.PropertyId)).Any(p =>
            Enumerable.Range(state.DiceCount, state.DiceCount * state.DiceSides - state.DiceCount + 1).Any(total =>
                (player.BoardPosition + total) % state.BoardSize == p.BoardPosition) ||
            rolls == 2 && Enumerable.Range(2 * state.DiceCount, 2 * state.DiceCount * state.DiceSides - 2 * state.DiceCount + 1).Any(total =>
                (player.BoardPosition + total) % state.BoardSize == p.BoardPosition));
    }

    /// <summary>A singleton contributes no consolidation progress; two or more members do.</summary>
    public static int MonopolyProgress(GameState state, HashSet<string> owned) => Sets(state).Sum(group =>
    {
        var count = group.Count(p => owned.Contains(p.PropertyId));
        return count * (count - 1);
    });

    public static bool CompletesMonopoly(GameState state, HashSet<string> before, HashSet<string> after) =>
        Sets(state).Any(group => !group.All(p => before.Contains(p.PropertyId)) && group.All(p => after.Contains(p.PropertyId)));

    public static IEnumerable<PropertyState[]> Sets(GameState state) => state.Properties.Where(p => p.Type == PropertyType.Street && p.ColorGroup is not null)
        .GroupBy(p => p.ColorGroup).Select(g => g.ToArray())
        .Concat(state.Properties.Where(p => p.Type != PropertyType.Street).GroupBy(p => p.Type).Select(g => g.ToArray()));

    public static bool FundsInvestment(Player player, GameState state, PropertyTrade trade, Func<PropertyState, bool> wanted)
    {
        var cashAfter = player.CashBalance - trade.NetCost;
        if (cashAfter <= player.CashBalance)
        {
            return false;
        }
        var after = player.OwnedPropertyIds.Except(trade.PropertyIdsGiven).Concat(trade.PropertyIdsReceived).ToHashSet(StringComparer.Ordinal);
        var expenses = new List<decimal>();
        var next = state.Properties.Where(p => p.IsAvailable && wanted(p))
            .OrderBy(p => InfrastructureStrategy.ForwardDistance(player.BoardPosition, p.BoardPosition, state.BoardSize)).FirstOrDefault();
        if (next is not null)
        {
            expenses.Add(next.SalePrice);
        }
        expenses.AddRange(state.Properties.Where(p => after.Contains(p.PropertyId) && p.IsMortgaged)
            .Select(p => p.MortgageValue * (1 + state.MortgageInterestRate)));
        foreach (var group in state.Properties.Where(p => p.ColorGroup is not null).GroupBy(p => p.ColorGroup))
        {
            if (!group.All(p => after.Contains(p.PropertyId) && !p.IsMortgaged))
            {
                continue;
            }
            var level = group.Min(p => p.HouseCount + 5 * p.HotelCount);
            if (level >= 5)
            {
                continue;
            }
            if (level < 4 && state.BankHouses <= 0 || level == 4 && state.BankHotels <= 0)
            {
                continue;
            }
            expenses.AddRange(group.Where(p => p.HouseCount == level && p.HotelCount == 0).Select(p => level == 4 ? p.HotelPrice : p.HousePrice));
        }
        return expenses.Any(cost => cost > player.CashBalance && cost <= cashAfter);
    }
}
