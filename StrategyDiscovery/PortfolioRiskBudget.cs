namespace QuantConnect.Genetic;

/// <summary>Remaining position and gross exposure capacity after existing, externally managed holdings.</summary>
public readonly record struct PortfolioRiskBudget(int AvailablePositions, decimal AvailableGrossExposureFraction)
{
    public static PortfolioRiskBudget Calculate(
        int maximumPositions,
        decimal maximumGrossExposureFraction,
        decimal portfolioValue,
        IEnumerable<decimal> externalAbsoluteHoldingsValues)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumPositions);
        if (maximumGrossExposureFraction is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(maximumGrossExposureFraction));
        if (portfolioValue < 0) throw new ArgumentOutOfRangeException(nameof(portfolioValue));
        ArgumentNullException.ThrowIfNull(externalAbsoluteHoldingsValues);

        var externalPositions = externalAbsoluteHoldingsValues.Where(value => value != 0).ToArray();
        if (externalPositions.Any(value => value < 0)) throw new ArgumentOutOfRangeException(nameof(externalAbsoluteHoldingsValues), "Holdings values must be absolute and non-negative.");

        var positionCapacity = Math.Max(0, maximumPositions - externalPositions.Length);
        var externalExposure = portfolioValue == 0 ? 1 : externalPositions.Sum() / portfolioValue;
        var exposureCapacity = Math.Max(0, maximumGrossExposureFraction - externalExposure);
        return new PortfolioRiskBudget(positionCapacity, exposureCapacity);
    }
}
