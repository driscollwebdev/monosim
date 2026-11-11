using MonoSim.Models;
using MonoSim.Trading;

namespace MonoSim.Strategies;

/// <summary>
/// Focuses on one color group and funds gradual development from positive turn cash flow.
/// Create a fresh instance for each simulation; decision memory is kept separately per player.
/// </summary>
public sealed class ConservativeStrategy(IStrategyActionExecutor executor) : IPlayingStrategy
{
    private readonly IStrategyActionExecutor executor = executor
        ?? throw new ArgumentNullException(nameof(executor));
    private readonly Dictionary<string, DecisionMemory> memory = [];

    public Action PreRoll(Player player, GameState gameState) => Decide(player, gameState);

    public Action PostRoll(Player player, GameState gameState) => Decide(player, gameState);

    private Action Decide(Player player, GameState state)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(state);

        var plan = memory.GetValueOrDefault(player.Id) ?? new DecisionMemory(null, state.TurnNumber, player.CashBalance, null);
        if (plan.TurnNumber != state.TurnNumber)
        {
            plan = plan with { TurnNumber = state.TurnNumber, OpeningCash = player.CashBalance };
        }

        // OwnedPropertyIds retain acquisition order, so an existing player's first group is stable.
        plan = plan with { TargetGroup = plan.TargetGroup ?? FirstOwnedGroup(player, state) };
        var first = SelectAction(player, state, plan);

        return () =>
        {
            var currentPlayer = player;
            var currentState = state;
            var next = first;
            memory[player.Id] = plan;
            while (next is not null)
            {
                if (TradeValidator.IsTradeAction(next) && !TradeValidator.ValidateAction(currentPlayer, currentState, next).IsValid)
                {
                    throw new InvalidOperationException("The trade is no longer valid for the current state.");
                }
                var updated = executor.Execute(currentPlayer.Id, next);
                if (updated.Player.Id != player.Id || updated.Player.CashBalance != currentPlayer.CashBalance - next.Cost ||
                    updated.GameState.TurnNumber != state.TurnNumber)
                {
                    throw new InvalidOperationException("The engine must return the acting player with the action cost applied in the same turn.");
                }

                if (next.Type == StrategyActionType.TradeProperties &&
                    !next.Trade!.PropertyIdsReceived.All(id => updated.Player.OwnedPropertyIds.Contains(id)))
                {
                    throw new InvalidOperationException("The engine must transfer the properties in the accepted trade.");
                }

                if (next.Type is StrategyActionType.BuyProperty or StrategyActionType.BuyMortgagedProperty &&
                    !updated.Player.OwnedPropertyIds.Contains(next.PropertyId!))
                {
                    throw new InvalidOperationException("The engine must transfer the purchased property to the player.");
                }

                if (next.Type == StrategyActionType.BuyProperty &&
                    (plan.TargetGroup is null || IsFullyDeveloped(currentPlayer, currentState, plan.TargetGroup)))
                {
                    plan = plan with { TargetGroup = FindProperty(currentState, next.PropertyId)?.ColorGroup };
                }

                if (next.Type is StrategyActionType.BuildHouse or StrategyActionType.BuildHotel)
                {
                    plan = plan with { LastUpgradeTurn = state.TurnNumber };
                }

                memory[player.Id] = plan;
                currentPlayer = updated.Player;
                currentState = updated.GameState;
                next = SelectAction(currentPlayer, currentState, plan);
            }
        };
    }

    private static StrategyAction? SelectAction(Player player, GameState state, DecisionMemory plan)
    {
        if (TradeSafetyAdvisor.DebtDue(player, state) > 0)
        {
            return TradeSafetyAdvisor.FindDebtFundingTrade(player, state);
        }
        // Voluntary release is never selected. The engine rolls doubles and enforces the third failed attempt.
        if (player.IsInJail)
        {
            return null;
        }

        var options = state.AvailableActions.Where(a => a.Cost <= player.CashBalance &&
            (!TradeValidator.IsTradeAction(a) || TradeValidator.ValidateAction(player, state, a).IsValid)).ToArray();
        var target = plan.TargetGroup;
        if (target is null)
        {
            return Ordered(options.Where(a => a.Type == StrategyActionType.BuyProperty && a.Cost > 0 &&
                FindProperty(state, a.PropertyId) is { IsAvailable: true, ColorGroup: not null })).FirstOrDefault();
        }

        var group = state.Properties.Where(p => p.ColorGroup == target).ToArray();
        if (group.Length == 0)
        {
            return null;
        }

        var owned = player.OwnedPropertyIds.ToHashSet(StringComparer.Ordinal);
        var complete = group.All(p => owned.Contains(p.PropertyId));
        if (IsFullyDeveloped(player, state, target))
        {
            return Ordered(options.Where(a => a.Type == StrategyActionType.BuyProperty && a.Cost > 0 &&
                FindProperty(state, a.PropertyId) is { IsAvailable: true, ColorGroup: not null } p && p.ColorGroup != target)).FirstOrDefault();
        }
        if (!complete)
        {
            var purchase = Ordered(options.Where(a => a.Cost > 0 && FindProperty(state, a.PropertyId)?.ColorGroup == target &&
                (a.Type == StrategyActionType.BuyProperty && FindProperty(state, a.PropertyId) is { IsAvailable: true } ||
                 a.Type == StrategyActionType.BuyMortgagedProperty && FindProperty(state, a.PropertyId) is { IsOwned: true, IsMortgaged: true } &&
                 !owned.Contains(a.PropertyId!) && state.OtherPlayers.Any(p => p.OwnedPropertyIds.Contains(a.PropertyId!))))).FirstOrDefault();
            if (purchase is not null)
            {
                return purchase;
            }

            var trade = Ordered(options.Where(a => a.Type == StrategyActionType.TradeProperties && a.Trade is not null &&
                a.Cost == a.Trade.NetCost && IsHelpfulTrade(player, state, target, a.Trade, owned, requireCompletion: false))).FirstOrDefault();
            if (trade is not null)
            {
                return trade;
            }

            return Ordered(options.Where(a => a.Type == StrategyActionType.BuyProperty && a.Cost > 0 &&
                IsTradeSetup(player, state, target, a))).FirstOrDefault();
        }

        var redemption = Ordered(options.Where(a => a.Type == StrategyActionType.UnmortgageProperty && a.Cost > 0 &&
            owned.Contains(a.PropertyId ?? "") && FindProperty(state, a.PropertyId) is { IsMortgaged: true } p &&
            p.ColorGroup == target && a.Cost <= player.CashBalance - plan.OpeningCash)).FirstOrDefault();
        if (redemption is not null)
        {
            return redemption;
        }

        if (plan.LastUpgradeTurn == state.TurnNumber || group.Any(p => p.IsMortgaged))
        {
            return null;
        }

        return options.Where(a => a.Type is StrategyActionType.BuildHouse or StrategyActionType.BuildHotel &&
                a.Cost > 0 && a.Cost <= player.CashBalance - plan.OpeningCash &&
                owned.Contains(a.PropertyId ?? "") && FindProperty(state, a.PropertyId)?.ColorGroup == target)
            .OrderBy(a => FindProperty(state, a.PropertyId)!.HouseCount)
            .ThenBy(a => a.Cost)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool IsTradeSetup(Player player, GameState state, string target, StrategyAction action)
    {
        var property = FindProperty(state, action.PropertyId);
        var trade = action.FollowUpTrade;
        if (property is not { IsAvailable: true, ColorGroup: not null } || property.ColorGroup == target || trade is null ||
            !trade.PropertyIdsGiven.Contains(property.PropertyId))
        {
            return false;
        }

        // Buy an unrelated property only when it is the last available member and a concrete deal uses it.
        if (state.Properties.Any(p => p.ColorGroup == property.ColorGroup && p.PropertyId != property.PropertyId && p.IsAvailable))
        {
            return false;
        }

        if (action.Cost + Math.Max(0, trade.NetCost) > player.CashBalance)
        {
            return false;
        }

        var afterPurchase = player.OwnedPropertyIds.Append(property.PropertyId).ToHashSet(StringComparer.Ordinal);
        return IsHelpfulTrade(player with { CashBalance = player.CashBalance - action.Cost }, state, target, trade, afterPurchase, requireCompletion: true);
    }

    private static bool IsHelpfulTrade(Player player, GameState state, string target, PropertyTrade trade,
        HashSet<string> owned, bool requireCompletion)
    {
        if (!TradeValidator.Validate(player with { OwnedPropertyIds = owned.ToArray() }, state, trade).IsValid)
        {
            return false;
        }
        if (TradeSafetyAdvisor.Assess(player with { OwnedPropertyIds = owned.ToArray() }, state, trade).ShouldAvoid)
        {
            return false;
        }
        var other = state.OtherPlayers.FirstOrDefault(p => p.Id == trade.OtherPlayerId && p.Id != player.Id);
        if (other is null || trade.CashPaid < 0 || trade.CashReceived < 0 || trade.BankFees < 0 ||
            trade.CashPaid + trade.BankFees > player.CashBalance || trade.PropertyIdsReceived.Length == 0 ||
            trade.PropertyIdsGiven.Distinct().Count() != trade.PropertyIdsGiven.Length ||
            trade.PropertyIdsReceived.Distinct().Count() != trade.PropertyIdsReceived.Length ||
            trade.PropertyIdsGiven.Intersect(trade.PropertyIdsReceived).Any())
        {
            return false;
        }

        if (trade.PropertyIdsGiven.Any(id => !owned.Contains(id) || FindProperty(state, id) is not { } p || p.ColorGroup == target) ||
            trade.PropertyIdsReceived.Any(id => !other.OwnedPropertyIds.Contains(id) || owned.Contains(id) || FindProperty(state, id)?.ColorGroup != target))
        {
            return false;
        }

        var after = owned.Except(trade.PropertyIdsGiven).Concat(trade.PropertyIdsReceived).ToHashSet(StringComparer.Ordinal);
        var targetIds = state.Properties.Where(p => p.ColorGroup == target).Select(p => p.PropertyId).ToArray();
        if (targetIds.Count(after.Contains) <= targetIds.Count(owned.Contains) ||
            requireCompletion && !targetIds.All(after.Contains))
        {
            return false;
        }

        if (!requireCompletion)
        {
            return true;
        }

        var otherBefore = other.OwnedPropertyIds.ToHashSet(StringComparer.Ordinal);
        var otherAfter = otherBefore.Except(trade.PropertyIdsReceived).Concat(trade.PropertyIdsGiven).ToHashSet(StringComparer.Ordinal);
        return state.Properties.Where(p => p.ColorGroup is not null && p.ColorGroup != target).GroupBy(p => p.ColorGroup)
            .Any(g => g.Any(p => otherBefore.Contains(p.PropertyId)) &&
                g.Count(p => otherAfter.Contains(p.PropertyId)) > g.Count(p => otherBefore.Contains(p.PropertyId)));
    }

    private static IOrderedEnumerable<StrategyAction> Ordered(IEnumerable<StrategyAction> actions) =>
        actions.OrderBy(a => a.Cost).ThenBy(a => a.Id, StringComparer.Ordinal);

    private static PropertyState? FindProperty(GameState state, string? id) => state.Properties.FirstOrDefault(p => p.PropertyId == id);

    private static string? FirstOwnedGroup(Player player, GameState state) => player.OwnedPropertyIds
        .Select(id => FindProperty(state, id)?.ColorGroup).FirstOrDefault(group => group is not null);

    private static bool IsFullyDeveloped(Player player, GameState state, string target)
    {
        var group = state.Properties.Where(p => p.ColorGroup == target).ToArray();
        return group.Length > 0 && group.All(p => player.OwnedPropertyIds.Contains(p.PropertyId) && !p.IsMortgaged && p.HotelCount == 1);
    }

    private sealed record DecisionMemory(string? TargetGroup, long TurnNumber, decimal OpeningCash, long? LastUpgradeTurn);
}
