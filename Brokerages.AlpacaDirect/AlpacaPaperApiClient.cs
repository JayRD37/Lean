using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace QuantConnect.Brokerages.AlpacaDirect;

/// <summary>
/// Minimal, paper-only client for Alpaca's public Trading API. It deliberately has no live endpoint setting.
/// </summary>
public sealed class AlpacaPaperApiClient : IDisposable
{
    private static readonly Uri PaperApiBaseUri = new("https://paper-api.alpaca.markets/v2/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private readonly string _apiSecret;

    public AlpacaPaperApiClient(string apiKey, string apiSecret)
        : this(new HttpClient(), apiKey, apiSecret, ownsHttpClient: true)
    {
    }

    public AlpacaPaperApiClient(HttpClient httpClient, string apiKey, string apiSecret)
        : this(httpClient, apiKey, apiSecret, ownsHttpClient: false)
    {
    }

    private AlpacaPaperApiClient(HttpClient httpClient, string apiKey, string apiSecret, bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? throw new ArgumentException("An Alpaca paper API key is required.", nameof(apiKey)) : apiKey;
        _apiSecret = string.IsNullOrWhiteSpace(apiSecret) ? throw new ArgumentException("An Alpaca paper API secret is required.", nameof(apiSecret)) : apiSecret;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<AlpacaAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync("account", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new AlpacaAccountSnapshot(
            GetString(root, "status"),
            GetString(root, "currency"),
            GetDecimal(root, "cash"),
            GetDecimal(root, "equity"),
            GetDecimal(root, "buying_power"),
            GetBool(root, "trading_blocked"),
            GetBool(root, "account_blocked"));
    }

    public async Task<IReadOnlyList<AlpacaPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync("positions", cancellationToken).ConfigureAwait(false);
        return document.RootElement.EnumerateArray().Select(item => new AlpacaPositionSnapshot(
            GetString(item, "symbol"),
            GetDecimal(item, "qty"),
            GetString(item, "side"),
            GetDecimal(item, "avg_entry_price"),
            GetDecimal(item, "market_value"),
            GetDecimal(item, "unrealized_pl"))).ToArray();
    }

    public async Task<IReadOnlyList<AlpacaOrderSnapshot>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync("orders?status=open&limit=500&nested=false", cancellationToken).ConfigureAwait(false);
        return document.RootElement.EnumerateArray().Select(ParseOrder).ToArray();
    }

    public async Task<AlpacaOrderSnapshot> SubmitOrderAsync(AlpacaPaperOrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var payload = new Dictionary<string, object?>
        {
            ["symbol"] = request.Symbol,
            ["qty"] = request.Quantity.ToString(CultureInfo.InvariantCulture),
            ["side"] = GetApiSide(request.Side),
            ["type"] = GetApiOrderType(request.Type),
            ["time_in_force"] = GetApiTimeInForce(request.TimeInForce),
            ["client_order_id"] = request.ClientOrderId,
            ["extended_hours"] = false
        };
        if (request.LimitPrice is { } limitPrice) payload["limit_price"] = limitPrice.ToString(CultureInfo.InvariantCulture);
        if (request.StopPrice is { } stopPrice) payload["stop_price"] = stopPrice.ToString(CultureInfo.InvariantCulture);

        using var document = await SendJsonAsync(HttpMethod.Post, "orders", payload, cancellationToken).ConfigureAwait(false);
        return ParseOrder(document.RootElement);
    }

    public async Task<AlpacaOrderSnapshot> ReplaceOrderAsync(string brokerOrderId, decimal? quantity, decimal? limitPrice, decimal? stopPrice, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?>();
        if (quantity is { } orderQuantity)
        {
            if (orderQuantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Replacement quantity must be positive.");
            payload["qty"] = orderQuantity.ToString(CultureInfo.InvariantCulture);
        }
        if (limitPrice is { } replacementLimit)
        {
            if (replacementLimit <= 0) throw new ArgumentOutOfRangeException(nameof(limitPrice), "Replacement limit price must be positive.");
            payload["limit_price"] = replacementLimit.ToString(CultureInfo.InvariantCulture);
        }
        if (stopPrice is { } replacementStop)
        {
            if (replacementStop <= 0) throw new ArgumentOutOfRangeException(nameof(stopPrice), "Replacement stop price must be positive.");
            payload["stop_price"] = replacementStop.ToString(CultureInfo.InvariantCulture);
        }
        if (payload.Count == 0) throw new ArgumentException("At least one order field must be changed.");

        using var document = await SendJsonAsync(HttpMethod.Patch, $"orders/{EscapeOrderId(brokerOrderId)}", payload, cancellationToken).ConfigureAwait(false);
        return ParseOrder(document.RootElement);
    }

    public async Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"orders/{EscapeOrderId(brokerOrderId)}");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return;
        throw await CreateApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await CreateApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object payload, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await CreateApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(PaperApiBaseUri, path));
        request.Headers.Add("APCA-API-KEY-ID", _apiKey);
        request.Headers.Add("APCA-API-SECRET-KEY", _apiSecret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<AlpacaApiException> CreateApiExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string code = "unknown";
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("code", out var codeElement)) code = codeElement.ToString();
        }
        catch (JsonException)
        {
            // Keep response bodies out of error messages; they can contain account or request details.
        }
        return new AlpacaApiException(response.StatusCode, code);
    }

    private static AlpacaOrderSnapshot ParseOrder(JsonElement item) => new(
        GetString(item, "id"),
        GetString(item, "client_order_id"),
        GetString(item, "symbol"),
        GetDecimal(item, "qty"),
        GetDecimal(item, "filled_qty"),
        GetString(item, "side"),
        GetString(item, "type"),
        GetString(item, "status"),
        GetString(item, "time_in_force"),
        GetNullableDecimal(item, "limit_price"),
        GetNullableDecimal(item, "stop_price"),
        GetNullableDecimal(item, "filled_avg_price"));

    private static string EscapeOrderId(string id) => Uri.EscapeDataString(string.IsNullOrWhiteSpace(id)
        ? throw new ArgumentException("An Alpaca order ID is required.", nameof(id))
        : id);

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : string.Empty;

    private static decimal GetDecimal(JsonElement element, string property) =>
        GetNullableDecimal(element, property) ?? 0m;

    private static decimal? GetNullableDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static bool? GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static string GetApiSide(AlpacaPaperOrderSide side) => side switch
    {
        AlpacaPaperOrderSide.Buy => "buy",
        AlpacaPaperOrderSide.Sell => "sell",
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    };

    private static string GetApiOrderType(AlpacaPaperOrderType type) => type switch
    {
        AlpacaPaperOrderType.Market => "market",
        AlpacaPaperOrderType.Limit => "limit",
        AlpacaPaperOrderType.Stop => "stop",
        AlpacaPaperOrderType.StopLimit => "stop_limit",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string GetApiTimeInForce(AlpacaPaperTimeInForce timeInForce) => timeInForce switch
    {
        AlpacaPaperTimeInForce.Day => "day",
        AlpacaPaperTimeInForce.Gtc => "gtc",
        AlpacaPaperTimeInForce.Ioc => "ioc",
        AlpacaPaperTimeInForce.Fok => "fok",
        _ => throw new ArgumentOutOfRangeException(nameof(timeInForce))
    };

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}

public sealed record AlpacaAccountSnapshot(string Status, string Currency, decimal Cash, decimal Equity, decimal BuyingPower, bool? TradingBlocked, bool? AccountBlocked);

public sealed record AlpacaPositionSnapshot(string Symbol, decimal Quantity, string Side, decimal AverageEntryPrice, decimal MarketValue, decimal UnrealizedProfitLoss);

public sealed record AlpacaOrderSnapshot(string Id, string ClientOrderId, string Symbol, decimal Quantity, decimal FilledQuantity, string Side, string Type, string Status, string TimeInForce, decimal? LimitPrice, decimal? StopPrice, decimal? AverageFillPrice);

public enum AlpacaPaperOrderSide { Buy, Sell }
public enum AlpacaPaperOrderType { Market, Limit, Stop, StopLimit }
public enum AlpacaPaperTimeInForce { Day, Gtc, Ioc, Fok }

public sealed record AlpacaPaperOrderRequest(
    string Symbol,
    decimal Quantity,
    AlpacaPaperOrderSide Side,
    AlpacaPaperOrderType Type,
    AlpacaPaperTimeInForce TimeInForce,
    string ClientOrderId,
    decimal? LimitPrice = null,
    decimal? StopPrice = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Symbol)) throw new ArgumentException("An Alpaca symbol is required.", nameof(Symbol));
        if (Quantity <= 0) throw new ArgumentOutOfRangeException(nameof(Quantity), "Order quantity must be positive.");
        if (string.IsNullOrWhiteSpace(ClientOrderId) || ClientOrderId.Length > 128) throw new ArgumentException("A client order ID between 1 and 128 characters is required.", nameof(ClientOrderId));
        if (Type is AlpacaPaperOrderType.Limit or AlpacaPaperOrderType.StopLimit && LimitPrice is not > 0)
            throw new ArgumentException("This order type requires a positive limit price.", nameof(LimitPrice));
        if (Type is AlpacaPaperOrderType.Stop or AlpacaPaperOrderType.StopLimit && StopPrice is not > 0)
            throw new ArgumentException("This order type requires a positive stop price.", nameof(StopPrice));
    }
}

public sealed class AlpacaApiException : HttpRequestException
{
    public AlpacaApiException() : this(HttpStatusCode.InternalServerError, "unknown") { }

    public AlpacaApiException(string message) : base(message)
    {
        Code = "unknown";
    }

    public AlpacaApiException(string message, Exception innerException) : base(message, innerException)
    {
        Code = "unknown";
    }

    public AlpacaApiException(HttpStatusCode statusCode, string code)
        : base($"Alpaca paper API request failed with HTTP {(int)statusCode} (code {code}).", null, statusCode)
    {
        Code = code;
    }

    public string Code { get; }
}
