using MonoSim.Models;
using MonoSim.Trading;
using static MonoSim.Strategies.InfrastructureStrategy;

namespace MonoSim.Strategies;

/// <summary>
/// Targets the first acquired color group and its clockwise neighbor.
/// Create a fresh instance per simulation; target pairs persist separately per player.
/// </summary>
public sealed class AdjacentGroupStrategy : IPlayingStrategy, IStrategyGroupFocus
{
    private readonly IStrategyActionExecutor executor;
    private readonly string? fixedFirstGroup;
    private readonly Func<Player, GameState, decimal, HashSet<string>, HashSet<string>, bool>? reservePolicy;
    private readonly Dictionary<string, TargetPair> targets = [];

    public AdjacentGroupStrategy(IStrategyActionExecutor executor) : this(executor, null, null) { }

    internal AdjacentGroupStrategy(IStrategyActionExecutor executor, string? fixedFirstGroup,
        Func<Player, GameState, decimal, HashSet<string>, HashSet<string>, bool>? reservePolicy)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.fixedFirstGroup = fixedFirstGroup;
        this.reservePolicy = reservePolicy;
    }

    public Action PreRoll(Player player, GameState gameState) => Decide(player, gameState, preRoll: true);
    public Action PostRoll(Player player, GameState gameState) => Decide(player, gameState, preRoll: false);

    string[] IStrategyGroupFocus.TargetGroups(Player player, GameState state)
    {
        var pair = targets.GetValueOrDefault(player.Id);
        if (fixedFirstGroup is not null)
        {
            pair = PairFor(state, fixedFirstGroup);
        }
        if (pair is null)
        {
            pair = player.OwnedPropertyIds.Select(id => Find(state, id)).Where(p => p?.ColorGroup is not null)
                .Select(p => PairFor(state, p!.ColorGroup!)).FirstOrDefault();
        }
        if (pair is null)
        {
            return [];
        }
        return [pair.FirstGroup, pair.SecondGroup];
    }

    private Action Decide(Player player, GameState state, bool preRoll)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(state);
        var pair = fixedFirstGroup is not null ? PairFor(state, fixedFirstGroup) : targets.GetValueOrDefault(player.Id) ?? player.OwnedPropertyIds
            .Select(id => Find(state, id)).Where(p => p?.ColorGroup is not null)
            .Select(p => PairFor(state, p!.ColorGroup!)).FirstOrDefault();
        if (fixedFirstGroup is not null && pair is null)
        {
            throw new ArgumentException("The game state must include the configured target groups.", nameof(state));
        }

        var action = ExecuteActions(executor, player, state,
            (p, s) => SelectAction(p, s, pair, preRoll, reservePolicy),
            (executed, previous, updated) =>
            {
                if (pair is null && executed.Type == StrategyActionType.BuyProperty && Find(previous, executed.PropertyId)?.ColorGroup is { } group)
                {
                    pair = PairFor(previous, group);
                    if (pair is not null)
                    {
                        targets[player.Id] = pair;
                    }
                }
            });
        return () =>
        {
            if (pair is not null)
            {
                targets[player.Id] = pair;
            }

            action();
        };
    }

    private static StrategyAction? SelectAction(Player player, GameState state, TargetPair? pair, bool preRoll,
        Func<Player, GameState, decimal, HashSet<string>, HashSet<string>, bool>? reservePolicy)
    {
        if (TradeSafetyAdvisor.DebtDue(player, state) > 0)
        {
            return TradeSafetyAdvisor.FindDebtFundingTrade(player, state);
        }
        var owned = player.OwnedPropertyIds.ToHashSet(StringComparer.Ordinal);
        if (player.IsInJail)
        {
            return preRoll ? SelectJailRelease(player, state,
                cost => reservePolicy?.Invoke(player, state, cost, owned, owned) ??
                    PreservesReserve(player, state, cost, owned, owned, p => pair?.Includes(p) == true)) : null;
        }

        if (pair is null)
        {
            // Only a successful initial landing purchase commits the target pair.
            return state.AvailableActions.Where(a => a.Type == StrategyActionType.BuyProperty && a.Cost > 0 &&
                    Find(state, a.PropertyId) is { IsAvailable: true, ColorGroup: not null })
                .Where(a =>
                {
                    var candidatePair = PairFor(state, Find(state, a.PropertyId)!.ColorGroup!);
                    var after = new HashSet<string>(owned, StringComparer.Ordinal) { a.PropertyId! };
                    return candidatePair is not null && PreservesReserve(player, state, a.Cost, owned, after, candidatePair.Includes);
                })
                .OrderBy(a => a.Cost).ThenBy(a => a.Id, StringComparer.Ordinal).FirstOrDefault();
        }

        var streets = state.Properties.Where(pair.Includes).ToArray();
        var bothOwned = streets.All(p => owned.Contains(p.PropertyId));
        var complete = bothOwned && (state.Properties.Any(p => pair.ContainsInfrastructure(p, state.BoardSize) && owned.Contains(p.PropertyId)) ||
            streets.All(p => p.HotelCount == 1 && !p.IsMortgaged));

        return state.AvailableActions.Select(a => Evaluate(player, state, a, pair, owned, bothOwned, complete, reservePolicy))
            .Where(c => c is not null)
            .OrderBy(c => c!.Priority)
            .ThenBy(c => c!.OldestLeverageIndex)
            .ThenBy(c => c!.Action.Cost)
            .ThenBy(c => c!.Action.Id, StringComparer.Ordinal)
            .Select(c => c!.Action).FirstOrDefault();
    }

    private static Candidate? Evaluate(Player player, GameState state, StrategyAction action, TargetPair pair,
        HashSet<string> owned, bool bothOwned, bool complete,
        Func<Player, GameState, decimal, HashSet<string>, HashSet<string>, bool>? reservePolicy)
    {
        var after = new HashSet<string>(owned, StringComparer.Ordinal);
        var property = Find(state, action.PropertyId);
        var priority = -1;
        var age = int.MaxValue;
        bool IsProtected(PropertyState p) => pair.Includes(p) || pair.ContainsInfrastructure(p, state.BoardSize);

        switch (action.Type)
        {
            case StrategyActionType.BuyProperty:
            case StrategyActionType.BuyMortgagedProperty:
                if (action.Cost <= 0 || property is null || owned.Contains(property.PropertyId))
                {
                    return null;
                }

                if (action.Type == StrategyActionType.BuyProperty && !property.IsAvailable)
                {
                    return null;
                }

                if (action.Type == StrategyActionType.BuyMortgagedProperty && (!property.IsOwned || !property.IsMortgaged ||
                    !TradeValidator.ValidateAction(player, state, action).IsValid))
                {
                    return null;
                }

                after.Add(property.PropertyId);
                if (pair.Includes(property))
                {
                    priority = 0;
                }
                else if (pair.ContainsInfrastructure(property, state.BoardSize))
                {
                    priority = bothOwned ? 1 : -1;
                }
                else if ((complete || bothOwned && action.Type == StrategyActionType.BuyMortgagedProperty) && property.ColorGroup is not null)
                {
                    priority = CompletesNewGroup(state, owned, after) ? 0 : 2;
                }
                else if (!complete && action.Type == StrategyActionType.BuyProperty)
                {
                    priority = 4;
                }

                break;

            case StrategyActionType.TradeProperties:
                if (!ProjectTrade(player, state, action, owned, out after, IsProtected))
                {
                    return null;
                }

                if (complete && !KeepsCompletedGroups(state, owned, after))
                {
                    return null;
                }

                if (state.Properties.Any(p => pair.Includes(p) && after.Contains(p.PropertyId) && !owned.Contains(p.PropertyId)))
                {
                    priority = 0;
                }
                else if (bothOwned && state.Properties.Any(p => pair.ContainsInfrastructure(p, state.BoardSize) && after.Contains(p.PropertyId) && !owned.Contains(p.PropertyId)))
                {
                    priority = 1;
                }
                else if (!complete && action.Trade!.PropertyIdsGiven.Length > 0 && action.Trade.PropertyIdsReceived.Length == 0 && action.Cost <= 0)
                {
                    priority = 2;
                }
                else if (complete && KeepsCompletedGroups(state, owned, after) && ColorProgress(state, after) > ColorProgress(state, owned))
                {
                    priority = CompletesNewGroup(state, owned, after) ? 0 : 2;
                }

                if (!complete && action.Trade!.PropertyIdsGiven.Length > 0)
                {
                    age = action.Trade.PropertyIdsGiven.Select(id => Array.IndexOf(player.OwnedPropertyIds, id)).Min();
                }

                break;

            case StrategyActionType.UnmortgageProperty:
                if (action.Cost <= 0 || property is not { IsMortgaged: true } || !owned.Contains(property.PropertyId))
                {
                    return null;
                }

                if (pair.Includes(property))
                {
                    priority = 0;
                }
                else
                {
                    priority = complete ? 3 : -1;
                }
                break;

            case StrategyActionType.BuildHouse:
            case StrategyActionType.BuildHotel:
                if (!bothOwned || action.Cost <= 0 || property?.ColorGroup is null || !owned.Contains(property.PropertyId))
                {
                    return null;
                }

                if (state.Properties.Any(p => p.ColorGroup == property.ColorGroup && (!owned.Contains(p.PropertyId) || p.IsMortgaged)))
                {
                    return null;
                }

                if (pair.Includes(property))
                {
                    priority = 1;
                }
                else
                {
                    priority = complete ? 3 : -1;
                }
                break;
        }

        if (priority < 0 || !(reservePolicy?.Invoke(player, state, action.Cost, owned, after) ??
            PreservesReserve(player, state, action.Cost, owned, after, pair.Includes)))
        {
            return null;
        }

        return new Candidate(action, priority, age);
    }

    private static TargetPair? PairFor(GameState state, string firstGroup)
    {
        var groups = state.Properties.Where(p => p.ColorGroup is not null).GroupBy(p => p.ColorGroup!)
            .OrderBy(g => g.Min(p => p.BoardPosition)).ToArray();
        var first = Array.FindIndex(groups, g => g.Key == firstGroup);
        if (first < 0 || groups.Length < 2)
        {
            return null;
        }

        var next = (first + 1) % groups.Length;
        return new TargetPair(groups[first].Key, groups[next].Key,
            groups[first].Min(p => p.BoardPosition), groups[next].Max(p => p.BoardPosition));
    }

    private sealed record TargetPair(string FirstGroup, string SecondGroup, int StartPosition, int EndPosition)
    {
        public bool Includes(PropertyState property) => property.ColorGroup == FirstGroup || property.ColorGroup == SecondGroup;

        public bool ContainsInfrastructure(PropertyState property, int boardSize) => IsInfrastructure(property) &&
            (property.BoardPosition - StartPosition + boardSize) % boardSize <= (EndPosition - StartPosition + boardSize) % boardSize;
    }

    private sealed record Candidate(StrategyAction Action, int Priority, int OldestLeverageIndex);
}
