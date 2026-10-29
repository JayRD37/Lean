using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using QuantConnect.Genetic;

namespace QuantConnect.Optimizer.Strategies;

/// <summary>Settings for the LEAN optimizer's genetic strategy-discovery adapter.</summary>
public sealed class GeneticSearchOptimizationStrategySettings : OptimizationStrategySettings
{
    [JsonProperty("population-size")] public int PopulationSize { get; set; } = 8;
    [JsonProperty("generation-count")] public int GenerationCount { get; set; } = 3;
    [JsonProperty("random-baseline-size")] public int RandomBaselineSize { get; set; } = 4;
    [JsonProperty("elite-count")] public int EliteCount { get; set; } = 2;
    [JsonProperty("tournament-size")] public int TournamentSize { get; set; } = 3;
    [JsonProperty("mutation-rate")] public double MutationRate { get; set; } = 0.15;
    [JsonProperty("seed")] public int Seed { get; set; } = 1729;
    [JsonProperty("maximum-positions")] public int MaximumPositions { get; set; } = StrategyGenome.MaxMaxPositions;
    [JsonProperty("maximum-exposure-percent")] public int MaximumExposurePercent { get; set; } = StrategyGenome.MaxExposurePct;
    [JsonProperty("seed-genomes")] public List<StrategyGenome> SeedGenomes { get; set; } = new();
    [JsonProperty("minimum-spread-bps")] public decimal MinimumSpreadBasisPoints { get; set; } = 5m;
    [JsonProperty("minimum-trades")] public int MinimumTrades { get; set; } = 5;
    [JsonProperty("monte-carlo-simulation-count")] public int MonteCarloSimulationCount { get; set; } = 1000;
    [JsonProperty("maximum-drawdown")] public double MaximumDrawdown { get; set; } = 0.20;
    [JsonProperty("allowed-timeframes")] public string AllowedTimeframes { get; set; } = "Daily,Hour";
    [JsonProperty("enable-multi-timeframe-confirmation")] public bool EnableMultiTimeframeConfirmation { get; set; } = true;
    [JsonProperty("symbols")] public string Symbols { get; set; } = "SPY,QQQ,IWM";
    [JsonProperty("universe-mode")] public string UniverseMode { get; set; } = "Configured";
    [JsonProperty("universe-size")] public int UniverseSize { get; set; } = 20;
    [JsonProperty("maximum-correlation")] public double MaximumCorrelation { get; set; } = 0.85;
    [JsonProperty("maximum-portfolio-drawdown")] public double MaximumPortfolioDrawdown { get; set; } = 0.10;
    [JsonProperty("validation-finalist-count")] public int ValidationFinalistCount { get; set; } = 2;
    [JsonProperty("validation-baseline-count")] public int ValidationBaselineCount { get; set; } = 2;
    [JsonProperty("validation-window-count")] public int ValidationWindowCount { get; set; } = 3;
    [JsonProperty("parameter-robustness-probe-count")] public int ParameterRobustnessProbeCount { get; set; } = 4;
    [JsonProperty("training-start-date")] public string TrainingStartDate { get; set; } = "2018-01-01";
    [JsonProperty("training-end-date")] public string TrainingEndDate { get; set; } = "2022-12-31";
    [JsonProperty("validation-start-date")] public string ValidationStartDate { get; set; } = "2023-01-01";
    [JsonProperty("validation-end-date")] public string ValidationEndDate { get; set; } = "2024-12-31";
    [JsonProperty("confirmation-start-date")] public string ConfirmationStartDate { get; set; } = "2025-01-01";
    [JsonProperty("confirmation-end-date")] public string ConfirmationEndDate { get; set; } = "2025-12-31";

    public StrategyDiscoveryOptions ToDiscoveryOptions()
    {
        var timeframes = AllowedTimeframes
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Enum.TryParse<StrategyTimeframe>(value, true, out var parsed)
                ? parsed
                : throw new ArgumentException($"Unknown timeframe '{value}'. Allowed values are Minute, Hour, Daily."))
            .Distinct()
            .ToArray();

        var options = new StrategyDiscoveryOptions
        {
            PopulationSize = PopulationSize,
            GenerationCount = GenerationCount,
            RandomBaselineSize = RandomBaselineSize,
            EliteCount = EliteCount,
            TournamentSize = TournamentSize,
            MutationRate = MutationRate,
            Seed = Seed,
            MaximumPositions = MaximumPositions,
            MaximumExposurePercent = MaximumExposurePercent,
            SeedGenomes = SeedGenomes.ToArray(),
            MinimumTrades = MinimumTrades,
            MaximumDrawdown = MaximumDrawdown,
            AllowedTimeframes = timeframes,
            EnableMultiTimeframeConfirmation = EnableMultiTimeframeConfirmation
        };
        options.Validate();
        if (MonteCarloSimulationCount < 1) throw new ArgumentOutOfRangeException(nameof(MonteCarloSimulationCount), "MonteCarloSimulationCount must be positive.");
        if (MinimumSpreadBasisPoints is < 0 or > 500) throw new ArgumentOutOfRangeException(nameof(MinimumSpreadBasisPoints), "MinimumSpreadBasisPoints must be between zero and five hundred.");

        if (string.IsNullOrWhiteSpace(Symbols) || Symbols.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length == 0)
        {
            throw new ArgumentException("At least one US equity ticker must be configured.", nameof(Symbols));
        }
        if (!string.Equals(UniverseMode, "Configured", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(UniverseMode, "TopDollarVolume", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("UniverseMode must be Configured or TopDollarVolume.", nameof(UniverseMode));
        }
        if (UniverseSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(UniverseSize), "UniverseSize must be between one and one hundred.");
        if (!double.IsFinite(MaximumCorrelation) || MaximumCorrelation is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCorrelation), "MaximumCorrelation must be between zero and one.");
        }
        if (!double.IsFinite(MaximumPortfolioDrawdown) || MaximumPortfolioDrawdown is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPortfolioDrawdown), "MaximumPortfolioDrawdown must be between zero and one.");
        }
        if (ValidationFinalistCount < 1) throw new ArgumentOutOfRangeException(nameof(ValidationFinalistCount), "At least one evolved strategy must be validated.");
        if (ValidationBaselineCount < 0) throw new ArgumentOutOfRangeException(nameof(ValidationBaselineCount), "ValidationBaselineCount cannot be negative.");
        if (ValidationWindowCount < 1) throw new ArgumentOutOfRangeException(nameof(ValidationWindowCount), "At least one chronological validation window is required.");
        if (ParameterRobustnessProbeCount < 0 || ParameterRobustnessProbeCount > StrategyGenome.MaxParameterNeighborhoodSize)
        {
            throw new ArgumentOutOfRangeException(nameof(ParameterRobustnessProbeCount), $"ParameterRobustnessProbeCount must be between zero and {StrategyGenome.MaxParameterNeighborhoodSize}.");
        }
        var trainingStart = DateOnly.ParseExact(TrainingStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var trainingEnd = DateOnly.ParseExact(TrainingEndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var validationStart = DateOnly.ParseExact(ValidationStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var validationEnd = DateOnly.ParseExact(ValidationEndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var confirmationStart = DateOnly.ParseExact(ConfirmationStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var confirmationEnd = DateOnly.ParseExact(ConfirmationEndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (trainingStart >= trainingEnd || validationStart >= validationEnd || confirmationStart >= confirmationEnd ||
            trainingEnd >= validationStart || validationEnd >= confirmationStart)
        {
            throw new ArgumentException("Training, validation, and final confirmation dates must be ordered and disjoint.");
        }
        _ = WalkForwardWindow.Split(validationStart, validationEnd, ValidationWindowCount);

        return options;
    }

    public IReadOnlyList<WalkForwardWindow> GetValidationWindows() => WalkForwardWindow.Split(
        DateOnly.ParseExact(ValidationStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateOnly.ParseExact(ValidationEndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        ValidationWindowCount);

    public WalkForwardWindow GetConfirmationWindow() => new(
        0,
        DateOnly.ParseExact(ConfirmationStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateOnly.ParseExact(ConfirmationEndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    public string SymbolsForParameterSet => string.Join('|', Symbols
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(symbol => symbol.ToUpper(CultureInfo.InvariantCulture)));
}

