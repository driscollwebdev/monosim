using MonoSim.Models;

namespace MonoSim.Strategies;

public interface IPlayerDecisionContext
{
    StrategyAction? ChooseOptionalAction(Player player, GameState state, Func<GameState, StrategyAction?> select);
}

public static class StrategyDecisions
{
    public static StrategyAction? Choose(IStrategyActionExecutor executor, Player player, GameState state, Func<GameState, StrategyAction?> select)
    {
        if (executor is IPlayerDecisionContext decisions)
        {
            return decisions.ChooseOptionalAction(player, state, select);
        }
        return select(state);
    }
}
