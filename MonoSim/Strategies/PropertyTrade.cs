namespace MonoSim.Strategies;

/// <summary>An exchange from the acting player's perspective, with explicit cash flows.</summary>
public sealed record PropertyTrade
{
    public required string OtherPlayerId { get; init; }

    public string[] PropertyIdsGiven { get; init; } = [];

    public string[] PropertyIdsReceived { get; init; } = [];

    public decimal CashPaid { get; init; }

    public decimal CashReceived { get; init; }

    /// <summary>Mandatory bank charges, including mortgage-transfer interest.</summary>
    public decimal BankFees { get; init; }

    public decimal NetCost => CashPaid - CashReceived + BankFees;
}
