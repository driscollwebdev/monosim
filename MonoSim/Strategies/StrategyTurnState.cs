using MonoSim.Models;

namespace MonoSim.Strategies;

/// <summary>The updated decision state after an engine action.</summary>
public sealed record StrategyTurnState(Player Player, GameState GameState);
