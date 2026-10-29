using System.Net;
using System.Text;
using NUnit.Framework;
using QuantConnect.Brokerages.AlpacaDirect;

namespace QuantConnect.Tests.StrategyDiscovery;

[TestFixture]
public class AlpacaPaperApiClientTests
{
    [Test]
    public async Task GetAccount_UsesPaperEndpointAndApiKeyHeaders()
    {
        using var handler = new RecordingHandler(_ => JsonResponse("""
            {"status":"ACTIVE","currency":"USD","cash":"10000","equity":"10000","buying_power":"20000","trading_blocked":false,"account_blocked":false}
            """));
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaPaperApiClient(httpClient, "paper-key", "paper-secret");

        var account = await client.GetAccountAsync();

        Assert.That(handler.RequestUri?.AbsoluteUri, Is.EqualTo("https://paper-api.alpaca.markets/v2/account"));
        Assert.That(handler.Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(handler.ApiKey, Is.EqualTo("paper-key"));
        Assert.That(handler.ApiSecret, Is.EqualTo("paper-secret"));
        Assert.That(account.Status, Is.EqualTo("ACTIVE"));
        Assert.That(account.Currency, Is.EqualTo("USD"));
        Assert.That(account.Cash, Is.EqualTo(10000m));
        Assert.That(account.BuyingPower, Is.EqualTo(20000m));
        Assert.That(account.TradingBlocked, Is.False);
    }

    [Test]
    public async Task SubmitOrder_UsesPaperEndpointAndExplicitlyDisablesExtendedHours()
    {
        using var handler = new RecordingHandler(_ => JsonResponse("""
            {"id":"broker-order-1","client_order_id":"lean-42","symbol":"SPY","qty":"2","filled_qty":"0","side":"buy","type":"market","status":"accepted","time_in_force":"day","limit_price":null,"stop_price":null,"filled_avg_price":null}
            """));
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaPaperApiClient(httpClient, "paper-key", "paper-secret");

        var order = await client.SubmitOrderAsync(new AlpacaPaperOrderRequest(
            "SPY", 2m, AlpacaPaperOrderSide.Buy, AlpacaPaperOrderType.Market,
            AlpacaPaperTimeInForce.Day, "lean-42"));

        Assert.That(handler.RequestUri?.AbsoluteUri, Is.EqualTo("https://paper-api.alpaca.markets/v2/orders"));
        Assert.That(handler.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(handler.Body, Does.Contain("\"extended_hours\":false"));
        Assert.That(handler.Body, Does.Contain("\"client_order_id\":\"lean-42\""));
        Assert.That(order.Id, Is.EqualTo("broker-order-1"));
        Assert.That(order.Status, Is.EqualTo("accepted"));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void SubmitOrder_RejectsNonPositiveQuantityBeforeSending(decimal quantity)
    {
        using var handler = new RecordingHandler(_ => JsonResponse("{}"));
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaPaperApiClient(httpClient, "paper-key", "paper-secret");

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SubmitOrderAsync(new AlpacaPaperOrderRequest(
            "SPY", quantity, AlpacaPaperOrderSide.Buy, AlpacaPaperOrderType.Market,
            AlpacaPaperTimeInForce.Day, "lean-invalid")));
        Assert.That(handler.RequestUri, Is.Null);
    }

    [Test]
    public void Client_RejectsMissingCredentials()
    {
        using var handler = new HttpClient(new RecordingHandler(_ => JsonResponse("{}")));
        Assert.Throws<ArgumentException>(() => new AlpacaPaperApiClient(handler, "", "paper-secret"));
        Assert.Throws<ArgumentException>(() => new AlpacaPaperApiClient(handler, "paper-key", " "));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? ApiKey { get; private set; }
        public string? ApiSecret { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            ApiKey = request.Headers.GetValues("APCA-API-KEY-ID").Single();
            ApiSecret = request.Headers.GetValues("APCA-API-SECRET-KEY").Single();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
