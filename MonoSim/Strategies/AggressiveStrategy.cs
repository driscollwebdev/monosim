using MonoSim.Models;
using MonoSim.Trading;

namespace MonoSim.Strategies;

/// <summary>Acquires properties aggressively, protecting later-lap consolidation opportunities when cash is limited.</summary>
public sealed class AggressiveStrategy(IStrategyActionExecutor executor) : IPlayingStrategy
{
    public decimal AdherenceAdjustment => AdherenceBonuses.Aggressive;
    private readonly IStrategyActionExecutor executor = executor
        ?? throw new ArgumentNullException(nameof(executor));

    public Action PreRoll(Player player, GameState gameState) => Decide(player, gameState, preRoll: true);

    public Action PostRoll(Player player, GameState gameState) => Decide(player, gameState, preRoll: false);

    private Action Decide(Player player, GameState gameState, bool preRoll)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(gameState);

        // Selecting an action has no side effects. The engine invokes the returned delegate.
        return () =>
        {
            var currentPlayer = player;
            var currentState = gameState;
            var next = StrategyDecisions.Choose(executor, currentPlayer, currentState, s => SelectAction(currentPlayer, s, preRoll));
            while (next is not null)
            {
                if (TradeValidator.IsTradeAction(next) && !TradeValidator.ValidateAction(currentPlayer, currentState, next).IsValid)
                {
                    throw new InvalidOperationException("The trade is no longer valid for the current state.");
                }
                var updated = executor.Execute(currentPlayer.Id, next);
                if (!updated.ActionExecuted)
                {
                    currentPlayer = updated.Player;
                    currentState = updated.GameState;
                    next = StrategyDecisions.Choose(executor, currentPlayer, currentState, s => SelectAction(currentPlayer, s, preRoll));
                    continue;
                }
                next = updated.ExecutedAction ?? next;
                if (updated.Player.Id != currentPlayer.Id ||
                    updated.Player.CashBalance != currentPlayer.CashBalance - next.Cost)
                {
                    throw new InvalidOperationException("The engine must return the acting player with the selected action's cost applied.");
                }

                if (next.Type == StrategyActionType.UseGetOutOfJailFreeCard && updated.Player.IsInJail)
                {
                    throw new InvalidOperationException("Using a jail-release card must release the player.");
                }

                if (next.Type == StrategyActionType.PayJailFine && updated.Player.IsInJail)
                {
                    throw new InvalidOperationException("Paying the jail fine must release the player.");
                }

                currentPlayer = updated.Player;
                currentState = updated.GameState;
                next = StrategyDecisions.Choose(executor, currentPlayer, currentState, s => SelectAction(currentPlayer, s, preRoll));
            }
            if (executor is IPlayerTurnController controller)
            {
                controller.DeclareActionsComplete(player.Id);
            }
        };
    }

    private static StrategyAction? SelectAction(Player player, GameState state, bool preRoll)
    {
        if (TradeSafetyAdvisor.DebtDue(player, state) > 0)
        {
            return TradeSafetyAdvisor.FindDebtFundingTrade(player, state);
        }
        if (player.IsInJail)
        {
            // Release before the next roll. Do not spend the release money after being jailed.
            if (!preRoll)
            {
                return null;
            }

            var card = state.AvailableActions
                .Where(a => a.Type == StrategyActionType.UseGetOutOfJailFreeCard && a.Cost == 0)
                .OrderBy(a => a.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            return card ?? state.AvailableActions
                .Where(a => a.Type == StrategyActionType.PayJailFine && a.Cost > 0 && a.Cost <= player.CashBalance)
                .OrderBy(a => a.Cost)
                .ThenBy(a => a.Id, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        return state.AvailableActions
            .Where(a => a.Cost <= player.CashBalance && IsInvestment(a.Type) &&
                (a.Cost > 0 || a.Type == StrategyActionType.TradeProperties) &&
                (!TradeValidator.IsTradeAction(a) || TradeValidator.ValidateAction(player, state, a).IsValid) &&
                TradeSafetyAdvisor.ShouldConsiderAction(player, state, a) &&
                PreservesPurchaseOpportunity(player, state, a) &&
                (a.Type != StrategyActionType.TradeProperties || HelpfulTrade(player, state, a.Trade!)))
            .OrderBy(Priority)
            .ThenByDescending(a => IsPurchase(a.Type) ? 0 : a.RentIncrease / a.Cost)
            .ThenBy(a => a.Cost)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool PreservesPurchaseOpportunity(Player player, GameState state, StrategyAction action)
    {
        if (player.CompletedLaps == 0 || action.Cost <= 0)
        {
            return true;
        }
        decimal reserve;
        if (action.Type == StrategyActionType.BuyProperty)
        {
            var property = state.Properties.FirstOrDefault(p => p.PropertyId == action.PropertyId);
            if (property is null)
            {
                return true;
            }
            reserve = AggressivePurchaseAdvisor.PurchaseReserve(player, state, property);
            if (!AggressivePurchaseAdvisor.CompletesAcquisition(player, state, [property.PropertyId]))
            {
                reserve = Math.Max(reserve, AggressivePurchaseAdvisor.DevelopmentReserve(player, state));
            }
        }
        else if (action.Type is StrategyActionType.BuyMortgagedProperty or StrategyActionType.TradeProperties)
        {
            reserve = AggressivePurchaseAdvisor.AcquisitionReserve(player, state);
        }
        else
        {
            reserve = AggressivePurchaseAdvisor.DevelopmentReserve(player, state);
        }
        return player.CashBalance - action.Cost >= reserve;
    }

    private static bool HelpfulTrade(Player player, GameState state, PropertyTrade trade)
    {
        var before = player.OwnedPropertyIds.ToHashSet(StringComparer.Ordinal);
        var after = before.Except(trade.PropertyIdsGiven).Concat(trade.PropertyIdsReceived).ToHashSet(StringComparer.Ordinal);
        return StrategicTradeAdvisor.MonopolyProgress(state, after) > StrategicTradeAdvisor.MonopolyProgress(state, before) ||
            state.Properties.Any(p => p.IsMortgaged && trade.PropertyIdsReceived.Contains(p.PropertyId)) ||
            StrategicTradeAdvisor.FundsInvestment(player, state, trade, _ => true);
    }

    private static bool IsPurchase(StrategyActionType type) =>
        type is StrategyActionType.BuyProperty or StrategyActionType.BuyMortgagedProperty or StrategyActionType.TradeProperties;

    private static bool IsInvestment(StrategyActionType type) =>
        IsPurchase(type) || type is StrategyActionType.UnmortgageProperty or
            StrategyActionType.BuildHouse or StrategyActionType.BuildHotel;

    private static int Priority(StrategyAction action) => action.Type switch
    {
        StrategyActionType.BuyProperty => 0,
        StrategyActionType.BuyMortgagedProperty => 1,
        StrategyActionType.TradeProperties => 1,
        _ => 2
    };
}
