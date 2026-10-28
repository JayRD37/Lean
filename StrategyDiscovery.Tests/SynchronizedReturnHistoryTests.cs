using NUnit.Framework;
using QuantConnect.Genetic;

namespace QuantConnect.Tests.Genetic;

[TestFixture]
public class SynchronizedReturnHistoryTests
{
    [Test]
    public void CorrelationUsesReturnsWithTheSameStartAndEndTimestamps()
    {
        var first = new SynchronizedReturnHistory(60);
        var second = new SynchronizedReturnHistory(60);
        var start = new DateTime(2024, 1, 2);
        var price = 100d;
        first.Update(start, price);
        second.Update(start, price);

        for (var index = 1; index <= 25; index++)
        {
            if (index == 10)
            {
                second.Update(start.AddDays(index - 1).AddHours(12), price * 10d);
            }

            price *= index % 2 == 0 ? 1.01d : 0.995d;
            var time = start.AddDays(index);
            first.Update(time, price);
            second.Update(time, index >= 10 ? price * 10d : price);
        }

        Assert.That(SynchronizedReturnHistory.Correlation(first, second, minimumAlignedSamples: 20), Is.EqualTo(1d).Within(1e-10));
    }

    [Test]
    public void CorrelationReturnsZeroWhenNoExactReturnIntervalsOverlap()
    {
        var first = new SynchronizedReturnHistory(60);
        var second = new SynchronizedReturnHistory(60);
        var start = new DateTime(2024, 1, 2);
        var firstPrice = 100d;
        var secondPrice = 100d;
        first.Update(start, firstPrice);
        second.Update(start, secondPrice);

        for (var index = 1; index <= 20; index++)
        {
            firstPrice *= 1.01d;
            var time = start.AddDays(index);
            first.Update(time, firstPrice);
            if (index % 2 == 1)
            {
                secondPrice *= 1.02d;
                second.Update(time, secondPrice);
            }
        }

        Assert.That(SynchronizedReturnHistory.Correlation(first, second, minimumAlignedSamples: 5), Is.Null);
    }
}

