using MonoSim.Strategies;

namespace MonoSim.Trading;

public sealed record CompletedTrade(long TurnNumber, string PlayerId, PropertyTrade Terms);
