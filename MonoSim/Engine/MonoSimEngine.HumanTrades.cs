using MonoSim.Models;
using MonoSim.Strategies;
using MonoSim.Trading;

namespace MonoSim;

public sealed partial class MonoSimEngine
{
    private StrategyAction? HumanTradeCandidate(string id, string otherId, GameState state, PropertyTrade reference, string actionId)
    {
        if (!TradeValidator.Validate(players[id], state, reference).IsValid)
        {
            return null;
        }
        PropertyTrade? opening = reference;
        if (market is IStrategyTradeNegotiator negotiator)
        {
            opening = MarketDecision(() => negotiator.CreateOffer(CopyPlayer(players[id]), state, CopyTrade(reference)));
        }
        var preferred = opening is not null;
        opening ??= reference;
        var terms = opening;
        var history = new List<PropertyTrade> { CopyTrade(opening) };
        preferred = preferred && MarketDecision(() => market.AcceptTrade(CopyPlayer(players[id]), state, CopyTrade(terms)));
        if (preferred)
        {
            var proposer = id;
            var recipient = otherId;
            var offered = terms;
            var agreed = false;
            for (var round = 0; round < 3; round++)
            {
                var received = ReverseTrade(proposer, offered);
                if (MarketDecision(() => market.AcceptTrade(CopyPlayer(players[recipient]), Snapshot(recipient, false), CopyTrade(received))))
                {
                    agreed = true;
                    terms = offered;
                    if (proposer != id)
                    {
                        terms = ReverseTrade(proposer, offered);
                    }
                    break;
                }
                if (market is not IStrategyTradeNegotiator bargaining)
                {
                    break;
                }
                var counter = MarketDecision(() => bargaining.CounterOffer(CopyPlayer(players[recipient]), Snapshot(recipient, false), CopyTrade(received)));
                if (counter is null)
                {
                    break;
                }
                offered = counter;
                (proposer, recipient) = (recipient, proposer);
                var view = offered;
                if (proposer != id)
                {
                    view = ReverseTrade(proposer, offered);
                }
                history.Add(CopyTrade(view));
            }
            preferred = agreed;
        }
        if (!preferred)
        {
            terms = opening;
            history = [CopyTrade(opening)];
        }
        var type = StrategyActionType.TradeProperties;
        string? propertyId = null;
        if (terms.PropertyIdsGiven.Length == 0 && terms.PropertyIdsReceived.Length == 1 && properties[terms.PropertyIdsReceived[0]].IsMortgaged)
        {
            type = StrategyActionType.BuyMortgagedProperty;
            propertyId = terms.PropertyIdsReceived[0];
        }
        return new()
        {
            Id = actionId, Type = type, Trade = terms, PropertyId = propertyId, Cost = terms.NetCost,
            IsStrategyPreferred = preferred, NegotiationHistory = history.ToArray(),
            CompletesColorGroup = InfrastructureStrategy.CompletesNewGroup(state, players[id].OwnedPropertyIds.ToHashSet(StringComparer.Ordinal),
                players[id].OwnedPropertyIds.Except(terms.PropertyIdsGiven).Concat(terms.PropertyIdsReceived).ToHashSet(StringComparer.Ordinal))
        };
    }

    private StrategyAction? ExecuteHumanTrade(string actor, StrategyAction action, GameState state)
    {
        var offered = TradeValidator.TermsForAction(players[actor], state, action)!;
        if (action.TradeOfferId is null && action.NegotiationHistory.Length > 0)
        {
            offered = CopyTrade(action.NegotiationHistory[0]);
        }
        var proposer = actor;
        var recipient = offered.OtherPlayerId;
        TradeOffer root;
        var explicitlyAccepted = false;
        if (action.TradeOfferId is { } offerId)
        {
            var existing = offers.SingleOrDefault(o => o.Id == offerId);
            if (existing is null || !acceptedOffers.Contains(offerId))
            {
                throw new InvalidOperationException("The offer has not been explicitly accepted.");
            }
            root = existing;
            proposer = existing.PlayerId;
            recipient = existing.Terms.OtherPlayerId;
            offered = CopyTrade(existing.Terms);
            explicitlyAccepted = true;
        }
        else
        {
            root = SubmitTradeOffer(actor, offered);
        }
        var actualHistory = new List<PropertyTrade>();
        var first = offered;
        if (proposer != actor)
        {
            first = ReverseTrade(proposer, offered);
        }
        actualHistory.Add(CopyTrade(first));
        for (var round = 0; round < 3; round++)
        {
            var received = ReverseTrade(proposer, offered);
            var recipientState = Snapshot(recipient, false);
            var expected = explicitlyAccepted || MarketDecision(() => market.AcceptTrade(CopyPlayer(players[recipient]), recipientState, CopyTrade(received)));
            var legal = TradeValidator.Validate(players[recipient], recipientState, received).IsValid;
            if (legal && Decide(recipient, $"accept-trade:{TradeKey(received)}", expected, true))
            {
                var agreed = offered;
                if (proposer != actor)
                {
                    agreed = ReverseTrade(proposer, offered);
                }
                agreed = agreed with { OriginalOfferId = root.Terms.OriginalOfferId ?? root.Id };
                actualHistory[^1] = CopyTrade(agreed);
                CommitTrade(actor, agreed, root);
                var actualType = StrategyActionType.TradeProperties;
                string? actualProperty = null;
                if (agreed.PropertyIdsGiven.Length == 0 && agreed.PropertyIdsReceived.Length == 1 &&
                    agreed.CardIdsGiven.Length + agreed.CardIdsReceived.Length == 0 && agreed.CashReceived == 0 &&
                    properties[agreed.PropertyIdsReceived[0]].IsMortgaged)
                {
                    actualType = StrategyActionType.BuyMortgagedProperty;
                    actualProperty = agreed.PropertyIdsReceived[0];
                }
                return action with { Trade = agreed, Cost = agreed.NetCost, Type = actualType, PropertyId = actualProperty, NegotiationHistory = actualHistory.ToArray() };
            }
            Log("TradeDeclined", recipient, $"Declined offer from {proposer}.");
            if (round == 2)
            {
                break;
            }
            PropertyTrade? counter = null;
            if (market is IStrategyTradeNegotiator bargaining)
            {
                counter = MarketDecision(() => bargaining.CounterOffer(CopyPlayer(players[recipient]), recipientState, CopyTrade(received)));
            }
            if (counter is not null && !TradeValidator.Validate(players[recipient], recipientState,
                counter with { OriginalOfferId = root.Terms.OriginalOfferId ?? root.Id }).IsValid)
            {
                counter = null;
            }
            var alternative = counter ?? UnexpectedCounter(recipient, recipientState, received);
            if (!Decide(recipient, $"counter-trade:{TradeKey(received)}", counter is not null, alternative is not null))
            {
                break;
            }
            counter ??= alternative;
            if (counter is null)
            {
                break;
            }
            counter = counter with { OriginalOfferId = root.Terms.OriginalOfferId ?? root.Id };
            SubmitTradeOffer(recipient, counter);
            var actorView = counter;
            if (recipient != actor)
            {
                actorView = ReverseTrade(recipient, counter);
            }
            actualHistory.Add(CopyTrade(actorView));
            offered = counter;
            (proposer, recipient) = (recipient, proposer);
            explicitlyAccepted = false;
        }
        rejectedTradePhases.Add(TradePhase(actor));
        var rootId = root.Terms.OriginalOfferId ?? root.Id;
        offers.RemoveAll(o => o.Id == rootId || o.Terms.OriginalOfferId == rootId);
        return null;
    }

    private static PropertyTrade? UnexpectedCounter(string playerId, GameState state, PropertyTrade received)
    {
        if (received.PropertyIdsGiven.Length + received.PropertyIdsReceived.Length == 0)
        {
            return null;
        }
        var net = received.CashPaid - received.CashReceived - 10;
        var counter = received with { CashPaid = Math.Max(0, net), CashReceived = Math.Max(0, -net) };
        var player = state.Players.Single(p => p.Id == playerId);
        if (TradeValidator.Validate(player, state, counter).IsValid)
        {
            return counter;
        }
        return null;
    }
}
