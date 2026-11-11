using MonoSim.Models;

namespace MonoSim.Strategies;

/// <summary>Collects infrastructure first, protecting cash for the next available infrastructure space.</summary>
public sealed class InfrastructureStrategy(IStrategyActionExecutor executor) : IPlayingStrategy
{
    private readonly IStrategyActionExecutor executor = executor
        ?? throw new ArgumentNullException(nameof(executor));

    public Action PreRoll(Player player, GameState gameState) => Decide(player, gameState, preRoll: true);

    public Action PostRoll(Player player, GameState gameState) => Decide(player, gameState, preRoll: false);

    private Action Decide(Player player, GameState state, bool preRoll)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(state);
        ValidateBoard(player, state);
        var first = SelectAction(player, state, preRoll);
        return () =>
        {
            var currentPlayer = player;
            var next = first;
            while (next is not null)
            {
                var updated = executor.Execute(currentPlayer.Id, next);
                if (updated.Player.Id != player.Id || updated.Player.CashBalance != currentPlayer.CashBalance - next.Cost ||
                    updated.GameState.TurnNumber != state.TurnNumber)
                    throw new InvalidOperationException("The engine must return the acting player with the action cost applied in the same turn.");

                if (next.Type is StrategyActionType.BuyProperty or StrategyActionType.BuyMortgagedProperty &&
                    !updated.Player.OwnedPropertyIds.Contains(next.PropertyId!))
                    throw new InvalidOperationException("The engine must transfer the purchased property.");
                if (next.Trade is { } trade &&
                    (!trade.PropertyIdsReceived.All(updated.Player.OwnedPropertyIds.Contains) ||
                     trade.PropertyIdsGiven.Any(updated.Player.OwnedPropertyIds.Contains)))
                    throw new InvalidOperationException("The engine must transfer all traded properties.");
                if (next.Type == StrategyActionType.UnmortgageProperty &&
                    Find(updated.GameState, next.PropertyId) is not { IsMortgaged: false })
                    throw new InvalidOperationException("The engine must redeem the mortgage.");
                if (next.Type is StrategyActionType.UseGetOutOfJailFreeCard or StrategyActionType.PayJailFine && updated.Player.IsInJail)
                    throw new InvalidOperationException("The engine must release the player from jail.");

                ValidateBoard(updated.Player, updated.GameState);
                currentPlayer = updated.Player;
                next = SelectAction(currentPlayer, updated.GameState, preRoll);
            }
        };
    }

    private static StrategyAction? SelectAction(Player player, GameState state, bool preRoll)
    {
        var owned = player.OwnedPropertyIds.ToHashSet(StringComparer.Ordinal);
        if (player.IsInJail)
        {
            if (!preRoll) return null;
            // Ahead of Jail means before the next GO crossing, not a property on the next circuit.
            if (!state.Properties.Any(p => IsInfrastructure(p) && p.IsAvailable && !owned.Contains(p.PropertyId) &&
                p.BoardPosition > player.BoardPosition)) return null;

            return state.AvailableActions
                .Where(a => a.Type == StrategyActionType.UseGetOutOfJailFreeCard && a.Cost == 0 ||
                    a.Type == StrategyActionType.PayJailFine && a.Cost > 0 && PreservesReserve(player, state, a.Cost, owned, owned))
                .OrderBy(a => a.Type == StrategyActionType.UseGetOutOfJailFreeCard ? 0 : 1)
                .ThenBy(a => a.Cost)
                .ThenBy(a => a.Id, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        var infrastructure = state.Properties.Where(IsInfrastructure).ToArray();
        var complete = infrastructure.Any(p => p.Type == PropertyType.Railroad) &&
            infrastructure.Any(p => p.Type == PropertyType.Utility) && infrastructure.All(p => owned.Contains(p.PropertyId));

        return state.AvailableActions
            .Select(action => Evaluate(player, state, action, owned, complete))
            .Where(candidate => candidate is not null)
            .OrderBy(candidate => candidate!.Priority)
            .ThenBy(candidate => candidate!.OldestLeverageIndex)
            .ThenByDescending(candidate => candidate!.InfrastructureGained)
            .ThenByDescending(candidate => candidate!.Action.Type is StrategyActionType.BuildHouse or StrategyActionType.BuildHotel
                ? candidate.Action.RentIncrease / candidate.Action.Cost : 0)
            .ThenBy(candidate => candidate!.Action.Cost)
            .ThenBy(candidate => candidate!.Action.Id, StringComparer.Ordinal)
            .Select(candidate => candidate!.Action)
            .FirstOrDefault();
    }

    private static Candidate? Evaluate(Player player, GameState state, StrategyAction action, HashSet<string> owned, bool complete)
    {
        var after = new HashSet<string>(owned, StringComparer.Ordinal);
        var property = Find(state, action.PropertyId);
        var priority = -1;
        var age = int.MaxValue;
        var gained = 0;

        switch (action.Type)
        {
            case StrategyActionType.BuyProperty:
            case StrategyActionType.BuyMortgagedProperty:
                if (action.Cost <= 0 || property is null || owned.Contains(property.PropertyId)) return null;
                if (action.Type == StrategyActionType.BuyProperty && !property.IsAvailable) return null;
                if (action.Type == StrategyActionType.BuyMortgagedProperty && (!property.IsOwned || !property.IsMortgaged ||
                    !state.OtherPlayers.Any(p => p.OwnedPropertyIds.Contains(property.PropertyId)))) return null;
                after.Add(property.PropertyId);
                if (IsInfrastructure(property)) { priority = 0; gained = 1; }
                else if (property.ColorGroup is not null && (complete || action.Type == StrategyActionType.BuyProperty))
                    priority = complete ? (CompletesNewGroup(state, owned, after) ? 1 : 2) : 4;
                break;

            case StrategyActionType.TradeProperties:
                if (!ProjectTrade(player, state, action, owned, out after)) return null;
                gained = state.Properties.Count(p => IsInfrastructure(p) && after.Contains(p.PropertyId) && !owned.Contains(p.PropertyId));
                if (gained > 0) priority = 1;
                else if (!complete && action.Trade!.PropertyIdsGiven.Length > 0 &&
                    action.Trade.PropertyIdsReceived.Length == 0 && action.Cost <= 0)
                    priority = 2; // Sell leverage promptly when an accepted cash offer exists.
                else if (complete && KeepsCompletedGroups(state, owned, after) && ColorProgress(state, after) > ColorProgress(state, owned))
                    priority = CompletesNewGroup(state, owned, after) ? 1 : 2;
                if (!complete && action.Trade!.PropertyIdsGiven.Length > 0)
                    age = action.Trade.PropertyIdsGiven.Select(id => Array.IndexOf(player.OwnedPropertyIds, id)).Min();
                break;

            case StrategyActionType.UnmortgageProperty:
                if (action.Cost <= 0 || property is not { IsMortgaged: true } || !owned.Contains(property.PropertyId)) return null;
                priority = IsInfrastructure(property) ? 3 : complete ? 3 : -1;
                break;

            case StrategyActionType.BuildHouse:
            case StrategyActionType.BuildHotel:
                if (!complete || action.Cost <= 0 || property?.ColorGroup is null || !owned.Contains(property.PropertyId)) return null;
                var group = state.Properties.Where(p => p.ColorGroup == property.ColorGroup).ToArray();
                if (group.Any(p => !owned.Contains(p.PropertyId) || p.IsMortgaged)) return null;
                priority = 4;
                break;
        }

        if (priority < 0 || !PreservesReserve(player, state, action.Cost, owned, after)) return null;
        return new Candidate(action, priority, age, gained);
    }

    private static bool ProjectTrade(Player player, GameState state, StrategyAction action, HashSet<string> owned, out HashSet<string> after)
    {
        after = new HashSet<string>(owned, StringComparer.Ordinal);
        var trade = action.Trade;
        if (trade is null || action.Cost != trade.NetCost || trade.CashPaid < 0 || trade.CashReceived < 0 || trade.BankFees < 0 ||
            trade.PropertyIdsGiven.Length + trade.PropertyIdsReceived.Length == 0 ||
            trade.PropertyIdsGiven.Distinct().Count() != trade.PropertyIdsGiven.Length ||
            trade.PropertyIdsReceived.Distinct().Count() != trade.PropertyIdsReceived.Length ||
            trade.PropertyIdsGiven.Intersect(trade.PropertyIdsReceived).Any()) return false;
        var other = state.OtherPlayers.FirstOrDefault(p => p.Id == trade.OtherPlayerId && p.Id != player.Id);
        if (other is null || trade.PropertyIdsGiven.Any(id => !owned.Contains(id) || Find(state, id) is not { } p || IsInfrastructure(p)) ||
            trade.PropertyIdsReceived.Any(id => owned.Contains(id) || !other.OwnedPropertyIds.Contains(id) || Find(state, id) is null)) return false;
        after.ExceptWith(trade.PropertyIdsGiven);
        after.UnionWith(trade.PropertyIdsReceived);
        return true;
    }

    private static bool PreservesReserve(Player player, GameState state, decimal cost, HashSet<string> before, HashSet<string> after)
    {
        var cashAfter = player.CashBalance - cost;
        if (cashAfter < 0) return false;
        var reserveAfter = Reserve(player, state, after);
        if (cashAfter >= reserveAfter) return true;
        // If mandatory game costs already depleted the reserve, a cash-raising sale can still help restore it.
        return cost <= 0 && Math.Max(0, reserveAfter - cashAfter) <= Math.Max(0, Reserve(player, state, before) - player.CashBalance);
    }

    private static decimal Reserve(Player player, GameState state, HashSet<string> owned) => state.Properties
        .Where(p => IsInfrastructure(p) && p.IsAvailable && !owned.Contains(p.PropertyId))
        .OrderBy(p => ForwardDistance(player.BoardPosition, p.BoardPosition, state.BoardSize))
        .Select(p => p.SalePrice).FirstOrDefault();

    private static int ForwardDistance(int from, int to, int size)
    {
        var distance = (to - from + size) % size;
        return distance == 0 ? size : distance;
    }

    private static bool CompletesNewGroup(GameState state, HashSet<string> before, HashSet<string> after) =>
        ColorGroups(state).Any(g => !g.All(p => before.Contains(p.PropertyId)) && g.All(p => after.Contains(p.PropertyId)));

    private static bool KeepsCompletedGroups(GameState state, HashSet<string> before, HashSet<string> after) =>
        ColorGroups(state).Where(g => g.All(p => before.Contains(p.PropertyId))).All(g => g.All(p => after.Contains(p.PropertyId)));

    private static int ColorProgress(GameState state, HashSet<string> owned) =>
        ColorGroups(state).Sum(g => { var count = g.Count(p => owned.Contains(p.PropertyId)); return count * count; });

    private static IEnumerable<IGrouping<string?, PropertyState>> ColorGroups(GameState state) =>
        state.Properties.Where(p => p.ColorGroup is not null).GroupBy(p => p.ColorGroup);

    private static bool IsInfrastructure(PropertyState property) => property.Type is PropertyType.Railroad or PropertyType.Utility;
    private static PropertyState? Find(GameState state, string? id) => state.Properties.FirstOrDefault(p => p.PropertyId == id);

    private static void ValidateBoard(Player player, GameState state)
    {
        if (state.BoardSize <= 0 || player.BoardPosition < 0 || player.BoardPosition >= state.BoardSize ||
            state.Properties.Any(p => p.BoardPosition < 0 || p.BoardPosition >= state.BoardSize || p.SalePrice < 0))
            throw new ArgumentException("Board size, positions, and property prices must describe a valid board.");
    }

    private sealed record Candidate(StrategyAction Action, int Priority, int OldestLeverageIndex, int InfrastructureGained);
}
