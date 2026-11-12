using System.Globalization;
using MonoSim.Models;
using MonoSim.Strategies;

namespace MonoSim.Cli;

/// <summary>One flat results row per simulation, with fixed slots for up to six players.</summary>
public sealed record SimulationOutcome(string RunId, string SimulationId, int Number, int BatchSeed, int SimulationSeed,
    SimulationDefinition Definition, string DefinitionPath, int MaximumRounds, bool ShuffleDecks, string LogPath,
    Player[] InitialPlayers, GameState? FinalState, Exception? Error)
{
    /// <summary>Player IDs in the initial randomized order, including players later eliminated.</summary>
    public string[] TurnOrder { get; init; } = InitialPlayers.Select(p => p.Id).ToArray();

    public static IReadOnlyList<string> Headers { get; } = Array.AsReadOnly(new[]
    {
        "ResultSchemaVersion", "RunId", "SimulationId", "SimulationNumber", "Status", "BatchSeed", "SimulationSeed",
        "DefinitionId", "DefinitionSchemaVersion", "DefinitionPath", "PlayerCount", "MaxRounds", "ShuffleDecks",
        "RoundsPlayed", "TurnsPlayed", "EndReason", "WinnerIds", "ErrorType", "ErrorMessage", "LogPath"
    }.Concat(Enumerable.Range(1, 6).SelectMany(i => new[]
    {
        $"Player{i}Strategy", $"Player{i}Cash", $"Player{i}PortfolioValue", $"Player{i}Bankrupt", $"Player{i}Winner"
    })).Concat(Enumerable.Range(1, 6).SelectMany(i => new[] { $"Player{i}Adherence", $"Player{i}EffectiveAdherence" }))
        .Concat(Enumerable.Range(1, 6).Select(i => $"Player{i}BankruptcyRound"))
        .Append("TurnOrder").ToArray());

    public string[] Fields()
    {
        var status = "completed";
        if (Error is OperationCanceledException)
        {
            status = "cancelled";
        }
        else if (Error is not null)
        {
            status = "failed";
        }
        var fields = new List<string>
        {
            "4", RunId, SimulationId, Text(Number), status, Text(BatchSeed), Text(SimulationSeed), Definition.EditionId,
            Text(Definition.SchemaVersion), DefinitionPath, Text(InitialPlayers.Length), Text(MaximumRounds), Text(ShuffleDecks),
            Text(FinalState?.RoundNumber), Text(FinalState?.TurnNumber), FinalState?.EndReason?.ToString() ?? "",
            string.Join("|", FinalState?.WinnerIds ?? []), Error?.GetType().FullName ?? "", Error?.Message ?? "", LogPath
        };
        for (var i = 0; i < 6; i++)
        {
            if (i >= InitialPlayers.Length)
            {
                fields.AddRange(["", "", "", "", ""]);
                continue;
            }
            var initial = InitialPlayers[i];
            var final = FinalState?.Players.Single(p => p.Id == initial.Id);
            var portfolio = FinalState?.PortfolioValues.Single(p => p.PlayerId == initial.Id);
            fields.AddRange([initial.Strategy, Text(final?.CashBalance), Text(portfolio?.Total), Text(final?.IsBankrupt), Text(FinalState?.WinnerIds.Contains(initial.Id))]);
        }
        for (var i = 0; i < 6; i++)
        {
            if (i >= InitialPlayers.Length)
            {
                fields.AddRange(["", ""]);
                continue;
            }
            var player = InitialPlayers[i];
            fields.AddRange([Text(player.StrategyAdherence), Text(player.StrategyAdherence + AdherenceBonuses.For(player.Strategy))]);
        }
        for (var i = 0; i < 6; i++)
        {
            long? bankruptcyRound = null;
            if (i < InitialPlayers.Length)
            {
                var final = FinalState?.Players.Single(p => p.Id == InitialPlayers[i].Id);
                if (final?.IsBankrupt == true)
                {
                    bankruptcyRound = final.BankruptcyRound;
                }
            }
            fields.Add(Text(bankruptcyRound));
        }
        fields.Add(string.Join("|", TurnOrder));
        return fields.ToArray();
    }

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
