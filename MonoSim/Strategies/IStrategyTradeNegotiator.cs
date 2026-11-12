using MonoSim.Models;

namespace MonoSim.Strategies;

/// <summary>Pure offer planning. Counter terms use the counterparty's own perspective.</summary>
public interface IStrategyTradeNegotiator
{
    PropertyTrade? CreateOffer(Player player, GameState state, PropertyTrade referenceOffer);
    PropertyTrade? CounterOffer(Player player, GameState state, PropertyTrade offer);
}
