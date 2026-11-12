using MonoSim.Models;
using MonoSim.Trading;

namespace MonoSim.Strategies;

/// <summary>Preserves the opportunity to consolidate an existing group on the next direct roll.</summary>
public static class AggressivePurchaseAdvisor
{
    public static decimal PurchaseReserve(Player player, GameState state, PropertyState purchase)
    {
        if (player.CompletedLaps == 0 || CompletesAcquisition(player, state, [purchase.PropertyId]))
        {
            return 0;
        }
        var current = HoldingsInGroup(player, state, purchase);
        return Reserve(player, state, p => p.PropertyId != purchase.PropertyId && HoldingsInGroup(player, state, p) > current);
    }

    public static decimal AcquisitionReserve(Player player, GameState state)
    {
        if (player.CompletedLaps == 0)
        {
            return 0;
        }
        return Reserve(player, state, p => HoldingsInGroup(player, state, p) > 0);
    }

    public static decimal DevelopmentReserve(Player player, GameState state)
    {
        if (player.CompletedLaps == 0)
        {
            return 0;
        }
        return Reserve(player, state, p => CompletesAcquisition(player, state, [p.PropertyId]));
    }

    public static bool CompletesAcquisition(Player player, GameState state, IEnumerable<string> received, IEnumerable<string>? given = null)
    {
        var before = player.OwnedPropertyIds.ToHashSet(StringComparer.Ordinal);
        var after = before.Except(given ?? []).Concat(received).ToHashSet(StringComparer.Ordinal);
        return StrategicTradeAdvisor.CompletesMonopoly(state, before, after);
    }

    private static decimal Reserve(Player player, GameState state, Func<PropertyState, bool> preferred)
    {
        var opportunities = state.Properties.Where(p => p.IsAvailable && p.SalePrice > 0 && p.SalePrice <= player.CashBalance && preferred(p))
            .Where(p => Enumerable.Range(state.DiceCount, state.DiceCount * state.DiceSides - state.DiceCount + 1)
                .Any(total => (player.BoardPosition + total) % state.BoardSize == p.BoardPosition))
            .Select(p => new { Property = p, Holdings = HoldingsInGroup(player, state, p) }).ToArray();
        if (opportunities.Length == 0)
        {
            return 0;
        }
        var strongest = opportunities.Max(p => p.Holdings);
        return opportunities.Where(p => p.Holdings == strongest).Min(p => p.Property.SalePrice);
    }

    private static int HoldingsInGroup(Player player, GameState state, PropertyState property) => state.Properties.Count(p =>
        player.OwnedPropertyIds.Contains(p.PropertyId) && p.Type == property.Type &&
        (property.Type != PropertyType.Street || property.ColorGroup is not null && p.ColorGroup == property.ColorGroup));
}
