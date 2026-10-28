namespace QuantConnect.Genetic;

/// <summary>Small, explicit execution-cost assumptions shared by the LEAN algorithm and tests.</summary>
public static class ExecutionCostAssumptions
{
    public static decimal HalfSpreadCost(decimal referencePrice, decimal fullSpreadBasisPoints)
    {
        if (referencePrice < 0) throw new ArgumentOutOfRangeException(nameof(referencePrice));
        if (fullSpreadBasisPoints < 0) throw new ArgumentOutOfRangeException(nameof(fullSpreadBasisPoints));
        return referencePrice * fullSpreadBasisPoints / 20_000m;
    }
}
