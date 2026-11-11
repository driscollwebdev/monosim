namespace MonoSim.Models;

/// <summary>A property's current visible state on the board.</summary>
public sealed record PropertyState
{
    /// <summary>The property's space ID in the game definition.</summary>
    public required string PropertyId { get; init; }

    public required string PropertyName { get; init; }

    /// <summary>The printed purchase price from the game definition.</summary>
    public required decimal SalePrice { get; init; }

    public decimal MortgageValue { get; init; }

    public decimal HousePrice { get; init; }

    /// <summary>The additional price to replace four houses with a hotel.</summary>
    public decimal HotelPrice { get; init; }

    public PropertyRent? Rent { get; init; }

    public PropertyType Type { get; init; }

    /// <summary>The space's zero-based position in the game definition.</summary>
    public int BoardPosition { get; init; }

    /// <summary>The game definition's color-group ID; null for railroads and utilities.</summary>
    public string? ColorGroup { get; init; }

    /// <summary>Number of houses currently on the property; zero when it has a hotel.</summary>
    public int HouseCount { get; init; }

    /// <summary>Number of hotels currently on the property.</summary>
    public int HotelCount { get; init; }

    public bool IsOwned { get; init; }

    public bool IsMortgaged { get; init; }

    /// <summary>Whether the property is unowned and available to purchase from the bank.</summary>
    public bool IsAvailable => !IsOwned;
}
