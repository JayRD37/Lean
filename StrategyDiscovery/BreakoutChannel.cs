namespace QuantConnect.Genetic;

public sealed class BreakoutChannel
{
    private readonly int _entryPeriod;
    private readonly int _exitPeriod;
    private readonly double _entryThresholdPercent;
    private readonly Queue<PriceBar> _history = new();
    private DateTime _lastEndTime;
    private bool _hasTimestamp;
    private bool _entered;

    public BreakoutChannel(int entryPeriod, int exitPeriod, double entryThresholdPercent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryPeriod);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exitPeriod);
        if (exitPeriod <= entryPeriod) throw new ArgumentException("The exit period must be longer than the entry period.", nameof(exitPeriod));
        if (!double.IsFinite(entryThresholdPercent) || entryThresholdPercent < 0)
            throw new ArgumentOutOfRangeException(nameof(entryThresholdPercent));

        _entryPeriod = entryPeriod;
        _exitPeriod = exitPeriod;
        _entryThresholdPercent = entryThresholdPercent;
    }

    public BreakoutSnapshot Current { get; private set; }

    public BreakoutSnapshot Update(DateTime endTime, decimal high, decimal low, decimal close, bool allowSignals = true)
    {
        if (high <= 0 || low <= 0 || close <= 0 || high < low || close < low || close > high)
            throw new ArgumentOutOfRangeException(nameof(close), "Breakout bar prices must be positive and internally consistent.");
        if (_hasTimestamp && endTime <= _lastEndTime) return Current;

        var ready = _history.Count >= _exitPeriod;
        decimal entryLevel = 0;
        decimal exitLevel = 0;
        if (ready)
        {
            var priorBars = _history.ToArray();
            entryLevel = priorBars.TakeLast(_entryPeriod).Max(bar => bar.High);
            exitLevel = priorBars.TakeLast(_exitPeriod).Min(bar => bar.Low);
            if (!allowSignals)
            {
                _entered = false;
            }
            else if (_entered)
            {
                if (close <= exitLevel) _entered = false;
            }
            else if ((double)close > (double)entryLevel * (1d + (_entryThresholdPercent / 100d)))
            {
                _entered = true;
            }
        }

        var strength = ready && _entered
            ? Math.Max(0d, ((double)close / (double)exitLevel) - 1d)
            : 0d;
        Current = new BreakoutSnapshot(ready, _entered, strength, entryLevel, exitLevel);
        _history.Enqueue(new PriceBar(high, low));
        while (_history.Count > _exitPeriod) _history.Dequeue();
        _lastEndTime = endTime;
        _hasTimestamp = true;
        return Current;
    }

    private readonly record struct PriceBar(decimal High, decimal Low);
}

public readonly record struct BreakoutSnapshot(
    bool IsReady,
    bool IsEntered,
    double Strength,
    decimal EntryLevel,
    decimal ExitLevel);
