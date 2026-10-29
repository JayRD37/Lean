namespace QuantConnect.Genetic;

/// <summary>Remaining position and gross exposure capacity after existing, externally managed holdings.</summary>
public readonly record struct PortfolioRiskBudget(int AvailablePositions, decimal AvailableGrossExposureFraction)
{
    public static PortfolioRiskBudget Calculate(
        int maximumPositions,
        decimal maximumGrossExposureFraction,
        decimal portfolioValue,
        IEnumerable<decimal> externalAbsoluteHoldingsValues,
        decimal capitalLimit = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumPositions);
        if (maximumGrossExposureFraction is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(maximumGrossExposureFraction));
        if (portfolioValue < 0) throw new ArgumentOutOfRangeException(nameof(portfolioValue));
        if (capitalLimit < 0) throw new ArgumentOutOfRangeException(nameof(capitalLimit));
        ArgumentNullException.ThrowIfNull(externalAbsoluteHoldingsValues);

        var externalPositions = externalAbsoluteHoldingsValues.Where(value => value != 0).ToArray();
        if (externalPositions.Any(value => value < 0)) throw new ArgumentOutOfRangeException(nameof(externalAbsoluteHoldingsValues), "Holdings values must be absolute and non-negative.");

        var positionCapacity = Math.Max(0, maximumPositions - externalPositions.Length);
        var maximumExposureValue = portfolioValue * maximumGrossExposureFraction;
        if (capitalLimit > 0) maximumExposureValue = Math.Min(maximumExposureValue, capitalLimit);
        var availableExposureValue = Math.Max(0, maximumExposureValue - externalPositions.Sum());
        var exposureCapacity = portfolioValue == 0 ? 0 : availableExposureValue / portfolioValue;
        return new PortfolioRiskBudget(positionCapacity, exposureCapacity);
    }
}
