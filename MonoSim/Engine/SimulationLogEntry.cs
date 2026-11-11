using MonoSim.Models;

namespace MonoSim;

public sealed record SimulationLogEntry(string SimulationId, long Sequence, long RoundNumber, long TurnNumber, int RollNumber, TurnPhase Phase,
    string Event, string? PlayerId, string Description, decimal? Amount = null, decimal? CashBalance = null)
    : IReadOnlyList<KeyValuePair<string, object?>>
{
    public int Count => 12;

    public KeyValuePair<string, object?> this[int index] => index switch
    {
        0 => new(nameof(SimulationId), SimulationId),
        1 => new(nameof(Sequence), Sequence),
        2 => new(nameof(RoundNumber), RoundNumber),
        3 => new(nameof(TurnNumber), TurnNumber),
        4 => new(nameof(RollNumber), RollNumber),
        5 => new(nameof(Phase), Phase.ToString()),
        6 => new(nameof(Event), Event),
        7 => new(nameof(PlayerId), PlayerId),
        8 => new(nameof(Description), Description),
        9 => new(nameof(Amount), Amount),
        10 => new(nameof(CashBalance), CashBalance),
        11 => new("{OriginalFormat}", "{Event}: {Description}"),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => Enumerable.Range(0, Count).Select(i => this[i]).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
