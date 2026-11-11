using MonoSim.Models;

namespace MonoSim.Strategies;

/// <summary>Bidding and acceptance decisions, kept outside the simulation engine.</summary>
public interface IStrategyMarketPolicy
{
    decimal MaximumBid(Player player, GameState state, AssetBundle bundle, decimal valuation, decimal bankFees);
    bool AcceptTrade(Player player, GameState state, PropertyTrade trade);
}
