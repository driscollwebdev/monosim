using MonoSim.Models;

namespace MonoSim.Strategies;

/// <summary>Bidding and acceptance decisions, kept outside the simulation engine.</summary>
public interface IStrategyMarketPolicy
{
    /// <summary>Private acquisition-interest ceiling; live policies can separately choose bounded bluff bids.</summary>
    decimal MaximumBid(Player player, GameState state, AssetBundle bundle, decimal valuation, decimal bankFees);
    bool AcceptTrade(Player player, GameState state, PropertyTrade trade);
}
