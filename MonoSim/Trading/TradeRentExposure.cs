namespace MonoSim.Trading;

public sealed record TradeRentExposure(string PropertyId, string PropertyName, decimal Rent, int[] DangerousDiceTotals);
