namespace MonoSim.Models;

public sealed record PlayerPortfolioValue(string PlayerId, decimal Cash, decimal Properties, decimal Cards)
{
    public decimal Total => Cash + Properties + Cards;
}
