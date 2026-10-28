using NUnit.Framework;
using QuantConnect;
using QuantConnect.Algorithm.CSharp;
using QuantConnect.Configuration;
using QuantConnect.Orders;

namespace QuantConnect.Genetic.Tests;

[TestFixture]
public sealed class GeneticDiscoveryBrokerageCallbackTests
{
    [OneTimeSetUp]
    public void ConfigureLeanDataPath()
    {
        var dataPath = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../Data"));
        Config.Set("data-folder", dataPath);
        Globals.Reset();
    }

    [Test]
    public void DisconnectCallbackPausesAndReconnectCallbackResumesStrategyOrders()
    {
        var algorithm = new GeneticDiscoveryAlgorithm();
        Assert.That(algorithm.CanSubmitStrategyOrders, Is.True);

        algorithm.OnBrokerageDisconnect();
        Assert.That(algorithm.CanSubmitStrategyOrders, Is.False);

        algorithm.OnBrokerageReconnect();
        Assert.That(algorithm.CanSubmitStrategyOrders, Is.True);
        Assert.That(algorithm.ExecutionSafetyTripped, Is.False);
    }

    [Test]
    public void InvalidOrderCallbackLatchesShutdownAcrossBrokerageReconnect()
    {
        var algorithm = new GeneticDiscoveryAlgorithm();
        var orderEvent = new OrderEvent
        {
            OrderId = 17,
            Symbol = Symbol.Empty,
            UtcTime = DateTime.UtcNow,
            Status = OrderStatus.Invalid,
            Message = "Injected brokerage rejection"
        };

        algorithm.OnOrderEvent(orderEvent);
        Assert.That(algorithm.ExecutionSafetyTripped, Is.True);
        Assert.That(algorithm.CanSubmitStrategyOrders, Is.False);

        algorithm.OnBrokerageDisconnect();
        algorithm.OnBrokerageReconnect();
        Assert.That(algorithm.ExecutionSafetyTripped, Is.True);
        Assert.That(algorithm.CanSubmitStrategyOrders, Is.False);
    }
}
