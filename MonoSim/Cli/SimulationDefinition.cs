using System.Text.Json;

namespace MonoSim.Cli;

public sealed record SimulationDefinition(string EditionId, int SchemaVersion, decimal StartingCash, int StartingPosition, bool StartsInJail)
{
    public static SimulationDefinition Load(string path)
    {
        _ = new GameDefinition(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var initial = root.GetProperty("initialState");
        var cash = initial.GetProperty("playerCash").GetDecimal();
        var positionId = initial.GetProperty("playerPositionId").GetString();
        var position = root.GetProperty("board").EnumerateArray().Single(p => p.GetProperty("id").GetString() == positionId).GetProperty("position").GetInt32();
        if (cash < 0)
        {
            throw new ArgumentException("Starting player cash cannot be negative.");
        }
        return new(root.GetProperty("edition").GetProperty("id").GetString()!, root.GetProperty("schemaVersion").GetInt32(),
            cash, position, initial.GetProperty("playerInJail").GetBoolean());
    }
}
