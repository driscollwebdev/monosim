using MonoSim.Models;

namespace MonoSim;

public sealed record AdherenceDecision(string PlayerId, string Decision, bool Expected, bool Actual,
    decimal Sample, decimal BaseProbability, decimal Adjustment, decimal EffectiveProbability);

/// <summary>Independent per-player random streams and stable decisions for repeated queries at one decision point.</summary>
public sealed class AdherenceDecisions(int seed, Func<string, decimal>? sampler = null, Action<AdherenceDecision>? record = null)
{
    private readonly Dictionary<string, Random> streams = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Player, string Context), bool> choices = [];

    public void BeginTurn() => choices.Clear();

    public bool Decide(Player player, decimal adjustment, string context, bool expected, bool hasAlternative = true, bool mandatory = false)
    {
        if (mandatory || !hasAlternative)
        {
            return expected;
        }
        var key = (player.Id, context);
        if (choices.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var probability = player.StrategyAdherence + adjustment;
        if (probability is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(adjustment), "Effective adherence must be between zero and one.");
        }
        decimal sample;
        if (sampler is not null)
        {
            sample = sampler(player.Id);
        }
        else
        {
            if (!streams.TryGetValue(player.Id, out var random))
            {
                uint hash = 2166136261;
                foreach (var c in player.Id)
                {
                    hash = unchecked((hash ^ c) * 16777619);
                }
                random = new Random(unchecked(seed ^ (int)hash ^ 0x41444845));
                streams.Add(player.Id, random);
            }
            sample = (decimal)random.NextDouble();
        }
        if (sample is < 0 or > 1)
        {
            throw new InvalidOperationException("Adherence samples must be between zero and one.");
        }
        var actual = expected;
        if (sample > probability)
        {
            actual = !expected;
        }
        choices.Add(key, actual);
        record?.Invoke(new(player.Id, context, expected, actual, sample, player.StrategyAdherence, adjustment, probability));
        return actual;
    }
}
