using MonoSim.Models;
using MonoSim.Strategies;
using MonoSim.Trading;

namespace MonoSim;

public sealed partial class MonoSimEngine
{
    private readonly HashSet<string> acceptedOffers = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedNegotiations = new(StringComparer.Ordinal);

    public StrategyTurnState AcceptTradeOffer(string playerId, string offerId)
    {
        var offer = offers.SingleOrDefault(o => o.Id == offerId);
        if (!playing || marketDecision || decisionPlayer is null || offer is null || offer.Terms.OtherPlayerId != playerId ||
            decisionPlayer != offer.PlayerId && decisionPlayer != playerId)
        {
            throw new InvalidOperationException("Only a current offer's recipient may accept within its two-party decision window.");
        }
        var actor = decisionPlayer;
        var terms = CopyTrade(offer.Terms);
        if (actor != offer.PlayerId)
        {
            terms = ReverseTrade(offer.PlayerId, terms);
        }
        acceptedOffers.Add(offerId);
        try
        {
            Execute(actor, new() { Id = $"accept-{offerId}", Type = StrategyActionType.TradeProperties, TradeOfferId = offerId,
                Trade = terms, Cost = terms.NetCost });
            return new(CopyPlayer(players[playerId]), Snapshot(playerId));
        }
        finally
        {
            acceptedOffers.Remove(offerId);
        }
    }

    public TradeOffer SubmitTradeOffer(string playerId, PropertyTrade trade)
    {
        if (!playing || marketDecision || decisionPlayer is null || !players.ContainsKey(playerId) || players[playerId].IsBankrupt)
        {
            throw new InvalidOperationException("Trade offers require an active decision window.");
        }
        if (declared && phase != TurnPhase.RollAndMovement)
        {
            throw new InvalidOperationException("Offers cannot be submitted after actions have been declared complete.");
        }
        if (strategies[playerId] is ITradeInitiationPolicy { InitiatesTrades: false } && trade.OriginalOfferId is null)
        {
            throw new InvalidOperationException("This strategy participates in trades but does not initiate offers.");
        }
        if (decisionPlayer != playerId && (trade.OriginalOfferId is null || trade.OtherPlayerId != decisionPlayer))
        {
            throw new InvalidOperationException("A non-acting player may only counter the acting player's offer.");
        }
        var id = $"offer-{turn}-{rollNumber}-{offers.Count}";
        var state = TradeValidator.RecordOffer(id, players[playerId], Snapshot(playerId, false), trade);
        var offer = state.TradeOffers[^1];
        offers.Add(offer);
        var name = "TradeOffered";
        if (trade.OriginalOfferId is not null)
        {
            name = "CounterOfferMade";
        }
        Log(name, playerId, $"{id} to {trade.OtherPlayerId}: {System.Text.Json.JsonSerializer.Serialize(trade)}");
        return offer with { Terms = CopyTrade(offer.Terms) };
    }

    private IEnumerable<StrategyAction> TradeActions(string id, GameState state)
    {
        if (strategies[id] is ITradeInitiationPolicy { InitiatesTrades: false })
        {
            return [];
        }
        var candidates = new List<StrategyAction>();
        foreach (var otherId in order.Where(p => p != id && !players[p].IsBankrupt))
        {
            foreach (var received in players[otherId].OwnedPropertyIds)
            {
                var price = TradeValidator.PropertyValue(properties[received], state.BuildingResaleFraction);
                Add([], [received], price, 0, $"buy-{otherId}-{received}");
                foreach (var given in players[id].OwnedPropertyIds)
                {
                    var difference = price - TradeValidator.PropertyValue(properties[given], state.BuildingResaleFraction);
                    Add([given], [received], Math.Max(0, difference), Math.Max(0, -difference), $"swap-{otherId}-{given}-{received}");
                }
            }
            foreach (var given in players[id].OwnedPropertyIds)
            {
                Add([given], [], 0, TradeValidator.PropertyValue(properties[given], state.BuildingResaleFraction), $"sell-{otherId}-{given}");
            }
            void Add(string[] given, string[] received, decimal paid, decimal receivedCash, string actionId)
            {
                var terms = new PropertyTrade
                {
                    OtherPlayerId = otherId, PropertyIdsGiven = given, PropertyIdsReceived = received,
                    CashPaid = paid, CashReceived = receivedCash,
                    BankFees = received.Where(p => properties[p].IsMortgaged).Sum(p => properties[p].MortgageValue * state.MortgageInterestRate),
                    OtherPlayerBankFees = given.Where(p => properties[p].IsMortgaged).Sum(p => properties[p].MortgageValue * state.MortgageInterestRate)
                };
                var history = new List<PropertyTrade>();
                if (market is IStrategyTradeNegotiator negotiator)
                {
                    var opening = MarketDecision(() => negotiator.CreateOffer(CopyPlayer(players[id]), state, CopyTrade(terms)));
                    if (opening is null || opening.OtherPlayerId != otherId)
                    {
                        return;
                    }
                    terms = opening;
                }
                if (!TradeValidator.Validate(players[id], state, terms).IsValid ||
                    !MarketDecision(() => market.AcceptTrade(CopyPlayer(players[id]), state, CopyTrade(terms))))
                {
                    return;
                }
                history.Add(CopyTrade(terms));
                var otherState = Snapshot(otherId, false);
                if (!MarketDecision(() => market.AcceptTrade(CopyPlayer(players[otherId]), otherState, ReverseTrade(id, terms))))
                {
                    TraceNegotiation("TradeProposalDeclined", otherId, id, terms);
                    if (market is not IStrategyTradeNegotiator bargaining)
                    {
                        return;
                    }
                    var counter = MarketDecision(() => bargaining.CounterOffer(CopyPlayer(players[otherId]), otherState, ReverseTrade(id, terms)));
                    if (counter is null)
                    {
                        return;
                    }
                    terms = ReverseTrade(otherId, counter);
                    history.Add(CopyTrade(terms));
                    TraceNegotiation("TradeCounterProposed", otherId, id, terms);
                    if (!MarketDecision(() => market.AcceptTrade(CopyPlayer(players[id]), state, CopyTrade(terms))))
                    {
                        TraceNegotiation("TradeCounterDeclined", id, otherId, terms);
                        var response = MarketDecision(() => bargaining.CounterOffer(CopyPlayer(players[id]), state, CopyTrade(terms)));
                        if (response is null || !MarketDecision(() => market.AcceptTrade(CopyPlayer(players[otherId]), otherState, ReverseTrade(id, response))))
                        {
                            return;
                        }
                        terms = response;
                        history.Add(CopyTrade(terms));
                    }
                }
                if (!TradeValidator.Validate(players[id], state, terms).IsValid)
                {
                    return;
                }
                var kind = StrategyActionType.TradeProperties;
                string? propertyId = null;
                if (given.Length == 0 && received.Length == 1 && properties[received[0]].IsMortgaged)
                {
                    kind = StrategyActionType.BuyMortgagedProperty;
                    propertyId = received[0];
                }
                candidates.Add(new()
                {
                    Id = actionId, Type = kind, Trade = terms, PropertyId = propertyId, Cost = terms.NetCost,
                    NegotiationHistory = history.Select(CopyTrade).ToArray(),
                    CompletesColorGroup = InfrastructureStrategy.CompletesNewGroup(state, players[id].OwnedPropertyIds.ToHashSet(StringComparer.Ordinal),
                        players[id].OwnedPropertyIds.Except(given).Concat(received).ToHashSet(StringComparer.Ordinal))
                });
            }
        }
        return candidates;
    }

    private void TraceNegotiation(string name, string actor, string other, PropertyTrade terms)
    {
        if (terms.OtherPlayerId == actor)
        {
            terms = ReverseTrade(other, terms);
        }
        var description = $"{other}: {System.Text.Json.JsonSerializer.Serialize(terms)}";
        if (loggedNegotiations.Add($"{turn}:{rollNumber}:{actor}:{name}:{description}"))
        {
            Log(name, actor, description);
        }
    }

    private void ExecuteTrade(string id, StrategyAction action, GameState state)
    {
        var validation = TradeValidator.ValidateAction(players[id], state, action);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(" ", validation.Errors));
        }
        var trade = TradeValidator.TermsForAction(players[id], state, action)!;
        var other = trade.OtherPlayerId;
        TradeOffer? submitted = null;
        if (action.TradeOfferId is { } offerId)
        {
            submitted = offers.SingleOrDefault(o => o.Id == offerId);
            if (submitted is null || !acceptedOffers.Contains(offerId))
            {
                throw new InvalidOperationException("This offer has not been explicitly accepted.");
            }
        }
        else if (!MarketDecision(() => market.AcceptTrade(CopyPlayer(players[other]), Snapshot(other, false), ReverseTrade(id, trade))))
        {
            Log("TradeDeclined", other, $"Declined offer from {id}.");
            throw new InvalidOperationException("The counterparty has not accepted the exchange.");
        }
        if (submitted is null && action.NegotiationHistory.Length > 1)
        {
            var opening = action.NegotiationHistory[0];
            if (opening.OtherPlayerId != other || action.NegotiationHistory.Any(t => t.OtherPlayerId != other ||
                !opening.PropertyIdsGiven.Concat(opening.PropertyIdsReceived).Intersect(t.PropertyIdsGiven.Concat(t.PropertyIdsReceived)).Any()) ||
                action.NegotiationHistory.Any(t => !TradeValidator.Validate(players[id], state, t).IsValid) ||
                System.Text.Json.JsonSerializer.Serialize(action.NegotiationHistory[^1]) != System.Text.Json.JsonSerializer.Serialize(trade))
            {
                throw new InvalidOperationException("Negotiation history must retain its original two parties and at least one original property.");
            }
            var root = SubmitTradeOffer(id, opening);
            for (var i = 1; i < action.NegotiationHistory.Length; i++)
            {
                var actor = id;
                var counter = action.NegotiationHistory[i];
                if (i % 2 == 1)
                {
                    actor = other;
                    counter = ReverseTrade(id, counter);
                }
                submitted = SubmitTradeOffer(actor, counter with { OriginalOfferId = root.Id });
            }
            trade = trade with { OriginalOfferId = root.Id };
        }
        submitted ??= SubmitTradeOffer(id, trade);
        var history = TradeValidator.RecordCompletedTrade(players[id], Snapshot(id, false), trade);
        // Validation has already checked both net balances and all current assets; commit the exchange atomically.
        var cash = trade.CashPaid - trade.CashReceived;
        if (cash > 0)
        {
            TransferCash(id, other, cash, "Trade cash settlement");
        }
        else if (cash < 0)
        {
            TransferCash(other, id, -cash, "Trade cash settlement");
        }
        TransferCash(id, null, trade.BankFees, "Trade mortgage-transfer interest");
        TransferCash(other, null, trade.OtherPlayerBankFees, "Trade mortgage-transfer interest");
        foreach (var property in trade.PropertyIdsGiven)
        {
            GiveProperty(id, other, property);
        }
        foreach (var property in trade.PropertyIdsReceived)
        {
            GiveProperty(other, id, property);
        }
        TransferCards(id, other, trade.CardIdsGiven);
        TransferCards(other, id, trade.CardIdsReceived);
        completedTrades.Clear();
        completedTrades.AddRange(history.CompletedTrades);
        Log("TradeCompleted", id, $"Two-party exchange with {other}; bank interest kept separate.");
        var rootId = submitted.Terms.OriginalOfferId ?? submitted.Id;
        offers.RemoveAll(o => o.Id == rootId || o.Terms.OriginalOfferId == rootId);
        Log("TradeNegotiationClosed", id, $"Offer {rootId} and its counters are closed after settlement.");
    }

    private static PropertyTrade ReverseTrade(string playerId, PropertyTrade trade) => trade with
    {
        OtherPlayerId = playerId, PropertyIdsGiven = [.. trade.PropertyIdsReceived], PropertyIdsReceived = [.. trade.PropertyIdsGiven],
        CardIdsGiven = [.. trade.CardIdsReceived], CardIdsReceived = [.. trade.CardIdsGiven],
        CashPaid = trade.CashReceived, CashReceived = trade.CashPaid, BankFees = trade.OtherPlayerBankFees, OtherPlayerBankFees = trade.BankFees
    };

    private void TransferCards(string from, string to, string[] cards)
    {
        foreach (var card in cards)
        {
            players[from] = players[from] with { HeldGetOutOfJailFreeCardIds = players[from].HeldGetOutOfJailFreeCardIds.Where(c => c != card).ToArray() };
            players[to] = players[to] with { HeldGetOutOfJailFreeCardIds = [.. players[to].HeldGetOutOfJailFreeCardIds, card] };
            Log("CardTransferred", to, $"{card}: {from} -> {to}");
        }
    }
}
