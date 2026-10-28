using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Configuration;
using QuantConnect.Genetic;
using QuantConnect.Logging;
using QuantConnect.Optimizer.Objectives;
using QuantConnect.Optimizer.Parameters;

namespace QuantConnect.Optimizer.Strategies;

/// <summary>
/// Evolves strategy genomes and delegates each evaluation to LEAN's existing isolated backtest runner.
/// Growth and consistency populations advance in batches; a random population is retained as a baseline.
/// </summary>
public sealed class GeneticSearchOptimizationStrategy : IOptimizationStrategy
{
    private static readonly object HistoryFileLock = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, PendingCandidate> _candidates = new();
    private readonly HashSet<int> _validationPending = new();
    private readonly List<ValidationRecord> _validationRecords = new();
    private readonly Dictionary<int, ValidationGroup> _validationGroups = new();
    private GeneticSearchEngine _engine;
    private GeneticSearchOptimizationStrategySettings _settings;
    private Target _target;
    private IReadOnlyList<Constraint> _constraints = Array.Empty<Constraint>();
    private string _historyFile;
    private bool _initialized;
    private double _solutionFitness = GeneticSearchEngine.InvalidFitness;

    public event EventHandler<ParameterSet> NewParameterSet;
    public OptimizationResult Solution { get; private set; }

    public void Initialize(
        Target target,
        IReadOnlyList<Constraint> constraints,
        HashSet<OptimizationParameter> parameters,
        OptimizationStrategySettings settings)
    {
        if (_initialized) throw new InvalidOperationException("GeneticSearchOptimizationStrategy cannot be reinitialized.");
        if (target is null || string.IsNullOrWhiteSpace(target.Target)) throw new ArgumentException("An optimization target is required.", nameof(target));
        if (parameters is null || parameters.Count == 0) throw new ArgumentException("LEAN requires at least one optimization parameter.", nameof(parameters));

        _settings = settings as GeneticSearchOptimizationStrategySettings
            ?? throw new ArgumentException($"Settings must be {nameof(GeneticSearchOptimizationStrategySettings)}.", nameof(settings));
        _engine = new GeneticSearchEngine(_settings.ToDiscoveryOptions());
        _target = target;
        _constraints = constraints ?? Array.Empty<Constraint>();
        var destination = Config.Get("results-destination-folder", string.Empty);
        _historyFile = string.IsNullOrWhiteSpace(destination) ? null : Path.Combine(destination, "genetic-search-history.jsonl");
        _initialized = true;
    }

    public void PushNewResults(OptimizationResult result)
    {
        if (!_initialized) throw new InvalidOperationException("Initialize the genetic search before submitting backtest results.");
        lock (_gate)
        {
            if (ReferenceEquals(result, OptimizationResult.Initial))
            {
                Publish(_engine.Start(), EvaluationPhase.Training);
                return;
            }

            if (result?.ParameterSet is null || !_candidates.Remove(result.ParameterSet.Id, out var candidate))
            {
                throw new InvalidOperationException("The LEAN optimizer returned a parameter set that is not pending in the genetic search.");
            }

            var metrics = ParseMetrics(result.JsonBacktestResult);
            Persist(candidate.Candidate, candidate.Phase, result, metrics);

            if (candidate.Phase == EvaluationPhase.Training)
            {
                Publish(_engine.Submit(candidate.Candidate.Id, metrics), EvaluationPhase.Training);
                if (_engine.IsComplete)
                {
                    PublishValidationBatch();
                    if (_validationPending.Count == 0) PersistRankings();
                }
                return;
            }

            _validationPending.Remove(candidate.Candidate.Id);
            var group = _validationGroups[candidate.ValidationGroupId];
            group.Folds.Add(new ValidationFold(
                candidate.Candidate.Genome,
                candidate.ValidationWindow,
                metrics,
                result,
                metrics is null
                    ? GeneticSearchEngine.InvalidFitness
                    : GeneticSearchEngine.Score(metrics, candidate.SourcePopulation, _settings.MinimumTrades, _settings.MaximumDrawdown)));

            if (group.Folds.Count == group.ExpectedFoldCount)
            {
                var orderedFolds = group.Folds.OrderBy(fold => fold.Genome == group.Genome ? -1 : fold.Genome.FastPeriod)
                    .ThenBy(fold => fold.Window.Index).ToArray();
                var centralFolds = orderedFolds.Where(fold => fold.Genome == group.Genome).OrderBy(fold => fold.Window.Index).ToArray();
                var validationFitness = AggregateWalkForwardFitness(centralFolds.Select(fold => fold.Fitness).ToArray());
                var probeFitness = group.ProbeGenomes
                    .Select(genome => AggregateWalkForwardFitness(orderedFolds.Where(fold => fold.Genome == genome).Select(fold => fold.Fitness).ToArray()))
                    .ToArray();
                var validProbeFitness = probeFitness.Where(score => score > GeneticSearchEngine.InvalidFitness).OrderBy(score => score).ToArray();
                var medianProbeFitness = validProbeFitness.Length == 0 ? (double?)null : Median(validProbeFitness);
                var robustnessAdjustedFitness = group.ProbeGenomes.Count == 0
                    ? validationFitness
                    : validProbeFitness.Length != group.ProbeGenomes.Count || medianProbeFitness is null
                        ? GeneticSearchEngine.InvalidFitness
                        : Math.Min(validationFitness, medianProbeFitness.Value);
                var robustness = new ParameterRobustnessRecord(
                    group.ProbeGenomes.Count,
                    validProbeFitness.Length,
                    medianProbeFitness,
                    validProbeFitness.Length == 0 ? null : validProbeFitness[0],
                    medianProbeFitness is null ? null : medianProbeFitness.Value - validationFitness);
                var completeAndConstrained = centralFolds.Length == group.ExpectedWindowCount && centralFolds.All(fold =>
                    fold.Metrics is not null && _constraints.All(constraint => constraint.IsMet(fold.Result.JsonBacktestResult)));
                var latestResult = centralFolds[^1].Result;
                _validationRecords.Add(new ValidationRecord(
                    group.Population,
                    group.Genome,
                    group.TrainingFitness,
                    validationFitness,
                    robustnessAdjustedFitness,
                    group.TrainingMetrics,
                    robustness,
                    orderedFolds.Select(fold => new ValidationWindowRecord(fold.Genome, fold.Window, fold.Metrics, fold.Fitness, fold.Result.BacktestId)).ToArray()));

                if (completeAndConstrained && robustnessAdjustedFitness > _solutionFitness && _target.MoveAhead(latestResult.JsonBacktestResult))
                {
                    Solution = latestResult;
                    _solutionFitness = robustnessAdjustedFitness;
                    _target.CheckCompliance();
                }
            }
            if (_validationPending.Count == 0) PersistRankings();
        }
    }

    public int GetTotalBacktestEstimate() => _settings is null
        ? 0
        : checked(_settings.ToDiscoveryOptions().EstimateBacktests() +
            ((_settings.ValidationFinalistCount * 2 * (1 + _settings.ParameterRobustnessProbeCount)) +
             (_settings.ValidationBaselineCount == 0 ? 0 : Math.Min(_settings.ValidationBaselineCount, _settings.RandomBaselineSize))) *
            _settings.ValidationWindowCount);

    private void Publish(IReadOnlyList<DiscoveryCandidate> candidates, EvaluationPhase phase)
    {
        foreach (var candidate in candidates)
        {
            var source = phase == EvaluationPhase.Training
                ? null
                : _engine.Results.First(item => item.CandidateId == candidate.Id);
            var pending = new PendingCandidate(candidate, phase, candidate.Population, source?.Fitness ?? GeneticSearchEngine.InvalidFitness, source?.Metrics, 0, null);
            _candidates.Add(candidate.Id, pending);
            if (phase == EvaluationPhase.Validation) _validationPending.Add(candidate.Id);
            var genome = candidate.Genome;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["discovery.population"] = candidate.Population.ToString(),
                ["discovery.phase"] = phase.ToString(),
                ["discovery.generation"] = candidate.Generation.ToString(CultureInfo.InvariantCulture),
                ["discovery.family"] = genome.Family.ToString(),
                ["discovery.timeframe"] = genome.Timeframe.ToString(),
                ["discovery.confirmation-timeframe"] = genome.ConfirmationTimeframe?.ToString() ?? "None",
                ["discovery.fast-period"] = genome.FastPeriod.ToString(CultureInfo.InvariantCulture),
                ["discovery.slow-period"] = genome.SlowPeriod.ToString(CultureInfo.InvariantCulture),
                ["discovery.entry-tenths"] = genome.EntryThresholdTenths.ToString(CultureInfo.InvariantCulture),
                ["discovery.exit-tenths"] = genome.ExitThresholdTenths.ToString(CultureInfo.InvariantCulture),
                ["discovery.max-positions"] = genome.MaxPositions.ToString(CultureInfo.InvariantCulture),
                ["discovery.max-exposure-percent"] = genome.MaxExposurePercent.ToString(CultureInfo.InvariantCulture),
                ["discovery.minimum-spread-bps"] = _settings.MinimumSpreadBasisPoints.ToString(CultureInfo.InvariantCulture),
                ["discovery.symbols"] = _settings.SymbolsForParameterSet,
                ["discovery.universe-mode"] = _settings.UniverseMode,
                ["discovery.universe-size"] = _settings.UniverseSize.ToString(CultureInfo.InvariantCulture),
                ["discovery.max-correlation"] = _settings.MaximumCorrelation.ToString("R", CultureInfo.InvariantCulture),
                ["discovery.max-drawdown"] = _settings.MaximumPortfolioDrawdown.ToString("R", CultureInfo.InvariantCulture),
                ["discovery.train-start"] = _settings.TrainingStartDate,
                ["discovery.train-end"] = _settings.TrainingEndDate,
                ["discovery.validation-start"] = _settings.ValidationStartDate,
                ["discovery.validation-end"] = _settings.ValidationEndDate,
                ["discovery.validation-window-count"] = _settings.ValidationWindowCount.ToString(CultureInfo.InvariantCulture)
            };
            NewParameterSet?.Invoke(this, new ParameterSet(candidate.Id, values));
        }
    }

    private void PublishValidationBatch()
    {
        var finalists = new List<ScoredGenome>();
        var results = _engine.Results.Where(result => result.Fitness > GeneticSearchEngine.InvalidFitness).ToArray();
        var nextId = results.Select(result => result.CandidateId).DefaultIfEmpty(0).Max() + 1;
        var windows = _settings.GetValidationWindows();

        foreach (var population in new[] { StrategyPopulation.Growth, StrategyPopulation.Consistency })
        {
            finalists.AddRange(results.Where(result => result.Population == population)
                         .OrderByDescending(result => result.Fitness)
                         .ThenBy(result => result.CandidateId)
                         .Take(_settings.ValidationFinalistCount));
        }

        finalists.AddRange(results.Where(result => result.Population == StrategyPopulation.RandomBaseline)
                     .OrderByDescending(result => result.Fitness)
                     .ThenBy(result => result.CandidateId)
                     .Take(Math.Min(_settings.ValidationBaselineCount, _settings.RandomBaselineSize)));

        foreach (var finalist in finalists)
        {
            var probeGenomes = finalist.Population == StrategyPopulation.RandomBaseline
                ? Array.Empty<StrategyGenome>()
                : finalist.Genome.CreateParameterNeighborhood(
                    _settings.ParameterRobustnessProbeCount,
                    _settings.ToDiscoveryOptions().AllowedTimeframes);
            var variants = new[] { finalist.Genome }.Concat(probeGenomes).ToArray();
            var groupId = nextId++;
            var group = new ValidationGroup(finalist.Population, finalist.Genome, finalist.Fitness, finalist.Metrics, probeGenomes, windows.Count);
            _validationGroups.Add(groupId, group);
            foreach (var variant in variants)
            {
                foreach (var window in windows)
                {
                    var candidate = new DiscoveryCandidate(nextId++, finalist.Generation, finalist.Population, variant);
                    _candidates.Add(candidate.Id, new PendingCandidate(
                        candidate,
                        EvaluationPhase.Validation,
                        finalist.Population,
                        finalist.Fitness,
                        finalist.Metrics,
                        groupId,
                        window));
                    _validationPending.Add(candidate.Id);
                    PublishOne(candidate, EvaluationPhase.Validation, window);
                }
            }
        }
    }

    private void PublishOne(DiscoveryCandidate candidate, EvaluationPhase phase, WalkForwardWindow? window = null)
    {
        var genome = candidate.Genome;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["discovery.population"] = candidate.Population.ToString(),
            ["discovery.phase"] = phase.ToString(),
            ["discovery.generation"] = candidate.Generation.ToString(CultureInfo.InvariantCulture),
            ["discovery.family"] = genome.Family.ToString(),
            ["discovery.timeframe"] = genome.Timeframe.ToString(),
            ["discovery.confirmation-timeframe"] = genome.ConfirmationTimeframe?.ToString() ?? "None",
            ["discovery.fast-period"] = genome.FastPeriod.ToString(CultureInfo.InvariantCulture),
            ["discovery.slow-period"] = genome.SlowPeriod.ToString(CultureInfo.InvariantCulture),
            ["discovery.entry-tenths"] = genome.EntryThresholdTenths.ToString(CultureInfo.InvariantCulture),
            ["discovery.exit-tenths"] = genome.ExitThresholdTenths.ToString(CultureInfo.InvariantCulture),
            ["discovery.max-positions"] = genome.MaxPositions.ToString(CultureInfo.InvariantCulture),
            ["discovery.max-exposure-percent"] = genome.MaxExposurePercent.ToString(CultureInfo.InvariantCulture),
            ["discovery.minimum-spread-bps"] = _settings.MinimumSpreadBasisPoints.ToString(CultureInfo.InvariantCulture),
            ["discovery.symbols"] = _settings.SymbolsForParameterSet,
            ["discovery.universe-mode"] = _settings.UniverseMode,
            ["discovery.universe-size"] = _settings.UniverseSize.ToString(CultureInfo.InvariantCulture),
            ["discovery.max-correlation"] = _settings.MaximumCorrelation.ToString("R", CultureInfo.InvariantCulture),
            ["discovery.max-drawdown"] = _settings.MaximumPortfolioDrawdown.ToString("R", CultureInfo.InvariantCulture),
            ["discovery.train-start"] = _settings.TrainingStartDate,
            ["discovery.train-end"] = _settings.TrainingEndDate,
            ["discovery.validation-start"] = window?.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? _settings.ValidationStartDate,
            ["discovery.validation-end"] = window?.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? _settings.ValidationEndDate,
            ["discovery.validation-window-index"] = window?.Index.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["discovery.validation-window-count"] = _settings.ValidationWindowCount.ToString(CultureInfo.InvariantCulture)
        };
        NewParameterSet?.Invoke(this, new ParameterSet(candidate.Id, values));
    }

    private static double AggregateWalkForwardFitness(IReadOnlyList<double> foldFitness)
    {
        if (foldFitness.Count == 0 || foldFitness.Any(score => score <= GeneticSearchEngine.InvalidFitness)) return GeneticSearchEngine.InvalidFitness;
        var ordered = foldFitness.OrderBy(score => score).ToArray();
        return ordered[(ordered.Length - 1) / 2];
    }

    private static StrategyBacktestMetrics ParseMetrics(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var root = JObject.Parse(json);
            var statistics = GetToken(root, "Statistics") as JObject;
            if (statistics is null) return null;

            var annualReturn = ReadNumber(GetToken(statistics, "Compounding Annual Return"), percentage: true);
            var sharpe = ReadNumber(GetToken(statistics, "Sharpe Ratio"));
            var drawdown = ReadNumber(GetToken(statistics, "Drawdown"), percentage: true);
            var winRate = ReadNumber(GetToken(statistics, "Win Rate"), percentage: true);
            var tradeStatistics = GetToken(GetToken(root, "totalPerformance") as JObject, "tradeStatistics") as JObject;
            var tradesValue = ReadNumber(GetToken(tradeStatistics, "totalNumberOfTrades") ?? GetToken(statistics, "Total Trades"));
            var totalReturn = ReadNumber(GetToken(statistics, "Net Profit"), percentage: true);
            if (annualReturn is null || sharpe is null || drawdown is null || winRate is null || tradesValue is null || totalReturn is null)
            {
                return null;
            }

            var closedTrades = GetToken(GetToken(root, "totalPerformance") as JObject, "closedTrades") as JArray;
            var tradeReturns = closedTrades?
                .OfType<JObject>()
                .Select(trade =>
                {
                    var profitLoss = ReadNumber(GetToken(trade, "profitLoss"));
                    var entryPrice = ReadNumber(GetToken(trade, "entryPrice"));
                    var quantity = ReadNumber(GetToken(trade, "quantity"));
                    return profitLoss is not null && entryPrice > 0 && quantity > 0
                        ? profitLoss.Value / (entryPrice.Value * Math.Abs(quantity.Value))
                        : (double?)null;
                })
                .Where(value => value.HasValue && double.IsFinite(value.Value) && value.Value > -1d)
                .Select(value => value!.Value)
                .ToArray();

            return new StrategyBacktestMetrics(
                annualReturn.Value,
                sharpe.Value,
                drawdown.Value,
                winRate.Value,
                (int)tradesValue.Value,
                totalReturn.Value,
                tradeReturns);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static JToken GetToken(JObject source, string name) =>
        source?.GetValue(name, StringComparison.OrdinalIgnoreCase);

    private static double? ReadNumber(JToken token, bool percentage = false)
    {
        if (token is null) return null;
        var text = token.ToString().Trim();
        var hasPercent = text.EndsWith('%');
        text = text.TrimEnd('%').Replace("$", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
        if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) return null;
        return percentage && hasPercent ? value / 100d : value;
    }

    private void Persist(DiscoveryCandidate candidate, EvaluationPhase phase, OptimizationResult result, StrategyBacktestMetrics metrics)
    {
        if (string.IsNullOrWhiteSpace(_historyFile)) return;
        var row = new
        {
            utc = DateTime.UtcNow,
            candidate.Id,
            candidate.Generation,
            candidate.Population,
            phase,
            candidate.Genome,
            BacktestId = result.BacktestId,
            Metrics = metrics,
            MonteCarlo = metrics?.TradeReturns is { Count: > 0 }
                ? MonteCarloAnalyzer.Analyze(metrics.TradeReturns, _settings.MonteCarloSimulationCount, _settings.Seed ^ candidate.Id)
                : null,
            TradeOrderShuffle = metrics?.TradeReturns is { Count: > 0 }
                ? TradeOrderShuffleAnalyzer.Analyze(
                    metrics.TradeReturns,
                    _settings.MonteCarloSimulationCount,
                    _settings.Seed ^ candidate.Id ^ 0x5F356495)
                : null,
            Fitness = metrics is null ? GeneticSearchEngine.InvalidFitness : GeneticSearchEngine.Score(metrics, candidate.Population, _settings.MinimumTrades, _settings.MaximumDrawdown)
        };
        try
        {
            lock (HistoryFileLock)
            {
                File.AppendAllText(_historyFile, JsonConvert.SerializeObject(row) + Environment.NewLine);
            }
        }
        catch (IOException exception)
        {
            Log.Error(exception, "Unable to append genetic-search history; the backtest result remains available in LEAN's result files.");
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.Error(exception, "Unable to write genetic-search history; the backtest result remains available in LEAN's result files.");
        }
    }

    private void PersistRankings()
    {
        if (string.IsNullOrWhiteSpace(_historyFile)) return;
        var path = Path.Combine(Path.GetDirectoryName(_historyFile), "genetic-search-rankings.json");
        var validation = _validationRecords.OrderByDescending(item => item.RobustnessAdjustedFitness).ToArray();
        var randomBaselineScores = validation
            .Where(item => item.Population == StrategyPopulation.RandomBaseline && item.ValidationFitness > GeneticSearchEngine.InvalidFitness)
            .Select(item => item.ValidationFitness)
            .OrderBy(value => value)
            .ToArray();
        var randomBaselineMedian = randomBaselineScores.Length == 0 ? (double?)null : Median(randomBaselineScores);
        var evolvedFinalists = validation
            .Where(item => item.Population != StrategyPopulation.RandomBaseline && item.RobustnessAdjustedFitness > GeneticSearchEngine.InvalidFitness)
            .ToArray();
        var randomizedBaselineComparisons = randomBaselineScores.Length == 0
            ? Array.Empty<object>()
            : evolvedFinalists
                .Select(item => (object)new
                {
                    item.Population,
                    item.Genome,
                    item.TrainingFitness,
                    item.ValidationFitness,
                    item.RobustnessAdjustedFitness,
                    Comparison = RandomizedBaselineAnalyzer.Compare(item.RobustnessAdjustedFitness, randomBaselineScores, evolvedFinalists.Length)
                })
                .ToArray();
        var rankings = new
        {
            training = _engine.Results
                .GroupBy(result => result.Population)
                .ToDictionary(group => group.Key.ToString(), group => group.OrderByDescending(item => item.Fitness).ThenBy(item => item.CandidateId).ToArray()),
            validation,
            randomizedBaselineComparison = new
            {
                baselineValidationCount = randomBaselineScores.Length,
                medianBaselineFitness = randomBaselineMedian,
                baselineCandidatesSelectedByTrainingFitness = true,
                minimumBaselineCountForFivePercentResolution = RandomizedBaselineAnalyzer.MinimumBaselineCountForFivePercentResolution,
                familyWiseCorrection = "Bonferroni across evolved validation finalists; does not correct training-stage search or randomized-baseline selection",
                comparisonFamilySize = evolvedFinalists.Length,
                evolvedStrategiesAboveBaselineMedian = randomBaselineMedian is null
                    ? 0
                    : validation.Count(item => item.Population != StrategyPopulation.RandomBaseline && item.RobustnessAdjustedFitness > randomBaselineMedian.Value),
                evolvedStrategyComparisons = randomizedBaselineComparisons
            }
        };
        try
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(rankings, Formatting.Indented));
        }
        catch (IOException exception)
        {
            Log.Error(exception, "Unable to write genetic-search rankings; the backtest result remains available in LEAN's result files.");
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.Error(exception, "Unable to write genetic-search rankings; the backtest result remains available in LEAN's result files.");
        }
    }

    private static double Median(IReadOnlyList<double> sortedValues)
    {
        var middle = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[middle - 1] + sortedValues[middle]) / 2d
            : sortedValues[middle];
    }

    private enum EvaluationPhase
    {
        Training,
        Validation
    }

    private sealed record PendingCandidate(
        DiscoveryCandidate Candidate,
        EvaluationPhase Phase,
        StrategyPopulation SourcePopulation,
        double TrainingFitness,
        StrategyBacktestMetrics TrainingMetrics,
        int ValidationGroupId,
        WalkForwardWindow? ValidationWindow);

    private sealed record ValidationRecord(
        StrategyPopulation Population,
        StrategyGenome Genome,
        double TrainingFitness,
        double ValidationFitness,
        double RobustnessAdjustedFitness,
        StrategyBacktestMetrics TrainingMetrics,
        ParameterRobustnessRecord ParameterRobustness,
        IReadOnlyList<ValidationWindowRecord> Windows);

    private sealed record ParameterRobustnessRecord(
        int RequestedProbeCount,
        int ValidProbeCount,
        double? MedianNeighborFitness,
        double? WorstNeighborFitness,
        double? MedianFitnessDelta);

    private sealed record ValidationWindowRecord(
        StrategyGenome Genome,
        WalkForwardWindow Window,
        StrategyBacktestMetrics Metrics,
        double Fitness,
        string BacktestId);

    private sealed record ValidationFold(
        StrategyGenome Genome,
        WalkForwardWindow Window,
        StrategyBacktestMetrics Metrics,
        OptimizationResult Result,
        double Fitness);

    private sealed class ValidationGroup(
        StrategyPopulation population,
        StrategyGenome genome,
        double trainingFitness,
        StrategyBacktestMetrics trainingMetrics,
        IReadOnlyList<StrategyGenome> probeGenomes,
        int expectedWindowCount)
    {
        public StrategyPopulation Population { get; } = population;
        public StrategyGenome Genome { get; } = genome;
        public double TrainingFitness { get; } = trainingFitness;
        public StrategyBacktestMetrics TrainingMetrics { get; } = trainingMetrics;
        public IReadOnlyList<StrategyGenome> ProbeGenomes { get; } = probeGenomes;
        public int ExpectedWindowCount { get; } = expectedWindowCount;
        public int ExpectedFoldCount { get; } = (1 + probeGenomes.Count) * expectedWindowCount;
        public List<ValidationFold> Folds { get; } = new((1 + probeGenomes.Count) * expectedWindowCount);
    }
}


