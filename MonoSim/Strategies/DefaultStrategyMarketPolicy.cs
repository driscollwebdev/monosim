using MonoSim.Models;
using MonoSim.Trading;

namespace MonoSim.Strategies;

/// <summary>Deterministic reserve-aware market decisions for the built-in strategy families.</summary>
public sealed class DefaultStrategyMarketPolicy(bool capBidsAtValuation = false,
    Func<Player, GameState, string[]?>? groupFocus = null,
    Func<Player, IStrategyMarketPolicy?>? playerPolicies = null,
    TradeNegotiationOptions? negotiationOptions = null) : IStrategyMarketPolicy, IStrategyTradeNegotiator, IStrategyAuctionPolicy
{
    private readonly Dictionary<string, string> focus = [];
    private readonly TradeNegotiationOptions pricing = Checked(negotiationOptions ?? new());

    private static TradeNegotiationOptions Checked(TradeNegotiationOptions options)
    {
        options.Validate();
        return options;
    }

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

    public decimal? NextBid(Player player, GameState state, AuctionState auction)
    {
        if (playerPolicies?.Invoke(player) is IStrategyAuctionPolicy specific)
        {
            return specific.NextBid(player, state, auction);
        }
        if (player.IsBankrupt || player.HasDeclaredBankruptcy || TradeSafetyAdvisor.DebtDue(player, state) > 0)
        {
            return null;
        }
        var fees = state.Properties.Where(p => auction.Bundle.PropertyIds.Contains(p.PropertyId) && p.IsMortgaged)
            .Sum(p => p.MortgageValue * state.MortgageInterestRate);
        var ceiling = MaximumBid(player, state, auction.Bundle, auction.ReferenceValue, fees);
        var doubleIncrement = AuctionOverbidder(player, state, auction.Bundle);
        if (!doubleIncrement)
        {
            ceiling = Math.Min(ceiling, auction.ReferenceValue);
            var incoming = state.Properties.Where(p => auction.Bundle.PropertyIds.Contains(p.PropertyId)).ToArray();
            var after = player.OwnedPropertyIds.Concat(auction.Bundle.PropertyIds).ToHashSet(StringComparer.Ordinal);
            var budget = decimal.Floor(Math.Max(0, player.CashBalance - fees - Reserve(player, state, after, incoming)));
            var proposed = auction.MinimumBid;
            var followUpMinimum = proposed + AuctionRules.MinimumIncrement(proposed);
            var canBluff = state.OtherPlayers.Any(other => other.Id != auction.SellerId && auction.ActiveBidderIds.Contains(other.Id) &&
                !other.IsBankrupt && !other.HasDeclaredBankruptcy && AuctionOverbidder(other, state, auction.Bundle) &&
                other.CashBalance - TradeSafetyAdvisor.DebtDue(other, state) - fees >= followUpMinimum);
            if (canBluff)
            {
                ceiling = Math.Max(ceiling, Math.Min(budget, auction.ReferenceValue));
            }
        }
        var increment = auction.MinimumIncrement;
        if (doubleIncrement)
        {
            increment *= 2;
        }
        var bid = Math.Min(ceiling, auction.CurrentOffer + increment);
        if (bid < auction.MinimumBid)
        {
            return null;
        }
        return bid;
    }

    private bool AuctionOverbidder(Player player, GameState state, AssetBundle bundle)
    {
        if (Kind(player) == "aggressive")
        {
            return true;
        }
        if (Kind(player) is not ("infrastructure" or "adjacent" or "bluegreen"))
        {
            return false;
        }
        return state.Properties.Any(p => bundle.PropertyIds.Contains(p.PropertyId) &&
            (IsPrimaryTarget(player, state, p) || TargetsOwned(player, state) && IsContained(state, p, TargetGroups(player, state))));
    }

    public bool AcceptTrade(Player player, GameState state, PropertyTrade trade)
    {
        var specific = playerPolicies?.Invoke(player);
        if (specific is not null)
        {
            return specific.AcceptTrade(player, state, trade);
        }
        var validity = TradeValidator.Validate(player, state, trade);
        if (!validity.IsValid)
        {
            return false;
        }
        var assessment = TradeSafetyAdvisor.Assess(player, state, trade);
        var given = state.Properties.Where(p => trade.PropertyIdsGiven.Contains(p.PropertyId)).ToArray();
        var incoming = state.Properties.Where(p => trade.PropertyIdsReceived.Contains(p.PropertyId)).ToArray();
        if (given.Any(p => Protects(player, state, p)) || assessment.ShouldAvoid || StrategicTradeAdvisor.AvoidsTransfer(player, state, trade))
        {
            return false;
        }
        if (TradeSafetyAdvisor.DebtDue(player, state) > player.CashBalance)
        {
            return assessment.FundsDueDebt;
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
        var conservative = ConservativeTrading(player, state);
        var givenValue = given.Sum(p => TradeValidator.PropertyValue(p, state.BuildingResaleFraction)) + trade.CardIdsGiven.Length * state.GetOutOfJailFreeCardTradeValue;
        var receivedValue = incoming.Sum(p => TradeValidator.PropertyValue(p, state.BuildingResaleFraction)) + trade.CardIdsReceived.Length * state.GetOutOfJailFreeCardTradeValue;
        var netCash = trade.CashPaid - trade.CashReceived;
        var completion = StrategicTradeAdvisor.CompletesMonopoly(state, owned, after);
        var premium = 1m;
        if (!conservative && completion)
        {
            premium = pricing.MonopolyCompletionMultiple;
        }
        if (netCash > 0 && netCash > Math.Max(0, receivedValue * premium - givenValue))
        {
            return false;
        }
        if (givenValue > receivedValue && conservative && -netCash < (givenValue - receivedValue) * pricing.ConservativeMinimumMultiple)
        {
            return false;
        }
        var progress = StrategicTradeAdvisor.MonopolyProgress(state, after) > StrategicTradeAdvisor.MonopolyProgress(state, owned);
        var targetGain = incoming.Any(p => IsPrimaryTarget(player, state, p));
        var soldThisTurn = state.CompletedTrades.Where(t => t.TurnNumber == state.TurnNumber)
            .SelectMany(t =>
            {
                if (t.PlayerId == player.Id)
                {
                    return t.Terms.PropertyIdsGiven;
                }
                if (t.Terms.OtherPlayerId == player.Id)
                {
                    return t.Terms.PropertyIdsReceived;
                }
                return [];
            }).ToHashSet(StringComparer.Ordinal);
        if (incoming.Any(p => soldThisTurn.Contains(p.PropertyId)))
        {
            return false;
        }
        var bargain = incoming.Length > 0 && incoming.All(p => p.IsMortgaged && !soldThisTurn.Contains(p.PropertyId)) &&
            given.Length == 0 && trade.CardIdsGiven.Length == 0 && netCash <= receivedValue;
        var funding = StrategicTradeAdvisor.FundsInvestment(player, state, trade, p => WantsInvestment(player, state, p));
        if (conservative)
        {
            return progress || bargain || funding;
        }
        if (Kind(player) is "infrastructure" or "adjacent" or "bluegreen")
        {
            return targetGain || funding || TargetsOwned(player, state) && (progress || bargain);
        }
        return progress || bargain || funding;
    }

    public PropertyTrade? CreateOffer(Player player, GameState state, PropertyTrade referenceOffer)
    {
        if (playerPolicies?.Invoke(player) is ITradeInitiationPolicy { InitiatesTrades: false })
        {
            return null;
        }
        var offer = referenceOffer;
        if (ConservativeTrading(player, state))
        {
            var before = player.OwnedPropertyIds.ToHashSet(StringComparer.Ordinal);
            var after = before.Except(offer.PropertyIdsGiven).Concat(offer.PropertyIdsReceived).ToHashSet(StringComparer.Ordinal);
            var mortgage = state.Properties.Any(p => p.IsMortgaged && offer.PropertyIdsReceived.Contains(p.PropertyId));
            var funding = StrategicTradeAdvisor.FundsInvestment(player, state, offer, p => WantsInvestment(player, state, p));
            if (TradeSafetyAdvisor.DebtDue(player, state) <= player.CashBalance &&
                (!mortgage && !funding && !StrategicTradeAdvisor.CompletesMonopoly(state, before, after) ||
                 state.CompletedTrades.Any(t => t.TurnNumber == state.TurnNumber && t.PlayerId == player.Id)))
            {
                return null;
            }
            if (offer.PropertyIdsGiven.Length > 0 && offer.PropertyIdsReceived.Length == 0 && TradeSafetyAdvisor.DebtDue(player, state) <= player.CashBalance)
            {
                var value = Assets(state, offer.PropertyIdsGiven) + offer.CardIdsGiven.Length * state.GetOutOfJailFreeCardTradeValue;
                offer = BalanceCash(offer, -decimal.Ceiling(value * pricing.ConservativeAskingMultiple));
            }
        }
        if (AcceptTrade(player, state, offer))
        {
            return offer;
        }
        return null;
    }

    public PropertyTrade? CounterOffer(Player player, GameState state, PropertyTrade offer)
    {
        if (playerPolicies?.Invoke(player) is not null || !TradeValidator.Validate(player, state, offer).IsValid)
        {
            return null;
        }
        var given = Assets(state, offer.PropertyIdsGiven) + offer.CardIdsGiven.Length * state.GetOutOfJailFreeCardTradeValue;
        var received = Assets(state, offer.PropertyIdsReceived) + offer.CardIdsReceived.Length * state.GetOutOfJailFreeCardTradeValue;
        PropertyTrade? counter = null;
        if (ConservativeTrading(player, state) && given > received)
        {
            var other = state.OtherPlayers.Single(p => p.Id == offer.OtherPlayerId);
            var ask = Math.Min(decimal.Ceiling((given - received) * pricing.ConservativeAskingMultiple), other.CashBalance - offer.OtherPlayerBankFees);
            if (ask > offer.CashReceived - offer.CashPaid)
            {
                counter = BalanceCash(offer, -ask);
            }
        }
        else if (!ConservativeTrading(player, state) && received > given)
        {
            var before = player.OwnedPropertyIds.ToHashSet(StringComparer.Ordinal);
            var after = before.Except(offer.PropertyIdsGiven).Concat(offer.PropertyIdsReceived).ToHashSet(StringComparer.Ordinal);
            var multiple = 1m;
            if (StrategicTradeAdvisor.CompletesMonopoly(state, before, after))
            {
                multiple = pricing.MonopolyCompletionMultiple;
            }
            var incoming = state.Properties.Where(p => offer.PropertyIdsReceived.Contains(p.PropertyId)).ToArray();
            var maximum = Math.Min(decimal.Floor(received * multiple - given), player.CashBalance - offer.BankFees - Reserve(player, state, after, incoming));
            counter = BalanceCash(offer, maximum);
        }
        if (counter is not null && counter != offer && AcceptTrade(player, state, counter))
        {
            return counter;
        }
        return null;
    }

    private static PropertyTrade BalanceCash(PropertyTrade trade, decimal netPaid) => trade with { CashPaid = Math.Max(0, netPaid), CashReceived = Math.Max(0, -netPaid) };
    private static decimal Assets(GameState state, string[] ids) => state.Properties.Where(p => ids.Contains(p.PropertyId)).Sum(p => TradeValidator.PropertyValue(p, state.BuildingResaleFraction));

    private bool ConservativeTrading(Player player, GameState state) => Kind(player) == "conservative" ||
        Kind(player) is "infrastructure" or "adjacent" or "bluegreen" && TargetsOwned(player, state);

    private bool IsPrimaryTarget(Player player, GameState state, PropertyState property)
    {
        if (Kind(player) == "infrastructure")
        {
            return InfrastructureStrategy.IsInfrastructure(property);
        }
        return Kind(player) is "adjacent" or "bluegreen" && TargetGroups(player, state).Contains(property.ColorGroup ?? "");
    }

    private bool TargetsOwned(Player player, GameState state)
    {
        var targets = state.Properties.Where(p => IsPrimaryTarget(player, state, p)).ToArray();
        return targets.Length > 0 && targets.All(p => player.OwnedPropertyIds.Contains(p.PropertyId));
    }

    private bool WantsInvestment(Player player, GameState state, PropertyState property) =>
        Kind(player) != "conservative" && ConservativeTrading(player, state) && property.Type == PropertyType.Street || Wants(player, state, property);

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
        if (kind == "aggressive")
        {
            if (incoming.Length > 0 && incoming.All(p => p.IsAvailable && state.OtherPlayers.All(other => !other.OwnedPropertyIds.Contains(p.PropertyId))))
            {
                if (AggressivePurchaseAdvisor.CompletesAcquisition(player, state, incoming.Select(p => p.PropertyId)))
                {
                    return 0;
                }
                return Math.Max(incoming.Min(p => AggressivePurchaseAdvisor.PurchaseReserve(player, state, p)),
                    AggressivePurchaseAdvisor.DevelopmentReserve(player, state));
            }
            return AggressivePurchaseAdvisor.AcquisitionReserve(player, state);
        }
        if (kind is "conservative" or "novice")
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
