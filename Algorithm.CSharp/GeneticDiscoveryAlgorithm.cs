using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Genetic;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Orders.Slippage;
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.CSharp;

/// <summary>
/// Parameterized long-only strategy used by the genetic optimizer. Each genome is evaluated
/// by the normal LEAN backtest runner and brokerage/risk models.
/// </summary>
public sealed class GeneticDiscoveryAlgorithm : QCAlgorithm
{
    private const int CorrelationWindow = 60;
    private readonly Dictionary<Symbol, SymbolState> _symbols = new();
    private readonly BrokerageSafetyGate _brokerageSafety = new();
    private StrategyFamily _family;
    private Resolution _resolution;
    private StrategyTimeframe? _confirmationTimeframe;
    private Resolution? _confirmationResolution;
    private int _maxPositions;
    private int _maxExposurePercent;
    private decimal _minimumSpreadBasisPoints;
    private double _maximumCorrelation;
    private double _maximumPortfolioDrawdown;
    private int _fastPeriod;
    private int _slowPeriod;
    private double _entryThreshold;
    private double _exitThreshold;
    private string _killSwitchFile;
    private bool _shutdownRequested;
    private PortfolioDrawdownGuard _drawdownGuard = null!;
    private StrategyLifecycleMonitor _lifecycleMonitor;
    private StrategyLifecycleOptions _lifecycleOptions;

    public override void Initialize()
    {
        _family = ParseEnum(GetParameter("discovery.family", nameof(StrategyFamily.TrendFollowing)), StrategyFamily.TrendFollowing);
        _resolution = ParseResolution(GetParameter("discovery.timeframe", nameof(StrategyTimeframe.Daily)));
        _confirmationTimeframe = ParseOptionalTimeframe(GetParameter("discovery.confirmation-timeframe", "None"));
        _confirmationResolution = _confirmationTimeframe is { } confirmation ? ParseResolution(confirmation.ToString()) : null;
        Settings.DailyPreciseEndTime = _resolution != Resolution.Daily;
        _fastPeriod = GetParameter("discovery.fast-period", 10);
        _slowPeriod = GetParameter("discovery.slow-period", 40);
        _entryThreshold = GetParameter("discovery.entry-tenths", 12) / 10d;
        _exitThreshold = GetParameter("discovery.exit-tenths", 4) / 10d;
        _maxPositions = GetParameter("discovery.max-positions", 3);
        _maxExposurePercent = GetParameter("discovery.max-exposure-percent", 50);
        _minimumSpreadBasisPoints = (decimal)GetParameter("discovery.minimum-spread-bps", 5d);
        _maximumCorrelation = GetParameter("discovery.max-correlation", 0.85d);
        _maximumPortfolioDrawdown = GetParameter("discovery.max-drawdown", 0.10d);
        _killSwitchFile = GetParameter("discovery.kill-switch-file", "emergency-stop");

        ValidateGenome();

        PaperExecutionAuthorization.Validate(
            LiveMode,
            string.Equals(GetParameter("discovery.allow-paper-trading", "false"), "true", StringComparison.OrdinalIgnoreCase),
            Config.GetBool("alpaca-paper-trading", false),
            Config.Get("live-mode-brokerage", string.Empty));

        SetBrokerageModel(BrokerageName.Alpaca, AccountType.Margin);
        AddSecurityInitializer(security => security.SetSlippageModel(new SpreadAndVolumeShareSlippageModel(_minimumSpreadBasisPoints)));
        if (!LiveMode)
        {
            var phase = GetParameter("discovery.phase", "Training");
            var startText = string.Equals(phase, "Validation", StringComparison.OrdinalIgnoreCase)
                ? GetParameter("discovery.validation-start", "2023-01-01")
                : GetParameter("discovery.train-start", "2018-01-01");
            var endText = string.Equals(phase, "Validation", StringComparison.OrdinalIgnoreCase)
                ? GetParameter("discovery.validation-end", "2024-12-31")
                : GetParameter("discovery.train-end", "2022-12-31");
            SetStartDate(ParseDate(startText));
            SetEndDate(ParseDate(endText));
            SetCash(100_000);
        }

        SetBenchmark("SPY");
        var symbols = GetParameter("discovery.symbols", "SPY|QQQ|IWM")
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (symbols.Length == 0) throw new ArgumentException("discovery.symbols must contain at least one ticker.");

        foreach (var ticker in symbols)
        {
            var symbol = AddEquity(ticker, _resolution, leverage: 1m).Symbol;
            var state = new SymbolState(symbol, SMA(symbol, _fastPeriod), SMA(symbol, _slowPeriod), STD(symbol, _slowPeriod));
            if (_confirmationResolution is { } confirmationResolution)
            {
                state.SetConfirmationIndicators(
                    SMA(symbol, _fastPeriod, confirmationResolution),
                    SMA(symbol, _slowPeriod, confirmationResolution),
                    STD(symbol, _slowPeriod, confirmationResolution));
            }
            _symbols.Add(symbol, state);
        }

        var warmupResolution = _confirmationResolution ?? _resolution;
        SetWarmUp(_slowPeriod + CorrelationWindow, warmupResolution);
        _drawdownGuard = new PortfolioDrawdownGuard(
            (decimal)_maximumPortfolioDrawdown,
            Portfolio.TotalPortfolioValue,
            GetParameter("discovery.drawdown-state-file", string.Empty));
        _lifecycleOptions = new StrategyLifecycleOptions
        {
            ReevaluationIntervalDays = GetParameter("discovery.reevaluation-interval-days", 30),
            MaximumConsecutiveNegativeReviews = GetParameter("discovery.max-negative-review-periods", 3),
            RetirementDrawdown = GetParameter("discovery.retirement-drawdown", 0.15d)
        };
    }

    public override void OnData(Slice slice)
    {
        foreach (var split in slice.Splits.Values)
        {
            Debug($"Corporate action: split symbol={split.Symbol.Value} type={split.Type} factor={split.SplitFactor.ToString(CultureInfo.InvariantCulture)} reference={split.ReferencePrice.ToString(CultureInfo.InvariantCulture)} holdingQuantity={Portfolio[split.Symbol].Quantity.ToString(CultureInfo.InvariantCulture)}");
        }
        foreach (var dividend in slice.Dividends.Values)
        {
            Debug($"Corporate action: dividend symbol={dividend.Symbol.Value} distribution={dividend.Distribution.ToString(CultureInfo.InvariantCulture)}");
        }

        if (_shutdownRequested || _brokerageSafety.MustLiquidate)
        {
            _shutdownRequested = true;
            if (!_brokerageSafety.CanLiquidate) return;
            LiquidateManagedPositions("emergency shutdown");
            if (_symbols.Keys.All(symbol => !Portfolio[symbol].Invested)) Quit("Emergency shutdown complete.");
            return;
        }

        if (ShouldShutdown())
        {
            _shutdownRequested = true;
            if (_brokerageSafety.CanLiquidate) LiquidateManagedPositions("drawdown or kill switch");
            return;
        }

        foreach (var state in _symbols.Values)
        {
            if (!slice.Bars.TryGetValue(state.Symbol, out TradeBar bar)) continue;
            state.UpdateReturns((double)bar.Close);
        }

        if (IsWarmingUp) return;

        if (!_brokerageSafety.CanSubmitOrders) return;

        var equity = Portfolio.TotalPortfolioValue;
        if (equity <= 0) return;
        _lifecycleMonitor ??= new StrategyLifecycleMonitor(_lifecycleOptions, DateOnly.FromDateTime(Time), equity);
        var health = _lifecycleMonitor.Observe(DateOnly.FromDateTime(Time), equity);
        if (health.Action == StrategyLifecycleAction.Retire)
        {
            _shutdownRequested = true;
            Debug($"Strategy retired after scheduled health review: return={health.PeriodReturn:P2}, drawdown={health.DrawdownFromPeak:P2}, negativeReviews={health.ConsecutiveNegativeReviews}.");
            LiquidateManagedPositions("strategy health retirement");
            return;
        }
        if (health.Action == StrategyLifecycleAction.Reevaluate)
        {
            Debug($"Scheduled strategy reevaluation requested: return={health.PeriodReturn:P2}, drawdown={health.DrawdownFromPeak:P2}.");
        }

        var candidates = new List<TradeSignal>();
        foreach (var state in _symbols.Values)
        {
            if (_confirmationTimeframe is { } confirmation && state.ConfirmationReady && !state.ConfirmationReadinessLogged)
            {
                state.ConfirmationReadinessLogged = true;
                Debug($"Multi-timeframe confirmation ready: symbol={state.Symbol.Value} primary={_resolution} confirmation={confirmation} fast={state.ConfirmationFast!.Current.Value} slow={state.ConfirmationSlow!.Current.Value}");
            }

            if (!state.Fast.IsReady || !state.Slow.IsReady || !state.Volatility.IsReady) continue;
            var price = Securities[state.Symbol].Price;
            if (price <= 0 || state.Volatility.Current.Value <= 0) continue;

            var zScore = ((double)price - (double)state.Slow.Current.Value) / (double)state.Volatility.Current.Value;
            double strength;
            if (_family == StrategyFamily.TrendFollowing)
            {
                state.MeanReversionEntered = false;
                var spread = (double)(state.Fast.Current.Value - state.Slow.Current.Value) / (double)price;
                if (spread >= _entryThreshold / 100d) state.TrendEntered = true;
                else if (spread <= _exitThreshold / 100d) state.TrendEntered = false;
                strength = state.TrendEntered ? Math.Max(0, spread) : 0;
            }
            else
            {
                if (zScore <= -_entryThreshold) state.MeanReversionEntered = true;
                else if (zScore >= -_exitThreshold) state.MeanReversionEntered = false;
                strength = state.MeanReversionEntered ? Math.Max(0, -zScore) : 0;
            }

            if (strength > 0 && (_confirmationTimeframe is null || ConfirmationAgrees(state, price)))
            {
                candidates.Add(new TradeSignal(state, strength));
            }
        }

        var riskBudget = GetAccountRiskBudget();
        var selected = SelectDiversified(candidates, riskBudget.AvailablePositions);
        var targetWeight = selected.Count == 0 ? 0m : riskBudget.AvailableGrossExposureFraction / selected.Count;
        var selectedSymbols = selected.Select(signal => signal.State.Symbol).ToHashSet();
        var targets = _symbols.Keys
            .Select(symbol => new PortfolioTarget(symbol, selectedSymbols.Contains(symbol) ? targetWeight : 0m))
            .ToList();
        if (_resolution != Resolution.Daily && _symbols.Keys.Any(symbol => !Securities[symbol].Exchange.ExchangeOpen))
        {
            // LEAN converts closed-market equity market orders into MOO orders; brokerage
            // adapters reject those outside their pre-open submission window. The next
            // regular-session bar will recompute targets from the latest completed bars.
            return;
        }

        var confirmationTag = _confirmationTimeframe is { } confirm ? $"+{confirm}" : string.Empty;
        SetHoldings(targets, liquidateExistingHoldings: false, tag: $"{_family} {_resolution}{confirmationTag}");
    }

    public override void OnBrokerageDisconnect()
    {
        _brokerageSafety.Disconnect();
        Debug("Brokerage disconnected; new strategy orders are paused until reconnection.");
    }

    public override void OnBrokerageReconnect()
    {
        _brokerageSafety.Reconnect();
        Debug(_brokerageSafety.CanSubmitOrders
            ? "Brokerage reconnected; strategy order submission resumed."
            : "Brokerage reconnected, but the execution safety gate remains tripped.");
    }

    public override void OnBrokerageMessage(BrokerageMessageEvent messageEvent)
    {
        Debug($"Brokerage message type={messageEvent.Type}, code={messageEvent.Code}: {messageEvent.Message}");
    }

    public override void OnOrderEvent(OrderEvent orderEvent)
    {
        if (orderEvent.Status != OrderStatus.Invalid) return;

        var reason = $"Order {orderEvent.OrderId} for {orderEvent.Symbol} was invalid: {orderEvent.Message}";
        _brokerageSafety.Trip(reason);
        _shutdownRequested = true;
        Debug($"Execution safety gate tripped; liquidation is deferred to the next data event. {reason}");
    }

    /// <summary>Provides an in-process kill switch and can also be called by an algorithm host.</summary>
    public void RequestEmergencyShutdown(string reason)
    {
        _shutdownRequested = true;
        Debug($"Emergency shutdown requested: {reason}");
        if (_brokerageSafety.CanLiquidate) LiquidateManagedPositions($"emergency shutdown: {reason}");
    }

    private List<TradeSignal> SelectDiversified(IReadOnlyList<TradeSignal> candidates, int availablePositions)
    {
        var selected = new List<TradeSignal>(Math.Min(_maxPositions, availablePositions));
        if (availablePositions <= 0) return selected;
        foreach (var candidate in candidates.OrderByDescending(item => item.Strength).ThenBy(item => item.State.Symbol.Value, StringComparer.Ordinal))
        {
            if (selected.Any(existing => Math.Abs(Correlation(candidate.State, existing.State)) >= _maximumCorrelation)) continue;
            selected.Add(candidate);
            if (selected.Count == availablePositions) break;
        }

        return selected;
    }

    private PortfolioRiskBudget GetAccountRiskBudget()
    {
        var managedSymbols = _symbols.Keys.ToHashSet();
        var externalHoldings = Portfolio.Values
            .Where(holding => holding.Invested && !managedSymbols.Contains(holding.Symbol))
            .Select(holding => holding.AbsoluteHoldingsValue);
        return PortfolioRiskBudget.Calculate(
            _maxPositions,
            _maxExposurePercent / 100m,
            Portfolio.TotalPortfolioValue,
            externalHoldings);
    }

    private void LiquidateManagedPositions(string tag)
    {
        foreach (var symbol in _symbols.Keys)
        {
            if (Portfolio[symbol].Invested) Liquidate(symbol, tag: tag);
        }
    }

    private bool ShouldShutdown()
    {
        if (!string.IsNullOrWhiteSpace(_killSwitchFile) && File.Exists(_killSwitchFile))
        {
            Debug($"Emergency stop file detected at '{_killSwitchFile}'.");
            return true;
        }

        return _drawdownGuard.Update(Portfolio.TotalPortfolioValue);
    }

    private bool ConfirmationAgrees(SymbolState state, decimal price)
    {
        if (state.ConfirmationFast is not { IsReady: true } fast ||
            state.ConfirmationSlow is not { IsReady: true } slow ||
            state.ConfirmationVolatility is not { IsReady: true } volatility ||
            volatility.Current.Value <= 0)
        {
            return false;
        }

        if (_family == StrategyFamily.TrendFollowing)
        {
            return fast.Current.Value > slow.Current.Value;
        }

        var zScore = ((double)price - (double)slow.Current.Value) / (double)volatility.Current.Value;
        return zScore <= -_exitThreshold;
    }

    private static double Correlation(SymbolState first, SymbolState second)
    {
        var count = Math.Min(first.Returns.Count, second.Returns.Count);
        if (count < 20) return 0;

        var firstMean = 0d;
        var secondMean = 0d;
        for (var index = 0; index < count; index++)
        {
            firstMean += first.Returns[index];
            secondMean += second.Returns[index];
        }
        firstMean /= count;
        secondMean /= count;

        var covariance = 0d;
        var firstVariance = 0d;
        var secondVariance = 0d;
        for (var index = 0; index < count; index++)
        {
            var firstDelta = first.Returns[index] - firstMean;
            var secondDelta = second.Returns[index] - secondMean;
            covariance += firstDelta * secondDelta;
            firstVariance += firstDelta * firstDelta;
            secondVariance += secondDelta * secondDelta;
        }

        return firstVariance <= 0 || secondVariance <= 0
            ? 0
            : covariance / Math.Sqrt(firstVariance * secondVariance);
    }

    private void ValidateGenome()
    {
        if (_fastPeriod is < StrategyGenome.MinFastPeriod or > StrategyGenome.MaxFastPeriod) throw new ArgumentOutOfRangeException("discovery.fast-period");
        if (_slowPeriod <= _fastPeriod || _slowPeriod > StrategyGenome.MaxSlowPeriod) throw new ArgumentOutOfRangeException("discovery.slow-period");
        if (_entryThreshold <= _exitThreshold || _exitThreshold <= 0) throw new ArgumentException("The entry threshold must exceed the positive exit threshold.");
        if (_maxPositions is < StrategyGenome.MinMaxPositions or > StrategyGenome.MaxMaxPositions) throw new ArgumentOutOfRangeException("discovery.max-positions");
        if (_maxExposurePercent is < StrategyGenome.MinExposurePct or > StrategyGenome.MaxExposurePct) throw new ArgumentOutOfRangeException("discovery.max-exposure-percent");
        if (_minimumSpreadBasisPoints < 0 || _minimumSpreadBasisPoints > 500) throw new ArgumentOutOfRangeException("discovery.minimum-spread-bps");
        if (_maximumCorrelation is < 0 or > 1) throw new ArgumentOutOfRangeException("discovery.max-correlation");
        if (_maximumPortfolioDrawdown is <= 0 or >= 1) throw new ArgumentOutOfRangeException("discovery.max-drawdown");
        if (_confirmationTimeframe is { } confirmation && confirmation <= ParseTimeframe(_resolution))
        {
            throw new ArgumentException("The confirmation timeframe must be slower than the primary timeframe.", "discovery.confirmation-timeframe");
        }
    }

    private static T ParseEnum<T>(string value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;

    private static Resolution ParseResolution(string value) =>
        Enum.TryParse<StrategyTimeframe>(value, true, out var timeframe)
            ? ParseResolution(timeframe)
            : throw new ArgumentException($"Unsupported discovery timeframe '{value}'.");

    private static Resolution ParseResolution(StrategyTimeframe timeframe) => timeframe switch
    {
        StrategyTimeframe.Minute => Resolution.Minute,
        StrategyTimeframe.Hour => Resolution.Hour,
        _ => Resolution.Daily
    };

    private static StrategyTimeframe? ParseOptionalTimeframe(string value)
    {
        if (string.Equals(value, "None", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<StrategyTimeframe>(value, true, out var timeframe)
            ? timeframe
            : throw new ArgumentException($"Unsupported confirmation timeframe '{value}'.");
    }

    private static StrategyTimeframe ParseTimeframe(Resolution resolution) => resolution switch
    {
        Resolution.Minute => StrategyTimeframe.Minute,
        Resolution.Hour => StrategyTimeframe.Hour,
        _ => StrategyTimeframe.Daily
    };

    private static DateTime ParseDate(string value) => DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class SymbolState(Symbol symbol, SimpleMovingAverage fast, SimpleMovingAverage slow, StandardDeviation volatility)
    {
        public Symbol Symbol { get; } = symbol;
        public SimpleMovingAverage Fast { get; } = fast;
        public SimpleMovingAverage Slow { get; } = slow;
        public StandardDeviation Volatility { get; } = volatility;
        public SimpleMovingAverage? ConfirmationFast { get; private set; }
        public SimpleMovingAverage? ConfirmationSlow { get; private set; }
        public StandardDeviation? ConfirmationVolatility { get; private set; }
        public bool ConfirmationReadinessLogged { get; set; }
        public bool ConfirmationReady => ConfirmationFast?.IsReady == true &&
                                        ConfirmationSlow?.IsReady == true &&
                                        ConfirmationVolatility?.IsReady == true;
        public List<double> Returns { get; } = new(CorrelationWindow);
        public bool MeanReversionEntered { get; set; }
        public bool TrendEntered { get; set; }
        private double _lastPrice;

        public void SetConfirmationIndicators(SimpleMovingAverage fast, SimpleMovingAverage slow, StandardDeviation volatility)
        {
            ConfirmationFast = fast;
            ConfirmationSlow = slow;
            ConfirmationVolatility = volatility;
        }

        public void UpdateReturns(double close)
        {
            if (_lastPrice > 0 && close > 0)
            {
                Returns.Add((close / _lastPrice) - 1d);
                if (Returns.Count > CorrelationWindow) Returns.RemoveAt(0);
            }
            _lastPrice = close;
        }
    }

    private sealed record TradeSignal(SymbolState State, double Strength);

    private sealed class SpreadAndVolumeShareSlippageModel(decimal fullSpreadBasisPoints) : ISlippageModel
    {
        private readonly VolumeShareSlippageModel _volumeImpact = new(0.025m, 0.10m);

        public decimal GetSlippageApproximation(Security asset, Order order)
        {
            var lastData = asset.GetLastData();
            if (lastData is null) return 0m;
            var referencePrice = order.Type == OrderType.MarketOnOpen && lastData is IBar bar
                ? bar.Open
                : lastData.Value;
            return ExecutionCostAssumptions.HalfSpreadCost(referencePrice, fullSpreadBasisPoints)
                + _volumeImpact.GetSlippageApproximation(asset, order);
        }
    }
}


