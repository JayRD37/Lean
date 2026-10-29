using System.ComponentModel.Composition;
using System.Reflection;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.AlpacaDirect;
using QuantConnect.Interfaces;
using QuantConnect.Util;

namespace QuantConnect.Tests.Genetic;

[TestFixture]
public sealed class AlpacaDirectCompositionTests
{
    [Test]
    public void BrokerageFactoryAndMarketDataQueueAreDiscoverableByLean()
    {
        var factoryAttribute = typeof(AlpacaDirectBrokerage).GetCustomAttribute<BrokerageFactoryAttribute>();
        Assert.That(factoryAttribute?.Type, Is.EqualTo(typeof(AlpacaDirectBrokerageFactory)));
        Assert.That(typeof(IBrokerage).IsAssignableFrom(typeof(AlpacaDirectBrokerage)), Is.True);
        Assert.That(typeof(IDataQueueHandler).IsAssignableFrom(typeof(AlpacaDirectDataQueueHandler)), Is.True);
        Assert.That(typeof(AlpacaDirectDataQueueHandler).GetCustomAttributes<ExportAttribute>()
            .Any(attribute => attribute.ContractType == typeof(IDataQueueHandler)), Is.True);
        var exportedFactoryTypes = Composer.Instance.GetExportedTypes<IBrokerageFactory>().ToArray();
        Assert.That(exportedFactoryTypes, Does.Contain(typeof(AlpacaDirectBrokerageFactory)));
        var factory = Composer.Instance.Single<IBrokerageFactory>(candidate =>
            candidate.BrokerageType.MatchesTypeName("AlpacaDirectBrokerage"));
        Assert.That(factory.BrokerageType, Is.EqualTo(typeof(AlpacaDirectBrokerage)));
        var dataQueue = Composer.Instance.GetExportedValueByTypeName<IDataQueueHandler>("AlpacaDirectDataQueueHandler");
        Assert.That(dataQueue, Is.TypeOf<AlpacaDirectDataQueueHandler>());
        dataQueue.Dispose();
    }
}
