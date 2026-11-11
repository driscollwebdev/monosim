using System.Text.Json.Serialization;

namespace MonoSim.Strategies;

/// <summary>An exchange from the acting player's perspective, with explicit cash flows.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PropertyTrade
{
    public required string OtherPlayerId { get; init; }

    public string[] PropertyIdsGiven { get; init; } = [];

    public string[] PropertyIdsReceived { get; init; } = [];

    public string[] CardIdsGiven { get; init; } = [];

    public string[] CardIdsReceived { get; init; } = [];

    /// <summary>Root original offer ID, regardless of the number of intervening counteroffers.</summary>
    public string? OriginalOfferId { get; init; }

    public decimal CashPaid { get; init; }

    public decimal CashReceived { get; init; }

    /// <summary>Mandatory bank charges, including mortgage-transfer interest.</summary>
    public decimal BankFees { get; init; }

    public decimal OtherPlayerBankFees { get; init; }

    public decimal NetCost => CashPaid - CashReceived + BankFees;
}
