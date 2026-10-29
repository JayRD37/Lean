using System.Text.Json;
using NUnit.Framework;
using QuantConnect.Genetic;

namespace QuantConnect.Tests.Genetic;

[TestFixture]
public sealed class PaperTradeOutcomeJournalTests
{
    [Test]
    public void JournalIsIdempotentAndKeepsFeeAdjustedWinsAndLosses()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LeanPaperOutcomeTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "paper-trade-outcomes.jsonl");
        var known = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var win = Outcome("session-a:trade-1", Genome(8), 102m, 2m, 1.00);
            var loss = Outcome("session-a:trade-2", Genome(8), -45m, 1m, -0.45);
            Assert.That(PaperTradeOutcomeJournal.TryAppendUnique(path, win, known), Is.True);
            Assert.That(PaperTradeOutcomeJournal.TryAppendUnique(path, win, known), Is.False);
            Assert.That(PaperTradeOutcomeJournal.TryAppendUnique(path, loss, known), Is.True);

            var read = PaperTradeOutcomeJournal.Read(path);
            var summary = PaperTradeOutcomeJournal.Summarize(read);

            Assert.That(read, Has.Count.EqualTo(2));
            Assert.That(summary.Wins, Is.EqualTo(1));
            Assert.That(summary.Losses, Is.EqualTo(1));
            Assert.That(summary.NetProfitLoss, Is.EqualTo(54m));
            Assert.That(summary.WinRate, Is.EqualTo(0.5));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void LargeJournalIgnoresMalformedRowsAndOnlySeedsExperiencedProfitableExactGenomes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LeanPaperOutcomeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "paper-trade-outcomes.jsonl");
        try
        {
            var profitable = Genome(8);
            var unprofitable = Genome(9);
            var lines = Enumerable.Range(0, 5_000)
                .Select(index => JsonSerializer.Serialize(index < 12
                    ? Outcome($"session:win-{index}", profitable, 9m, 1m, 0.09)
                    : Outcome($"session:loss-{index}", unprofitable, -12m, 1m, -0.12)))
                .Prepend("{malformed")
                .ToArray();
            File.WriteAllLines(path, lines);

            var outcomes = PaperTradeOutcomeJournal.Read(path);
            var seeds = PaperTradeOutcomeJournal.GetSeedGenomes(outcomes, maximumCount: 2, minimumTrades: 8);

            Assert.That(outcomes, Has.Count.EqualTo(5_000));
            Assert.That(seeds, Is.EqualTo(new[] { profitable }));
            Assert.That(PaperTradeOutcomeJournal.GetSeedGenomes(outcomes.Take(7), 2, minimumTrades: 8), Is.Empty);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void BreakEvenOutcomesAreNotCountedAsWinsOrLosses()
    {
        var summary = PaperTradeOutcomeJournal.Summarize([Outcome("s:flat", Genome(8), 1m, 1m, 0)]);

        Assert.That(summary.Wins, Is.Zero);
        Assert.That(summary.Losses, Is.Zero);
        Assert.That(summary.BreakEven, Is.EqualTo(1));
        Assert.That(summary.WinRate, Is.Zero);
    }

    private static StrategyGenome Genome(int fastPeriod) => new(StrategyFamily.TrendFollowing, StrategyTimeframe.Daily, fastPeriod, 40, 12, 4, 2, 30);

    private static PaperTradeOutcome Outcome(string id, StrategyGenome genome, decimal gross, decimal fees, double returnPercent) => new(
        id,
        "SPY",
        new DateTime(2025, 1, 2),
        new DateTime(2025, 1, 3),
        100m,
        gross > 0 ? 102m : 98m,
        1m,
        gross,
        fees,
        gross - fees,
        returnPercent,
        gross - fees > 0 ? "Win" : gross - fees < 0 ? "Loss" : "BreakEven",
        genome,
        id.Split(':')[0]);
}
