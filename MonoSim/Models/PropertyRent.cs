namespace MonoSim.Models;

/// <summary>Rent schedules copied from the property's game definition.</summary>
public sealed record PropertyRent
{
    public decimal Base { get; init; }
    public decimal CompleteColorGroup { get; init; }
    public decimal[] Houses { get; init; } = [];
    public decimal Hotel { get; init; }
    public decimal[] ByRailroadsOwned { get; init; } = [];
    public int[] DiceMultipliersByUtilitiesOwned { get; init; } = [];
}
