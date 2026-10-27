namespace QuantConnect.Genetic;

/// <summary>
/// Deterministic two-population genetic search with a random-search baseline.
/// Backtests are supplied by the caller; LEAN's optimizer controls execution concurrency.
/// </summary>
public sealed class GeneticSearchEngine
{
    public const double InvalidFitness = -1_000_000_000d;

    private readonly StrategyDiscoveryOptions _options;
    private readonly DeterministicRandom _random;
    private readonly Dictionary<int, DiscoveryCandidate> _pending = new();
    private readonly Dictionary<int, ScoredGenome> _results = new();
    private readonly Dictionary<StrategyPopulation, List<ScoredGenome>> _latestPopulation = new();
    private readonly List<DiscoveryCandidate> _batch = new();
    private int _nextCandidateId = 1;
    private int _generation;
    private bool _started;

    public GeneticSearchEngine(StrategyDiscoveryOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _random = new DeterministicRandom(_options.Seed);
    }

    public bool IsComplete { get; private set; }
    public IReadOnlyCollection<ScoredGenome> Results => _results.Values.OrderBy(result => result.CandidateId).ToArray();

    public IReadOnlyList<DiscoveryCandidate> Start()
    {
        if (_started) throw new InvalidOperationException("The search has already started.");
        _started = true;
        return CreateBatch();
    }

    /// <summary>
    /// Records one completed backtest and returns the next generation only after every
    /// candidate in the current generation has completed.
    /// </summary>
    public IReadOnlyList<DiscoveryCandidate> Submit(int candidateId, StrategyBacktestMetrics? metrics)
    {
        if (!_started) throw new InvalidOperationException("Call Start before submitting results.");
        if (IsComplete) throw new InvalidOperationException("The search has completed.");
        if (!_pending.Remove(candidateId, out var candidate))
        {
            throw new ArgumentException($"Candidate {candidateId} is not pending or was already submitted.", nameof(candidateId));
        }

        var fitness = metrics is null
            ? InvalidFitness
            : Score(metrics, candidate.Population, _options.MinimumTrades, _options.MaximumDrawdown);
        var scored = new ScoredGenome(candidate.Id, candidate.Generation, candidate.Population, candidate.Genome, fitness, metrics);
        _results.Add(candidate.Id, scored);

        if (_pending.Count > 0)
        {
            return Array.Empty<DiscoveryCandidate>();
        }

        foreach (var group in _batch.Where(item => item.Population != StrategyPopulation.RandomBaseline).GroupBy(item => item.Population))
        {
            _latestPopulation[group.Key] = group.Select(item => _results[item.Id]).OrderByDescending(item => item.Fitness).ThenBy(item => item.CandidateId).ToList();
        }

        if (_generation + 1 >= _options.GenerationCount)
        {
            IsComplete = true;
            return Array.Empty<DiscoveryCandidate>();
        }

        _generation++;
        return CreateBatch();
    }

    public static double Score(StrategyBacktestMetrics metrics, StrategyPopulation population, int minimumTrades = 0, double maximumDrawdown = 1)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (!double.IsFinite(metrics.AnnualReturn) ||
            !double.IsFinite(metrics.SharpeRatio) ||
            !double.IsFinite(metrics.Drawdown) ||
            !double.IsFinite(metrics.WinRate) ||
            !double.IsFinite(metrics.TotalReturn) ||
            metrics.TradeCount < minimumTrades ||
            metrics.Drawdown < 0 || metrics.Drawdown > maximumDrawdown ||
            metrics.WinRate is < 0 or > 1)
        {
            return InvalidFitness;
        }

        var sharpe = Math.Clamp(metrics.SharpeRatio, -5d, 5d);
        return population switch
        {
            StrategyPopulation.Growth => metrics.AnnualReturn - (1.5d * metrics.Drawdown) + (0.025d * sharpe),
            StrategyPopulation.Consistency => (0.10d * sharpe) + (0.25d * metrics.WinRate) + (0.15d * metrics.AnnualReturn) - (0.75d * metrics.Drawdown),
            StrategyPopulation.RandomBaseline => metrics.AnnualReturn - (1.5d * metrics.Drawdown) + (0.025d * sharpe),
            _ => throw new ArgumentOutOfRangeException(nameof(population), population, "Unknown population.")
        };
    }

    private DiscoveryCandidate[] CreateBatch()
    {
        _batch.Clear();
        _pending.Clear();

        AddPopulation(StrategyPopulation.Growth, CreateEvolvedPopulation(StrategyPopulation.Growth));
        AddPopulation(StrategyPopulation.Consistency, CreateEvolvedPopulation(StrategyPopulation.Consistency));
        if (_options.RandomBaselineSize > 0)
        {
            AddPopulation(StrategyPopulation.RandomBaseline, Enumerable.Range(0, _options.RandomBaselineSize).Select(_ => RandomGenome()).ToArray());
        }

        return _batch.ToArray();
    }

    private IReadOnlyList<StrategyGenome> CreateEvolvedPopulation(StrategyPopulation population)
    {
        if (_generation == 0)
        {
            return Enumerable.Range(0, _options.PopulationSize).Select(_ => RandomGenome()).ToArray();
        }

        var parents = _latestPopulation[population];
        var next = new List<StrategyGenome>(_options.PopulationSize);
        foreach (var elite in parents.Take(Math.Min(_options.EliteCount, _options.PopulationSize)))
        {
            next.Add(elite.Genome);
        }

        while (next.Count < _options.PopulationSize)
        {
            var first = TournamentSelect(parents).Genome;
            var second = TournamentSelect(parents).Genome;
            next.Add(Mutate(Crossover(first, second)));
        }

        return next;
    }

    private void AddPopulation(StrategyPopulation population, IReadOnlyList<StrategyGenome> genomes)
    {
        foreach (var genome in genomes)
        {
            var candidate = new DiscoveryCandidate(_nextCandidateId++, _generation, population, genome.Normalize(_options.AllowedTimeframes));
            _batch.Add(candidate);
            _pending.Add(candidate.Id, candidate);
        }
    }

    private StrategyGenome RandomGenome()
    {
        var fast = _random.Next(StrategyGenome.MinFastPeriod, StrategyGenome.MaxFastPeriod + 1);
        var slow = _random.Next(Math.Max(StrategyGenome.MinSlowPeriod, fast + 2), StrategyGenome.MaxSlowPeriod + 1);
        var entry = _random.Next(StrategyGenome.MinEntryThresholdTenths, StrategyGenome.MaxEntryThresholdTenths + 1);
        return new StrategyGenome(
            RandomEnum<StrategyFamily>(),
            _options.AllowedTimeframes[_random.Next(_options.AllowedTimeframes.Count)],
            fast,
            slow,
            entry,
            _random.Next(1, entry),
            _random.Next(StrategyGenome.MinMaxPositions, StrategyGenome.MaxMaxPositions + 1),
            _random.Next(StrategyGenome.MinExposurePct, StrategyGenome.MaxExposurePct + 1));
    }

    private StrategyGenome Crossover(StrategyGenome first, StrategyGenome second)
    {
        return new StrategyGenome(
            Pick(first.Family, second.Family),
            Pick(first.Timeframe, second.Timeframe),
            Pick(first.FastPeriod, second.FastPeriod),
            Pick(first.SlowPeriod, second.SlowPeriod),
            Pick(first.EntryThresholdTenths, second.EntryThresholdTenths),
            Pick(first.ExitThresholdTenths, second.ExitThresholdTenths),
            Pick(first.MaxPositions, second.MaxPositions),
            Pick(first.MaxExposurePercent, second.MaxExposurePercent));
    }

    private StrategyGenome Mutate(StrategyGenome genome)
    {
        if (_random.NextDouble() < _options.MutationRate) genome = genome with { Family = RandomEnum<StrategyFamily>() };
        if (_random.NextDouble() < _options.MutationRate) genome = genome with { Timeframe = _options.AllowedTimeframes[_random.Next(_options.AllowedTimeframes.Count)] };
        if (_random.NextDouble() < _options.MutationRate) genome = genome with { FastPeriod = _random.Next(StrategyGenome.MinFastPeriod, StrategyGenome.MaxFastPeriod + 1) };
        if (_random.NextDouble() < _options.MutationRate) genome = genome with { SlowPeriod = _random.Next(StrategyGenome.MinSlowPeriod, StrategyGenome.MaxSlowPeriod + 1) };
        if (_random.NextDouble() < _options.MutationRate) genome = genome with { EntryThresholdTenths = _random.Next(StrategyGenome.MinEntryThresholdTenths, StrategyGenome.MaxEntryThresholdTenths + 1) };
        if (_random.NextDouble() < _options.MutationRate) genome = genome with { ExitThresholdTenths = _random.Next(1, StrategyGenome.MaxEntryThresholdTenths) };
        if (_random.NextDouble() < _options.MutationRate) genome = genome with { MaxPositions = _random.Next(StrategyGenome.MinMaxPositions, StrategyGenome.MaxMaxPositions + 1) };
        if (_random.NextDouble() < _options.MutationRate) genome = genome with { MaxExposurePercent = _random.Next(StrategyGenome.MinExposurePct, StrategyGenome.MaxExposurePct + 1) };
        return genome.Normalize(_options.AllowedTimeframes);
    }

    private ScoredGenome TournamentSelect(List<ScoredGenome> population)
    {
        ScoredGenome? best = null;
        var sampleCount = Math.Min(_options.TournamentSize, population.Count);
        for (var index = 0; index < sampleCount; index++)
        {
            var contestant = population[_random.Next(population.Count)];
            if (best is null || contestant.Fitness > best.Fitness)
            {
                best = contestant;
            }
        }

        return best!;
    }

    private T Pick<T>(T first, T second) => _random.Next(2) == 0 ? first : second;

    private T RandomEnum<T>() where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        return values[_random.Next(values.Length)];
    }

    /// <summary>Small, fixed-sequence PRNG for repeatable search runs; it is not for security tokens.</summary>
    private sealed class DeterministicRandom
    {
        private ulong _state;
        private readonly ulong _increment;

        public DeterministicRandom(int seed)
        {
            _increment = ((ulong)(uint)seed << 1) | 1UL;
            NextUInt32();
            _state += (uint)seed + 0x9E3779B9UL;
            NextUInt32();
        }

        public int Next(int exclusiveMaximum) => (int)(NextDouble() * exclusiveMaximum);

        public int Next(int inclusiveMinimum, int exclusiveMaximum) =>
            inclusiveMinimum + (int)(NextDouble() * (exclusiveMaximum - inclusiveMinimum));

        public double NextDouble() => NextUInt32() / 4294967296d;

        private uint NextUInt32()
        {
            var previous = _state;
            _state = (previous * 6364136223846793005UL) + _increment;
            var shifted = (uint)(((previous >> 18) ^ previous) >> 27);
            var rotation = (int)(previous >> 59);
            return (shifted >> rotation) | (shifted << ((-rotation) & 31));
        }
    }
}
