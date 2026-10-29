using NUnit.Framework;
using QuantConnect;
using QuantConnect.Algorithm.CSharp;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Backtesting;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Lean.Engine.TransactionHandlers;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

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
    public void BrokerageTransactionHandlerForwardsRejectedOrderToStrategySafetyGate()
    {
        var algorithm = new GeneticDiscoveryAlgorithm();
        var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();
        algorithm.Securities.SetSecurityService(new SecurityService(
            algorithm.Portfolio.CashBook,
            marketHoursDatabase,
            SymbolPropertiesDatabase.FromDataFolder(),
            algorithm,
            RegisteredSecurityDataTypesProvider.Null,
            new SecurityCacheProvider(algorithm.Portfolio),
            algorithm: algorithm));
        algorithm.SetLiveMode(true);
        var symbol = new Symbol(SecurityIdentifier.GenerateEquity("SPY", Market.USA, mapSymbol: false), "SPY");
        var timeZone = TimeZones.NewYork;
        var config = new SubscriptionDataConfig(typeof(TradeBar), symbol, Resolution.Minute, timeZone, timeZone, true, true, false);
        var security = new Security(
            SecurityExchangeHours.AlwaysOpen(timeZone),
            config,
            new Cash(Currencies.USD, 0m, 1m),
            SymbolProperties.GetDefault(Currencies.USD),
            ErrorCurrencyConverter.Instance,
            RegisteredSecurityDataTypesProvider.Null,
            new SecurityCache());
        algorithm.Securities.Add(symbol, security);

        using var brokerage = new TestBacktestingBrokerage(algorithm);
        var transactionHandler = new BrokerageTransactionHandler();
        transactionHandler.Initialize(algorithm, brokerage, new BacktestingResultHandler());
        try
        {
            var order = new MarketOrder(symbol, 1, DateTime.UtcNow);
            transactionHandler.AddOpenOrder(order, algorithm);
            brokerage.EmitOrderEvent(new OrderEvent(order, algorithm.UtcTime, OrderFee.Zero)
            {
                Status = OrderStatus.Invalid,
                Message = "Injected brokerage rejection"
            });

            Assert.That(algorithm.ExecutionSafetyTripped, Is.True);
            Assert.That(algorithm.CanSubmitStrategyOrders, Is.False);
        }
        finally
        {
            transactionHandler.Exit();
        }
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

        public void EmitOrderEvent(OrderEvent orderEvent) => OnOrderEvent(orderEvent);
    }

    private sealed class RecordingBrokerageMessageHandler : IBrokerageMessageHandler
    {
        public List<BrokerageMessageEvent> Messages { get; } = new();

        public void HandleMessage(BrokerageMessageEvent message) => Messages.Add(message);

        public bool HandleOrder(NewBrokerageOrderNotificationEventArgs eventArgs) => false;
    }
}
