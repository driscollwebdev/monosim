namespace MonoSim.Models;

/// <summary>A snapshot of a player's current state in one simulation.</summary>
public sealed record Player
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Identifies the strategy used to make this player's decisions.</summary>
    public required string Strategy { get; init; }

    /// <summary>Base likelihood of following the strategy, before its fixed adjustment.</summary>
    public decimal StrategyAdherence { get; init; } = 0.85m;

    public required decimal CashBalance { get; init; }

    /// <summary>
    /// Sum of the title-deed mortgage values of all owned properties,
    /// regardless of whether they are currently mortgaged.
    /// </summary>
    public decimal TotalMortgageValue { get; init; }

    /// <summary>Zero-based position in the game definition's board.</summary>
    public int BoardPosition { get; init; }

    /// <summary>Forward crossings of GO, including forward card movement; jail transfers and backward movement do not count.</summary>
    public long CompletedLaps { get; init; }

    /// <summary>Whether the player is imprisoned, rather than just visiting Jail.</summary>
    public bool IsInJail { get; init; }

    public int FailedJailRolls { get; init; }

    public bool HasDeclaredBankruptcy { get; init; }

    public bool IsBankrupt { get; init; }

    /// <summary>One-based round when bankruptcy became final; null for players still in the game.</summary>
    public long? BankruptcyRound { get; init; }

    /// <summary>Property space IDs from the game definition, in acquisition order.</summary>
    public string[] OwnedPropertyIds { get; init; } = [];

    public string[] HeldGetOutOfJailFreeCardIds { get; init; } = [];

    /// <summary>Transactions in chronological order.</summary>
    public Transaction[] FinancialLedger { get; init; } = [];
}
