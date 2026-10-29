using Newtonsoft.Json.Linq;
using QuantConnect.Genetic;

namespace QuantConnect.Genetic.Supervisor;

public sealed record PromotedStrategy(StrategyGenome Genome, double Fitness, StrategyPopulation Population, int ValidationWindowCount);

public static class PromotionSelector
{
    public static PromotedStrategy SelectBest(string rankingsPath, SupervisorSettings settings)
    {
        var root = JObjectConfig.Read(rankingsPath);
        var validation = root["validation"] as JArray
            ?? throw new InvalidDataException("Rankings file has no validation results.");
        var confirmation = root["confirmation"] as JArray
            ?? throw new InvalidDataException("Rankings file has no untouched confirmation results.");
        var baseline = root["randomizedBaselineComparison"] as JObject
            ?? throw new InvalidDataException("Rankings file has no randomized baseline comparison.");
        var baselineCount = baseline["baselineValidationCount"]?.Value<int>() ?? 0;
        var medianBaselineFitness = baseline["medianBaselineFitness"]?.Value<double>();
        if (baselineCount < 1 || medianBaselineFitness is null || !double.IsFinite(medianBaselineFitness.Value))
            throw new InvalidDataException("Paper promotion requires at least one valid held-out randomized baseline result.");
        var candidates = new List<PromotedStrategy>();

        foreach (var item in validation.OfType<JObject>())
        {
            var populationValue = item["Population"]?.Value<int>();
            if (populationValue is null || !Enum.IsDefined(typeof(StrategyPopulation), populationValue.Value)) continue;
            var population = (StrategyPopulation)populationValue.Value;
            if (population == StrategyPopulation.RandomBaseline) continue;
            var fitness = item["RobustnessAdjustedFitness"]?.Value<double>();
            if (fitness is null || !double.IsFinite(fitness.Value) || fitness.Value < settings.MinimumRobustnessAdjustedFitness || fitness.Value <= medianBaselineFitness.Value) continue;

            var robustness = item["ParameterRobustness"] as JObject;
            var requestedProbes = robustness?["RequestedProbeCount"]?.Value<int>() ?? 0;
            var validProbes = robustness?["ValidProbeCount"]?.Value<int>() ?? 0;
            if (requestedProbes < settings.RequiredParameterProbeCount || requestedProbes != validProbes) continue;

            var genomeToken = item["Genome"];
            var genome = genomeToken?.ToObject<StrategyGenome>();
            if (genome is null) continue;
            if (genome.MaxPositions > settings.MaximumPaperPositions || genome.MaxExposurePercent > settings.MaximumPaperExposurePercent) continue;
            var windows = item["Windows"] as JArray;
            if (windows is null || windows.Count == 0) continue;

            var centralWindows = windows.OfType<JObject>()
                .Where(window => JToken.DeepEquals(window["Genome"], genomeToken))
                .ToArray();
            if (centralWindows.Length != settings.RequiredValidationWindowCount || centralWindows.Any(window =>
                window["Metrics"] is not JObject metrics ||
                (metrics["TradeCount"]?.Value<int>() ?? -1) < settings.MinimumValidationTradesPerWindow ||
                (metrics["Drawdown"]?.Value<double>() ?? double.PositiveInfinity) > settings.MaximumValidationDrawdown))
            {
                continue;
            }

            var confirmationResult = confirmation.OfType<JObject>().FirstOrDefault(item =>
                item["Passed"]?.Value<bool>() == true && JToken.DeepEquals(item["Genome"], genomeToken));
            var confirmationFitness = confirmationResult?["ConfirmationFitness"]?.Value<double>();
            var confirmationMetrics = confirmationResult?["Metrics"] as JObject;
            if (confirmationFitness is null || !double.IsFinite(confirmationFitness.Value) ||
                confirmationFitness.Value < settings.MinimumRobustnessAdjustedFitness ||
                confirmationMetrics is null ||
                (confirmationMetrics["TradeCount"]?.Value<int>() ?? -1) < settings.MinimumValidationTradesPerWindow ||
                (confirmationMetrics["Drawdown"]?.Value<double>() ?? double.PositiveInfinity) > settings.MaximumValidationDrawdown)
            {
                continue;
            }

            candidates.Add(new PromotedStrategy(genome, confirmationFitness.Value, population, centralWindows.Length));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Fitness)
            .ThenBy(candidate => candidate.Genome.FastPeriod)
            .FirstOrDefault()
            ?? throw new InvalidDataException("No evolved candidate passed randomized-baseline, walk-forward validation, robustness, untouched confirmation, trade-count, and drawdown promotion gates.");
    }
}
