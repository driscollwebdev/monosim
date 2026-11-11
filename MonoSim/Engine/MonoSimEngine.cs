using Microsoft.Extensions.Logging;
using MonoSim.Models;
using MonoSim.Strategies;
using MonoSim.Trading;
using System.Text.Json;

namespace MonoSim;

/// <summary>Runs one isolated simulation; strategies and log output are supplied separately.</summary>
public sealed partial class MonoSimEngine : IStrategyActionExecutor, IPlayerTurnController
{
    private readonly ILogger logger;
    private readonly MonoSimEngineOptions options;
    private readonly GameDefinition definition;
    private readonly Random random;
    private readonly int randomSeed;
    private readonly string simulationId = Guid.NewGuid().ToString("N");
    private readonly IStrategyMarketPolicy market;
    private readonly string[] order;
    private readonly Dictionary<string, Player> players = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PropertyState> properties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> decks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPlayingStrategy> strategies = new(StringComparer.Ordinal);
    private readonly List<DebtState> debts = [];
    private readonly List<CompletedTrade> completedTrades = [];
    private readonly List<TradeOffer> offers = [];
    private long turn;
    private long round;
    private int rollNumber;
    private long logSequence;
    private long debtSequence;
    private TurnPhase phase;
    private string? currentPlayer;
    private string? decisionPlayer;
    private string? landingProperty;
    private bool declared;
    private bool played;
    private bool playing;
    private bool resolvingDebt;
    private bool marketDecision;
    private int bankHouses;
    private int bankHotels;
    private GameEndReason? endReason;
    private string[] winners = [];

    public MonoSimEngine(Player[] players, ILogger logger) : this(players, logger, new MonoSimEngineOptions()) { }

    public MonoSimEngine(Player[] players, ILogger logger, MonoSimEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(players);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (players.Length is < 2 or > 6 || players.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name) || p.CashBalance < 0 || p.IsBankrupt) ||
            players.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != players.Length)
        {
            throw new ArgumentException("Provide two through six non-bankrupt players with unique nonempty IDs, names, and nonnegative cash.", nameof(players));
        }
        if (options.MaximumRounds is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumRounds must be positive when supplied.");
        }
        randomSeed = options.RandomSeed ?? Random.Shared.Next();
        random = new Random(randomSeed);
        definition = new GameDefinition(options.DefinitionPath);
        market = options.MarketPolicy ?? new DefaultStrategyMarketPolicy(capBidsAtValuation: false, (player, state) =>
        {
            if (strategies.TryGetValue(player.Id, out var strategy) && strategy is IStrategyGroupFocus focused)
            {
                return focused.TargetGroups(player, state);
            }
            return null;
        }, player =>
        {
            if (strategies.TryGetValue(player.Id, out var strategy) && strategy is IStrategyMarketPolicy policy)
            {
                return policy;
            }
            return null;
        });
        order = players.Select(p => p.Id).ToArray();
        foreach (var property in definition.Properties)
        {
            properties.Add(property.PropertyId, CloneProperty(property));
        }
        var held = new HashSet<string>(StringComparer.Ordinal);
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var player in players)
        {
            if (player.BoardPosition < 0 || player.BoardPosition >= definition.Board.Length ||
                player.OwnedPropertyIds.Any(id => !properties.ContainsKey(id) || !owned.Add(id)) ||
                player.HeldGetOutOfJailFreeCardIds.Any(id => !definition.Cards.ContainsKey(id) || !id.EndsWith("jail-free", StringComparison.Ordinal) || !held.Add(id)))
            {
                throw new ArgumentException("Player positions, properties, and held cards must be valid and exclusively owned.", nameof(players));
            }
            this.players.Add(player.Id, CopyPlayer(player));
            foreach (var id in player.OwnedPropertyIds)
            {
                properties[id] = properties[id] with { IsOwned = true };
            }
        }
        bankHouses = definition.InitialHouses;
        bankHotels = definition.InitialHotels;
        if (options.InitialDeckOrder is not null && options.InitialDeckOrder.Keys.Any(id => !definition.Decks.ContainsKey(id)))
        {
            throw new ArgumentException("A specified deck does not exist in the definition.", nameof(options));
        }
        foreach (var deck in definition.Decks)
        {
            var ids = deck.Value.Where(id => !held.Contains(id)).ToArray();
            if (options.InitialDeckOrder is not null && options.InitialDeckOrder.TryGetValue(deck.Key, out var specified))
            {
                if (specified.Length != ids.Length || specified.Distinct(StringComparer.Ordinal).Count() != ids.Length || !specified.ToHashSet(StringComparer.Ordinal).SetEquals(ids))
                {
                    throw new ArgumentException("Each specified deck order must contain exactly its non-held cards.", nameof(options));
                }
                ids = [.. specified];
            }
            else if (options.ShuffleDecks && definition.ShuffleDecks)
            {
                random.Shuffle(ids);
            }
            decks.Add(deck.Key, new Queue<string>(ids));
        }
        foreach (var player in this.players.Values.ToArray())
        {
            RecalculateMortgageValue(player.Id);
            strategies.Add(player.Id, CreateStrategy(this.players[player.Id]));
        }
    }

    public GameState Play()
    {
        if (played || playing)
        {
            throw new InvalidOperationException("An engine instance runs exactly one simulation.");
        }
        played = true;
        playing = true;
        Log("GameStarted", null, $"Simulation started with {order.Length} players; random seed {randomSeed}; definition {options.DefinitionPath}.");
        foreach (var id in order)
        {
            Log("PlayerInitialized", id, $"{players[id].Name}; strategy {players[id].Strategy}; starting cash {players[id].CashBalance}.");
        }
        try
        {
            while (ActiveCount > 1)
            {
                if (options.MaximumRounds is { } maximum && round >= maximum)
                {
                    return CompleteGame(GameEndReason.RoundLimit);
                }
                round++;
                Log("RoundStarted", null, $"Round {round} begins in original player order, skipping finalized bankrupt players.");
                foreach (var id in order)
                {
                    if (ActiveCount <= 1)
                    {
                        break;
                    }
                    if (players[id].IsBankrupt)
                    {
                        continue;
                    }
                    TakeTurn(id);
                }
                if (ActiveCount > 1)
                {
                    Log("RoundCompleted", null, $"All remaining players completed round {round}.");
                }
            }
            return CompleteGame(GameEndReason.LastPlayerStanding);
        }
        catch (Exception error)
        {
            Log("SimulationFailed", currentPlayer, error.Message);
            throw;
        }
        finally
        {
            playing = false;
            decisionPlayer = null;
        }
    }

    public GameState GetState() => Snapshot(null);

    public void DeclareActionsComplete(string playerId)
    {
        if (!playing || marketDecision || decisionPlayer != playerId)
        {
            throw new InvalidOperationException("Only the player currently making a decision may declare completion.");
        }
        if (phase == TurnPhase.RollAndMovement)
        {
            Log("DebtActionsCompleted", playerId, "Debt-funding actions completed; cash settlement remains mandatory.");
            return;
        }
        if (declared)
        {
            throw new InvalidOperationException("Completion has already been declared for this action phase.");
        }
        declared = true;
        var eventName = "ActionsCompleted";
        if (phase == TurnPhase.PostRoll)
        {
            eventName = "TurnFinished";
        }
        Log(eventName, playerId, $"Player declared completion of {phase}.");
    }

    private void TakeTurn(string id)
    {
        currentPlayer = id;
        turn++;
        rollNumber = 0;
        phase = TurnPhase.PreRoll;
        completedTrades.Clear();
        if (offers.Count > 0)
        {
            Log("TradeOffersExpired", id, $"{offers.Count} offers expired at the previous turn boundary.");
        }
        offers.Clear();
        landingProperty = null;
        Log("TurnStarted", id, "Previous turn is finished; this player's turn begins.");
        RunActions(id, TurnPhase.PreRoll);
        var doubles = 0;
        while (!players[id].IsBankrupt && ActiveCount > 1)
        {
            phase = TurnPhase.RollAndMovement;
            rollNumber++;
            landingProperty = null;
            var dice = RollDice(id);
            var isDouble = dice.Distinct().Count() == 1;
            var total = dice.Sum();
            var wasJailed = players[id].IsInJail;
            if (wasJailed)
            {
                if (isDouble)
                {
                    ReleaseJail(id, "Rolled doubles.");
                }
                else
                {
                    players[id] = players[id] with { FailedJailRolls = players[id].FailedJailRolls + 1 };
                    Log("JailAttemptFailed", id, $"Failed jail attempt {players[id].FailedJailRolls}.");
                    if (players[id].FailedJailRolls < definition.Int("maxJailTurns"))
                    {
                        RunActions(id, TurnPhase.PostRoll);
                        break;
                    }
                    Owe(id, null, definition.Money("jailReleaseFee"), false, "Mandatory jail release fine");
                    SettleDebts();
                    if (players[id].IsBankrupt || ActiveCount <= 1)
                    {
                        break;
                    }
                    ReleaseJail(id, "Paid mandatory fine after the final jail attempt.");
                }
            }
            if (isDouble && !wasJailed)
            {
                doubles++;
            }
            if (doubles >= definition.Int("consecutiveDoublesToJail"))
            {
                SendToJail(id, "Consecutive doubles limit");
            }
            else
            {
                Move(id, total, true);
                ResolveSpace(id, total);
                SettleDebts();
            }
            if (players[id].IsBankrupt || ActiveCount <= 1)
            {
                break;
            }
            var extra = isDouble && !wasJailed && !players[id].IsInJail;
            if (extra)
            {
                RunActions(id, TurnPhase.CombinedPostRollPreRoll);
                FinishLandingOffer(id);
            }
            else
            {
                RunActions(id, TurnPhase.PostRoll);
                FinishLandingOffer(id);
                break;
            }
        }
        if (players[id].IsBankrupt)
        {
            Log("TurnTerminatedByBankruptcy", id, "Final bankruptcy terminates the player's turn.");
        }
    }

    private void RunActions(string id, TurnPhase nextPhase)
    {
        phase = nextPhase;
        declared = false;
        decisionPlayer = id;
        Log("PhaseStarted", id, nextPhase.ToString());
        var state = Snapshot(id);
        Action action;
        if (nextPhase == TurnPhase.PreRoll)
        {
            action = strategies[id].PreRoll(CopyPlayer(players[id]), state);
        }
        else
        {
            action = strategies[id].PostRoll(CopyPlayer(players[id]), state);
        }
        action();
        decisionPlayer = null;
        if (!declared)
        {
            throw new InvalidOperationException("A strategy must explicitly declare completion through IPlayerTurnController.");
        }
    }

    private int[] RollDice(string id)
    {
        var dice = options.DiceRoller?.Invoke();
        if (dice is null)
        {
            dice = Enumerable.Range(0, definition.Int("diceCount")).Select(_ => random.Next(1, definition.Int("diceSides") + 1)).ToArray();
        }
        if (dice.Length != definition.Int("diceCount") || dice.Any(d => d < 1 || d > definition.Int("diceSides")))
        {
            throw new InvalidOperationException("The dice source returned an invalid roll.");
        }
        Log("DiceRolled", id, string.Join(",", dice));
        return dice;
    }

    private IPlayingStrategy CreateStrategy(Player player)
    {
        if (options.StrategyFactory is not null)
        {
            return options.StrategyFactory(CopyPlayer(player), this) ?? throw new ArgumentException("StrategyFactory returned null.", nameof(options));
        }
        return DefaultStrategyMarketPolicy.Kind(player) switch
        {
            "aggressive" => new AggressiveStrategy(this),
            "conservative" => new ConservativeStrategy(this),
            "infrastructure" => new InfrastructureStrategy(this),
            "adjacent" => new AdjacentGroupStrategy(this),
            "bluegreen" => new BlueGreenStrategy(this),
            "novice" => new NoviceStrategy(this, new Random(unchecked(randomSeed * 397 + Array.IndexOf(order, player.Id) * 7919 + 0x4E4F5649))),
            _ => throw new ArgumentException($"Unknown strategy: {player.Strategy}.")
        };
    }

    private int ActiveCount => players.Values.Count(p => !p.IsBankrupt);

    private T MarketDecision<T>(Func<T> decision)
    {
        var previous = marketDecision;
        marketDecision = true;
        try
        {
            return decision();
        }
        finally
        {
            marketDecision = previous;
        }
    }

    private void Log(string name, string? id, string description, decimal? amount = null)
    {
        decimal? balance = null;
        if (id is not null && players.TryGetValue(id, out var player))
        {
            balance = player.CashBalance;
        }
        var entry = new SimulationLogEntry(simulationId, ++logSequence, round, turn, rollNumber, phase, name, id, description, amount, balance);
        logger.Log(LogLevel.Information, new EventId(1, name), entry, null,
            static (state, _) => JsonSerializer.Serialize(state.Where(p => p.Key != "{OriginalFormat}").ToDictionary(p => p.Key, p => p.Value)));
    }

    private static Player CopyPlayer(Player player) => player with
    {
        OwnedPropertyIds = [.. player.OwnedPropertyIds], HeldGetOutOfJailFreeCardIds = [.. player.HeldGetOutOfJailFreeCardIds], FinancialLedger = [.. player.FinancialLedger]
    };

    private static PropertyState CloneProperty(PropertyState property)
    {
        var rent = property.Rent;
        if (rent is not null)
        {
            rent = rent with { Houses = [.. rent.Houses], ByRailroadsOwned = [.. rent.ByRailroadsOwned], DiceMultipliersByUtilitiesOwned = [.. rent.DiceMultipliersByUtilitiesOwned] };
        }
        return property with { Rent = rent };
    }
}
