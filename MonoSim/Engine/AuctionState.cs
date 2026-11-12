namespace MonoSim;

/// <summary>Public auction state; opponents' willingness ceilings are never exposed.</summary>
public sealed record AuctionState(string? SellerId, AssetBundle Bundle, decimal ReferenceValue,
    decimal LiquidValue, decimal OpeningAsk, decimal? CurrentBid, string? HighestBidderId, string[] ActiveBidderIds)
{
    public decimal CurrentOffer => CurrentBid ?? OpeningAsk;
    public decimal MinimumIncrement => AuctionRules.MinimumIncrement(CurrentOffer);
    public decimal MinimumBid => CurrentOffer + MinimumIncrement;
}
