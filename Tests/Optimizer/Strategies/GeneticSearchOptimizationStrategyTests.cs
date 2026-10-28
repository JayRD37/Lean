using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
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
    public void RunsGrowthConsistencyBaselineThenHeldOutValidationThroughLeanOptimizerContract()
    {
        var strategy = new GeneticSearchOptimizationStrategy();
        var pending = new Queue<ParameterSet>();
        strategy.NewParameterSet += (_, parameterSet) => pending.Enqueue(parameterSet);
        strategy.Initialize(
            new Target("Statistics.Sharpe Ratio", new Maximization(), null),
            Array.Empty<Constraint>(),
            new HashSet<OptimizationParameter> { new StaticOptimizationParameter("discovery.marker", "1") },
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
                AllowedTimeframes = "Daily"
            });

        strategy.PushNewResults(OptimizationResult.Initial);
        Assert.That(pending, Has.Count.EqualTo(5));
        Assert.That(strategy.GetTotalBacktestEstimate(), Is.EqualTo(8));

        for (var index = 0; index < 5; index++)
        {
            Assert.That(pending.TryDequeue(out var parameterSet), Is.True);
            strategy.PushNewResults(CreateResult(parameterSet!));
        }

        Assert.That(pending, Has.Count.EqualTo(3));
        Assert.That(pending.Select(item => item.Value["discovery.phase"]), Is.All.EqualTo("Validation"));
        while (pending.TryDequeue(out var parameterSet)) strategy.PushNewResults(CreateResult(parameterSet!));

        Assert.That(strategy.Solution, Is.Not.Null);
        Assert.That(strategy.Solution.ParameterSet.Value["discovery.phase"], Is.EqualTo("Validation"));
        Assert.That(strategy.Solution.ParameterSet.Value["discovery.train-end"], Is.EqualTo("2022-12-31"));
        Assert.That(strategy.Solution.ParameterSet.Value["discovery.validation-start"], Is.EqualTo("2023-01-01"));
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
            new HashSet<OptimizationParameter> { new StaticOptimizationParameter("discovery.marker", "1") },
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

        Assert.That(strategy.GetTotalBacktestEstimate(), Is.EqualTo(14));
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
        Assert.That(strategy.Solution, Is.Not.Null);
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
            new HashSet<OptimizationParameter> { new StaticOptimizationParameter("discovery.marker", "1") },
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

        Assert.That(strategy.GetTotalBacktestEstimate(), Is.EqualTo(12));
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
            new HashSet<OptimizationParameter> { new StaticOptimizationParameter("discovery.marker", "1") },
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

    private static OptimizationResult CreateResult(ParameterSet parameterSet)
    {
        var result = new
        {
            statistics = new Dictionary<string, string>
            {
                ["Compounding Annual Return"] = "12.0%",
                ["Sharpe Ratio"] = parameterSet.Value["discovery.fast-period"],
                ["Drawdown"] = "5.0%",
                ["Win Rate"] = "55.0%",
                ["Total Trades"] = "20",
                ["Net Profit"] = "18.0%"
            },
            totalPerformance = new
            {
                tradeStatistics = new { totalNumberOfTrades = 20 },
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
