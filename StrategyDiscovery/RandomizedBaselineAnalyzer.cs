namespace QuantConnect.Genetic;

public sealed record RandomizedBaselineComparison(
    int BaselineCount,
    double MedianBaselineFitness,
    int BaselineScoresAtOrAboveCandidate,
    double ProbabilityCandidateBeatsBaseline,
    double EmpiricalOneSidedPValue,
    bool HasResolutionForFivePercentThreshold);

/// <summary>
/// Compares a held-out evolved strategy against held-out randomized strategies.
/// The empirical tail probability is descriptive and has no multiple-testing correction.
/// </summary>
public static class RandomizedBaselineAnalyzer
{
    public const int MinimumBaselineCountForFivePercentResolution = 19;

    public static RandomizedBaselineComparison Compare(double candidateFitness, IReadOnlyList<double> baselineFitnesses)
    {
        ArgumentNullException.ThrowIfNull(baselineFitnesses);
        if (!double.IsFinite(candidateFitness)) throw new ArgumentOutOfRangeException(nameof(candidateFitness));
        if (baselineFitnesses.Count == 0) throw new ArgumentException("At least one held-out randomized baseline score is required.", nameof(baselineFitnesses));
        if (baselineFitnesses.Any(score => !double.IsFinite(score)))
            throw new ArgumentException("Randomized baseline scores must be finite.", nameof(baselineFitnesses));

        var ordered = baselineFitnesses.OrderBy(score => score).ToArray();
        var atOrAboveCandidate = ordered.Count(score => score >= candidateFitness);
        var strictlyBelowCandidate = ordered.Length - ordered.Count(score => score >= candidateFitness);
        var median = ordered.Length % 2 == 0
            ? (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2d
            : ordered[ordered.Length / 2];

        return new RandomizedBaselineComparison(
            ordered.Length,
            median,
            atOrAboveCandidate,
            (double)strictlyBelowCandidate / ordered.Length,
            (double)(1 + atOrAboveCandidate) / (ordered.Length + 1),
            ordered.Length >= MinimumBaselineCountForFivePercentResolution);
    }
}
