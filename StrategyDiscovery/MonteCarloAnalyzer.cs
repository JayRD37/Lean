namespace QuantConnect.Genetic;

public sealed record MonteCarloSummary(
    int TradeCount,
    int SimulationCount,
    double ProbabilityOfLoss,
    double FifthPercentileTerminalReturn,
    double MedianTerminalReturn,
    double NinetyFifthPercentileDrawdown);

/// <summary>
/// IID bootstrap of observed closed-trade returns. This is a sequencing stress test,
/// not a forecast or a substitute for time-series-aware validation.
/// </summary>
public static class MonteCarloAnalyzer
{
    public static MonteCarloSummary Analyze(IReadOnlyList<double> tradeReturns, int simulations, int seed)
    {
        ArgumentNullException.ThrowIfNull(tradeReturns);
        if (tradeReturns.Count == 0) throw new ArgumentException("At least one closed-trade return is required.", nameof(tradeReturns));
        if (simulations < 1) throw new ArgumentOutOfRangeException(nameof(simulations), "At least one simulation is required.");
        if (tradeReturns.Any(value => !double.IsFinite(value) || value <= -1d))
        {
            throw new ArgumentException("Trade returns must be finite and greater than -100%.", nameof(tradeReturns));
        }

        var random = new Random(seed);
        var terminalReturns = new double[simulations];
        var drawdowns = new double[simulations];
        var losses = 0;

        for (var simulation = 0; simulation < simulations; simulation++)
        {
            var equity = 1d;
            var peak = 1d;
            var maximumDrawdown = 0d;
            for (var trade = 0; trade < tradeReturns.Count; trade++)
            {
                equity *= 1d + tradeReturns[random.Next(tradeReturns.Count)];
                if (!double.IsFinite(equity)) throw new ArgumentException("Trade returns overflowed during simulation.", nameof(tradeReturns));
                if (equity > peak) peak = equity;
                maximumDrawdown = Math.Max(maximumDrawdown, peak <= 0 ? 1d : 1d - (equity / peak));
            }

            terminalReturns[simulation] = equity - 1d;
            drawdowns[simulation] = maximumDrawdown;
            if (equity < 1d) losses++;
        }

        Array.Sort(terminalReturns);
        Array.Sort(drawdowns);
        return new MonteCarloSummary(
            tradeReturns.Count,
            simulations,
            (double)losses / simulations,
            Percentile(terminalReturns, 0.05),
            Percentile(terminalReturns, 0.50),
            Percentile(drawdowns, 0.95));
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
