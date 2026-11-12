namespace MonoSim.Strategies;

/// <summary>A currently executable option offered to the acting player by the engine.</summary>
public sealed record StrategyAction
{
    /// <summary>Identifies this particular option, including a trade offer or held card.</summary>
    public required string Id { get; init; }

    public required StrategyActionType Type { get; init; }

    public string? PropertyId { get; init; }

    public string? CardId { get; init; }

    public AssetBundle? Bundle { get; init; }

    /// <summary>
    /// Total cash payable by the player, including mandatory fees and interest.
    /// Property purchases, redemption, construction, and jail fines must have positive costs.
    /// For a property trade this is net cash paid plus bank fees; it may be zero or negative.
    /// </summary>
    public required decimal Cost { get; init; }

    public bool CompletesColorGroup { get; init; }

    /// <summary>Increase in ordinary rent from this redemption or building upgrade.</summary>
    public decimal RentIncrease { get; init; }

    /// <summary>The accepted exchange executed by a TradeProperties action.</summary>
    public PropertyTrade? Trade { get; init; }

    /// <summary>Opening offer and counters in the acting player's perspective; final terms match Trade.</summary>
    public PropertyTrade[] NegotiationHistory { get; init; } = [];

    public string? TradeOfferId { get; init; }

    /// <summary>
    /// A nonbinding local plan evaluated as if this landing property had been bought.
    /// It cannot be published or accepted until the property is actually owned;
    /// execution requires a fresh, valid, accepted TradeProperties offer.
    /// </summary>
    public PropertyTrade? FollowUpTrade { get; init; }
}
