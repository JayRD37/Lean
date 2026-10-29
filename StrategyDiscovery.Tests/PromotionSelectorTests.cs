using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Genetic;
using QuantConnect.Genetic.Supervisor;

namespace QuantConnect.Tests.Genetic.Supervisor;

[TestFixture]
public class PromotionSelectorTests
{
    [Test]
    public void SelectsBestEvolvedCandidatePassingProbeAndPerWindowRiskGates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LeanGeneticSupervisorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var center = Genome(10, 40, 12, 4, 3, 30);
            var neighbor = Genome(11, 40, 12, 4, 3, 30);
            var invalidProbe = Genome(12, 42, 14, 2, 4, 30);
            var rankings = new JObject
            {
                ["randomizedBaselineComparison"] = new JObject
                {
                    ["baselineValidationCount"] = 2,
                    ["medianBaselineFitness"] = 0.6
                },
                ["validation"] = new JArray
                {
                    Candidate(0, 0.7, center, 1, 1, Window(center, 15, 0.06), Window(neighbor, 14, 0.08)),
                    Candidate(1, 0.95, invalidProbe, 2, 1, Window(invalidProbe, 20, 0.04)),
                    Candidate(0, 1.2, Genome(9, 36, 11, 3, 3, 40), 0, 0,
                        Window(Genome(9, 36, 11, 3, 3, 40), 20, 0.04)),
                    Candidate(2, 1.5, Genome(8, 35, 10, 3, 4, 30), 0, 0,
                        Window(Genome(8, 35, 10, 3, 4, 30), 20, 0.04))
                },
                ["confirmation"] = new JArray
                {
                    Confirmation(center, 0.75, 15, 0.07)
                }
            };
            var rankingsPath = Path.Combine(directory, "rankings.json");
            File.WriteAllText(rankingsPath, rankings.ToString(Formatting.Indented));
            var settings = new SupervisorSettings
            {
                MinimumRobustnessAdjustedFitness = 0.1,
                MinimumValidationTradesPerWindow = 10,
                MaximumValidationDrawdown = 0.10
            };

            var selected = PromotionSelector.SelectBest(rankingsPath, settings);

            Assert.That(selected.Population, Is.EqualTo(StrategyPopulation.Growth));
            Assert.That(selected.Genome.FastPeriod, Is.EqualTo(10));
            Assert.That(selected.Fitness, Is.EqualTo(0.75));
            Assert.That(selected.ValidationWindowCount, Is.EqualTo(1));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void PromotionRejectsCandidatesThatDoNotBeatTheRandomizedBaselineMedian()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LeanGeneticSupervisorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var genome = Genome(10, 40, 12, 4, 3, 30);
            var rankings = new JObject
            {
                ["randomizedBaselineComparison"] = new JObject
                {
                    ["baselineValidationCount"] = 2,
                    ["medianBaselineFitness"] = 0.8
                },
                ["validation"] = new JArray
                {
                    Candidate(0, 0.7, genome, 1, 1, Window(genome, 15, 0.06))
                },
                ["confirmation"] = new JArray { Confirmation(genome, 0.9, 15, 0.06) }
            };
            var path = Path.Combine(directory, "rankings.json");
            File.WriteAllText(path, rankings.ToString(Formatting.Indented));

            Assert.Throws<InvalidDataException>(() => PromotionSelector.SelectBest(path, new SupervisorSettings
            {
                MinimumRobustnessAdjustedFitness = 0.1,
                MinimumValidationTradesPerWindow = 10,
                MaximumValidationDrawdown = 0.10
            }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void PromotionRejectsWalkForwardWinnerWithoutPassingUntouchedConfirmation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LeanGeneticSupervisorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var genome = Genome(10, 40, 12, 4, 3, 30);
            var rankings = new JObject
            {
                ["randomizedBaselineComparison"] = new JObject
                {
                    ["baselineValidationCount"] = 2,
                    ["medianBaselineFitness"] = 0.6
                },
                ["validation"] = new JArray { Candidate(0, 0.8, genome, 1, 1, Window(genome, 15, 0.06)) },
                ["confirmation"] = new JArray { Confirmation(genome, 0.9, 15, 0.06) }
            };
            ((JObject)rankings["confirmation"]![0]!)["Passed"] = false;
            var path = Path.Combine(directory, "rankings.json");
            File.WriteAllText(path, rankings.ToString(Formatting.Indented));

            Assert.Throws<InvalidDataException>(() => PromotionSelector.SelectBest(path, new SupervisorSettings
            {
                MinimumRobustnessAdjustedFitness = 0.1,
                MinimumValidationTradesPerWindow = 10,
                MaximumValidationDrawdown = 0.10
            }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void PaperConfigFactoryPinsAlpacaPaperAndCopiesApprovedGenome()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LeanGeneticSupervisorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var basePath = Path.Combine(directory, "base.json");
            var overlayPath = Path.Combine(directory, "overlay.json");
            var pluginDirectory = Path.Combine(directory, "plugins");
            Directory.CreateDirectory(pluginDirectory);
            File.WriteAllText(Path.Combine(pluginDirectory, "QuantConnect.Brokerages.Alpaca.dll"), string.Empty);
            File.WriteAllText(Path.Combine(pluginDirectory, "Alpaca.Markets.dll"), string.Empty);
            File.WriteAllText(basePath, "{\"environment\":\"backtesting\",\"algorithm-type-name\":\"Basic\",\"environments\":{}}");
            File.WriteAllText(overlayPath, "{\"alpaca-paper-trading\":true,\"environments\":{\"live-alpaca\":{\"live-mode\":true,\"live-mode-brokerage\":\"AlpacaBrokerage\"}},\"parameters\":{\"discovery.max-positions\":\"3\",\"discovery.max-exposure-percent\":\"30\"}}");
            var resultsDirectory = Path.Combine(directory, "paper-results");
            Directory.CreateDirectory(resultsDirectory);
            var genome = new StrategyGenome(StrategyFamily.MeanReversion, StrategyTimeframe.Hour, 9, 35, 14, 3, 2, 25)
            {
                ConfirmationTimeframe = StrategyTimeframe.Daily
            };
            var selected = new PromotedStrategy(genome, 0.5, StrategyPopulation.Consistency, 3);

            var config = PaperConfigFactory.Create(basePath, overlayPath, "GeneticDiscoveryAlgorithm", selected, resultsDirectory, pluginDirectory, Path.Combine(directory, "stop"), "PK-paper-key", "paper-secret", "test-user", "test-token");

            Assert.That(config["environment"]?.Value<string>(), Is.EqualTo("live-alpaca"));
            Assert.That(config["alpaca-paper-trading"]?.Value<bool>(), Is.True);
            Assert.That(config["alpaca-api-key"]?.Value<string>(), Is.EqualTo("PK-paper-key"));
            Assert.That(config["alpaca-api-secret"]?.Value<string>(), Is.EqualTo("paper-secret"));
            Assert.That(config["results-destination-folder"]?.Value<string>(), Is.EqualTo(Path.GetFullPath(resultsDirectory)));
            Assert.That(config["log-handler"]?.Value<string>(), Is.EqualTo("QuantConnect.Logging.FileLogHandler"));
            Assert.That(config["parameters"]?["discovery.allow-paper-trading"]?.Value<string>(), Is.EqualTo("true"));
            Assert.That(config["parameters"]?["discovery.family"]?.Value<string>(), Is.EqualTo("MeanReversion"));
            Assert.That(config["parameters"]?["discovery.timeframe"]?.Value<string>(), Is.EqualTo("Hour"));
            Assert.That(config["parameters"]?["discovery.confirmation-timeframe"]?.Value<string>(), Is.EqualTo("Daily"));
            Assert.That(config["parameters"]?["discovery.fast-period"]?.Value<string>(), Is.EqualTo("9"));
            Assert.That(config["parameters"]?["discovery.drawdown-state-file"]?.Value<string>(),
                Is.EqualTo(Path.Combine(directory, "portfolio-drawdown-state.json")));

            var configWithDifferentCredentialText = PaperConfigFactory.Create(
                basePath, overlayPath, "GeneticDiscoveryAlgorithm", selected, resultsDirectory,
                pluginDirectory, Path.Combine(directory, "stop"), "bad-live-key", "paper-secret", "test-user", "test-token");
            Assert.That(configWithDifferentCredentialText["alpaca-paper-trading"]?.Value<bool>(), Is.True,
                "The generated runtime must remain pinned to paper mode regardless of the credential string.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void PaperConfigFactoryRejectsMissingCredentials()
    {
        Assert.Throws<InvalidOperationException>(() => PaperConfigFactory.Create(
            "missing-base.json", "missing-overlay.json", "GeneticDiscoveryAlgorithm",
            new PromotedStrategy(Genome(10, 40, 12, 4, 3, 30).ToObject<StrategyGenome>()!, 0.5, StrategyPopulation.Growth, 1),
            "results", "plugin", "stop", string.Empty, string.Empty, string.Empty, string.Empty));
    }

    [Test]
    public void SupervisorSettingsRequireOptimizerAndPaperRiskConfigurationToMatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LeanGeneticSupervisorTests", Guid.NewGuid().ToString("N"));
        var optimizerDirectory = Path.Combine(directory, "optimizer");
        var leanDirectory = Path.Combine(directory, "lean");
        Directory.CreateDirectory(optimizerDirectory);
        Directory.CreateDirectory(leanDirectory);
        try
        {
            File.WriteAllText(Path.Combine(optimizerDirectory, "launcher.dll"), string.Empty);
            File.WriteAllText(Path.Combine(leanDirectory, "launcher.dll"), string.Empty);
            var pluginDirectory = Path.Combine(directory, "plugins");
            Directory.CreateDirectory(pluginDirectory);
            File.WriteAllText(Path.Combine(pluginDirectory, "QuantConnect.Brokerages.Alpaca.dll"), string.Empty);
            File.WriteAllText(Path.Combine(pluginDirectory, "Alpaca.Markets.dll"), string.Empty);
            File.WriteAllText(Path.Combine(optimizerDirectory, "optimizer.json"), OptimizerConfig());
            File.WriteAllText(Path.Combine(directory, "base.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "overlay.json"), PaperOverlay());
            var settingsPath = Path.Combine(directory, "supervisor.json");
            File.WriteAllText(settingsPath, SupervisorConfig());

            var settings = SupervisorSettings.Load(settingsPath);

            Assert.That(settings.MaximumPaperExposurePercent, Is.EqualTo(30));
            Assert.That(settings.RequiredValidationWindowCount, Is.EqualTo(3));
            Assert.That(settings.RankingsFile, Is.EqualTo(Path.Combine(optimizerDirectory, "results", "genetic-search-rankings.json")));

            File.WriteAllText(Path.Combine(optimizerDirectory, "optimizer.json"), OptimizerConfig().Replace("SPY,QQQ,IWM", "SPY,QQQ", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => SupervisorSettings.Load(settingsPath));

            File.WriteAllText(Path.Combine(optimizerDirectory, "optimizer.json"), OptimizerConfig());
            File.WriteAllText(Path.Combine(directory, "overlay.json"), PaperOverlay().Replace("Configured", "TopDollarVolume", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => SupervisorSettings.Load(settingsPath));

            File.WriteAllText(Path.Combine(directory, "overlay.json"), PaperOverlay().Replace("\"20\"", "\"10\"", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => SupervisorSettings.Load(settingsPath));

            File.WriteAllText(Path.Combine(optimizerDirectory, "optimizer.json"), OptimizerConfig().Replace("\"universe-mode\": \"Configured\"", "\"universe-mode\": \"Unknown\"", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(directory, "overlay.json"), PaperOverlay().Replace("\"discovery.universe-mode\": \"Configured\"", "\"discovery.universe-mode\": \"Unknown\"", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => SupervisorSettings.Load(settingsPath));

            File.WriteAllText(Path.Combine(optimizerDirectory, "optimizer.json"), OptimizerConfig().Replace("\"universe-size\": 20", "\"universe-size\": 101", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(directory, "overlay.json"), PaperOverlay().Replace("\"discovery.universe-size\": \"20\"", "\"discovery.universe-size\": \"101\"", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => SupervisorSettings.Load(settingsPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void PaperRestartPolicyUsesBackoffAndStopsAfterConfiguredFailureLimit()
    {
        var policy = new PaperRestartPolicy(3, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));

        Assert.That(policy.RegisterExit(1, TimeSpan.FromMinutes(1)), Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(policy.RegisterExit(1, TimeSpan.FromMinutes(1)), Is.EqualTo(TimeSpan.FromSeconds(20)));
        Assert.Throws<InvalidOperationException>(() => policy.RegisterExit(1, TimeSpan.FromMinutes(1)));
    }

    [Test]
    public void HealthyPaperSessionResetsFailureCount()
    {
        var policy = new PaperRestartPolicy(3, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
        policy.RegisterExit(1, TimeSpan.FromMinutes(1));

        Assert.That(policy.RegisterExit(0, TimeSpan.FromMinutes(10)), Is.EqualTo(TimeSpan.Zero));
        Assert.That(policy.ConsecutiveFailures, Is.Zero);
    }

    private static string SupervisorConfig() => """
        {
          "optimizer-launcher-dll": "optimizer/launcher.dll",
          "optimizer-working-directory": "optimizer",
          "optimizer-config": "optimizer/optimizer.json",
          "lean-launcher-dll": "lean/launcher.dll",
          "lean-working-directory": "lean",
          "lean-base-config": "base.json",
          "paper-config-overlay": "overlay.json",
          "alpaca-plugin-directory": "plugins",
          "output-directory": "output",
          "kill-switch-file": "stop",
          "maximum-validation-drawdown": 0.08
        }
        """;

    private static string OptimizerConfig() => """
        {
          "algorithm-type-name": "GeneticDiscoveryAlgorithm",
          "results-destination-folder": "results",
          "optimization-strategy-settings": {
            "validation-window-count": 3,
            "parameter-robustness-probe-count": 1,
            "maximum-positions": 3,
            "maximum-exposure-percent": 30,
          "minimum-spread-bps": 5,
          "random-baseline-size": 4,
          "validation-baseline-count": 2,
            "maximum-correlation": 0.8,
            "maximum-portfolio-drawdown": 0.08,
            "symbols": "SPY,QQQ,IWM",
            "universe-mode": "Configured",
            "universe-size": 20
          }
        }
        """;

    private static string PaperOverlay() => """
        {
          "parameters": {
            "discovery.max-positions": "3",
            "discovery.max-exposure-percent": "30",
            "discovery.minimum-spread-bps": "5",
            "discovery.max-correlation": "0.80",
            "discovery.max-drawdown": "0.08",
            "discovery.symbols": "SPY|QQQ|IWM",
            "discovery.universe-mode": "Configured",
            "discovery.universe-size": "20"
          }
        }
        """;

    private static JObject Genome(int fast, int slow, int entry, int exit, int positions, int exposure) => new()
    {
        ["Family"] = 0,
        ["Timeframe"] = 2,
        ["FastPeriod"] = fast,
        ["SlowPeriod"] = slow,
        ["EntryThresholdTenths"] = entry,
        ["ExitThresholdTenths"] = exit,
        ["MaxPositions"] = positions,
        ["MaxExposurePercent"] = exposure,
        ["EntryThreshold"] = entry / 10d,
        ["ExitThreshold"] = exit / 10d
    };

    private static JObject Window(JObject genome, int trades, double drawdown) => new()
    {
        ["Genome"] = genome.DeepClone(),
        ["Window"] = new JObject { ["Index"] = 0, ["Start"] = "2024-01-01", ["End"] = "2024-06-30" },
        ["Metrics"] = new JObject { ["TradeCount"] = trades, ["Drawdown"] = drawdown },
        ["Fitness"] = 0.5
    };

    private static JObject Candidate(int population, double fitness, JObject genome, int requested, int valid, params JObject[] windows) => new()
    {
        ["Population"] = population,
        ["Genome"] = genome.DeepClone(),
        ["RobustnessAdjustedFitness"] = fitness,
        ["ParameterRobustness"] = new JObject { ["RequestedProbeCount"] = requested, ["ValidProbeCount"] = valid },
        ["Windows"] = new JArray(windows)
    };

    private static JObject Confirmation(JObject genome, double fitness, int trades, double drawdown) => new()
    {
        ["Genome"] = genome.DeepClone(),
        ["ConfirmationFitness"] = fitness,
        ["Metrics"] = new JObject { ["TradeCount"] = trades, ["Drawdown"] = drawdown },
        ["Passed"] = true
    };
}

