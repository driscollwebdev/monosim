using System.Text.Json.Serialization;
using MonoSim.Strategies;

namespace MonoSim.Trading;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TradeOffer(string Id, string PlayerId, PropertyTrade Terms);
