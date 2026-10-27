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
    private GeneticSearchEngine _engine;
    private GeneticSearchOptimizationStrategySettings _settings;
    private Target _target;
    private IReadOnlyList<Constraint> _constraints = Array.Empty<Constraint>();
    private string _historyFile;
    private bool _initialized;

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
            var validationFitness = metrics is null
                ? GeneticSearchEngine.InvalidFitness
                : GeneticSearchEngine.Score(metrics, candidate.SourcePopulation, _settings.MinimumTrades, _settings.MaximumDrawdown);
            _validationRecords.Add(new ValidationRecord(
                candidate.SourcePopulation,
                candidate.Candidate.Genome,
                candidate.TrainingFitness,
                validationFitness,
                candidate.TrainingMetrics,
                metrics,
                result.BacktestId));

            if (metrics is not null && _constraints.All(constraint => constraint.IsMet(result.JsonBacktestResult)) && _target.MoveAhead(result.JsonBacktestResult))
            {
                Solution = result;
                _target.CheckCompliance();
            }
            if (_validationPending.Count == 0) PersistRankings();
        }
    }

    public int GetTotalBacktestEstimate() => _settings is null
        ? 0
        : checked(_settings.ToDiscoveryOptions().EstimateBacktests() +
            (_settings.ValidationFinalistCount * 2) +
            (_settings.ValidationBaselineCount == 0 ? 0 : Math.Min(_settings.ValidationBaselineCount, _settings.RandomBaselineSize)));

    private void Publish(IReadOnlyList<DiscoveryCandidate> candidates, EvaluationPhase phase)
    {
        foreach (var candidate in candidates)
        {
            var source = phase == EvaluationPhase.Training
                ? null
                : _engine.Results.First(item => item.CandidateId == candidate.Id);
            var pending = new PendingCandidate(candidate, phase, candidate.Population, source?.Fitness ?? GeneticSearchEngine.InvalidFitness, source?.Metrics);
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
                ["discovery.fast-period"] = genome.FastPeriod.ToString(CultureInfo.InvariantCulture),
                ["discovery.slow-period"] = genome.SlowPeriod.ToString(CultureInfo.InvariantCulture),
                ["discovery.entry-tenths"] = genome.EntryThresholdTenths.ToString(CultureInfo.InvariantCulture),
                ["discovery.exit-tenths"] = genome.ExitThresholdTenths.ToString(CultureInfo.InvariantCulture),
                ["discovery.max-positions"] = genome.MaxPositions.ToString(CultureInfo.InvariantCulture),
                ["discovery.max-exposure-percent"] = genome.MaxExposurePercent.ToString(CultureInfo.InvariantCulture),
                ["discovery.symbols"] = _settings.SymbolsForParameterSet,
                ["discovery.max-correlation"] = _settings.MaximumCorrelation.ToString("R", CultureInfo.InvariantCulture),
                ["discovery.max-drawdown"] = _settings.MaximumPortfolioDrawdown.ToString("R", CultureInfo.InvariantCulture),
                ["discovery.train-start"] = _settings.TrainingStartDate,
                ["discovery.train-end"] = _settings.TrainingEndDate,
                ["discovery.validation-start"] = _settings.ValidationStartDate,
                ["discovery.validation-end"] = _settings.ValidationEndDate
            };
            NewParameterSet?.Invoke(this, new ParameterSet(candidate.Id, values));
        }
    }

    private void PublishValidationBatch()
    {
        var candidates = new List<DiscoveryCandidate>();
        var results = _engine.Results.Where(result => result.Fitness > GeneticSearchEngine.InvalidFitness).ToArray();
        var nextId = results.Select(result => result.CandidateId).DefaultIfEmpty(0).Max() + 1;

        foreach (var population in new[] { StrategyPopulation.Growth, StrategyPopulation.Consistency })
        {
            foreach (var result in results.Where(result => result.Population == population)
                         .OrderByDescending(result => result.Fitness)
                         .ThenBy(result => result.CandidateId)
                         .Take(_settings.ValidationFinalistCount))
            {
                candidates.Add(new DiscoveryCandidate(nextId++, result.Generation, population, result.Genome));
            }
        }

        foreach (var result in results.Where(result => result.Population == StrategyPopulation.RandomBaseline)
                     .OrderByDescending(result => result.Fitness)
                     .ThenBy(result => result.CandidateId)
                     .Take(Math.Min(_settings.ValidationBaselineCount, _settings.RandomBaselineSize)))
        {
            candidates.Add(new DiscoveryCandidate(nextId++, result.Generation, StrategyPopulation.RandomBaseline, result.Genome));
        }

        var trainingByGenome = results
            .GroupBy(result => (result.Population, result.Genome))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Fitness).First());
        foreach (var candidate in candidates)
        {
            var training = trainingByGenome[(candidate.Population, candidate.Genome)];
            _candidates.Add(candidate.Id, new PendingCandidate(
                candidate,
                EvaluationPhase.Validation,
                candidate.Population,
                training.Fitness,
                training.Metrics));
            _validationPending.Add(candidate.Id);
            PublishOne(candidate, EvaluationPhase.Validation);
        }
    }

    private void PublishOne(DiscoveryCandidate candidate, EvaluationPhase phase)
    {
        var genome = candidate.Genome;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["discovery.population"] = candidate.Population.ToString(),
            ["discovery.phase"] = phase.ToString(),
            ["discovery.generation"] = candidate.Generation.ToString(CultureInfo.InvariantCulture),
            ["discovery.family"] = genome.Family.ToString(),
            ["discovery.timeframe"] = genome.Timeframe.ToString(),
            ["discovery.fast-period"] = genome.FastPeriod.ToString(CultureInfo.InvariantCulture),
            ["discovery.slow-period"] = genome.SlowPeriod.ToString(CultureInfo.InvariantCulture),
            ["discovery.entry-tenths"] = genome.EntryThresholdTenths.ToString(CultureInfo.InvariantCulture),
            ["discovery.exit-tenths"] = genome.ExitThresholdTenths.ToString(CultureInfo.InvariantCulture),
            ["discovery.max-positions"] = genome.MaxPositions.ToString(CultureInfo.InvariantCulture),
            ["discovery.max-exposure-percent"] = genome.MaxExposurePercent.ToString(CultureInfo.InvariantCulture),
            ["discovery.symbols"] = _settings.SymbolsForParameterSet,
            ["discovery.max-correlation"] = _settings.MaximumCorrelation.ToString("R", CultureInfo.InvariantCulture),
            ["discovery.max-drawdown"] = _settings.MaximumPortfolioDrawdown.ToString("R", CultureInfo.InvariantCulture),
            ["discovery.train-start"] = _settings.TrainingStartDate,
            ["discovery.train-end"] = _settings.TrainingEndDate,
            ["discovery.validation-start"] = _settings.ValidationStartDate,
            ["discovery.validation-end"] = _settings.ValidationEndDate
        };
        NewParameterSet?.Invoke(this, new ParameterSet(candidate.Id, values));
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

            return new StrategyBacktestMetrics(annualReturn.Value, sharpe.Value, drawdown.Value, winRate.Value, (int)tradesValue.Value, totalReturn.Value);
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
        var rankings = new
        {
            training = _engine.Results
                .GroupBy(result => result.Population)
                .ToDictionary(group => group.Key.ToString(), group => group.OrderByDescending(item => item.Fitness).ThenBy(item => item.CandidateId).ToArray()),
            validation = _validationRecords.OrderByDescending(item => item.ValidationFitness).ToArray()
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
        StrategyBacktestMetrics TrainingMetrics);

    private sealed record ValidationRecord(
        StrategyPopulation Population,
        StrategyGenome Genome,
        double TrainingFitness,
        double ValidationFitness,
        StrategyBacktestMetrics TrainingMetrics,
        StrategyBacktestMetrics ValidationMetrics,
        string BacktestId);
}
