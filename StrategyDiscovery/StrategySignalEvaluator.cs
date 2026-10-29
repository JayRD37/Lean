namespace QuantConnect.Genetic;

/// <summary>Shared entry, exit, strength, and confirmation rules used by LEAN and the research preview.</summary>
public static class StrategySignalEvaluator
{
    public static StrategySignalEvaluation Evaluate(
        StrategyFamily family,
        bool trendEntered,
        bool meanReversionEntered,
        double entryThreshold,
        double exitThreshold,
        decimal price,
        decimal fastAverage,
        decimal slowAverage,
        decimal volatility,
        BreakoutSnapshot breakout)
    {
        if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));
        if (volatility <= 0) throw new ArgumentOutOfRangeException(nameof(volatility));
        if (!double.IsFinite(entryThreshold) || !double.IsFinite(exitThreshold) || entryThreshold <= exitThreshold || exitThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(entryThreshold), "Entry threshold must exceed a positive exit threshold.");

        var spread = (double)(fastAverage - slowAverage) / (double)price;
        var zScore = ((double)price - (double)slowAverage) / (double)volatility;
        double strength;

        switch (family)
        {
            case StrategyFamily.TrendFollowing:
                meanReversionEntered = false;
                if (spread >= entryThreshold / 100d) trendEntered = true;
                else if (spread <= exitThreshold / 100d) trendEntered = false;
                strength = trendEntered ? Math.Max(0, spread) : 0;
                break;
            case StrategyFamily.MeanReversion:
                trendEntered = false;
                if (zScore <= -entryThreshold) meanReversionEntered = true;
                else if (zScore >= -exitThreshold) meanReversionEntered = false;
                strength = meanReversionEntered ? Math.Max(0, -zScore) : 0;
                break;
            case StrategyFamily.Breakout:
                trendEntered = false;
                meanReversionEntered = false;
                strength = breakout.IsEntered ? breakout.Strength : 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(family));
        }

        return new StrategySignalEvaluation(trendEntered, meanReversionEntered, spread, zScore, strength);
    }

    public static bool ConfirmationAgrees(
        StrategyFamily family,
        decimal price,
        decimal fastAverage,
        decimal slowAverage,
        decimal volatility,
        double exitThreshold)
    {
        if (price <= 0 || volatility <= 0) return false;
        if (family is StrategyFamily.TrendFollowing or StrategyFamily.Breakout)
        {
            return fastAverage > slowAverage;
        }

        var zScore = ((double)price - (double)slowAverage) / (double)volatility;
        return zScore <= -exitThreshold;
    }
}

public readonly record struct StrategySignalEvaluation(
    bool TrendEntered,
    bool MeanReversionEntered,
    double Spread,
    double ZScore,
    double Strength);
