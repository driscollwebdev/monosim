namespace MonoSim.Trading;

public sealed record TradeSafetyAssessment(decimal PostTradeCash, decimal PostTradeAssetValue,
    TradeRentExposure[] Exposures, bool FundsDueDebt)
{
    public decimal PostTradeTotalValue => PostTradeCash + PostTradeAssetValue;
    public bool HasImminentJeopardy => Exposures.Any(e => e.DangerousDiceTotals.Length > 0);
    public bool ShouldAvoid => HasImminentJeopardy && !FundsDueDebt;
}
