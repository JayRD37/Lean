using System.Globalization;
using System.Collections.Concurrent;
using AlpacaAuthStatus = Alpaca.Markets.AuthStatus;
using AlpacaEnvironments = Alpaca.Markets.Environments;
using AlpacaEnvironmentExtensions = Alpaca.Markets.EnvironmentExtensions;
using AlpacaStreamingClient = Alpaca.Markets.IAlpacaStreamingClient;
using AlpacaSecretKey = Alpaca.Markets.SecretKey;
using AlpacaTradeEvent = Alpaca.Markets.TradeEvent;
using AlpacaTradeUpdate = Alpaca.Markets.ITradeUpdate;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.AlpacaDirect;

/// <summary>
/// LEAN brokerage adapter for Alpaca's paper-only trading REST API.
/// It intentionally rejects unsupported order types and live credentials.
/// </summary>
[BrokerageFactoryAttribute(typeof(AlpacaDirectBrokerageFactory))]
public sealed class AlpacaDirectBrokerage : Brokerage
{
    private readonly IAlgorithm _algorithm;
    private readonly AlpacaPaperApiClient _client;
    private readonly ConcurrentDictionary<string, Order> _ordersByBrokerId = new(StringComparer.Ordinal);
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private AlpacaStreamingClient? _tradeStream;
    private bool _connected;

    public AlpacaDirectBrokerage(string apiKey, string apiSecret, IAlgorithm algorithm)
        : base("AlpacaDirectPaper")
    {
        if (!apiKey.StartsWith("PK", StringComparison.Ordinal))
            throw new ArgumentException("AlpacaDirect accepts paper API keys only (PK prefix required).", nameof(apiKey));
        ArgumentNullException.ThrowIfNull(algorithm);
        _algorithm = algorithm;
        _apiKey = apiKey;
        _apiSecret = apiSecret;
        _client = new AlpacaPaperApiClient(apiKey, apiSecret);
    }

    public override bool IsConnected => _connected;
    public override bool AccountInstantlyUpdated => false;
    public override string AccountBaseCurrency => "USD";

    public override void Connect()
    {
        var account = _client.GetAccountAsync().GetAwaiter().GetResult();
        if (!account.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) || account.AccountBlocked == true || account.TradingBlocked == true)
            throw new InvalidOperationException("Alpaca paper account is inactive or blocked from trading.");
        _tradeStream = AlpacaEnvironmentExtensions.GetAlpacaStreamingClient(AlpacaEnvironments.Paper, new AlpacaSecretKey(_apiKey, _apiSecret));
        _tradeStream.OnTradeUpdate += HandleTradeUpdate;
        _tradeStream.OnError += error =>
        {
            if (!_connected) return;
            _connected = false;
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Disconnect, "AlpacaTradeStreamError", error.Message));
        };
        _tradeStream.SocketClosed += () =>
        {
            if (!_connected) return;
            _connected = false;
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Disconnect, "AlpacaTradeStreamClosed", "Alpaca paper trade-update stream closed."));
        };
        _tradeStream.SocketOpened += () =>
        {
            _connected = true;
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Reconnect, "AlpacaTradeStreamOpened", "Alpaca paper trade-update stream connected."));
        };
        var auth = _tradeStream.ConnectAndAuthenticateAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (auth != AlpacaAuthStatus.Authorized)
            throw new InvalidOperationException($"Alpaca trade-update stream authentication failed ({auth}).");
        _connected = true;
    }

    public override void Disconnect()
    {
        _connected = false;
        _tradeStream?.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private void HandleTradeUpdate(AlpacaTradeUpdate update)
    {
        if (!_ordersByBrokerId.TryGetValue(update.Order.OrderId.ToString("D"), out var order)) return;
        var status = update.Event switch
        {
            AlpacaTradeEvent.PartialFill or AlpacaTradeEvent.PartiallyFilled => OrderStatus.PartiallyFilled,
            AlpacaTradeEvent.Fill => OrderStatus.Filled,
            AlpacaTradeEvent.Canceled or AlpacaTradeEvent.Expired => OrderStatus.Canceled,
            AlpacaTradeEvent.Rejected => OrderStatus.Invalid,
            AlpacaTradeEvent.PendingCancel or AlpacaTradeEvent.PendingReplace => OrderStatus.CancelPending,
            AlpacaTradeEvent.Replaced => OrderStatus.UpdateSubmitted,
            _ => OrderStatus.Submitted
        };
        OnOrderEvent(new OrderEvent(order, update.TimestampUtc ?? DateTime.UtcNow, OrderFee.Zero)
        {
            Status = status,
            FillQuantity = status is OrderStatus.PartiallyFilled or OrderStatus.Filled ? update.TradeQuantity ?? 0m : 0m,
            FillPrice = update.Price ?? 0m
        });
    }

    public override bool PlaceOrder(Order order)
    {
        EnsureConnected();
        var request = ToPaperRequest(order);
        try
        {
            var result = _client.SubmitOrderAsync(request).GetAwaiter().GetResult();
            order.BrokerId.Add(result.Id);
            _ordersByBrokerId[result.Id] = order;
            OnOrderIdChangedEvent(new BrokerageOrderIdChangedEvent { OrderId = order.Id, BrokerId = order.BrokerId });
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero) { Status = OrderStatus.Submitted });
            return true;
        }
        catch (Exception error)
        {
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, error.Message) { Status = OrderStatus.Invalid });
            return false;
        }
    }

    public override bool UpdateOrder(Order order)
    {
        EnsureConnected();
        var brokerId = order.BrokerId.LastOrDefault();
        if (string.IsNullOrEmpty(brokerId)) return false;
        try
        {
            var limit = order is LimitOrder limitOrder ? limitOrder.LimitPrice : (decimal?)null;
            var stop = order switch
            {
                StopMarketOrder stopMarket => stopMarket.StopPrice,
                StopLimitOrder stopLimit => stopLimit.StopPrice,
                _ => (decimal?)null
            };
            var result = _client.ReplaceOrderAsync(brokerId, Math.Abs(order.Quantity), limit, stop).GetAwaiter().GetResult();
            _ordersByBrokerId[result.Id] = order;
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero) { Status = OrderStatus.UpdateSubmitted });
            return true;
        }
        catch (Exception error)
        {
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "AlpacaOrderReplaceFailed", error.Message));
            return false;
        }
    }

    public override bool CancelOrder(Order order)
    {
        EnsureConnected();
        var brokerId = order.BrokerId.LastOrDefault();
        if (string.IsNullOrEmpty(brokerId)) return false;
        try
        {
            _client.CancelOrderAsync(brokerId).GetAwaiter().GetResult();
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero) { Status = OrderStatus.Canceled });
            return true;
        }
        catch (Exception error)
        {
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "AlpacaOrderCancelFailed", error.Message));
            return false;
        }
    }

    public override List<Order> GetOpenOrders()
    {
        EnsureConnected();
        var brokerOrders = _client.GetOpenOrdersAsync().GetAwaiter().GetResult();
        var orders = new List<Order>();
        foreach (var brokerOrder in brokerOrders)
        {
            if (!_ordersByBrokerId.TryGetValue(brokerOrder.Id, out var order) &&
                brokerOrder.ClientOrderId.StartsWith("lean-", StringComparison.Ordinal) &&
                int.TryParse(brokerOrder.ClientOrderId.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var leanOrderId))
            {
                order = _algorithm.Transactions.GetOrderById(leanOrderId);
                if (order is not null) _ordersByBrokerId[brokerOrder.Id] = order;
            }
            if (order is not null) orders.Add(order);
        }
        return orders;
    }

    public override List<Holding> GetAccountHoldings()
    {
        EnsureConnected();
        return _client.GetPositionsAsync().GetAwaiter().GetResult().Select(position => new Holding
        {
            Symbol = Symbol.Create(position.Symbol, SecurityType.Equity, Market.USA),
            Quantity = position.Side.Equals("short", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(position.Quantity) : position.Quantity,
            AveragePrice = position.AverageEntryPrice,
            MarketPrice = position.Quantity == 0 ? 0 : Math.Abs(position.MarketValue / position.Quantity),
            MarketValue = position.MarketValue,
            UnrealizedPnL = position.UnrealizedProfitLoss,
            CurrencySymbol = "$",
            ConversionRate = 1m
        }).ToList();
    }

    public override List<CashAmount> GetCashBalance()
    {
        EnsureConnected();
        var account = _client.GetAccountAsync().GetAwaiter().GetResult();
        return [new CashAmount(account.Cash, string.IsNullOrWhiteSpace(account.Currency) ? "USD" : account.Currency)];
    }

    public override IEnumerable<BaseData> GetHistory(QuantConnect.Data.HistoryRequest request) => Enumerable.Empty<BaseData>();

    private AlpacaPaperOrderRequest ToPaperRequest(Order order)
    {
        if (order.Symbol.ID.SecurityType != SecurityType.Equity || order.Symbol.ID.Market != Market.USA)
            throw new NotSupportedException("AlpacaDirect paper adapter currently supports US equities only.");
        var type = order.Type switch
        {
            OrderType.Market => AlpacaPaperOrderType.Market,
            OrderType.Limit => AlpacaPaperOrderType.Limit,
            OrderType.StopMarket => AlpacaPaperOrderType.Stop,
            OrderType.StopLimit => AlpacaPaperOrderType.StopLimit,
            _ => throw new NotSupportedException($"Order type {order.Type} is not supported by AlpacaDirect.")
        };
        var tif = order.TimeInForce.ToString().ToLowerInvariant() switch
        {
            "day" => AlpacaPaperTimeInForce.Day,
            "goodtilcanceled" or "gtc" => AlpacaPaperTimeInForce.Gtc,
            "immediateorcancel" or "ioc" => AlpacaPaperTimeInForce.Ioc,
            "fillorkill" or "fok" => AlpacaPaperTimeInForce.Fok,
            _ => throw new NotSupportedException($"Time in force {order.TimeInForce} is not supported.")
        };
        var limit = order switch { LimitOrder limitOrder => limitOrder.LimitPrice, StopLimitOrder stopLimit => stopLimit.LimitPrice, _ => (decimal?)null };
        var stop = order switch { StopMarketOrder stopMarket => stopMarket.StopPrice, StopLimitOrder stopLimit => stopLimit.StopPrice, _ => (decimal?)null };
        return new AlpacaPaperOrderRequest(order.Symbol.Value, Math.Abs(order.Quantity), order.Quantity > 0 ? AlpacaPaperOrderSide.Buy : AlpacaPaperOrderSide.Sell,
            type, tif, $"lean-{order.Id.ToString(CultureInfo.InvariantCulture)}", limit, stop);
    }

    private void EnsureConnected()
    {
        if (!_connected) throw new InvalidOperationException("AlpacaDirect paper brokerage is not connected.");
    }

    public override void Dispose()
    {
        if (_tradeStream is not null)
        {
            _tradeStream.OnTradeUpdate -= HandleTradeUpdate;
            Disconnect();
            _tradeStream.Dispose();
        }
        else Disconnect();
        _client.Dispose();
        base.Dispose();
    }
}

[BrokerageFactoryAttribute(typeof(AlpacaDirectBrokerageFactory))]
public sealed class AlpacaDirectBrokerageFactory : BrokerageFactory
{
    public AlpacaDirectBrokerageFactory() : base(typeof(AlpacaDirectBrokerage)) { }
    public override Dictionary<string, string> BrokerageData => new()
    {
        ["alpaca-direct-api-key"] = Config.Get("alpaca-direct-api-key"),
        ["alpaca-direct-api-secret"] = Config.Get("alpaca-direct-api-secret")
    };
    public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider) => new AlpacaBrokerageModel();
    public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
    {
        var key = job.BrokerageData.GetValueOrDefault("alpaca-direct-api-key") ?? Config.Get("alpaca-direct-api-key");
        var secret = job.BrokerageData.GetValueOrDefault("alpaca-direct-api-secret") ?? Config.Get("alpaca-direct-api-secret");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Set alpaca-direct-api-key and alpaca-direct-api-secret using paper credentials.");
        return new AlpacaDirectBrokerage(key, secret, algorithm);
    }
    public override void Dispose() { }
}
