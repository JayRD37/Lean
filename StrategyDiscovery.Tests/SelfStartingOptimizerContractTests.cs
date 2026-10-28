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
    public void GridSearchStillRequiresInputParameterRanges()
    {
        var packet = CreatePacket(typeof(GridSearchOptimizationStrategy), new StepBaseOptimizationStrategySettings());

        Assert.Throws<ArgumentException>(() => new OptimizerProbe(packet));
    }

    private static OptimizationNodePacket CreatePacket(Type strategyType, OptimizationStrategySettings settings) => new()
    {
        OptimizationStrategy = strategyType.AssemblyQualifiedName!,
        Criterion = new Target("Statistics.Sharpe Ratio", new Maximization(), null),
        Constraints = Array.Empty<Constraint>(),
        OptimizationParameters = new HashSet<OptimizationParameter>(),
        OptimizationStrategySettings = settings,
        MaximumConcurrentBacktests = 1
    };

    private sealed class OptimizerProbe(OptimizationNodePacket packet) : LeanOptimizer(packet)
    {
        protected override string RunLean(ParameterSet parameterSet, string backtestName) => "unused";

        protected override void AbortLean(string backtestId)
        {
        }

        protected override void SendUpdate()
        {
        }
    }
}
