using MonoSim.Models;

namespace MonoSim.Strategies;

/// <summary>Chooses a binding live bid from public auction information, or null to withdraw.</summary>
public interface IStrategyAuctionPolicy
{
    decimal? NextBid(Player player, GameState state, AuctionState auction);
}
