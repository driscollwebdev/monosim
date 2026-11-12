namespace MonoSim.Strategies;

public sealed record TradeNegotiationOptions
{
    public decimal MonopolyCompletionMultiple { get; init; } = 1.5m;
    public decimal ConservativeAskingMultiple { get; init; } = 2m;
    public decimal ConservativeMinimumMultiple { get; init; } = 1.5m;

    public void Validate()
    {
        if (MonopolyCompletionMultiple < 1 || ConservativeMinimumMultiple < 1 || ConservativeAskingMultiple < ConservativeMinimumMultiple)
        {
            throw new ArgumentException("Trade multiples must be at least one; the asking multiple cannot be below the minimum accepted multiple.");
        }
    }
}
