using MonoSim.Models;

namespace MonoSim.Strategies;

/// <summary>Defines a player's actions before and after rolling the dice.</summary>
public interface IPlayingStrategy
{
    decimal AdherenceAdjustment => 0;
    /// <summary>Selects the player's pre-roll action using the current state.</summary>
    /// <param name="player">The player's own current state.</param>
    /// <param name="gameState">The current board and other players' states.</param>
    /// <returns>An action for the game engine to execute.</returns>
    Action PreRoll(Player player, GameState gameState);

    /// <summary>Selects the player's post-roll action using the current state.</summary>
    /// <param name="player">The player's own current state.</param>
    /// <param name="gameState">The current board and other players' states.</param>
    /// <returns>An action for the game engine to execute.</returns>
    Action PostRoll(Player player, GameState gameState);
}
