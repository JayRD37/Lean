using NUnit.Framework;
using QuantConnect.Genetic;

namespace QuantConnect.Tests.Genetic;

[TestFixture]
public class RandomizedBaselineAnalyzerTests
{
    [Test]
    public void AppliesBonferroniAcrossTheSpecifiedFinalistFamily()
    {
        var baseline = Enumerable.Range(0, 19).Select(index => (double)index).ToArray();

        var comparison = RandomizedBaselineAnalyzer.Compare(20, baseline, comparisonFamilySize: 4);

        Assert.That(comparison.EmpiricalOneSidedPValue, Is.EqualTo(0.05).Within(1e-12));
        Assert.That(comparison.ComparisonFamilySize, Is.EqualTo(4));
        Assert.That(comparison.BonferroniAdjustedPValue, Is.EqualTo(0.20).Within(1e-12));
        Assert.That(comparison.HasResolutionForFivePercentThreshold, Is.True);
    }

    [Test]
    public void CapsAdjustedProbabilityAtOne()
    {
        var comparison = RandomizedBaselineAnalyzer.Compare(0, [0d, 1d], comparisonFamilySize: 20);

        Assert.That(comparison.BonferroniAdjustedPValue, Is.EqualTo(1));
    }

    [Test]
    public void RejectsEmptyComparisonFamily()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RandomizedBaselineAnalyzer.Compare(1, [0d], comparisonFamilySize: 0));
    }
}

