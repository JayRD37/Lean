namespace QuantConnect.Genetic;

public enum StrategyFamily
{
    TrendFollowing,
    MeanReversion
}

public enum StrategyTimeframe
{
    Minute,
    Hour,
    Daily
}

public enum StrategyPopulation
{
    Growth,
    Consistency,
    RandomBaseline
}

/// <summary>
/// Compact, serializable genome for a long-only moving-average strategy.
/// Thresholds are stored in tenths to keep mutation deterministic and bounded.
/// </summary>
public sealed record StrategyGenome(
    StrategyFamily Family,
    StrategyTimeframe Timeframe,
    int FastPeriod,
    int SlowPeriod,
    int EntryThresholdTenths,
    int ExitThresholdTenths,
    int MaxPositions,
    int MaxExposurePercent)
{
    public const int MinFastPeriod = 3;
    public const int MaxFastPeriod = 30;
    public const int MinSlowPeriod = 15;
    public const int MaxSlowPeriod = 180;
    public const int MinEntryThresholdTenths = 6;
    public const int MaxEntryThresholdTenths = 30;
    public const int MinMaxPositions = 1;
    public const int MaxMaxPositions = 5;
    public const int MinExposurePct = 20;
    public const int MaxExposurePct = 80;

    public double EntryThreshold => EntryThresholdTenths / 10d;
    public double ExitThreshold => ExitThresholdTenths / 10d;

    public StrategyGenome Normalize(IReadOnlyList<StrategyTimeframe> allowedTimeframes)
    {
        ArgumentNullException.ThrowIfNull(allowedTimeframes);
        if (allowedTimeframes.Count == 0)
        {
            throw new ArgumentException("At least one timeframe must be allowed.", nameof(allowedTimeframes));
        }

        var fast = Math.Clamp(FastPeriod, MinFastPeriod, MaxFastPeriod);
        var slow = Math.Clamp(SlowPeriod, MinSlowPeriod, MaxSlowPeriod);
        if (slow <= fast)
        {
            slow = Math.Min(MaxSlowPeriod, Math.Max(MinSlowPeriod, fast + 2));
            if (slow <= fast)
            {
                fast = slow - 2;
            }
        }

        var entry = Math.Clamp(EntryThresholdTenths, MinEntryThresholdTenths, MaxEntryThresholdTenths);
        var exit = Math.Clamp(ExitThresholdTenths, 1, entry - 1);
        var timeframe = allowedTimeframes.Contains(Timeframe) ? Timeframe : allowedTimeframes[0];

        return this with
        {
            Timeframe = timeframe,
            FastPeriod = fast,
            SlowPeriod = slow,
            EntryThresholdTenths = entry,
            ExitThresholdTenths = exit,
            MaxPositions = Math.Clamp(MaxPositions, MinMaxPositions, MaxMaxPositions),
            MaxExposurePercent = Math.Clamp(MaxExposurePercent, MinExposurePct, MaxExposurePct)
        };
    }
}

public sealed record StrategyDiscoveryOptions
{
    public int PopulationSize { get; init; } = 8;
    public int GenerationCount { get; init; } = 3;
    public int RandomBaselineSize { get; init; } = 4;
    public int EliteCount { get; init; } = 2;
    public int TournamentSize { get; init; } = 3;
    public double MutationRate { get; init; } = 0.15;
    public int Seed { get; init; } = 1729;
    public int MinimumTrades { get; init; } = 5;
    public double MaximumDrawdown { get; init; } = 0.20;
    public IReadOnlyList<StrategyTimeframe> AllowedTimeframes { get; init; } = [StrategyTimeframe.Daily, StrategyTimeframe.Hour];

    public void Validate()
    {
        if (PopulationSize < 2) throw new ArgumentOutOfRangeException(nameof(PopulationSize), "PopulationSize must be at least 2.");
        if (GenerationCount < 1) throw new ArgumentOutOfRangeException(nameof(GenerationCount), "GenerationCount must be positive.");
        if (EliteCount < 0 || EliteCount >= PopulationSize) throw new ArgumentOutOfRangeException(nameof(EliteCount), "EliteCount must be non-negative and smaller than PopulationSize.");
        if (TournamentSize < 1) throw new ArgumentOutOfRangeException(nameof(TournamentSize), "TournamentSize must be positive.");
        if (MutationRate is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(MutationRate), "MutationRate must be between zero and one.");
        if (RandomBaselineSize < 0) throw new ArgumentOutOfRangeException(nameof(RandomBaselineSize), "RandomBaselineSize cannot be negative.");
        if (MinimumTrades < 0) throw new ArgumentOutOfRangeException(nameof(MinimumTrades), "MinimumTrades cannot be negative.");
        if (!double.IsFinite(MaximumDrawdown) || MaximumDrawdown <= 0 || MaximumDrawdown >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDrawdown), "MaximumDrawdown must be between zero and one.");
        }
        if (AllowedTimeframes is null || AllowedTimeframes.Count == 0)
        {
            throw new ArgumentException("At least one timeframe must be allowed.", nameof(AllowedTimeframes));
        }
    }

    public int EstimateBacktests() => checked((GenerationCount * PopulationSize * 2) + (GenerationCount * RandomBaselineSize));
}

public sealed record StrategyBacktestMetrics(
    double AnnualReturn,
    double SharpeRatio,
    double Drawdown,
    double WinRate,
    int TradeCount,
    double TotalReturn,
    IReadOnlyList<double>? TradeReturns = null);

public sealed record ScoredGenome(
    int CandidateId,
    int Generation,
    StrategyPopulation Population,
    StrategyGenome Genome,
    double Fitness,
    StrategyBacktestMetrics? Metrics);

public sealed record DiscoveryCandidate(
    int Id,
    int Generation,
    StrategyPopulation Population,
    StrategyGenome Genome);
