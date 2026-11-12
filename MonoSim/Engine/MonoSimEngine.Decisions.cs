using System.Text.Json;
using MonoSim.Models;
using MonoSim.Strategies;
using MonoSim.Trading;

namespace MonoSim;

public sealed partial class MonoSimEngine
{
    private readonly AdherenceDecisions adherence;
    private long decisionRevision;
    private long auctionSequence;
    private readonly HashSet<string> rejectedTradePhases = new(StringComparer.Ordinal);

    public StrategyAction? ChooseOptionalAction(Player player, GameState state, Func<GameState, StrategyAction?> select)
    {
        var preferred = state with { AvailableActions = state.AvailableActions.Where(a => a.IsStrategyPreferred).ToArray() };
        if (!options.EnableHumanDecisions || Owed(player.Id) > 0)
        {
            return select(preferred);
        }
        var legal = state.AvailableActions.Where(a => a.Cost <= player.CashBalance && Family(a) is not null &&
            (!TradeValidator.IsTradeAction(a) || TradeValidator.ValidateAction(player, state, a).IsValid)).ToArray();
        if (legal.Length == 0)
        {
            return select(preferred);
        }
        var remaining = legal.Select(a => Family(a)!).Distinct().ToList();
        while (remaining.Count > 0)
        {
            var view = preferred with { AvailableActions = preferred.AvailableActions.Where(a => remaining.Contains(Family(a) ?? "")).ToArray() };
            var expected = select(view);
            var family = remaining[0];
            if (expected is not null && Family(expected) is { } selected)
            {
                family = selected;
            }
            var choosesAction = Decide(player.Id, $"optional:{family}", expected is not null, true);
            if (choosesAction)
            {
                if (expected is not null)
                {
                    return expected;
                }
                return legal.Where(a => Family(a) == family).OrderByDescending(a => a.IsStrategyPreferred)
                    .ThenByDescending(a => a.CompletesColorGroup).ThenBy(a => a.Cost).ThenBy(a => a.Id, StringComparer.Ordinal).First();
            }
            remaining.Remove(family);
        }
        return null;
    }

    private static string? Family(StrategyAction action) => action.Type switch
    {
        StrategyActionType.BuyProperty => "purchase",
        StrategyActionType.BuyMortgagedProperty => "mortgage-purchase",
        StrategyActionType.TradeProperties => "propose-trade",
        StrategyActionType.BuildHouse or StrategyActionType.BuildHotel => "improve",
        StrategyActionType.UnmortgageProperty => "redeem-mortgage",
        StrategyActionType.UseGetOutOfJailFreeCard or StrategyActionType.PayJailFine => "leave-jail",
        _ => null
    };

    private bool Decide(string playerId, string point, bool expected, bool hasAlternative)
    {
        if (!options.EnableHumanDecisions)
        {
            return expected;
        }
        var context = $"{turn}:{rollNumber}:{phase}:{decisionRevision}:{point}";
        return adherence.Decide(players[playerId], strategies[playerId].AdherenceAdjustment, context, expected, hasAlternative,
            mandatory: Owed(playerId) > 0);
    }

    private static string TradeKey(PropertyTrade trade) => JsonSerializer.Serialize(trade with
    {
        OriginalOfferId = null,
        PropertyIdsGiven = trade.PropertyIdsGiven.Order(StringComparer.Ordinal).ToArray(),
        PropertyIdsReceived = trade.PropertyIdsReceived.Order(StringComparer.Ordinal).ToArray(),
        CardIdsGiven = trade.CardIdsGiven.Order(StringComparer.Ordinal).ToArray(),
        CardIdsReceived = trade.CardIdsReceived.Order(StringComparer.Ordinal).ToArray()
    });

    private string TradePhase(string id) => $"{turn}:{rollNumber}:{phase}:{id}";
}
