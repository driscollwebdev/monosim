using MonoSim.Models;
using MonoSim.Strategies;
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
        var liquidValue = AuctionRules.LiquidValue(bundle, Snapshot(null, false));
        var opening = decimal.Ceiling(liquidValue / 2);
        Log("AuctionOpeningAsk", seller, $"Half of bundle liquidation collateral {liquidValue}, rounded up to a whole dollar; reference value {valuation}.", opening);
        var ceilings = new Dictionary<string, decimal>();
        var bidders = order.Where(id => id != seller && !players[id].IsBankrupt && !players[id].HasDeclaredBankruptcy).ToArray();
        if (market is not IStrategyAuctionPolicy)
        {
            foreach (var bidder in bidders)
            {
                var maximum = MarketDecision(() => market.MaximumBid(CopyPlayer(players[bidder]), Snapshot(bidder, false), bundle with { PropertyIds = [.. bundle.PropertyIds], CardIds = [.. bundle.CardIds] }, valuation, fees));
                ceilings[bidder] = decimal.Floor(Math.Max(0, Math.Min(maximum, players[bidder].CashBalance - Owed(bidder) - fees)));
            }
        }
        string? winner = null;
        decimal price = 0;
        var active = bidders.ToHashSet(StringComparer.Ordinal);
        while (active.Count > 0)
        {
            var raised = false;
            foreach (var bidder in bidders.Where(id => active.Contains(id)))
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                if (bidder == winner)
                {
                    continue;
                }
                decimal? current = null;
                if (winner is not null)
                {
                    current = price;
                }
                var auction = new AuctionState(seller, bundle with { PropertyIds = [.. bundle.PropertyIds], CardIds = [.. bundle.CardIds] },
                    valuation, liquidValue, opening, current, winner, bidders.Where(active.Contains).ToArray());
                decimal? offered;
                if (market is IStrategyAuctionPolicy live)
                {
                    offered = MarketDecision(() => live.NextBid(CopyPlayer(players[bidder]), Snapshot(bidder, false), auction));
                }
                else
                {
                    offered = Math.Min(ceilings[bidder], auction.MinimumBid);
                }
                var budget = Math.Max(0, players[bidder].CashBalance - Owed(bidder) - fees);
                var bid = Math.Min(offered ?? 0, budget);
                if (offered is null || bid < auction.MinimumBid)
                {
                    active.Remove(bidder);
                    Log("AuctionPassed", bidder, "Withdrew; no affordable legal raise selected.");
                    continue;
                }
                winner = bidder;
                price = bid;
                raised = true;
                Log("AuctionBid", bidder, $"Binding bid; previous offer {auction.CurrentOffer}; minimum increment {auction.MinimumIncrement}; fees {fees} kept separate.", bid);
            }
            if (!raised || winner is not null && active.Count == 1)
            {
                break;
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
