using MonoSim.Models;
using MonoSim.Trading;

namespace MonoSim.Strategies;

/// <summary>Deterministic reserve-aware market decisions for the built-in strategy families.</summary>
public sealed class DefaultStrategyMarketPolicy(bool capBidsAtValuation = false,
    Func<Player, GameState, string[]?>? groupFocus = null,
    Func<Player, IStrategyMarketPolicy?>? playerPolicies = null) : IStrategyMarketPolicy
{
    private readonly Dictionary<string, string> focus = [];

    public decimal MaximumBid(Player player, GameState state, AssetBundle bundle, decimal valuation, decimal bankFees)
    {
        var specific = playerPolicies?.Invoke(player);
        if (specific is not null)
        {
            return specific.MaximumBid(player, state, bundle, valuation, bankFees);
        }
        if (player.IsBankrupt || player.HasDeclaredBankruptcy || TradeSafetyAdvisor.DebtDue(player, state) > 0)
        {
            return 0;
        }
        var incoming = state.Properties.Where(p => bundle.PropertyIds.Contains(p.PropertyId)).ToArray();
        if (incoming.Length == 0 && bundle.CardIds.Length == 0 || incoming.Length > 0 && !incoming.Any(p => Wants(player, state, p)))
        {
            return 0;
        }
        var after = player.OwnedPropertyIds.Concat(bundle.PropertyIds).ToHashSet(StringComparer.Ordinal);
        var available = Math.Max(0, player.CashBalance - bankFees - Reserve(player, state, after, incoming));
        if (capBidsAtValuation)
        {
            available = Math.Min(available, valuation);
        }
        return decimal.Floor(available);
    }

    public bool AcceptTrade(Player player, GameState state, PropertyTrade trade)
    {
        var specific = playerPolicies?.Invoke(player);
        if (specific is not null)
        {
            return specific.AcceptTrade(player, state, trade);
        }
        if (!TradeValidator.Validate(player, state, trade).IsValid)
        {
            return false;
        }
        var assessment = TradeSafetyAdvisor.Assess(player, state, trade);
        if (assessment.ShouldAvoid)
        {
            return false;
        }
        if (TradeSafetyAdvisor.DebtDue(player, state) > player.CashBalance)
        {
            return assessment.FundsDueDebt;
        }
        var given = state.Properties.Where(p => trade.PropertyIdsGiven.Contains(p.PropertyId)).ToArray();
        var incoming = state.Properties.Where(p => trade.PropertyIdsReceived.Contains(p.PropertyId)).ToArray();
        if (given.Any(p => Protects(player, state, p)))
        {
            return false;
        }
        var owned = player.OwnedPropertyIds.ToHashSet(StringComparer.Ordinal);
        var after = owned.Except(trade.PropertyIdsGiven).Concat(trade.PropertyIdsReceived).ToHashSet(StringComparer.Ordinal);
        if (!InfrastructureStrategy.KeepsCompletedGroups(state, owned, after))
        {
            return false;
        }
        if (player.CashBalance - trade.NetCost < Reserve(player, state, after, incoming) && trade.NetCost > 0)
        {
            return false;
        }
        if (Kind(player) == "aggressive")
        {
            return incoming.Length > given.Length || InfrastructureStrategy.ColorProgress(state, after) > InfrastructureStrategy.ColorProgress(state, owned);
        }
        return incoming.Any(p => Wants(player, state, p)) || incoming.Length == 0 && trade.NetCost < 0;
    }

    private bool Wants(Player player, GameState state, PropertyState property)
    {
        var kind = Kind(player);
        if (kind == "aggressive")
        {
            return true;
        }
        var targets = TargetGroups(player, state);
        if (kind == "infrastructure")
        {
            var infrastructure = state.Properties.Where(InfrastructureStrategy.IsInfrastructure).ToArray();
            return InfrastructureStrategy.IsInfrastructure(property) || infrastructure.All(p => player.OwnedPropertyIds.Contains(p.PropertyId));
        }
        if (property.ColorGroup is not null && targets.Contains(property.ColorGroup))
        {
            return true;
        }
        if (targets.Length == 0)
        {
            return kind != "conservative" && property.Type == PropertyType.Street;
        }
        var streets = state.Properties.Where(p => p.ColorGroup is not null && targets.Contains(p.ColorGroup)).ToArray();
        var ownsTargets = streets.All(p => player.OwnedPropertyIds.Contains(p.PropertyId));
        if (kind == "conservative")
        {
            return ownsTargets && streets.All(p => p.HotelCount == 1) && property.Type == PropertyType.Street;
        }
        if (ownsTargets && IsContained(state, property, targets))
        {
            return true;
        }
        return ownsTargets && (streets.All(p => p.HotelCount == 1) ||
            state.Properties.Any(p => IsContained(state, p, targets) && player.OwnedPropertyIds.Contains(p.PropertyId)));
    }

    private bool Protects(Player player, GameState state, PropertyState property)
    {
        if (Kind(player) == "aggressive")
        {
            return false;
        }
        if (Kind(player) == "infrastructure")
        {
            return InfrastructureStrategy.IsInfrastructure(property);
        }
        var targets = TargetGroups(player, state);
        return targets.Contains(property.ColorGroup ?? "") || IsContained(state, property, targets);
    }

    private decimal Reserve(Player player, GameState state, HashSet<string> after, PropertyState[] incoming)
    {
        var kind = Kind(player);
        if (kind is "aggressive" or "conservative")
        {
            return 0;
        }
        var groups = TargetGroups(player, state);
        bool Target(PropertyState p) => groups.Contains(p.ColorGroup ?? "");
        if (kind == "infrastructure")
        {
            return InfrastructureStrategy.Reserve(player, state, after);
        }
        var reserve = InfrastructureStrategy.Reserve(player, state, after, Target);
        if (kind == "bluegreen")
        {
            var targets = state.Properties.Where(Target).ToArray();
            var onSide = player.BoardPosition >= targets.Min(p => p.BoardPosition);
            if (onSide && incoming.Any(Target))
            {
                return 0;
            }
            if (!onSide && targets.Any(p => !after.Contains(p.PropertyId)))
            {
                reserve = Math.Max(400, reserve);
            }
        }
        return reserve;
    }

    private string[] TargetGroups(Player player, GameState state)
    {
        var chosen = groupFocus?.Invoke(player, state);
        if (chosen is not null)
        {
            return chosen;
        }
        var kind = Kind(player);
        if (kind == "bluegreen")
        {
            return ["green", "darkBlue"];
        }
        if (!focus.TryGetValue(player.Id, out var first))
        {
            first = player.OwnedPropertyIds.Select(id => state.Properties.First(p => p.PropertyId == id).ColorGroup).FirstOrDefault(g => g is not null);
            if (first is null)
            {
                return [];
            }
            focus[player.Id] = first;
        }
        if (kind == "conservative")
        {
            return [first];
        }
        var groups = state.Properties.Where(p => p.ColorGroup is not null).GroupBy(p => p.ColorGroup!).OrderBy(g => g.Min(p => p.BoardPosition)).Select(g => g.Key).ToArray();
        var index = Array.IndexOf(groups, first);
        return [first, groups[(index + 1) % groups.Length]];
    }

    private static bool IsContained(GameState state, PropertyState property, string[] groups)
    {
        if (groups.Length != 2 || !InfrastructureStrategy.IsInfrastructure(property))
        {
            return false;
        }
        var start = state.Properties.Where(p => p.ColorGroup == groups[0]).Min(p => p.BoardPosition);
        var end = state.Properties.Where(p => p.ColorGroup == groups[1]).Max(p => p.BoardPosition);
        return (property.BoardPosition - start + state.BoardSize) % state.BoardSize <= (end - start + state.BoardSize) % state.BoardSize;
    }

    internal static string Kind(Player player) => new string(player.Strategy.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray())
        .Replace("strategy", "").Replace("group", "");
}
