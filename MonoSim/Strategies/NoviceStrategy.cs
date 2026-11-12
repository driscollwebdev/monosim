using MonoSim.Models;
using MonoSim.Trading;
using System.Text.Json;

namespace MonoSim.Strategies;

/// <summary>A rent-biased, reactive strategy that responds to trades but never proposes them.</summary>
public sealed class NoviceStrategy : IPlayingStrategy, IStrategyMarketPolicy, ITradeInitiationPolicy
{
    public decimal AdherenceAdjustment => AdherenceBonuses.Novice;
    private readonly IStrategyActionExecutor executor;
    private readonly Random random;
    private readonly Dictionary<(string Player, long Turn, int Roll, string Property), bool> purchaseDecisions = [];
    private readonly Dictionary<(string Player, long Turn, int Roll, string Bundle), bool> auctionDecisions = [];
    private readonly Dictionary<(string Player, long Turn, int Roll, string Terms), bool> tradeDecisions = [];

    public NoviceStrategy(IStrategyActionExecutor executor, Random? random = null)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.random = random ?? new Random();
    }

    public bool InitiatesTrades => false;

    public Action PreRoll(Player player, GameState gameState) => Decide(player, gameState, preRoll: true);

    public Action PostRoll(Player player, GameState gameState) => Decide(player, gameState, preRoll: false);

    private Action Decide(Player player, GameState state, bool preRoll)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(state);
        // Sampling and execution are deferred until the engine invokes the returned action.
        return () => InfrastructureStrategy.ExecuteActions(executor, player, state, (p, s) => SelectAction(p, s, preRoll))();
    }

    public double PurchaseProbability(Player player, GameState state, PropertyState property)
    {
        if (!property.IsAvailable)
        {
            return 0;
        }
        var group = MonopolySet(state, property);
        if (group.Length > 1 && state.OtherPlayers.Where(p => !p.IsBankrupt).Any(p => group.Where(g => g.PropertyId != property.PropertyId).All(g => p.OwnedPropertyIds.Contains(g.PropertyId))))
        {
            return 1;
        }
        if (property.Type == PropertyType.Utility)
        {
            return 0;
        }
        if (property.Type == PropertyType.Railroad)
        {
            if (state.Properties.Any(p => p.Type == PropertyType.Railroad && player.OwnedPropertyIds.Contains(p.PropertyId)))
            {
                return 1;
            }
            return 0.25;
        }
        if (state.Properties.Any(p => p.ColorGroup == property.ColorGroup && player.OwnedPropertyIds.Contains(p.PropertyId)))
        {
            return 1;
        }
        return property.ColorGroup switch
        {
            "brown" => 0.125,
            "lightBlue" => 0.25,
            "pink" => 0.375,
            "orange" => 0.5,
            "red" => 0.625,
            "yellow" => 0.75,
            "green" => 0.875,
            "darkBlue" => 1,
            _ => 0
        };
    }

    private StrategyAction? SelectAction(Player player, GameState state, bool preRoll)
    {
        // Debt liquidation belongs to the engine: the novice does not initiate even a rescue trade.
        if (TradeSafetyAdvisor.DebtDue(player, state) > 0)
        {
            return null;
        }
        if (player.IsInJail)
        {
            if (!preRoll)
            {
                return null;
            }
            return JailRelease(player, state);
        }
        foreach (var action in state.AvailableActions.Where(a => a.Type == StrategyActionType.BuyProperty && a.Cost <= player.CashBalance && a.Cost >= 0)
            .OrderBy(a => state.Properties.Single(p => p.PropertyId == a.PropertyId).BoardPosition))
        {
            var property = state.Properties.Single(p => p.PropertyId == action.PropertyId);
            var probability = PurchaseProbability(player, state, property);
            if (probability == 1)
            {
                return action;
            }
            if (probability <= 0)
            {
                continue;
            }
            var key = (player.Id, state.TurnNumber, state.RollNumber, property.PropertyId);
            if (!purchaseDecisions.TryGetValue(key, out var buy))
            {
                buy = Sample(probability);
                purchaseDecisions[key] = buy;
            }
            if (buy)
            {
                return action;
            }
        }

        var groups = state.Properties.Where(p => p.Type == PropertyType.Street && p.ColorGroup is not null).GroupBy(p => p.ColorGroup!)
            .Where(g => g.All(p => player.OwnedPropertyIds.Contains(p.PropertyId)))
            .Where(g => g.Any(p => state.OtherPlayers.Any(other => !other.IsBankrupt && Reachable(other.BoardPosition, p.BoardPosition, state))))
            .ToDictionary(g => g.Key, g => g.Average(p => p.HouseCount + 5 * p.HotelCount), StringComparer.Ordinal);
        return state.AvailableActions.Where(a => a.Cost > 0 && a.Cost <= player.CashBalance &&
                a.Type is StrategyActionType.BuildHouse or StrategyActionType.BuildHotel or StrategyActionType.UnmortgageProperty)
            .Where(a => state.Properties.Any(p => p.PropertyId == a.PropertyId && player.OwnedPropertyIds.Contains(p.PropertyId) && groups.ContainsKey(p.ColorGroup ?? "")))
            .OrderBy(a => groups[state.Properties.Single(p => p.PropertyId == a.PropertyId).ColorGroup!])
            .ThenBy(a => state.Properties.Single(p => p.PropertyId == a.PropertyId).HouseCount)
            .ThenBy(a => a.Cost).ThenBy(a => a.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    public bool AcceptTrade(Player player, GameState state, PropertyTrade trade)
    {
        var probability = TradeAcceptanceProbability(player, state, trade);
        if (probability < 0.625)
        {
            return false;
        }
        if (probability >= 1)
        {
            return true;
        }
        // Candidate generation and execution query the same exchange repeatedly.
        // Normalize asset order and omit offer IDs so those queries share one decision.
        var terms = JsonSerializer.Serialize(new
        {
            trade.OtherPlayerId, trade.CashPaid, trade.CashReceived, trade.BankFees, trade.OtherPlayerBankFees,
            Given = trade.PropertyIdsGiven.Order(StringComparer.Ordinal).ToArray(),
            Received = trade.PropertyIdsReceived.Order(StringComparer.Ordinal).ToArray(),
            CardsGiven = trade.CardIdsGiven.Order(StringComparer.Ordinal).ToArray(),
            CardsReceived = trade.CardIdsReceived.Order(StringComparer.Ordinal).ToArray(),
            player.BoardPosition, Probability = probability
        });
        var key = (player.Id, state.TurnNumber, state.RollNumber, terms);
        if (!tradeDecisions.TryGetValue(key, out var accept))
        {
            accept = Sample(probability);
            tradeDecisions[key] = accept;
        }
        return accept;
    }

    /// <summary>Combines ordinal group distance with equally counted direct-roll avoidance.</summary>
    public double TradeAcceptanceProbability(Player player, GameState state, PropertyTrade trade)
    {
        if (!TradeValidator.Validate(player, state, trade).IsValid || TradeSafetyAdvisor.Assess(player, state, trade).ShouldAvoid)
        {
            return 0;
        }
        var other = state.OtherPlayers.Single(p => p.Id == trade.OtherPlayerId);
        var mineBefore = player.OwnedPropertyIds.ToHashSet(StringComparer.Ordinal);
        var mineAfter = mineBefore.Except(trade.PropertyIdsGiven).Concat(trade.PropertyIdsReceived).ToHashSet(StringComparer.Ordinal);
        var theirsBefore = other.OwnedPropertyIds.ToHashSet(StringComparer.Ordinal);
        var theirsAfter = theirsBefore.Except(trade.PropertyIdsReceived).Concat(trade.PropertyIdsGiven).ToHashSet(StringComparer.Ordinal);
        var sets = AllMonopolySets(state).ToArray();
        var theirNewMonopolies = sets.Where(g => !g.All(p => theirsBefore.Contains(p.PropertyId)) && g.All(p => theirsAfter.Contains(p.PropertyId))).ToArray();
        if (theirNewMonopolies.Length == 0)
        {
            return 1;
        }
        var myNewMonopolies = sets.Where(g => !g.All(p => mineBefore.Contains(p.PropertyId)) && g.All(p => mineAfter.Contains(p.PropertyId))).ToArray();
        if (myNewMonopolies.Length == 0)
        {
            return 0;
        }
        var distance = myNewMonopolies.SelectMany(mine => theirNewMonopolies.Select(theirs => MonopolyDistanceProbability(mine, theirs))).Max();
        return distance * MonopolyAvoidanceProbability(player, state, theirNewMonopolies.SelectMany(g => g));
    }

    private static double MonopolyDistanceProbability(PropertyState[] mine, PropertyState[] theirs)
    {
        if (mine[0].Type != PropertyType.Street || theirs[0].Type != PropertyType.Street)
        {
            return 1;
        }
        return ColorGroupDistanceProbability(mine[0].ColorGroup!, theirs[0].ColorGroup!);
    }

    public decimal MaximumBid(Player player, GameState state, AssetBundle bundle, decimal valuation, decimal bankFees)
    {
        if (player.IsBankrupt || player.HasDeclaredBankruptcy || TradeSafetyAdvisor.DebtDue(player, state) > 0)
        {
            return 0;
        }
        var available = decimal.Floor(Math.Max(0, player.CashBalance - bankFees));
        if (available < 1)
        {
            return 0;
        }
        // Auctioned assets no longer count among their seller's holdings when judging monopoly blocking.
        var projected = state with { OtherPlayers = state.OtherPlayers.Select(p => p with
            { OwnedPropertyIds = p.OwnedPropertyIds.Except(bundle.PropertyIds).ToArray() }).ToArray() };
        var probabilities = bundle.PropertyIds.Select(id => PurchaseProbability(player, projected,
            state.Properties.Single(p => p.PropertyId == id) with { IsOwned = false })).ToArray();
        if (probabilities.Length == 0)
        {
            return 0;
        }
        var probability = probabilities.Max();
        if (probability >= 1)
        {
            return available;
        }
        var key = (player.Id, state.TurnNumber, state.RollNumber, string.Join("|", bundle.PropertyIds.Order(StringComparer.Ordinal)));
        if (!auctionDecisions.TryGetValue(key, out var bid))
        {
            bid = Sample(probability);
            auctionDecisions[key] = bid;
        }
        if (bid)
        {
            return available;
        }
        return 0;
    }

    public NoviceJailAssessment AssessJail(Player player, GameState state)
    {
        var outcomes = DiceOutcomes(state);
        var debtWeight = outcomes.Where(o => state.Properties.Any(p => p.BoardPosition == (player.BoardPosition + o.Total) % state.BoardSize &&
            !p.IsMortgaged && state.OtherPlayers.Any(other => !other.IsBankrupt && other.OwnedPropertyIds.Contains(p.PropertyId)))).Sum(o => o.Weight);
        var certain = state.Properties.Where(p => p.IsAvailable && p.SalePrice <= player.CashBalance && PurchaseProbability(player, state, p) == 1 &&
            Reachable(player.BoardPosition, p.BoardPosition, state)).Select(p => p.PropertyId).ToArray();
        return new((double)debtWeight / outcomes.Sum(o => o.Weight), certain);
    }

    private StrategyAction? JailRelease(Player player, GameState state)
    {
        var assessment = AssessJail(player, state);
        if (assessment.DebtProbability > 0.5 || assessment.CertainPurchasePropertyIds.Length == 0)
        {
            return null;
        }
        return state.AvailableActions.Where(a => a.Type is StrategyActionType.UseGetOutOfJailFreeCard or StrategyActionType.PayJailFine &&
                a.Cost >= 0 && a.Cost <= player.CashBalance && assessment.CertainPurchasePropertyIds.Any(id => state.Properties.Single(p => p.PropertyId == id).SalePrice <= player.CashBalance - a.Cost))
            .OrderBy(a => a.Type == StrategyActionType.UseGetOutOfJailFreeCard ? 0 : 1)
            .ThenBy(a => a.Cost).ThenBy(a => a.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    private bool Sample(double probability)
    {
        if (probability <= 0)
        {
            return false;
        }
        var sample = random.NextDouble();
        if (sample < 0 || sample >= 1)
        {
            throw new InvalidOperationException("Probability samples must be in [0, 1).");
        }
        return sample < probability;
    }

    private static bool Reachable(int from, int to, GameState state) => DiceOutcomes(state).Any(o => (from + o.Total) % state.BoardSize == to);

    /// <summary>Uses equally counted reachable spaces, rather than weighted dice outcomes, for trade risk.</summary>
    public static double MonopolyAvoidanceProbability(Player player, GameState state, IEnumerable<PropertyState> monopolies)
    {
        var destinations = DiceOutcomes(state).Select(o => (player.BoardPosition + o.Total) % state.BoardSize).ToHashSet();
        var reachable = monopolies.Select(p => p.BoardPosition).Distinct().Count(destinations.Contains);
        return 1 - (double)reachable / destinations.Count;
    }

    /// <summary>Absolute ordinal separation, with brown numbered one and dark blue numbered eight.</summary>
    public static double ColorGroupDistanceProbability(string first, string second) => Math.Abs(ColorGroupNumber(first) - ColorGroupNumber(second)) / 8.0;

    private static int ColorGroupNumber(string group) => group switch
    {
        "brown" => 1,
        "lightBlue" => 2,
        "pink" => 3,
        "orange" => 4,
        "red" => 5,
        "yellow" => 6,
        "green" => 7,
        "darkBlue" => 8,
        _ => throw new ArgumentException($"Unknown color group: {group}.", nameof(group))
    };

    private static (int Total, long Weight)[] DiceOutcomes(GameState state)
    {
        if (state.BoardSize <= 0 || state.DiceCount <= 0 || state.DiceSides <= 0)
        {
            throw new ArgumentException("Board and dice configuration must be positive.", nameof(state));
        }
        var counts = new Dictionary<int, long> { [0] = 1 };
        for (var die = 0; die < state.DiceCount; die++)
        {
            var next = new Dictionary<int, long>();
            foreach (var outcome in counts)
            {
                for (var face = 1; face <= state.DiceSides; face++)
                {
                    next[outcome.Key + face] = next.GetValueOrDefault(outcome.Key + face) + outcome.Value;
                }
            }
            counts = next;
        }
        return counts.Select(o => (o.Key, o.Value)).ToArray();
    }

    private static PropertyState[] MonopolySet(GameState state, PropertyState property)
    {
        if (property.Type != PropertyType.Street)
        {
            return state.Properties.Where(p => p.Type == property.Type).ToArray();
        }
        return state.Properties.Where(p => p.ColorGroup == property.ColorGroup).ToArray();
    }

    private static IEnumerable<PropertyState[]> AllMonopolySets(GameState state) =>
        state.Properties.Where(p => p.Type == PropertyType.Street && p.ColorGroup is not null).GroupBy(p => p.ColorGroup).Select(g => g.ToArray())
            .Concat(new[] { state.Properties.Where(p => p.Type == PropertyType.Railroad).ToArray(), state.Properties.Where(p => p.Type == PropertyType.Utility).ToArray() })
            .Where(g => g.Length > 0);
}
