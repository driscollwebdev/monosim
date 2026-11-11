namespace MonoSim.Strategies;

/// <summary>The engine operations needed to execute a strategy's decisions.</summary>
public interface IStrategyActionExecutor
{
    /// <summary>
    /// Validates and executes a current option, updating balances, ledgers, ownership,
    /// buildings, or held cards as appropriate. Returns fresh state and available options.
    /// Throws if the option can no longer be executed. A trade option represents an
    /// accepted seller offer; its cost includes any mandatory mortgage-transfer interest.
    /// TradeProperties exchanges all PropertyTrade assets and cash atomically, updating
    /// both players' ownership, balances, ledgers, and mortgage values. A conditional
    /// FollowUpTrade must become an available option after its prerequisite purchase.
    /// </summary>
    StrategyTurnState Execute(string playerId, StrategyAction action);
}
