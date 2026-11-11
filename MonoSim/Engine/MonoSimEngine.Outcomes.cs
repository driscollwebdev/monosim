using MonoSim.Models;
using MonoSim.Trading;

namespace MonoSim;

public sealed partial class MonoSimEngine
{
    public decimal PortfolioValue(string playerId) => Portfolio(playerId).Total;

    private PlayerPortfolioValue Portfolio(string playerId)
    {
        var player = players[playerId];
        return new(playerId, player.CashBalance,
            player.OwnedPropertyIds.Sum(id => TradeValidator.PropertyValue(properties[id], definition.Money("buildingResaleFraction"))),
            player.HeldGetOutOfJailFreeCardIds.Length * 50);
    }

    private GameState CompleteGame(GameEndReason reason)
    {
        var remaining = order.Where(id => !players[id].IsBankrupt).ToArray();
        if (reason == GameEndReason.RoundLimit)
        {
            Log("RoundLimitReached", null, $"Completed {round} rounds ({turn} player turns); portfolio valuation decides the result.");
            var highest = remaining.Max(PortfolioValue);
            winners = remaining.Where(id => PortfolioValue(id) == highest).ToArray();
        }
        else
        {
            winners = [remaining.Single()];
        }
        endReason = reason;
        phase = TurnPhase.Completed;
        foreach (var id in order)
        {
            var value = Portfolio(id);
            Log("PortfolioValued", id, $"Cash {value.Cash}; properties {value.Properties}; cards {value.Cards}; total {value.Total}.", value.Total);
        }
        string? uniqueWinner = null;
        if (winners.Length == 1)
        {
            uniqueWinner = winners[0];
        }
        Log("GameCompleted", uniqueWinner, $"{reason}; winner(s): {string.Join(",", winners)}.");
        return Snapshot(null);
    }
}
