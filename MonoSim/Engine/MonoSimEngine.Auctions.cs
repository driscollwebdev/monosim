using MonoSim.Models;
using MonoSim.Trading;

namespace MonoSim;

public sealed partial class MonoSimEngine
{
    private void Auction(string? seller, AssetBundle bundle)
    {
        if (bundle.PropertyIds.Length + bundle.CardIds.Length == 0 || bundle.PropertyIds.Distinct().Count() != bundle.PropertyIds.Length ||
            bundle.CardIds.Distinct().Count() != bundle.CardIds.Length || bundle.PropertyIds.Any(id => !properties.ContainsKey(id) || Owner(id) != seller || properties[id].HouseCount + properties[id].HotelCount > 0) ||
            bundle.CardIds.Any(id => seller is null || !players[seller].HeldGetOutOfJailFreeCardIds.Contains(id)))
        {
            throw new InvalidOperationException("An auction requires exclusively owned assets, with buildings already sold back to the bank.");
        }
        Log("AuctionStarted", seller, System.Text.Json.JsonSerializer.Serialize(bundle));
        var valuation = bundle.PropertyIds.Sum(p => TradeValidator.PropertyValue(properties[p], definition.Money("buildingResaleFraction"))) + bundle.CardIds.Length * 50;
        var fees = bundle.PropertyIds.Where(p => properties[p].IsMortgaged).Sum(p => properties[p].MortgageValue * definition.Money("mortgageInterestRate"));
        var ceilings = new Dictionary<string, decimal>();
        foreach (var bidder in order.Where(id => id != seller && !players[id].IsBankrupt))
        {
            var maximum = MarketDecision(() => market.MaximumBid(CopyPlayer(players[bidder]), Snapshot(bidder, false), bundle with { PropertyIds = [.. bundle.PropertyIds], CardIds = [.. bundle.CardIds] }, valuation, fees));
            ceilings[bidder] = decimal.Floor(Math.Max(0, Math.Min(maximum, players[bidder].CashBalance - Owed(bidder) - fees)));
        }
        string? winner = null;
        decimal price = 0;
        // Binding proxy bids clear at the runner-up's limit plus $1, capped by the winner's limit.
        var ranked = ceilings.Where(b => b.Value >= 1).OrderByDescending(b => b.Value).ThenBy(b => Array.IndexOf(order, b.Key)).ToArray();
        if (ranked.Length > 0)
        {
            winner = ranked[0].Key;
            price = 1;
            if (ranked.Length > 1)
            {
                price = Math.Min(ranked[0].Value, ranked[1].Value + 1);
            }
            foreach (var bidder in ranked)
            {
                Log("AuctionBid", bidder.Key, $"Maximum committed bid {bidder.Value}; current cash and fees verified.", bidder.Value);
            }
        }
        if (winner is null)
        {
            Log("AuctionNoBids", seller, "Unsold assets revert to the bank for zero.", 0);
            foreach (var property in bundle.PropertyIds)
            {
                GiveProperty(seller, null, property);
            }
            foreach (var card in bundle.CardIds)
            {
                ReturnCard(seller!, card);
            }
            return;
        }
        Log("AuctionPriceStruck", seller, $"Irrevocable sale to {winner} for {price}.", price);
        TransferCash(winner, seller, price, "Binding auction sale");
        TransferCash(winner, null, fees, "Auction mortgage-transfer interest");
        foreach (var property in bundle.PropertyIds)
        {
            GiveProperty(seller, winner, property);
        }
        if (seller is not null)
        {
            TransferCards(seller, winner, bundle.CardIds);
        }
        Log("AuctionCompleted", winner, $"Bundle purchased from {seller ?? "bank"}.", price);
    }
}
