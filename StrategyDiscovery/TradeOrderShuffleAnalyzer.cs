namespace QuantConnect.Genetic;

public sealed record TradeOrderShuffleSummary(
    int TradeCount,
    int SimulationCount,
    double DrawdownThreshold,
    double ProbabilityOfExceedingDrawdownThreshold,
    double NinetyFifthPercentileDrawdown,
    double WorstObservedDrawdown);

public static class TradeOrderShuffleAnalyzer
{
    public static TradeOrderShuffleSummary Analyze(
        IReadOnlyList<double> tradeReturns,
        int simulations,
        int seed,
        double drawdownThreshold = 0.40)
    {
        ArgumentNullException.ThrowIfNull(tradeReturns);
        if (tradeReturns.Count == 0) throw new ArgumentException("At least one closed-trade return is required.", nameof(tradeReturns));
        if (simulations < 1) throw new ArgumentOutOfRangeException(nameof(simulations), "At least one simulation is required.");
        if (!double.IsFinite(drawdownThreshold) || drawdownThreshold is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(drawdownThreshold), "Drawdown threshold must be between zero and one.");
        }
        if (tradeReturns.Any(value => !double.IsFinite(value) || value <= -1d))
        {
            throw new ArgumentException("Trade returns must be finite and greater than -100%.", nameof(tradeReturns));
        }

        var random = new Random(seed);
        var order = tradeReturns.ToArray();
        var drawdowns = new double[simulations];
        var exceedances = 0;

        for (var simulation = 0; simulation < simulations; simulation++)
        {
            Shuffle(order, random);
            var equity = 1d;
            var peak = 1d;
            var maximumDrawdown = 0d;
            foreach (var tradeReturn in order)
            {
                equity *= 1d + tradeReturn;
                if (!double.IsFinite(equity)) throw new ArgumentException("Trade returns overflowed during simulation.", nameof(tradeReturns));
                if (equity > peak) peak = equity;
                maximumDrawdown = Math.Max(maximumDrawdown, peak <= 0 ? 1d : 1d - (equity / peak));
            }

            drawdowns[simulation] = maximumDrawdown;
            if (maximumDrawdown > drawdownThreshold) exceedances++;
        }

        Array.Sort(drawdowns);
        return new TradeOrderShuffleSummary(
            tradeReturns.Count,
            simulations,
            drawdownThreshold,
            (double)exceedances / simulations,
            Percentile(drawdowns, 0.95),
            drawdowns[^1]);
    }

    private static void Shuffle(double[] values, Random random)
    {
        for (var index = values.Length - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
        }
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 1) return sortedValues[0];
        var position = percentile * (sortedValues.Count - 1);
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);
        var fraction = position - lowerIndex;
        return sortedValues[lowerIndex] + ((sortedValues[upperIndex] - sortedValues[lowerIndex]) * fraction);
    }
}
