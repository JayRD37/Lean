using System.Text.Json;

namespace QuantConnect.Genetic;

/// <summary>A completed Alpaca paper trade with fees included in its outcome.</summary>
public sealed record PaperTradeOutcome(
    string OutcomeId,
    string Ticker,
    DateTime EntryTime,
    DateTime ExitTime,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal Quantity,
    decimal GrossProfitLoss,
    decimal Fees,
    decimal NetProfitLoss,
    double NetReturnPercent,
    string Result,
    StrategyGenome Genome,
    string SessionId);

public sealed record PaperExperienceSummary(
    int TradeCount,
    int Wins,
    int Losses,
    int BreakEven,
    decimal NetProfitLoss,
    double WinRate,
    double AverageNetReturnPercent,
    double? ProfitFactor);

/// <summary>Idempotent JSONL storage and conservative exact-genome feedback for later searches.</summary>
public static class PaperTradeOutcomeJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<PaperTradeOutcome> Read(string path)
    {
        if (!File.Exists(path)) return Array.Empty<PaperTradeOutcome>();
        var outcomes = new List<PaperTradeOutcome>();
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                var outcome = JsonSerializer.Deserialize<PaperTradeOutcome>(line, JsonOptions);
                if (IsValid(outcome)) outcomes.Add(outcome!);
            }
            catch (JsonException) { }
        }
        return outcomes;
    }

    public static bool TryAppendUnique(string path, PaperTradeOutcome outcome, ISet<string> knownIds)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(knownIds);
        if (!IsValid(outcome)) throw new ArgumentException("The paper trade outcome is incomplete or invalid.", nameof(outcome));
        if (knownIds.Contains(outcome.OutcomeId)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.AppendAllText(path, JsonSerializer.Serialize(outcome) + Environment.NewLine);
        knownIds.Add(outcome.OutcomeId);
        return true;
    }

    public static PaperExperienceSummary Summarize(IEnumerable<PaperTradeOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var items = outcomes.ToArray();
        var wins = items.Count(item => item.Result == "Win");
        var losses = items.Count(item => item.Result == "Loss");
        var breakEven = items.Count(item => item.Result == "BreakEven");
        var positive = items.Where(item => item.NetProfitLoss > 0).Sum(item => item.NetProfitLoss);
        var negative = items.Where(item => item.NetProfitLoss < 0).Sum(item => Math.Abs(item.NetProfitLoss));
        return new PaperExperienceSummary(
            items.Length,
            wins,
            losses,
            breakEven,
            items.Sum(item => item.NetProfitLoss),
            items.Length == 0 ? 0 : wins / (double)items.Length,
            items.Length == 0 ? 0 : items.Average(item => item.NetReturnPercent),
            negative == 0 ? null : (double)(positive / negative));
    }

    /// <summary>
    /// Returns exact previously tested genomes with enough positive net paper evidence.
    /// Unproven genomes are never penalized or excluded from a fresh search.
    /// </summary>
    public static IReadOnlyList<StrategyGenome> GetSeedGenomes(
        IEnumerable<PaperTradeOutcome> outcomes,
        int maximumCount,
        int minimumTrades = 8)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumTrades);
        return outcomes
            .Where(IsValid)
            .GroupBy(item => JsonSerializer.Serialize(item.Genome))
            .Select(group => new
            {
                Genome = group.First().Genome,
                Trades = group.Count(),
                AverageReturn = group.Average(item => item.NetReturnPercent),
                NetProfit = group.Sum(item => item.NetProfitLoss)
            })
            .Where(item => item.Trades >= minimumTrades && item.AverageReturn > 0 && item.NetProfit > 0)
            .OrderByDescending(item => item.AverageReturn * Math.Sqrt(item.Trades / (double)(item.Trades + 10)))
            .ThenByDescending(item => item.Trades)
            .Take(maximumCount)
            .Select(item => item.Genome)
            .ToArray();
    }

    public static bool TryParse(string json, out PaperTradeOutcome? outcome)
    {
        try
        {
            outcome = JsonSerializer.Deserialize<PaperTradeOutcome>(json, JsonOptions);
            return IsValid(outcome);
        }
        catch (JsonException)
        {
            outcome = null;
            return false;
        }
    }

    private static bool IsValid(PaperTradeOutcome? outcome) =>
        outcome is not null &&
        !string.IsNullOrWhiteSpace(outcome.OutcomeId) &&
        !string.IsNullOrWhiteSpace(outcome.Ticker) &&
        !string.IsNullOrWhiteSpace(outcome.SessionId) &&
        outcome.Genome is not null &&
        outcome.Quantity > 0 &&
        outcome.EntryPrice > 0 &&
        outcome.ExitPrice > 0 &&
        outcome.EntryTime <= outcome.ExitTime &&
        double.IsFinite(outcome.NetReturnPercent) &&
        outcome.Result == (outcome.NetProfitLoss > 0 ? "Win" : outcome.NetProfitLoss < 0 ? "Loss" : "BreakEven") &&
        outcome.NetProfitLoss == outcome.GrossProfitLoss - outcome.Fees;
}
