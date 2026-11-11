namespace MonoSim.Models;

/// <summary>A cash movement recorded in a player's financial ledger.</summary>
public sealed record Transaction(
    string Description,
    decimal Amount,
    decimal NewCashBalance);
