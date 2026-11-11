namespace MonoSim.Trading;

public sealed record TradeValidationResult(string[] Errors, decimal ValueGiven, decimal ValueReceived)
{
    public bool IsValid => Errors.Length == 0;
}
