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
using QuantConnect.Data.UniverseSelection;
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
    private readonly ManagedSymbolRegistry<Symbol> _managedSymbols = new();
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
    private bool _auditPortfolioSelection;
    private bool _useTopDollarVolumeUniverse;
    private int _universeSize;
    private PortfolioDrawdownGuard _drawdownGuard = null!;
    private StrategyLifecycleMonitor _lifecycleMonitor;
    private StrategyLifecycleOptions _lifecycleOptions;

    /// <summary>True when LEAN may submit new strategy orders.</summary>
    public bool CanSubmitStrategyOrders => _brokerageSafety.CanSubmitOrders;

    /// <summary>True after an invalid order has latched the execution safety shutdown.</summary>
    public bool ExecutionSafetyTripped => _brokerageSafety.IsTripped;

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
        _auditPortfolioSelection = string.Equals(GetParameter("discovery.audit-portfolio-selection", "false"), "true", StringComparison.OrdinalIgnoreCase);
        var universeMode = GetParameter("discovery.universe-mode", "Configured");
        _useTopDollarVolumeUniverse = string.Equals(universeMode, "TopDollarVolume", StringComparison.OrdinalIgnoreCase);
        if (!_useTopDollarVolumeUniverse && !string.Equals(universeMode, "Configured", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("discovery.universe-mode must be Configured or TopDollarVolume.");
        _universeSize = GetParameter("discovery.universe-size", 20);
        _killSwitchFile = GetParameter("discovery.kill-switch-file", "emergency-stop");

        ValidateGenome();

        PaperExecutionAuthorization.Validate(
            LiveMode,
            string.Equals(GetParameter("discovery.allow-paper-trading", "false"), "true", StringComparison.OrdinalIgnoreCase),
            Config.GetBool("alpaca-paper-trading", false),
            Config.Get("live-mode-brokerage", string.Empty));

        SetBrokerageModel(BrokerageName.Alpaca, AccountType.Margin);
        AddSecurityInitializer(security =>
        {
            security.SetSlippageModel(new SpreadAndVolumeShareSlippageModel(_minimumSpreadBasisPoints));
            if (security.Type == SecurityType.Equity && security.Symbol.ID.Market == Market.USA)
            {
                security.SetFeeModel(new AlpacaEquityRegulatoryFeeModel());
            }
        });
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
            _managedSymbols.AddAnchor(symbol);
            AddSymbolState(symbol, warmUp: false);
        }
        if (_useTopDollarVolumeUniverse)
        {
            UniverseSettings.Resolution = _resolution;
            UniverseSettings.Leverage = 1m;
            AddUniverse(SelectTopDollarVolume);
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
            if (_managedSymbols.Symbols.All(symbol => !Portfolio[symbol].Invested)) Quit("Emergency shutdown complete.");
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
            state.UpdateReturns(bar.EndTime, (double)bar.Close);
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
        if (_auditPortfolioSelection)
        {
            var selectedTickers = string.Join(',', selected.Select(signal => signal.State.Symbol.Value).Order(StringComparer.Ordinal));
            Debug($"Portfolio selection audit: symbols={selectedTickers} positionCount={selected.Count} availablePositions={riskBudget.AvailablePositions} targetWeight={targetWeight.ToString("P2", CultureInfo.InvariantCulture)} grossTarget={(targetWeight * selected.Count).ToString("P2", CultureInfo.InvariantCulture)} maxGross={_maxExposurePercent}%");
        }
        SetHoldings(targets, liquidateExistingHoldings: false, tag: $"{_family} {_resolution}{confirmationTag}");
    }

    public override void OnBrokerageDisconnect()
    {
        _brokerageSafety.Disconnect();
        Debug("Brokerage disconnected; new strategy orders are paused until reconnection.");
    }

    public override void OnSecuritiesChanged(SecurityChanges changes)
    {
        foreach (var security in changes.AddedSecurities)
        {
            if (_managedSymbols.IsAnchor(security.Symbol)) continue;
            AddSymbolState(security.Symbol, warmUp: true);
        }

        foreach (var security in changes.RemovedSecurities)
        {
            if (_managedSymbols.IsAnchor(security.Symbol)) continue;
            if (Portfolio[security.Symbol].Invested)
            {
                Liquidate(security.Symbol, tag: "point-in-time universe removal");
            }
            _symbols.Remove(security.Symbol);
            _managedSymbols.RemoveUniverseMember(security.Symbol, Portfolio[security.Symbol].Invested);
        }
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
        if (_managedSymbols.Contains(orderEvent.Symbol) &&
            (orderEvent.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled or OrderStatus.Canceled or OrderStatus.Invalid))
        {
            _managedSymbols.OnPositionStateChanged(orderEvent.Symbol, Portfolio[orderEvent.Symbol].Invested);
        }
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
            if (selected.Any(existing => Correlation(candidate.State, existing.State) is not { } correlation || Math.Abs(correlation) >= _maximumCorrelation)) continue;
            selected.Add(candidate);
            if (selected.Count == availablePositions) break;
        }

        return selected;
    }

    private PortfolioRiskBudget GetAccountRiskBudget()
    {
        var externalHoldings = Portfolio.Values
            .Where(holding => holding.Invested && !_managedSymbols.Contains(holding.Symbol))
            .Select(holding => holding.AbsoluteHoldingsValue);
        return PortfolioRiskBudget.Calculate(
            _maxPositions,
            _maxExposurePercent / 100m,
            Portfolio.TotalPortfolioValue,
            externalHoldings);
    }

    private void LiquidateManagedPositions(string tag)
    {
        foreach (var symbol in _managedSymbols.Symbols)
        {
            if (Portfolio[symbol].Invested) Liquidate(symbol, tag: tag);
        }
    }

    private void AddSymbolState(Symbol symbol, bool warmUp)
    {
        if (_symbols.ContainsKey(symbol)) return;
        var state = new SymbolState(symbol, SMA(symbol, _fastPeriod), SMA(symbol, _slowPeriod), STD(symbol, _slowPeriod));
        if (_confirmationResolution is { } confirmationResolution)
        {
            state.SetConfirmationIndicators(
                SMA(symbol, _fastPeriod, confirmationResolution),
                SMA(symbol, _slowPeriod, confirmationResolution),
                STD(symbol, _slowPeriod, confirmationResolution));
        }
        _symbols.Add(symbol, state);
        _managedSymbols.AddUniverseMember(symbol);
        if (!warmUp) return;

        WarmUpIndicator(symbol, state.Fast, _resolution);
        WarmUpIndicator(symbol, state.Slow, _resolution);
        WarmUpIndicator(symbol, state.Volatility, _resolution);
        if (_confirmationResolution is { } resolution && state.ConfirmationFast is not null)
        {
            WarmUpIndicator(symbol, state.ConfirmationFast, resolution);
            WarmUpIndicator(symbol, state.ConfirmationSlow!, resolution);
            WarmUpIndicator(symbol, state.ConfirmationVolatility!, resolution);
        }
        foreach (var bar in History<TradeBar>(symbol, CorrelationWindow + 1, _resolution))
        {
            state.UpdateReturns(bar.EndTime, (double)bar.Close);
        }
    }

    private IEnumerable<Symbol> SelectTopDollarVolume(IEnumerable<CoarseFundamental> coarse)
    {
        var records = coarse.ToArray();
        var selected = records
            .Where(item => item.Price >= 5m && item.DollarVolume >= 25_000_000d && !_managedSymbols.IsAnchor(item.Symbol))
            .OrderByDescending(item => item.DollarVolume)
            .ThenBy(item => item.Symbol.Value, StringComparer.Ordinal)
            .Take(_universeSize)
            .Select(item => item.Symbol)
            .ToArray();
        var eligibleCount = records.Count(item => item.Price >= 5m && item.DollarVolume >= 25_000_000d);
        var sample = string.Join(';', records.Take(3).Select(item => $"{item.Symbol.Value}:{item.Price}:{item.DollarVolume}"));
        Debug($"Point-in-time coarse universe selected {selected.Length} securities from {records.Length} records ({eligibleCount} eligible; sample={sample}): {string.Join(',', selected.Select(symbol => symbol.Value))}");
        return selected;
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

    private static double? Correlation(SymbolState first, SymbolState second)
        => SynchronizedReturnHistory.Correlation(first.Returns, second.Returns);

    private void ValidateGenome()
    {
        if (_fastPeriod is < StrategyGenome.MinFastPeriod or > StrategyGenome.MaxFastPeriod) throw new ArgumentOutOfRangeException("discovery.fast-period");
        if (_slowPeriod <= _fastPeriod || _slowPeriod > StrategyGenome.MaxSlowPeriod) throw new ArgumentOutOfRangeException("discovery.slow-period");
        if (_entryThreshold <= _exitThreshold || _exitThreshold <= 0) throw new ArgumentException("The entry threshold must exceed the positive exit threshold.");
        if (_maxPositions is < StrategyGenome.MinMaxPositions or > StrategyGenome.MaxMaxPositions) throw new ArgumentOutOfRangeException("discovery.max-positions");
        if (_maxExposurePercent is < StrategyGenome.MinExposurePct or > StrategyGenome.MaxExposurePct) throw new ArgumentOutOfRangeException("discovery.max-exposure-percent");
        if (_universeSize is < 1 or > 100) throw new ArgumentOutOfRangeException("discovery.universe-size");
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
        public SynchronizedReturnHistory Returns { get; } = new(CorrelationWindow);
        public bool MeanReversionEntered { get; set; }
        public bool TrendEntered { get; set; }
        public void SetConfirmationIndicators(SimpleMovingAverage fast, SimpleMovingAverage slow, StandardDeviation volatility)
        {
            ConfirmationFast = fast;
            ConfirmationSlow = slow;
            ConfirmationVolatility = volatility;
        }

        public void UpdateReturns(DateTime time, double close) => Returns.Update(time, close);
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



