using MonoSim.Models;

namespace MonoSim;

public static class AuctionRules
{
    public static decimal MinimumIncrement(decimal offer)
    {
        if (offer < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offer));
        }
        if (offer < 100)
        {
            return 10;
        }
        if (offer < 500)
        {
            return 25;
        }
        if (offer <= 1000)
        {
            return 50;
        }
        return 100;
    }

    /// <summary>Auction collateral includes mortgage principal even for already mortgaged titles.</summary>
    public static decimal LiquidValue(AssetBundle bundle, GameState state) =>
        bundle.PropertyIds.Sum(id => state.Properties.Single(p => p.PropertyId == id).MortgageValue) +
        bundle.CardIds.Length * state.GetOutOfJailFreeCardTradeValue;
}
