namespace QuantConnect.Genetic;

public enum StrategyLifecycleAction
{
    None,
    Reevaluate,
    Retire
}

public sealed record StrategyLifecycleOptions
{
    public int ReevaluationIntervalDays { get; init; } = 30;
    public int MaximumConsecutiveNegativeReviews { get; init; } = 3;
    public double RetirementDrawdown { get; init; } = 0.10;

    public void Validate()
    {
        if (ReevaluationIntervalDays < 1) throw new ArgumentOutOfRangeException(nameof(ReevaluationIntervalDays));
        if (MaximumConsecutiveNegativeReviews < 1) throw new ArgumentOutOfRangeException(nameof(MaximumConsecutiveNegativeReviews));
        if (!double.IsFinite(RetirementDrawdown) || RetirementDrawdown <= 0 || RetirementDrawdown >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(RetirementDrawdown), "RetirementDrawdown must be between zero and one.");
        }
    }
}

public sealed record StrategyLifecycleReview(
    StrategyLifecycleAction Action,
    DateOnly ReviewDate,
    double PeriodReturn,
    double DrawdownFromPeak,
    int ConsecutiveNegativeReviews);

/// <summary>
/// Tracks portfolio health and emits scheduled reevaluation or retirement decisions.
/// The host remains responsible for launching a new optimization and deploying its result.
/// </summary>
public sealed class StrategyLifecycleMonitor
{
    private readonly StrategyLifecycleOptions _options;
    private DateOnly _lastReviewDate;
    private DateOnly _nextReviewDate;
    private decimal _lastReviewEquity;
    private decimal _peakEquity;
    private int _consecutiveNegativeReviews;
    private bool _retired;

    public StrategyLifecycleMonitor(StrategyLifecycleOptions options, DateOnly startDate, decimal startingEquity)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        if (startingEquity <= 0) throw new ArgumentOutOfRangeException(nameof(startingEquity));

        _lastReviewDate = startDate;
        _nextReviewDate = startDate.AddDays(_options.ReevaluationIntervalDays);
        _lastReviewEquity = startingEquity;
        _peakEquity = startingEquity;
    }

    public bool IsRetired => _retired;
    public DateOnly NextReviewDate => _nextReviewDate;

    public StrategyLifecycleReview Observe(DateOnly date, decimal equity)
    {
        if (equity <= 0) throw new ArgumentOutOfRangeException(nameof(equity), "Equity must remain positive.");
        if (_retired)
        {
            return new StrategyLifecycleReview(StrategyLifecycleAction.Retire, date, 0, Drawdown(equity), _consecutiveNegativeReviews);
        }
        if (date < _lastReviewDate) throw new ArgumentException("Lifecycle observations must be chronological.", nameof(date));

        if (equity > _peakEquity) _peakEquity = equity;
        var drawdown = Drawdown(equity);
        if (drawdown >= _options.RetirementDrawdown)
        {
            _retired = true;
            return new StrategyLifecycleReview(StrategyLifecycleAction.Retire, date, PeriodReturn(equity), drawdown, _consecutiveNegativeReviews);
        }

        if (date < _nextReviewDate)
        {
            return new StrategyLifecycleReview(StrategyLifecycleAction.None, date, PeriodReturn(equity), drawdown, _consecutiveNegativeReviews);
        }

        var periodReturn = PeriodReturn(equity);
        _consecutiveNegativeReviews = periodReturn < 0 ? _consecutiveNegativeReviews + 1 : 0;
        _lastReviewDate = date;
        _lastReviewEquity = equity;
        _nextReviewDate = date.AddDays(_options.ReevaluationIntervalDays);

        if (_consecutiveNegativeReviews >= _options.MaximumConsecutiveNegativeReviews)
        {
            _retired = true;
            return new StrategyLifecycleReview(StrategyLifecycleAction.Retire, date, periodReturn, drawdown, _consecutiveNegativeReviews);
        }

        return new StrategyLifecycleReview(StrategyLifecycleAction.Reevaluate, date, periodReturn, drawdown, _consecutiveNegativeReviews);
    }

    private double Drawdown(decimal equity) => (double)(1m - (equity / _peakEquity));

    private double PeriodReturn(decimal equity) => (double)((equity / _lastReviewEquity) - 1m);
}
