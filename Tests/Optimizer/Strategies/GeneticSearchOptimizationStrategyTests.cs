using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.Genetic;
using QuantConnect.Optimizer;
using QuantConnect.Optimizer.Objectives;
using QuantConnect.Optimizer.Parameters;
using QuantConnect.Optimizer.Strategies;

namespace QuantConnect.Tests.Optimizer.Strategies;

[TestFixture]
public class GeneticSearchOptimizationStrategyTests
{
    [Test]
    public void SettingsValidatePointInTimeUniverseModeAndSize()
    {
        Assert.Throws<ArgumentException>(() => new GeneticSearchOptimizationStrategySettings
        {
            UniverseMode = "Unknown"
        }.ToDiscoveryOptions());
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeneticSearchOptimizationStrategySettings
        {
            UniverseSize = 101
        }.ToDiscoveryOptions());
        Assert.Throws<ArgumentException>(() => new GeneticSearchOptimizationStrategySettings
        {
            ConfirmationStartDate = "2024-12-31"
        }.ToDiscoveryOptions());
    }

    [Test]
    public void RunsGrowthConsistencyBaselineThenHeldOutValidationThroughLeanOptimizerContract()
    {
        var strategy = new GeneticSearchOptimizationStrategy();
        var pending = new Queue<ParameterSet>();
        strategy.NewParameterSet += (_, parameterSet) => pending.Enqueue(parameterSet);
        strategy.Initialize(
            new Target("Statistics.Sharpe Ratio", new Maximization(), null),
            Array.Empty<Constraint>(),
            new HashSet<OptimizationParameter>(),
            new GeneticSearchOptimizationStrategySettings
            {
                PopulationSize = 2,
                GenerationCount = 1,
                RandomBaselineSize = 1,
                EliteCount = 0,
                TournamentSize = 1,
                ValidationFinalistCount = 1,
                ValidationBaselineCount = 1,
                ValidationWindowCount = 1,
                ParameterRobustnessProbeCount = 0,
                UniverseMode = "TopDollarVolume",
                UniverseSize = 12,
                AllowedTimeframes = "Daily"
            });

        strategy.PushNewResults(OptimizationResult.Initial);
        Assert.That(pending, Has.Count.EqualTo(5));
        Assert.That(strategy.GetTotalBacktestEstimate(), Is.EqualTo(9));

        for (var index = 0; index < 5; index++)
        {
            Assert.That(pending.TryDequeue(out var parameterSet), Is.True);
            Assert.That(parameterSet!.Value["discovery.minimum-spread-bps"], Is.EqualTo("5"));
            Assert.That(parameterSet.Value["discovery.universe-mode"], Is.EqualTo("TopDollarVolume"));
            Assert.That(parameterSet.Value["discovery.universe-size"], Is.EqualTo("12"));
            strategy.PushNewResults(CreateResult(parameterSet!));
        }

        Assert.That(pending, Has.Count.EqualTo(3));
        Assert.That(pending.Select(item => item.Value["discovery.phase"]), Is.All.EqualTo("Validation"));
        for (var index = 0; index < 3; index++) strategy.PushNewResults(CreateResult(pending.Dequeue()));

        Assert.That(pending, Has.Count.EqualTo(1));
        Assert.That(pending.Peek().Value["discovery.phase"], Is.EqualTo("Confirmation"));
        Assert.That(pending.Peek().Value["discovery.validation-start"], Is.EqualTo("2025-01-01"));
        Assert.That(strategy.Solution, Is.Null);
        strategy.PushNewResults(CreateResult(pending.Dequeue()));

        Assert.That(strategy.Solution, Is.Not.Null);
        Assert.That(strategy.Solution.ParameterSet.Value["discovery.phase"], Is.EqualTo("Confirmation"));
        Assert.That(strategy.Solution.ParameterSet.Value["discovery.population"], Is.Not.EqualTo(nameof(StrategyPopulation.RandomBaseline)));
        Assert.That(strategy.Solution.ParameterSet.Value["discovery.train-end"], Is.EqualTo("2022-12-31"));
        Assert.That(strategy.Solution.ParameterSet.Value["discovery.confirmation-start"], Is.EqualTo("2025-01-01"));
        Assert.That(strategy.Solution.ParameterSet.Value["discovery.validation-start"], Is.EqualTo("2025-01-01"));
    }

    [Test]
    public void WalkForwardValidationSplitsTheHoldoutAndWaitsForAllWindowsPerFinalist()
    {
        var strategy = new GeneticSearchOptimizationStrategy();
        var pending = new Queue<ParameterSet>();
        strategy.NewParameterSet += (_, parameterSet) => pending.Enqueue(parameterSet);
        strategy.Initialize(
            new Target("Statistics.Sharpe Ratio", new Maximization(), null),
            Array.Empty<Constraint>(),
            new HashSet<OptimizationParameter>(),
            new GeneticSearchOptimizationStrategySettings
            {
                PopulationSize = 2,
                GenerationCount = 1,
                RandomBaselineSize = 1,
                EliteCount = 0,
                TournamentSize = 1,
                ValidationFinalistCount = 1,
                ValidationBaselineCount = 1,
                ValidationWindowCount = 3,
                ParameterRobustnessProbeCount = 0,
                AllowedTimeframes = "Daily"
            });

        strategy.PushNewResults(OptimizationResult.Initial);
        for (var index = 0; index < 5; index++)
        {
            Assert.That(pending.TryDequeue(out var training), Is.True);
            strategy.PushNewResults(CreateResult(training!));
        }

        Assert.That(strategy.GetTotalBacktestEstimate(), Is.EqualTo(15));
        Assert.That(pending, Has.Count.EqualTo(9));
        var firstFinalistWindows = new List<(string Start, string End)>();
        for (var index = 0; index < 3; index++)
        {
            Assert.That(pending.TryDequeue(out var parameterSet), Is.True);
            Assert.That(parameterSet!.Value["discovery.validation-window-index"], Is.EqualTo(index.ToString()));
            firstFinalistWindows.Add((parameterSet.Value["discovery.validation-start"], parameterSet.Value["discovery.validation-end"]));
            strategy.PushNewResults(CreateResult(parameterSet));
            if (index < 2) Assert.That(strategy.Solution, Is.Null);
        }

        Assert.That(firstFinalistWindows[0].Start, Is.EqualTo("2023-01-01"));
        Assert.That(DateOnly.Parse(firstFinalistWindows[0].End).AddDays(1), Is.EqualTo(DateOnly.Parse(firstFinalistWindows[1].Start)));
        Assert.That(DateOnly.Parse(firstFinalistWindows[1].End).AddDays(1), Is.EqualTo(DateOnly.Parse(firstFinalistWindows[2].Start)));
        Assert.That(firstFinalistWindows[^1].End, Is.EqualTo("2024-12-31"));
        Assert.That(strategy.Solution, Is.Null);
        for (var index = 0; index < 6; index++) strategy.PushNewResults(CreateResult(pending.Dequeue()));
        Assert.That(pending, Has.Count.EqualTo(1));
        strategy.PushNewResults(CreateResult(pending.Dequeue()));
        Assert.That(strategy.Solution, Is.Not.Null);
        Assert.That(strategy.Solution.ParameterSet.Value["discovery.phase"], Is.EqualTo("Confirmation"));
    }

    [Test]
    public void ParameterRobustnessProbesRunBeforeFinalistCanWin()
    {
        var strategy = new GeneticSearchOptimizationStrategy();
        var pending = new Queue<ParameterSet>();
        strategy.NewParameterSet += (_, parameterSet) => pending.Enqueue(parameterSet);
        strategy.Initialize(
            new Target("Statistics.Sharpe Ratio", new Maximization(), null),
            Array.Empty<Constraint>(),
            new HashSet<OptimizationParameter>(),
            new GeneticSearchOptimizationStrategySettings
            {
                PopulationSize = 2,
                GenerationCount = 1,
                RandomBaselineSize = 1,
                EliteCount = 0,
                TournamentSize = 1,
                ValidationFinalistCount = 1,
                ValidationBaselineCount = 1,
                ValidationWindowCount = 1,
                ParameterRobustnessProbeCount = 2,
                AllowedTimeframes = "Daily"
            });

        strategy.PushNewResults(OptimizationResult.Initial);
        for (var index = 0; index < 5; index++)
        {
            Assert.That(pending.TryDequeue(out var training), Is.True);
            strategy.PushNewResults(CreateResult(training!));
        }

        Assert.That(strategy.GetTotalBacktestEstimate(), Is.EqualTo(13));
        Assert.That(pending, Has.Count.EqualTo(7));
        var firstFinalist = new List<ParameterSet>();
        for (var index = 0; index < 3; index++)
        {
            Assert.That(pending.TryDequeue(out var candidate), Is.True);
            firstFinalist.Add(candidate!);
            strategy.PushNewResults(CreateResult(candidate!));
            if (index < 2) Assert.That(strategy.Solution, Is.Null);
        }
        Assert.That(firstFinalist.Select(candidate => candidate.Value["discovery.fast-period"]).Distinct().Count(), Is.GreaterThan(1));
        Assert.That(strategy.Solution, Is.Null);
        for (var index = 0; index < 4; index++) strategy.PushNewResults(CreateResult(pending.Dequeue()));
        Assert.That(pending, Has.Count.EqualTo(1));
        strategy.PushNewResults(CreateResult(pending.Dequeue()));
        Assert.That(strategy.Solution, Is.Not.Null);
    }

    [Test]
    public void MissingBacktestStatisticsAreRejectedWithoutBreakingTheOptimizationRun()
    {
        var strategy = new GeneticSearchOptimizationStrategy();
        var pending = new Queue<ParameterSet>();
        strategy.NewParameterSet += (_, parameterSet) => pending.Enqueue(parameterSet);
        strategy.Initialize(
            new Target("Statistics.Sharpe Ratio", new Maximization(), null),
            Array.Empty<Constraint>(),
            new HashSet<OptimizationParameter>(),
            new GeneticSearchOptimizationStrategySettings
            {
                PopulationSize = 2,
                GenerationCount = 1,
                RandomBaselineSize = 0,
                EliteCount = 0,
                TournamentSize = 1,
                ValidationFinalistCount = 1,
                ParameterRobustnessProbeCount = 0,
                AllowedTimeframes = "Daily"
            });

        strategy.PushNewResults(OptimizationResult.Initial);
        while (pending.TryDequeue(out var parameterSet))
        {
            strategy.PushNewResults(new OptimizationResult("{}", parameterSet!, Guid.NewGuid().ToString("N")));
        }

        Assert.That(strategy.Solution, Is.Null);
        Assert.That(pending, Is.Empty);
    }

    [Test]
    public void ConfirmationMustPassTradeCountGateBeforeItCanBecomeTheSolution()
    {
        var strategy = new GeneticSearchOptimizationStrategy();
        var pending = new Queue<ParameterSet>();
        strategy.NewParameterSet += (_, parameterSet) => pending.Enqueue(parameterSet);
        strategy.Initialize(
            new Target("Statistics.Sharpe Ratio", new Maximization(), null),
            Array.Empty<Constraint>(),
            new HashSet<OptimizationParameter>(),
            new GeneticSearchOptimizationStrategySettings
            {
                PopulationSize = 2,
                GenerationCount = 1,
                RandomBaselineSize = 0,
                EliteCount = 0,
                TournamentSize = 1,
                ValidationFinalistCount = 1,
                ValidationBaselineCount = 0,
                ValidationWindowCount = 1,
                ParameterRobustnessProbeCount = 0,
                AllowedTimeframes = "Daily"
            });

        strategy.PushNewResults(OptimizationResult.Initial);
        for (var index = 0; index < 4; index++) strategy.PushNewResults(CreateResult(pending.Dequeue()));
        for (var index = 0; index < 2; index++) strategy.PushNewResults(CreateResult(pending.Dequeue()));
        Assert.That(pending, Has.Count.EqualTo(1));
        Assert.That(pending.Peek().Value["discovery.phase"], Is.EqualTo("Confirmation"));

        strategy.PushNewResults(CreateResult(pending.Dequeue(), tradeCount: 0));

        Assert.That(strategy.Solution, Is.Null);
    }

    [Test]
    public void RankingsPersistHeldOutRandomizedBaselineComparisonAndItsLimits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LeanGeneticRankingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousDestination = Config.Get("results-destination-folder", string.Empty);
        Config.Set("results-destination-folder", directory);
        try
        {
            var strategy = new GeneticSearchOptimizationStrategy();
            var pending = new Queue<ParameterSet>();
            strategy.NewParameterSet += (_, parameterSet) => pending.Enqueue(parameterSet);
            strategy.Initialize(
                new Target("Statistics.Sharpe Ratio", new Maximization(), null),
                Array.Empty<Constraint>(),
                new HashSet<OptimizationParameter>(),
                new GeneticSearchOptimizationStrategySettings
                {
                    PopulationSize = 2,
                    GenerationCount = 1,
                    RandomBaselineSize = 2,
                    EliteCount = 0,
                    TournamentSize = 1,
                    ValidationFinalistCount = 1,
                    ValidationBaselineCount = 2,
                    ValidationWindowCount = 1,
                    ParameterRobustnessProbeCount = 0,
                    AllowedTimeframes = "Daily"
                });

            strategy.PushNewResults(OptimizationResult.Initial);
            while (pending.TryDequeue(out var training)) strategy.PushNewResults(CreateResult(training!));
            while (pending.TryDequeue(out var validation)) strategy.PushNewResults(CreateResult(validation!));

            var rankingsPath = Path.Combine(directory, "genetic-search-rankings.json");
            var rankings = JObject.Parse(File.ReadAllText(rankingsPath));
            var baseline = (JObject)rankings["randomizedBaselineComparison"]!;

            Assert.That(baseline["baselineValidationCount"]!.Value<int>(), Is.EqualTo(2));
            Assert.That(baseline["baselineCandidatesSelectedByTrainingFitness"]!.Value<bool>(), Is.True);
            Assert.That(baseline["minimumBaselineCountForFivePercentResolution"]!.Value<int>(), Is.EqualTo(19));
            Assert.That(baseline["comparisonFamilySize"]!.Value<int>(), Is.EqualTo(2));
            Assert.That(baseline["familyWiseCorrection"]!.Value<string>(), Does.Contain("Bonferroni"));
            Assert.That(((JArray)baseline["evolvedStrategyComparisons"]!).Count, Is.EqualTo(2));
            Assert.That(baseline["evolvedStrategyComparisons"]![0]!["Comparison"]!["EmpiricalOneSidedPValue"], Is.Not.Null);
            Assert.That(baseline["evolvedStrategyComparisons"]![0]!["Comparison"]!["BonferroniAdjustedPValue"], Is.Not.Null);
        }
        finally
        {
            Config.Set("results-destination-folder", previousDestination);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void ExecutionSpreadAssumptionRejectsValuesOutsideConfiguredBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeneticSearchOptimizationStrategySettings
        {
            MinimumSpreadBasisPoints = -0.1m
        }.ToDiscoveryOptions());
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeneticSearchOptimizationStrategySettings
        {
            MinimumSpreadBasisPoints = 501m
        }.ToDiscoveryOptions());
    }

    private static OptimizationResult CreateResult(ParameterSet parameterSet, int tradeCount = 20)
    {
        var result = new
        {
            statistics = new Dictionary<string, string>
            {
                ["Compounding Annual Return"] = "12.0%",
                ["Sharpe Ratio"] = parameterSet.Value["discovery.fast-period"],
                ["Drawdown"] = "5.0%",
                ["Win Rate"] = "55.0%",
                ["Total Trades"] = tradeCount.ToString(),
                ["Net Profit"] = "18.0%"
            },
            totalPerformance = new
            {
                tradeStatistics = new { totalNumberOfTrades = tradeCount },
                closedTrades = new[]
                {
                    new { profitLoss = 20d, entryPrice = 100d, quantity = 2d },
                    new { profitLoss = -10d, entryPrice = 100d, quantity = 1d },
                    new { profitLoss = 5d, entryPrice = 50d, quantity = 1d }
                }
            }
        };
        return new OptimizationResult(JsonConvert.SerializeObject(result), parameterSet, Guid.NewGuid().ToString("N"));
    }
}

