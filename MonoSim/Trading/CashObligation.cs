namespace MonoSim.Trading;

/// <summary>A currently due non-rent cash debt; null payee identifies the bank.</summary>
public sealed record CashObligation(string PayerId, string? PayeeId, decimal Amount, string Description);
