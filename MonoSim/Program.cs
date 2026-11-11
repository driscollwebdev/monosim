using System.Text.Json;

var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "monopoly-us-classic.json");
using var document = JsonDocument.Parse(File.ReadAllText(dataPath));
var game = document.RootElement;

Console.WriteLine(game.GetProperty("edition").GetProperty("name").GetString());
Console.WriteLine($"Loaded {game.GetProperty("board").GetArrayLength()} board spaces.");
foreach (var deck in game.GetProperty("decks").EnumerateArray())
{
    Console.WriteLine($"Loaded {deck.GetProperty("cards").GetArrayLength()} {deck.GetProperty("id").GetString()} cards.");
}
