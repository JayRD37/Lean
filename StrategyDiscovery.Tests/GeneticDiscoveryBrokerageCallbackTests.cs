using NUnit.Framework;
using QuantConnect;
using QuantConnect.Algorithm.CSharp;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Backtesting;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine;
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
    public void BacktestingBrokerageDisconnectEventsUseTheLeanEngineRouter()
    {
        var algorithm = new GeneticDiscoveryAlgorithm();
        var messageHandler = new RecordingBrokerageMessageHandler();
        algorithm.SetBrokerageMessageHandler(messageHandler);
        algorithm.SetLocked();
        using var brokerage = new TestBacktestingBrokerage(algorithm);
        BrokerageEventRouter.Subscribe(brokerage, algorithm);

        brokerage.Emit(BrokerageMessageEvent.Disconnected("Injected disconnect"));
        Assert.That(algorithm.CanSubmitStrategyOrders, Is.False);
        Assert.That(messageHandler.Messages.Select(message => message.Type), Is.EqualTo(new[] { BrokerageMessageType.Disconnect }));

        brokerage.Emit(BrokerageMessageEvent.Reconnected("Injected reconnect"));
        Assert.That(algorithm.CanSubmitStrategyOrders, Is.True);
        Assert.That(algorithm.ExecutionSafetyTripped, Is.False);
        Assert.That(messageHandler.Messages.Select(message => message.Type), Is.EqualTo(new[]
        {
            BrokerageMessageType.Disconnect,
            BrokerageMessageType.Reconnect
        }));
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

    private sealed class TestBacktestingBrokerage(IAlgorithm algorithm) : BacktestingBrokerage(algorithm)
    {
        public void Emit(BrokerageMessageEvent message) => OnMessage(message);
    }

    private sealed class RecordingBrokerageMessageHandler : IBrokerageMessageHandler
    {
        public List<BrokerageMessageEvent> Messages { get; } = new();

        public void HandleMessage(BrokerageMessageEvent message) => Messages.Add(message);

        public bool HandleOrder(NewBrokerageOrderNotificationEventArgs eventArgs) => false;
    }
}
