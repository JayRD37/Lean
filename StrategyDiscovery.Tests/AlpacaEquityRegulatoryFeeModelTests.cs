using NUnit.Framework;
using QuantConnect.Algorithm.CSharp;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Genetic.Tests;

[TestFixture]
public sealed class AlpacaEquityRegulatoryFeeModelTests
{
    [OneTimeSetUp]
    public void ConfigureLeanDataPath()
    {
        var dataPath = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../Data"));
        Config.Set("data-folder", dataPath);
        Globals.Reset();
    }

    [Test]
    public void BuyChargesCatFeeOnly()
    {
        var security = CreateEquitySecurity();
        var order = new MarketOrder(security.Symbol, 10m, DateTime.UtcNow);

        var fee = new AlpacaEquityRegulatoryFeeModel().GetOrderFee(new OrderFeeParameters(security, order));

        Assert.That(fee.Value.Currency, Is.EqualTo(Currencies.USD));
        Assert.That(fee.Value.Amount, Is.EqualTo(0.000030m));
    }

    [Test]
    public void SellChargesSecTafAndCatFees()
    {
        var security = CreateEquitySecurity();
        var order = new MarketOrder(security.Symbol, -10m, DateTime.UtcNow);

        var fee = new AlpacaEquityRegulatoryFeeModel().GetOrderFee(new OrderFeeParameters(security, order));

        Assert.That(fee.Value.Amount, Is.EqualTo(0.022580m));
    }

    [Test]
    public void SellTradingActivityFeeIsCappedPerTrade()
    {
        var security = CreateEquitySecurity();
        var order = new MarketOrder(security.Symbol, -100_000m, DateTime.UtcNow);

        var fee = new AlpacaEquityRegulatoryFeeModel().GetOrderFee(new OrderFeeParameters(security, order));

        Assert.That(fee.Value.Amount, Is.EqualTo(216.09m));
    }

    private static Security CreateEquitySecurity()
    {
        var symbol = new Symbol(SecurityIdentifier.GenerateEquity("SPY", Market.USA, mapSymbol: false), "SPY");
        var timeZone = TimeZones.NewYork;
        RegisteredSecurityDataTypesProvider.Null.RegisterType(typeof(TradeBar));
        var config = new SubscriptionDataConfig(typeof(TradeBar), symbol, Resolution.Minute, timeZone, timeZone, true, true, false);
        var security = new Security(
            SecurityExchangeHours.AlwaysOpen(timeZone),
            config,
            new Cash(Currencies.USD, 0m, 1m),
            SymbolProperties.GetDefault(Currencies.USD),
            ErrorCurrencyConverter.Instance,
            RegisteredSecurityDataTypesProvider.Null,
            new SecurityCache());
        security.SetMarketPrice(new Tick(DateTime.UtcNow, symbol, 100m, 100m));
        return security;
    }
}
