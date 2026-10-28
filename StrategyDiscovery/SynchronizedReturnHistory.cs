namespace QuantConnect.Genetic;

/// <summary>Bounded returns indexed by their exact start and end timestamps.</summary>
public sealed class SynchronizedReturnHistory
{
    private readonly int _capacity;
    private readonly SortedDictionary<DateTime, ReturnInterval> _returns = new();
    private DateTime? _lastTime;
    private double _lastPrice;

    public SynchronizedReturnHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int Count => _returns.Count;

    public void Update(DateTime time, double price)
    {
        if (!double.IsFinite(price) || price <= 0) throw new ArgumentOutOfRangeException(nameof(price));
        if (_lastTime is { } previousTime && time <= previousTime) return;

        if (_lastTime is { } startTime)
        {
            _returns[time] = new ReturnInterval(startTime, time, (price / _lastPrice) - 1d);
            while (_returns.Count > _capacity) _returns.Remove(_returns.Keys.First());
        }

        _lastTime = time;
        _lastPrice = price;
    }

    public static double? Correlation(SynchronizedReturnHistory first, SynchronizedReturnHistory second, int minimumAlignedSamples = 20)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumAlignedSamples);

        var aligned = first._returns.Values
            .Where(interval => second._returns.TryGetValue(interval.EndTime, out var other) && other.StartTime == interval.StartTime)
            .Select(interval => (First: interval.Value, Second: second._returns[interval.EndTime].Value))
            .ToArray();
        if (aligned.Length < minimumAlignedSamples) return null;

        var firstMean = aligned.Average(pair => pair.First);
        var secondMean = aligned.Average(pair => pair.Second);
        var covariance = 0d;
        var firstVariance = 0d;
        var secondVariance = 0d;
        foreach (var pair in aligned)
        {
            var firstDelta = pair.First - firstMean;
            var secondDelta = pair.Second - secondMean;
            covariance += firstDelta * secondDelta;
            firstVariance += firstDelta * firstDelta;
            secondVariance += secondDelta * secondDelta;
        }

        return firstVariance <= 0 || secondVariance <= 0
            ? null
            : Math.Clamp(covariance / Math.Sqrt(firstVariance * secondVariance), -1d, 1d);
    }

    private readonly record struct ReturnInterval(DateTime StartTime, DateTime EndTime, double Value);
}

