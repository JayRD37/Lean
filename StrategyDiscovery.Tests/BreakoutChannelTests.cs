using NUnit.Framework;
using QuantConnect.Genetic;

namespace QuantConnect.Tests.StrategyDiscovery;

[TestFixture]
public class BreakoutChannelTests
{
    [Test]
    public void EntryUsesOnlyCompletedPriorBarsAndExitUsesTheLongerPriorChannel()
    {
        var channel = new BreakoutChannel(entryPeriod: 3, exitPeriod: 5, entryThresholdPercent: 1);
        var start = new DateTime(2024, 1, 1);
        var bars = new (decimal High, decimal Low, decimal Close)[]
        {
            (10.5m, 9.5m, 10m),
            (10.4m, 9.6m, 10m),
            (10.6m, 9.8m, 10.1m),
            (10.5m, 9.7m, 10m),
            (10.7m, 9.9m, 10.2m)
        };

        BreakoutSnapshot snapshot = default;
        for (var index = 0; index < bars.Length; index++)
        {
            var bar = bars[index];
            snapshot = channel.Update(start.AddDays(index), bar.High, bar.Low, bar.Close);
        }

        Assert.That(snapshot.IsReady, Is.False, "The current bar cannot fill its own prior-window channel.");

        snapshot = channel.Update(start.AddDays(5), 20m, 10m, 10.81m);

        Assert.That(snapshot.IsReady, Is.True);
        Assert.That(snapshot.IsEntered, Is.True, "The current high must not raise the entry threshold for its own close.");
        Assert.That(snapshot.EntryLevel, Is.EqualTo(10.7m));
        Assert.That(snapshot.ExitLevel, Is.EqualTo(9.5m));
        Assert.That(snapshot.Strength, Is.GreaterThan(0));

        var duplicateTimestamp = channel.Update(start.AddDays(5), 20m, 9m, 9m);
        Assert.That(duplicateTimestamp, Is.EqualTo(snapshot), "A duplicate timestamp must not advance channel state.");

        snapshot = channel.Update(start.AddDays(6), 10.5m, 9.6m, 9.6m);
        Assert.That(snapshot.IsEntered, Is.False, "A close at the prior slow-channel low exits the breakout.");
        Assert.That(snapshot.Strength, Is.Zero);
    }

    [Test]
    public void ChannelRequiresAnExitPeriodLongerThanTheEntryPeriod()
    {
        Assert.Throws<ArgumentException>(() => new BreakoutChannel(5, 5, 1));
    }

    [Test]
    public void WarmupBarsSeedTheChannelWithoutCreatingAnOpenStrategySignal()
    {
        var channel = new BreakoutChannel(entryPeriod: 2, exitPeriod: 3, entryThresholdPercent: 1);
        var start = new DateTime(2024, 1, 1);
        channel.Update(start, 10m, 9m, 9.5m, allowSignals: false);
        channel.Update(start.AddDays(1), 10.1m, 9.2m, 9.8m, allowSignals: false);
        channel.Update(start.AddDays(2), 10.2m, 9.5m, 10m, allowSignals: false);
        var warmupBreakout = channel.Update(start.AddDays(3), 12m, 9.7m, 11m, allowSignals: false);

        Assert.That(warmupBreakout.IsReady, Is.True);
        Assert.That(warmupBreakout.IsEntered, Is.False);

        var firstActiveBreakout = channel.Update(start.AddDays(4), 12.3m, 12m, 12.2m);
        Assert.That(firstActiveBreakout.IsEntered, Is.True);
    }
}
