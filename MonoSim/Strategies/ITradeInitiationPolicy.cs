namespace MonoSim.Strategies;

/// <summary>Distinguishes responding to an offer from initiating a negotiation.</summary>
public interface ITradeInitiationPolicy
{
    bool InitiatesTrades { get; }
}
