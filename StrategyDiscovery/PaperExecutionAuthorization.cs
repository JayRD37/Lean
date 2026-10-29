namespace QuantConnect.Genetic;

public static class PaperExecutionAuthorization
{
    public static void Validate(bool liveMode, bool explicitPaperOptIn, bool alpacaPaperMode, string configuredBrokerage)
    {
        if (!liveMode) return;
        if (!explicitPaperOptIn || !alpacaPaperMode ||
            !string.Equals(configuredBrokerage, "AlpacaDirectBrokerage", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Live execution is disabled. Paper mode requires explicit strategy opt-in, alpaca-paper-trading=true, and the paper-only AlpacaDirectBrokerage adapter.");
        }
    }
}
