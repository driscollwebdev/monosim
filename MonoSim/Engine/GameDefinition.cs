using System.Text.Json;
using MonoSim.Models;

namespace MonoSim;

internal sealed class GameDefinition
{
    internal sealed record Space(int Position, string Id, string Name, string Type, string? DeckId, JsonElement Effects);
    internal sealed record Card(string Id, string DeckId, JsonElement Effects);
    internal readonly Space[] Board;
    internal readonly PropertyState[] Properties;
    internal readonly Dictionary<string, Card> Cards = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, string[]> Decks = new(StringComparer.Ordinal);
    internal readonly JsonElement Rules;
    internal readonly int InitialHouses;
    internal readonly int InitialHotels;
    internal readonly bool ShuffleDecks;

    internal GameDefinition(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Rules = root.GetProperty("rules").Clone();
        var initial = root.GetProperty("initialState");
        InitialHouses = initial.GetProperty("bankHouses").GetInt32();
        InitialHotels = initial.GetProperty("bankHotels").GetInt32();
        ShuffleDecks = initial.GetProperty("shuffleDecks").GetBoolean();
        var board = new List<Space>();
        var properties = new List<PropertyState>();
        foreach (var space in root.GetProperty("board").EnumerateArray())
        {
            var position = space.GetProperty("position").GetInt32();
            var id = space.GetProperty("id").GetString()!;
            var name = space.GetProperty("name").GetString()!;
            var type = space.GetProperty("type").GetString()!;
            string? deck = null;
            if (space.TryGetProperty("deckId", out var deckId))
            {
                deck = deckId.GetString();
            }
            var effects = default(JsonElement);
            if (space.TryGetProperty("effects", out var effectList))
            {
                effects = effectList.Clone();
            }
            board.Add(new(position, id, name, type, deck, effects));
            if (!space.TryGetProperty("price", out var price))
            {
                continue;
            }
            string? color = null;
            if (space.TryGetProperty("colorGroup", out var group))
            {
                color = group.GetString();
            }
            decimal housePrice = 0;
            decimal hotelPrice = 0;
            if (space.TryGetProperty("housePrice", out var house))
            {
                housePrice = house.GetDecimal();
                hotelPrice = space.GetProperty("hotelPrice").GetDecimal();
            }
            properties.Add(new PropertyState
            {
                PropertyId = id, PropertyName = name, BoardPosition = position,
                Type = type switch { "railroad" => PropertyType.Railroad, "utility" => PropertyType.Utility, _ => PropertyType.Street },
                ColorGroup = color, SalePrice = price.GetDecimal(), MortgageValue = space.GetProperty("mortgageValue").GetDecimal(),
                HousePrice = housePrice, HotelPrice = hotelPrice,
                Rent = JsonSerializer.Deserialize<PropertyRent>(space.GetProperty("rent").GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
        }
        Board = board.OrderBy(s => s.Position).ToArray();
        Properties = properties.ToArray();
        if (Board.Length != 40 || !Board.Select(s => s.Position).SequenceEqual(Enumerable.Range(0, Board.Length)))
        {
            throw new ArgumentException("The game definition must contain the complete ordered 40-space board.", nameof(path));
        }
        foreach (var deck in root.GetProperty("decks").EnumerateArray())
        {
            var id = deck.GetProperty("id").GetString()!;
            var ids = new List<string>();
            foreach (var card in deck.GetProperty("cards").EnumerateArray())
            {
                var cardId = card.GetProperty("id").GetString()!;
                Cards.Add(cardId, new(cardId, id, card.GetProperty("effects").Clone()));
                ids.Add(cardId);
            }
            Decks.Add(id, ids.ToArray());
        }
    }

    internal int Int(string name) => Rules.GetProperty(name).GetInt32();
    internal decimal Money(string name) => Rules.GetProperty(name).GetDecimal();
}
