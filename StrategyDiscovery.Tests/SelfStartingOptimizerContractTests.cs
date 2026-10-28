using NUnit.Framework;
using QuantConnect.Genetic;
using QuantConnect.Optimizer;
using QuantConnect.Optimizer.Objectives;
using QuantConnect.Optimizer.Parameters;
using QuantConnect.Optimizer.Strategies;

namespace QuantConnect.Genetic.Tests;

[TestFixture]
public sealed class SelfStartingOptimizerContractTests
{
    [Test]
    public void GeneticOptimizerAcceptsAnEmptyInputParameterSet()
    {
        using var optimizer = new OptimizerProbe(CreatePacket(
            typeof(GeneticSearchOptimizationStrategy),
            new GeneticSearchOptimizationStrategySettings()));

        Assert.That(optimizer, Is.Not.Null);
    }

    [Test]
    public void GeneticOptimizerQueuesGeneratedCandidatesThroughLeanBacktestRunner()
    {
        using var optimizer = new OptimizerProbe(CreatePacket(
            typeof(GeneticSearchOptimizationStrategy),
            new GeneticSearchOptimizationStrategySettings
            {
                PopulationSize = 2,
                GenerationCount = 1,
                RandomBaselineSize = 1,
                EliteCount = 0,
                TournamentSize = 1,
                AllowedTimeframes = "Daily",
                ValidationFinalistCount = 1,
                ValidationBaselineCount = 0,
                ParameterRobustnessProbeCount = 0
            },
            maximumConcurrentBacktests: 2));

        optimizer.Start();

        Assert.That(optimizer.StartedParameterSets, Has.Count.EqualTo(2));
        Assert.That(optimizer.StartedParameterSets.All(parameterSet =>
            parameterSet.Value.ContainsKey("discovery.family") &&
            parameterSet.Value.ContainsKey("discovery.phase") &&
            parameterSet.Value["discovery.phase"] == "Training"), Is.True);
        Assert.That(optimizer.RunningCount, Is.EqualTo(2));
        Assert.That(optimizer.PendingCount, Is.EqualTo(3));
    }

    [Test]
    public void GridSearchStillRequiresInputParameterRanges()
    {
        var packet = CreatePacket(typeof(GridSearchOptimizationStrategy), new StepBaseOptimizationStrategySettings());

        Assert.Throws<ArgumentException>(() => new OptimizerProbe(packet));
    }

    private static OptimizationNodePacket CreatePacket(
        Type strategyType,
        OptimizationStrategySettings settings,
        int maximumConcurrentBacktests = 1) => new()
    {
        OptimizationStrategy = strategyType.AssemblyQualifiedName!,
        Criterion = new Target("Statistics.Sharpe Ratio", new Maximization(), null),
        Constraints = Array.Empty<Constraint>(),
        OptimizationParameters = new HashSet<OptimizationParameter>(),
        OptimizationStrategySettings = settings,
        MaximumConcurrentBacktests = maximumConcurrentBacktests
    };

    private sealed class OptimizerProbe(OptimizationNodePacket packet) : LeanOptimizer(packet)
    {
        private int _nextBacktestId;

        public List<ParameterSet> StartedParameterSets { get; } = new();
        public int RunningCount => RunningParameterSetForBacktest.Count;
        public int PendingCount => PendingParameterSet.Count;

        protected override string RunLean(ParameterSet parameterSet, string backtestName)
        {
            StartedParameterSets.Add(parameterSet);
            return $"backtest-{++_nextBacktestId}";
        }

        protected override void AbortLean(string backtestId)
        {
        }

        protected override void SendUpdate()
        {
        }
    }
}

