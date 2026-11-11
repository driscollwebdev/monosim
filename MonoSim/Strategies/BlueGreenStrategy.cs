using MonoSim.Models;
using static MonoSim.Strategies.InfrastructureStrategy;

namespace MonoSim.Strategies;

/// <summary>The fixed green/dark-blue variant of Adjacent Group Strategy.</summary>
public sealed class BlueGreenStrategy : IPlayingStrategy
{
    private readonly AdjacentGroupStrategy strategy;

    public BlueGreenStrategy(IStrategyActionExecutor executor)
    {
        strategy = new AdjacentGroupStrategy(executor, "green", PreservesTargetCash);
    }

    public Action PreRoll(Player player, GameState gameState) => strategy.PreRoll(player, gameState);

    public Action PostRoll(Player player, GameState gameState) => strategy.PostRoll(player, gameState);

    private static bool IsTarget(PropertyState property) => property.ColorGroup is "green" or "darkBlue";

    private static bool PreservesTargetCash(Player player, GameState state, decimal cost, HashSet<string> before, HashSet<string> after)
    {
        var targets = state.Properties.Where(IsTarget).ToArray();
        var cashAfter = player.CashBalance - cost;
        if (cashAfter < 0) return false;

        var onFinalSide = player.BoardPosition >= targets.Min(p => p.BoardPosition);
        var acquiresTarget = targets.Any(p => after.Contains(p.PropertyId) && !before.Contains(p.PropertyId));
        // The acquisition fund can be deployed on any target on this side, without group-order requirements.
        if (onFinalSide && acquiresTarget) return true;

        decimal Required(HashSet<string> owned)
        {
            var floor = !onFinalSide && targets.Any(p => !owned.Contains(p.PropertyId)) ? 400m : 0m;
            return Math.Max(floor, Reserve(player, state, owned, IsTarget));
        }

        var reserveAfter = Required(after);
        if (cashAfter >= reserveAfter) return true;
        // Forced charges may already have depleted cash; sales can still improve the shortfall.
        return cost <= 0 && Math.Max(0, reserveAfter - cashAfter) <= Math.Max(0, Required(before) - player.CashBalance);
    }
}
