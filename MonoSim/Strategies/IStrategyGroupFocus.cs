using MonoSim.Models;

namespace MonoSim.Strategies;

internal interface IStrategyGroupFocus
{
    string[] TargetGroups(Player player, GameState state);
}
