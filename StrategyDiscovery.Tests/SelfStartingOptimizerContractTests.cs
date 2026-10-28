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

        optimizer.CompleteNextBacktest("""
            {
              "statistics": {
                "Compounding Annual Return": "12.0%",
                "Sharpe Ratio": "1.2",
                "Drawdown": "5.0%",
                "Win Rate": "55.0%",
                "Total Trades": "20",
                "Net Profit": "18.0%"
              },
              "totalPerformance": {
                "tradeStatistics": { "totalNumberOfTrades": 20 },
                "closedTrades": []
              }
            }
            """);

        Assert.That(optimizer.StartedParameterSets, Has.Count.EqualTo(3));
        Assert.That(optimizer.StartedParameterSets[^1].Value["discovery.phase"], Is.EqualTo("Training"));
        Assert.That(optimizer.RunningCount, Is.EqualTo(2));
        Assert.That(optimizer.PendingCount, Is.EqualTo(2));
        Assert.That(optimizer.GetRuntimeStatistics()["Completed"], Is.EqualTo("1"));
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
        private readonly Queue<string> _queuedBacktestIds = new();

        public List<ParameterSet> StartedParameterSets { get; } = new();
        public int RunningCount => RunningParameterSetForBacktest.Count;
        public int PendingCount => PendingParameterSet.Count;

        protected override string RunLean(ParameterSet parameterSet, string backtestName)
        {
            StartedParameterSets.Add(parameterSet);
            var backtestId = $"backtest-{++_nextBacktestId}";
            _queuedBacktestIds.Enqueue(backtestId);
            return backtestId;
        }

        public void CompleteNextBacktest(string result) => NewResult(result, _queuedBacktestIds.Dequeue());

        protected override void AbortLean(string backtestId)
        {
        }

        protected override void SendUpdate()
        {
        }
    }
}


