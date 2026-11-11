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
    /// both players' ownership, held cards, balances, ledgers, and mortgage values.
    /// Validate offers and execution through TradeValidator, including BuyMortgagedProperty.
    /// Persist RecordCompletedTrade receipt history in the returned state and between
    /// phases for the entire full turn. Rent obligations remain separate cash debts.
    /// FollowUpTrade is only a plan; a fresh accepted offer is needed after purchase.
    /// </summary>
    StrategyTurnState Execute(string playerId, StrategyAction action);
}
