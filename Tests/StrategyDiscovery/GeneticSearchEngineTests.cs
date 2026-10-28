using System;
using System.Collections.Generic;
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
