using MonoSim.Models;

namespace MonoSim.Strategies;

/// <summary>The updated decision state after an engine action.</summary>
public sealed record StrategyTurnState(Player Player, GameState GameState)
{
    public bool ActionExecuted { get; init; } = true;
    public StrategyAction? ExecutedAction { get; init; }
}
