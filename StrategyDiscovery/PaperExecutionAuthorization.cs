namespace QuantConnect.Genetic;

public static class PaperExecutionAuthorization
{
    public static void Validate(bool liveMode, bool explicitPaperOptIn, bool alpacaPaperMode, string configuredBrokerage)
    {
        if (!liveMode) return;
        if (!explicitPaperOptIn || !alpacaPaperMode ||
            !string.Equals(configuredBrokerage, "AlpacaBrokerage", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Live execution is disabled. Live mode requires explicit strategy paper opt-in, alpaca-paper-trading=true, and the AlpacaBrokerage adapter.");
        }
    }
}
