using MonoSim.Strategies;
using MonoSim.Trading;

namespace MonoSim.Models;

/// <summary>The current game state visible to the player making a decision.</summary>
public sealed record GameState
{
    public string? SimulationId { get; init; }

    public int? RandomSeed { get; init; }

    /// <summary>Identifies a full player turn; unchanged between its pre-roll and post-roll phases.</summary>
    public long TurnNumber { get; init; }

    public long RoundNumber { get; init; }

    public int RollNumber { get; init; }

    public TurnPhase Phase { get; init; }

    public string? CurrentPlayerId { get; init; }

    public string? WinnerId { get; init; }

    /// <summary>Contains every shared winner; WinnerId is populated only for a unique winner.</summary>
    public string[] WinnerIds { get; init; } = [];

    public GameEndReason? EndReason { get; init; }

    public PlayerPortfolioValue[] PortfolioValues { get; init; } = [];

    /// <summary>All players, including finalized bankrupt players, in original turn order.</summary>
    public Player[] Players { get; init; } = [];

    public int BankHouses { get; init; } = 32;

    public int BankHotels { get; init; } = 12;

    public DeckState[] Decks { get; init; } = [];

    /// <summary>All debts in creation order, including amounts left unpaid after bankruptcy.</summary>
    public DebtState[] Debts { get; init; } = [];

    /// <summary>Number of spaces on the board, used for clockwise distance calculations.</summary>
    public int BoardSize { get; init; } = 40;

    public decimal BuildingResaleFraction { get; init; } = 0.5m;

    public decimal MortgageInterestRate { get; init; } = 0.1m;

    public decimal GetOutOfJailFreeCardTradeValue { get; init; } = 50m;

    public int DiceCount { get; init; } = 2;

    public int DiceSides { get; init; } = 6;

    /// <summary>Executed trades in the current full turn, including either participant's transfers.</summary>
    public CompletedTrade[] CompletedTrades { get; init; } = [];

    /// <summary>Original offers retained while their counteroffer negotiations remain open.</summary>
    public TradeOffer[] TradeOffers { get; init; } = [];

    /// <summary>Cash obligations that trades cannot settle or waive.</summary>
    public RentObligation[] RentObligations { get; init; } = [];

    public CashObligation[] CashObligations { get; init; } = [];

    /// <summary>Every purchasable board property, including railroads and utilities.</summary>
    public PropertyState[] Properties { get; init; } = [];

    /// <summary>
    /// All other players, excluding the player receiving this view.
    /// Each player's BoardPosition gives their current zero-based board position.
    /// </summary>
    public Player[] OtherPlayers { get; init; } = [];

    /// <summary>
    /// Currently legal options for the player receiving this view. Costs may exceed
    /// their cash; the strategy checks affordability. The engine supplies only legal
    /// landing purchases, accepted trade offers, even-building upgrades with stock
    /// available, mortgage redemptions, and jail releases backed by a held card or fine.
    /// Options must be refreshed after every executed action.
    /// </summary>
    public StrategyAction[] AvailableActions { get; init; } = [];
}
