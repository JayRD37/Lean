using System.Collections.Concurrent;
using System.ComponentModel.Composition;
using Alpaca.Markets;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Packets;

namespace QuantConnect.Brokerages.AlpacaDirect;

/// <summary>
/// LEAN live market-data queue backed by Alpaca's paper-account-authenticated IEX stream.
/// This uses Alpaca credentials directly and does not load the QuantConnect brokerage plugin.
/// </summary>
[Export(typeof(IDataQueueHandler))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class AlpacaDirectDataQueueHandler : IDataQueueHandler
{
    private readonly ConcurrentDictionary<SubscriptionDataConfig, SubscriptionState> _subscriptions = new();
    private IAlpacaDataStreamingClient? _stream;
    private volatile bool _connected;

    public bool IsConnected => _connected;

    public void SetJob(LiveNodePacket job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!job.BrokerageData.TryGetValue("alpaca-direct-api-key", out var key) || string.IsNullOrWhiteSpace(key) ||
            !job.BrokerageData.TryGetValue("alpaca-direct-api-secret", out var secret) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("AlpacaDirect market data requires alpaca-direct-api-key and alpaca-direct-api-secret.");
        if (!key.StartsWith("PK", StringComparison.Ordinal))
            throw new InvalidOperationException("AlpacaDirect market data is restricted to Alpaca paper API keys.");

        _stream = Environments.Paper.GetAlpacaDataStreamingClient(new SecretKey(key, secret));
        _stream.OnError += exception => _connected = false;
        _stream.SocketClosed += () => _connected = false;
        _stream.SocketOpened += () => _connected = true;
        var auth = _stream.ConnectAndAuthenticateAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (auth != AuthStatus.Authorized)
            throw new InvalidOperationException($"Alpaca market-data stream authentication failed ({auth}).");
        _connected = true;
    }

    public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
    {
        ArgumentNullException.ThrowIfNull(dataConfig);
        if (_stream is null || !_connected) throw new InvalidOperationException("AlpacaDirect data stream is not connected.");
        if (dataConfig.Symbol.ID.SecurityType != SecurityType.Equity || dataConfig.Symbol.ID.Market != Market.USA)
            throw new NotSupportedException("AlpacaDirect market data currently supports US equities only.");

        var buffer = new BlockingCollection<BaseData>(new ConcurrentQueue<BaseData>());
        var state = new SubscriptionState(buffer, newDataAvailableHandler);
        IAlpacaDataSubscription subscription;
        if (dataConfig.Resolution == Resolution.Tick)
        {
            var tradeSubscription = _stream.GetTradeSubscription(dataConfig.Symbol.Value);
            Action<ITrade> received = trade =>
            {
                buffer.Add(new Tick(trade.TimestampUtc, dataConfig.Symbol, string.Empty, trade.Exchange, trade.Size, trade.Price));
                newDataAvailableHandler?.Invoke(this, EventArgs.Empty);
            };
            tradeSubscription.Received += received;
            state.RemoveHandler = () => tradeSubscription.Received -= received;
            subscription = tradeSubscription;
        }
        else
        {
            var barSubscription = _stream.GetMinuteBarSubscription(dataConfig.Symbol.Value);
            Action<Alpaca.Markets.IBar> received = bar =>
            {
                buffer.Add(new TradeBar(bar.TimeUtc, dataConfig.Symbol, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, Time.OneMinute));
                newDataAvailableHandler?.Invoke(this, EventArgs.Empty);
            };
            barSubscription.Received += received;
            state.RemoveHandler = () => barSubscription.Received -= received;
            subscription = barSubscription;
        }
        state.Subscription = subscription;
        if (!_subscriptions.TryAdd(dataConfig, state))
        {
            state.RemoveHandler();
            buffer.Dispose();
            throw new InvalidOperationException($"LEAN requested duplicate AlpacaDirect subscription for {dataConfig.Symbol}.");
        }
        _stream.SubscribeAsync(subscription).AsTask().GetAwaiter().GetResult();
        return buffer.GetConsumingEnumerable().GetEnumerator();
    }

    public void Unsubscribe(SubscriptionDataConfig dataConfig)
    {
        if (!_subscriptions.TryRemove(dataConfig, out var state)) return;
        state.RemoveHandler?.Invoke();
        state.Buffer.CompleteAdding();
        state.Buffer.Dispose();
        if (_stream is not null && state.Subscription is not null)
            _stream.UnsubscribeAsync(state.Subscription).AsTask().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (var config in _subscriptions.Keys) Unsubscribe(config);
        if (_stream is not null)
        {
            _stream.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            _stream.Dispose();
        }
        _connected = false;
    }

    private sealed class SubscriptionState(BlockingCollection<BaseData> buffer, EventHandler handler)
    {
        public BlockingCollection<BaseData> Buffer { get; } = buffer;
        public EventHandler Handler { get; } = handler;
        public IAlpacaDataSubscription? Subscription { get; set; }
        public Action? RemoveHandler { get; set; }
    }
}
