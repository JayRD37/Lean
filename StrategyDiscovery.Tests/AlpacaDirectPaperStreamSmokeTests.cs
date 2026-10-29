using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Brokerages.AlpacaDirect;
using QuantConnect.Configuration;
using QuantConnect.Packets;

namespace QuantConnect.Tests.Genetic;

[TestFixture]
public sealed class AlpacaDirectPaperStreamSmokeTests
{
    [Test]
    public void PaperMarketDataStreamAuthenticatesWithoutSubmittingOrders()
    {
        if (Environment.GetEnvironmentVariable("ALGORY_RUN_ALPACA_STREAM_SMOKE") != "1")
            Assert.Ignore("Run scripts/check-alpaca-stream.ps1 to explicitly enable this paper-only network smoke check.");

        var key = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_KEY");
        var secret = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_SECRET");
        Assert.That(key, Does.StartWith("PK"));
        Assert.That(secret, Is.Not.Null.And.Not.Empty);

        using var handler = new AlpacaDirectDataQueueHandler();
        handler.SetJob(new LiveNodePacket
        {
            BrokerageData = new Dictionary<string, string>
            {
                ["alpaca-direct-api-key"] = key!,
                ["alpaca-direct-api-secret"] = secret!
            }
        });

        Assert.That(handler.IsConnected, Is.True);

        Config.Set("data-folder", Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../Data")));
        using var brokerage = new AlpacaDirectBrokerage(key!, secret!, new QCAlgorithm());
        brokerage.Connect();
        Assert.That(brokerage.IsConnected, Is.True);
    }
}
