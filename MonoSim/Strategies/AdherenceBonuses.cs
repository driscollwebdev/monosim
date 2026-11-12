namespace MonoSim.Strategies;

public static class AdherenceBonuses
{
    public const decimal Aggressive = 0.02m;
    public const decimal Conservative = 0.02m;
    public const decimal Infrastructure = 0.01m;
    public const decimal AdjacentGroup = -0.02m;
    public const decimal BlueGreen = -0.02m;
    public const decimal Novice = -0.04m;

    public static decimal For(string strategy) => new string(strategy.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray())
        .Replace("strategy", "").Replace("group", "") switch
    {
        "aggressive" => Aggressive, "conservative" => Conservative, "infrastructure" => Infrastructure,
        "adjacent" => AdjacentGroup, "bluegreen" => BlueGreen, "novice" => Novice, _ => 0
    };
}
