using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Genetic;

namespace QuantConnect.Tests.StrategyDiscovery;

[TestFixture]
public class GeneticSearchEngineTests
{
    private static StrategyDiscoveryOptions CreateOptions() => new()
    {
        PopulationSize = 6,
        GenerationCount = 3,
        RandomBaselineSize = 4,
        EliteCount = 2,
        TournamentSize = 3,
        MutationRate = 0.35,
        Seed = 481,
        MinimumTrades = 4,
        MaximumDrawdown = 0.25,
        AllowedTimeframes = [StrategyTimeframe.Daily, StrategyTimeframe.Hour]
    };

    [Test]
    public void SameSeedProducesSamePopulationsAndGenomeProgression()
    {
        var first = RunToCompletion(new GeneticSearchEngine(CreateOptions()));
        var second = RunToCompletion(new GeneticSearchEngine(CreateOptions()));

        Assert.That(first, Is.EqualTo(second));
        Assert.That(first.Select(item => item.Population).Distinct(), Is.EquivalentTo(new[]
        {
            StrategyPopulation.Growth,
            StrategyPopulation.Consistency,
            StrategyPopulation.RandomBaseline
        }));
        Assert.That(first.Select(item => item.Generation).Distinct(), Is.EquivalentTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void CandidateGenerationEvolvesOnlySlowerConfirmationTimeframes()
    {
        var options = CreateOptions() with
        {
            PopulationSize = 32,
            GenerationCount = 1,
            RandomBaselineSize = 0,
            EliteCount = 0,
            AllowedTimeframes = [StrategyTimeframe.Minute, StrategyTimeframe.Hour, StrategyTimeframe.Daily]
        };

        var candidates = new GeneticSearchEngine(options).Start();

        Assert.That(candidates.Any(candidate => candidate.Genome.ConfirmationTimeframe.HasValue), Is.True);
        Assert.That(candidates.All(candidate => candidate.Genome.ConfirmationTimeframe is null ||
            candidate.Genome.ConfirmationTimeframe > candidate.Genome.Timeframe), Is.True);
    }

    [Test]
    public void CandidateGenerationRespectsConfiguredPaperPositionAndExposureCaps()
    {
        var options = CreateOptions() with { MaximumPositions = 3, MaximumExposurePercent = 30 };
        var candidates = new GeneticSearchEngine(options).Start();

        Assert.That(candidates.All(candidate => candidate.Genome.MaxPositions <= 3), Is.True);
        Assert.That(candidates.All(candidate => candidate.Genome.MaxExposurePercent <= 30), Is.True);
    }

    [Test]
    public void SearchWaitsForEveryParallelEvaluationBeforeBreedingNextGeneration()
    {
        var engine = new GeneticSearchEngine(CreateOptions());
        var firstBatch = engine.Start();

        Assert.That(firstBatch, Has.Count.EqualTo(16));
        Assert.That(engine.Submit(firstBatch[0].Id, ValidMetrics()), Is.Empty);
        Assert.That(engine.Submit(firstBatch[1].Id, ValidMetrics()), Is.Empty);
        Assert.That(engine.IsComplete, Is.False);
    }

    [Test]
    public void InvalidAndOverdrawnBacktestsReceiveNoEvolutionaryFitness()
    {
        var engine = new GeneticSearchEngine(CreateOptions());
        var candidate = engine.Start()[0];

        engine.Submit(candidate.Id, new StrategyBacktestMetrics(0.10, 1.0, 0.40, 0.55, 20, 0.20));
        Assert.That(engine.Results.Single().Fitness, Is.EqualTo(GeneticSearchEngine.InvalidFitness));
    }

    [Test]
    public void GrowthAndConsistencyUseDifferentScoringObjectives()
    {
        var growthHeavy = new StrategyBacktestMetrics(0.35, 0.8, 0.10, 0.50, 50, 0.40);
        var smooth = new StrategyBacktestMetrics(0.12, 2.0, 0.04, 0.62, 50, 0.20);

        Assert.That(GeneticSearchEngine.Score(growthHeavy, StrategyPopulation.Growth), Is.GreaterThan(GeneticSearchEngine.Score(smooth, StrategyPopulation.Growth)));
        Assert.That(GeneticSearchEngine.Score(smooth, StrategyPopulation.Consistency), Is.GreaterThan(GeneticSearchEngine.Score(growthHeavy, StrategyPopulation.Consistency)));
    }

    [Test]
    public void MonteCarloBootstrapIsRepeatableAndReportsTailRisk()
    {
        var returns = new[] { 0.02, -0.03, 0.01, 0.04, -0.01 };

        var first = MonteCarloAnalyzer.Analyze(returns, 2000, 1234);
        var second = MonteCarloAnalyzer.Analyze(returns, 2000, 1234);

        Assert.That(first, Is.EqualTo(second));
        Assert.That(first.TradeCount, Is.EqualTo(returns.Length));
        Assert.That(first.SimulationCount, Is.EqualTo(2000));
        Assert.That(first.ProbabilityOfLoss, Is.InRange(0d, 1d));
        Assert.That(first.NinetyFifthPercentileDrawdown, Is.InRange(0d, 1d));
    }

    [Test]
    public void MonteCarloRejectsInvalidTradeReturns()
    {
        Assert.Throws<ArgumentException>(() => MonteCarloAnalyzer.Analyze([double.NaN], 100, 1));
        Assert.Throws<ArgumentException>(() => MonteCarloAnalyzer.Analyze([-1d], 100, 1));
    }

    [Test]
    public void RandomizedBaselineComparisonReportsEmpiricalTailProbabilityAndResolution()
    {
        var baseline = Enumerable.Range(1, 19).Select(index => index / 20d).ToArray();

        var comparison = RandomizedBaselineAnalyzer.Compare(1d, baseline);

        Assert.That(comparison.BaselineCount, Is.EqualTo(19));
        Assert.That(comparison.MedianBaselineFitness, Is.EqualTo(0.5d));
        Assert.That(comparison.BaselineScoresAtOrAboveCandidate, Is.Zero);
        Assert.That(comparison.ProbabilityCandidateBeatsBaseline, Is.EqualTo(1d));
        Assert.That(comparison.EmpiricalOneSidedPValue, Is.EqualTo(0.05d));
        Assert.That(comparison.HasResolutionForFivePercentThreshold, Is.True);
    }

    [Test]
    public void RandomizedBaselineComparisonHandlesTiesConservativelyAndFlagsSmallSamples()
    {
        var comparison = RandomizedBaselineAnalyzer.Compare(0.5d, [0.2d, 0.5d, 0.5d, 0.8d]);

        Assert.That(comparison.BaselineScoresAtOrAboveCandidate, Is.EqualTo(3));
        Assert.That(comparison.ProbabilityCandidateBeatsBaseline, Is.EqualTo(0.25d));
        Assert.That(comparison.EmpiricalOneSidedPValue, Is.EqualTo(0.8d));
        Assert.That(comparison.HasResolutionForFivePercentThreshold, Is.False);
    }

    [Test]
    public void RandomizedBaselineComparisonRejectsEmptyAndNonFiniteSamples()
    {
        Assert.Throws<ArgumentException>(() => RandomizedBaselineAnalyzer.Compare(0.5d, Array.Empty<double>()));
        Assert.Throws<ArgumentException>(() => RandomizedBaselineAnalyzer.Compare(0.5d, [0.2d, double.NaN]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RandomizedBaselineAnalyzer.Compare(double.PositiveInfinity, [0.2d]));
    }

    [Test]
    public void HalfSpreadCostAppliesHalfTheConfiguredFullSpreadPerOrder()
    {
        Assert.That(ExecutionCostAssumptions.HalfSpreadCost(200m, 10m), Is.EqualTo(0.10m));
        Assert.That(ExecutionCostAssumptions.HalfSpreadCost(200m, 0m), Is.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => ExecutionCostAssumptions.HalfSpreadCost(200m, -1m));
    }

    [Test]
    public void LifecycleMonitorSchedulesReviewsAndRetiresAfterRepeatedWeakPeriods()
    {
        var monitor = new StrategyLifecycleMonitor(
            new StrategyLifecycleOptions { ReevaluationIntervalDays = 30, MaximumConsecutiveNegativeReviews = 2, RetirementDrawdown = 0.20 },
            new DateOnly(2024, 1, 1),
            100m);

        Assert.That(monitor.Observe(new DateOnly(2024, 1, 20), 99m).Action, Is.EqualTo(StrategyLifecycleAction.None));
        Assert.That(monitor.Observe(new DateOnly(2024, 1, 31), 98m).Action, Is.EqualTo(StrategyLifecycleAction.Reevaluate));
        var decision = monitor.Observe(new DateOnly(2024, 3, 1), 95m);

        Assert.That(decision.Action, Is.EqualTo(StrategyLifecycleAction.Retire));
        Assert.That(monitor.IsRetired, Is.True);
        Assert.That(monitor.Observe(new DateOnly(2024, 3, 2), 95m).Action, Is.EqualTo(StrategyLifecycleAction.Retire));
    }

    [Test]
    public void BrokerageSafetyGatePausesOnDisconnectAndResumesAfterReconnect()
    {
        var gate = new BrokerageSafetyGate();
        Assert.That(gate.CanSubmitOrders, Is.True);

        gate.Disconnect();
        Assert.That(gate.CanSubmitOrders, Is.False);
        Assert.That(gate.CanLiquidate, Is.False);
        Assert.That(gate.MustLiquidate, Is.False);

        gate.Reconnect();
        Assert.That(gate.CanSubmitOrders, Is.True);
        Assert.That(gate.CanLiquidate, Is.True);
    }

    [Test]
    public void BrokerageSafetyGateKeepsOrderRejectionShutdownLatchedAcrossReconnect()
    {
        var gate = new BrokerageSafetyGate();
        gate.Trip("order rejected");

        Assert.That(gate.CanSubmitOrders, Is.False);
        Assert.That(gate.MustLiquidate, Is.True);
        Assert.That(gate.IsConnected, Is.True);
        Assert.That(gate.TripReason, Is.EqualTo("order rejected"));

        gate.Reconnect();
        Assert.That(gate.CanSubmitOrders, Is.False);
        Assert.That(gate.CanLiquidate, Is.True);
        Assert.That(gate.MustLiquidate, Is.True);
        Assert.Throws<ArgumentException>(() => gate.Trip(" "));
    }

    [Test]
    public void ConcurrentBrokerageEventsKeepRejectionLatchedAndItsReasonAvailable()
    {
        var gate = new BrokerageSafetyGate();

        Parallel.For(0, 256, index =>
        {
            switch (index % 3)
            {
                case 0:
                    gate.Disconnect();
                    break;
                case 1:
                    gate.Reconnect();
                    break;
                default:
                    gate.Trip($"order rejected {index}");
                    break;
            }
        });

        Assert.That(gate.IsTripped, Is.True);
        Assert.That(gate.MustLiquidate, Is.True);
        Assert.That(gate.CanSubmitOrders, Is.False);
        Assert.That(gate.TripReason, Does.StartWith("order rejected "));
        Assert.That(gate.CanLiquidate, Is.EqualTo(gate.IsConnected));
    }

    [Test]
    public void LiveExecutionRequiresExplicitAlpacaPaperAuthorization()
    {
        Assert.DoesNotThrow(() => PaperExecutionAuthorization.Validate(false, false, false, "InteractiveBrokersBrokerage"));
        Assert.Throws<InvalidOperationException>(() => PaperExecutionAuthorization.Validate(true, false, true, "AlpacaBrokerage"));
        Assert.Throws<InvalidOperationException>(() => PaperExecutionAuthorization.Validate(true, true, false, "AlpacaBrokerage"));
        Assert.Throws<InvalidOperationException>(() => PaperExecutionAuthorization.Validate(true, true, true, "InteractiveBrokersBrokerage"));
        Assert.Throws<InvalidOperationException>(() => PaperExecutionAuthorization.Validate(true, true, true, string.Empty));
        Assert.DoesNotThrow(() => PaperExecutionAuthorization.Validate(true, true, true, "AlpacaBrokerage"));
    }

    [Test]
    public void PortfolioDrawdownGuardRestoresHighWatermarkAndKeepsTripLatchedAcrossRestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lean-drawdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var statePath = Path.Combine(folder, "portfolio-drawdown-state.json");
            var firstProcess = new PortfolioDrawdownGuard(0.10m, 100m, statePath);
            Assert.That(PortfolioDrawdownGuard.LoadState(statePath)?.HighWatermark, Is.EqualTo(100m));
            Assert.That(firstProcess.Update(120m), Is.False);
            Assert.That(firstProcess.HighWatermark, Is.EqualTo(120m));

            var restartedProcess = new PortfolioDrawdownGuard(0.10m, 107m, statePath);
            Assert.That(restartedProcess.HighWatermark, Is.EqualTo(120m));
            Assert.That(restartedProcess.IsTripped, Is.True);
            Assert.That(restartedProcess.Update(130m), Is.True);

            var secondRestart = new PortfolioDrawdownGuard(0.10m, 130m, statePath);
            Assert.That(secondRestart.IsTripped, Is.True);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public void PortfolioDrawdownGuardFailsClosedOnCorruptStateAndZeroEquity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lean-drawdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var statePath = Path.Combine(folder, "portfolio-drawdown-state.json");
            File.WriteAllText(statePath, "{broken");
            Assert.Throws<InvalidDataException>(() => new PortfolioDrawdownGuard(0.10m, 100m, statePath));

            File.WriteAllText(statePath, "{\"Version\":9,\"HighWatermark\":100,\"Tripped\":false}");
            Assert.Throws<InvalidDataException>(() => PortfolioDrawdownGuard.LoadState(statePath));

            File.Delete(statePath);
            var guard = new PortfolioDrawdownGuard(0.10m, 100m, statePath);
            Assert.That(guard.Update(0m), Is.True);
            Assert.That(PortfolioDrawdownGuard.LoadState(statePath)?.Tripped, Is.True);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public void LifecycleMonitorRetiresImmediatelyAtDrawdownLimit()
    {
        var monitor = new StrategyLifecycleMonitor(
            new StrategyLifecycleOptions { RetirementDrawdown = 0.10 },
            new DateOnly(2024, 1, 1),
            100m);

        var decision = monitor.Observe(new DateOnly(2024, 1, 2), 90m);

        Assert.That(decision.Action, Is.EqualTo(StrategyLifecycleAction.Retire));
        Assert.That(decision.DrawdownFromPeak, Is.EqualTo(0.10).Within(1e-12));
    }

    [Test]
    public void GenomeNormalizationPreservesValidTimeframeAndExposureBounds()
    {
        var genome = new StrategyGenome(StrategyFamily.TrendFollowing, StrategyTimeframe.Minute, 150, 1, 1, 30, 99, 99)
            .Normalize([StrategyTimeframe.Daily]);

        Assert.That(genome.Timeframe, Is.EqualTo(StrategyTimeframe.Daily));
        Assert.That(genome.FastPeriod, Is.InRange(StrategyGenome.MinFastPeriod, StrategyGenome.MaxFastPeriod));
        Assert.That(genome.SlowPeriod, Is.GreaterThan(genome.FastPeriod));
        Assert.That(genome.ExitThresholdTenths, Is.LessThan(genome.EntryThresholdTenths));
        Assert.That(genome.MaxPositions, Is.EqualTo(StrategyGenome.MaxMaxPositions));
        Assert.That(genome.MaxExposurePercent, Is.EqualTo(StrategyGenome.MaxExposurePct));
    }

    [Test]
    public void GenomeNormalizationDropsInvalidOrFasterConfirmationTimeframes()
    {
        var genome = new StrategyGenome(StrategyFamily.TrendFollowing, StrategyTimeframe.Hour, 10, 40, 12, 4, 3, 50)
        {
            ConfirmationTimeframe = StrategyTimeframe.Minute
        };

        var normalized = genome.Normalize([StrategyTimeframe.Minute, StrategyTimeframe.Hour, StrategyTimeframe.Daily]);

        Assert.That(normalized.ConfirmationTimeframe, Is.Null);
        Assert.That((genome with { ConfirmationTimeframe = StrategyTimeframe.Daily })
            .Normalize([StrategyTimeframe.Minute, StrategyTimeframe.Hour, StrategyTimeframe.Daily]).ConfirmationTimeframe,
            Is.EqualTo(StrategyTimeframe.Daily));
    }

    [Test]
    public void ParameterNeighborhoodProducesDeterministicDistinctValidNeighbors()
    {
        var genome = new StrategyGenome(StrategyFamily.TrendFollowing, StrategyTimeframe.Daily, 10, 40, 12, 4, 3, 50);
        var first = genome.CreateParameterNeighborhood(4, [StrategyTimeframe.Daily]);
        var second = genome.CreateParameterNeighborhood(4, [StrategyTimeframe.Daily]);

        Assert.That(first, Has.Count.EqualTo(4));
        Assert.That(first, Is.EqualTo(second));
        Assert.That(first, Does.Not.Contain(genome));
        Assert.That(first.Distinct().Count(), Is.EqualTo(first.Count));
        Assert.That(first.All(candidate => candidate.FastPeriod >= StrategyGenome.MinFastPeriod &&
            candidate.SlowPeriod > candidate.FastPeriod && candidate.ExitThresholdTenths < candidate.EntryThresholdTenths), Is.True);
    }

    [Test]
    public void ParameterNeighborhoodCanProbeHigherTimeframeConfirmation()
    {
        var genome = new StrategyGenome(StrategyFamily.TrendFollowing, StrategyTimeframe.Hour, 10, 40, 12, 4, 3, 50);

        var neighbors = genome.CreateParameterNeighborhood(9, [StrategyTimeframe.Hour, StrategyTimeframe.Daily]);

        Assert.That(neighbors.Any(candidate => candidate.ConfirmationTimeframe == StrategyTimeframe.Daily), Is.True);
    }

    [Test]
    public void PortfolioRiskBudgetSubtractsExistingPositionsAndExposure()
    {
        var budget = PortfolioRiskBudget.Calculate(5, 0.80m, 1_000m, [200m, 100m, 0m]);

        Assert.That(budget.AvailablePositions, Is.EqualTo(3));
        Assert.That(budget.AvailableGrossExposureFraction, Is.EqualTo(0.50m));
    }

    [Test]
    public void PortfolioRiskBudgetStopsNewExposureWhenExistingHoldingsExceedLimit()
    {
        var budget = PortfolioRiskBudget.Calculate(4, 0.50m, 1_000m, [600m, 100m]);

        Assert.That(budget.AvailablePositions, Is.EqualTo(2));
        Assert.That(budget.AvailableGrossExposureFraction, Is.Zero);
    }

    [Test]
    public void WalkForwardWindowsAreChronologicalContiguousAndCoverTheInclusiveRange()
    {
        var start = new DateOnly(2023, 1, 1);
        var end = new DateOnly(2023, 6, 30);
        var windows = WalkForwardWindow.Split(start, end, 3);

        Assert.That(windows, Has.Count.EqualTo(3));
        Assert.That(windows[0].Start, Is.EqualTo(start));
        Assert.That(windows[^1].End, Is.EqualTo(end));
        for (var index = 0; index < windows.Count; index++)
        {
            Assert.That(windows[index].Index, Is.EqualTo(index));
            Assert.That(windows[index].Start, Is.LessThanOrEqualTo(windows[index].End));
            if (index > 0) Assert.That(windows[index - 1].End.AddDays(1), Is.EqualTo(windows[index].Start));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => WalkForwardWindow.Split(start, end, 182));
    }

    [Test]
    public void DuplicateOrUnknownCandidateCompletionIsRejected()
    {
        var engine = new GeneticSearchEngine(CreateOptions() with { GenerationCount = 1, PopulationSize = 2, RandomBaselineSize = 0, EliteCount = 0 });
        var candidates = engine.Start();
        foreach (var candidate in candidates) engine.Submit(candidate.Id, ValidMetrics());

        Assert.That(engine.IsComplete, Is.True);
        Assert.Throws<InvalidOperationException>(() => engine.Submit(candidates[0].Id, ValidMetrics()));
    }

    private static IReadOnlyList<ScoredGenome> RunToCompletion(GeneticSearchEngine engine)
    {
        var batch = engine.Start();
        while (true)
        {
            var candidates = batch;
            var next = Array.Empty<DiscoveryCandidate>();
            foreach (var candidate in candidates)
            {
                var metrics = ValidMetrics(candidate.Genome);
                var produced = engine.Submit(candidate.Id, metrics);
                if (produced.Count > 0) next = produced.ToArray();
            }

            if (engine.IsComplete) return engine.Results.OrderBy(item => item.CandidateId).ToArray();
            batch = next;
        }
    }

    private static StrategyBacktestMetrics ValidMetrics(StrategyGenome? genome = null)
    {
        var periodEffect = (genome?.FastPeriod ?? 10) / 1000d;
        return new StrategyBacktestMetrics(0.10 + periodEffect, 1.2, 0.08, 0.55, 40, 0.20);
    }
}

