namespace MonoSim.Strategies;

/// <summary>Explicit completion declarations made by a strategy after its chosen actions.</summary>
public interface IPlayerTurnController
{
    void DeclareActionsComplete(string playerId);
}
