using Microsoft.Extensions.Logging;
using MonoSim.Logging;
using MonoSim.Models;

namespace MonoSim.Cli;

public sealed record BatchResult(int Requested, int Completed, int Failed, int Seed, int Workers);

public sealed class SimulationBatchRunner(Func<Player[], ILogger, MonoSimEngineOptions, GameState>? simulate = null)
{
    public static int MaximumWorkers => Math.Clamp(Environment.ProcessorCount, 1, 4);

    public async Task<BatchResult> RunAsync(CliOptions options, SimulationDefinition definition, ResultsWriter results,
        SimulationLogging logging, CancellationToken cancellationToken = default)
    {
        var seed = options.Seed ?? Random.Shared.Next();
        var adherenceProfile = CreateAdherenceProfile(options.PlayerCount, seed);
        var completed = 0;
        var failed = 0;
        var stopRequested = 0;
        var workers = Math.Min(MaximumWorkers, options.Simulations);
        var logger = logging.Factory.CreateLogger("MonoSim.Batch");
        var progress = logging.Factory.CreateLogger(SimulationLogging.ProgressCategory);
        progress.LogInformation("Starting {SimulationCount} simulations with {WorkerCount} workers; batch seed {BatchSeed}; results {ResultsPath}",
            options.Simulations, workers, seed, results.FilePath);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, options.Simulations), new ParallelOptions
            {
                MaxDegreeOfParallelism = workers, CancellationToken = stop.Token
            }, (index, _) =>
            {
                if (Volatile.Read(ref stopRequested) != 0)
                {
                    return ValueTask.CompletedTask;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var gameSeed = unchecked(seed + index * 7919);
                var id = Guid.NewGuid().ToString("N");
                var players = CreatePlayers(options, definition, gameSeed, adherenceProfile);
                var turnOrder = players.ToArray();
                var turnRandom = new Random(unchecked(gameSeed ^ 0x5455524E));
                turnRandom.Shuffle(turnOrder);
                GameState? final = null;
                Exception? failure = null;
                try
                {
                    logger.LogInformation("Simulation {SimulationId} turn order: {TurnOrder}", id,
                        string.Join(", ", turnOrder.Select(p => p.Id)));
                    var engineOptions = new MonoSimEngineOptions
                    {
                        SimulationId = id, RandomSeed = gameSeed, DefinitionPath = options.DefinitionPath,
                        MaximumRounds = options.MaximumRounds, ShuffleDecks = options.ShuffleDecks,
                        CancellationToken = cancellationToken
                    };
                    var engineLogger = logging.Factory.CreateLogger<MonoSimEngine>();
                    if (simulate is not null)
                    {
                        final = simulate(turnOrder, engineLogger, engineOptions);
                    }
                    else
                    {
                        final = new MonoSimEngine(turnOrder, engineLogger, engineOptions).Play();
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                    if (options.StopOnError && Interlocked.Exchange(ref stopRequested, 1) == 0)
                    {
                        stop.Cancel();
                    }
                    logger.LogError(error, "Simulation {SimulationId} (number {SimulationNumber}, seed {SimulationSeed}) failed: {ErrorMessage}", id, index + 1, gameSeed, error.Message);
                }
                // File failures abort the batch; they are not recoverable simulation errors.
                results.Write(new(logging.RunId, id, index + 1, seed, gameSeed, definition, options.DefinitionPath,
                    options.MaximumRounds, options.ShuffleDecks, logging.GetSimulationFilePath(id), players, final, failure));
                if (failure is null)
                {
                    Interlocked.Increment(ref completed);
                    progress.LogInformation("Simulation {SimulationId} ({SimulationNumber}/{SimulationCount}) completed after {Rounds} rounds; winners {Winners}.",
                        id, index + 1, options.Simulations, final!.RoundNumber, string.Join(", ", final.WinnerIds));
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
                return ValueTask.CompletedTask;
            });
        }
        catch (OperationCanceledException) when (Volatile.Read(ref stopRequested) != 0 && !cancellationToken.IsCancellationRequested)
        {
            // --on-error stop prevents further scheduling; in-flight games still finish and record their rows.
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress.LogInformation("Batch finished: {Completed} completed, {Failed} failed, {NotStarted} not started; batch seed {BatchSeed}.",
            completed, failed, options.Simulations - completed - failed, seed);
        return new(options.Simulations, completed, failed, seed, workers);
    }

    public static decimal[] CreateAdherenceProfile(int playerCount, int batchSeed)
    {
        var random = new Random(unchecked(batchSeed ^ 0x50524F46));
        return Enumerable.Range(0, playerCount).Select(_ => random.Next(750000, 950001) / 1000000m).ToArray();
    }

    public static Player[] CreatePlayers(CliOptions options, SimulationDefinition definition, int simulationSeed, IReadOnlyList<decimal>? adherenceProfile = null)
    {
        adherenceProfile ??= CreateAdherenceProfile(options.PlayerCount, simulationSeed);
        if (adherenceProfile.Count != options.PlayerCount || adherenceProfile.Any(p => p is < 0.75m or > 0.95m))
        {
            throw new ArgumentException("An adherence profile must contain one value from 0.75 through 0.95 for each player.", nameof(adherenceProfile));
        }
        var random = new Random(unchecked(simulationSeed ^ 0x53545241));
        var players = new Player[options.PlayerCount];
        for (var i = 0; i < players.Length; i++)
        {
            var strategy = options.Strategies[Math.Min(i, options.Strategies.Length - 1)];
            if (strategy == "random")
            {
                strategy = CliOptions.StrategyNames[random.Next(CliOptions.StrategyNames.Count)];
            }
            players[i] = new()
            {
                Id = $"player-{i + 1}", Name = $"Player {i + 1}", Strategy = strategy,
                StrategyAdherence = adherenceProfile[i],
                CashBalance = definition.StartingCash, BoardPosition = definition.StartingPosition, IsInJail = definition.StartsInJail
            };
        }
        return players;
    }
}
