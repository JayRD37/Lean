using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Genetic;

namespace QuantConnect.Genetic.Supervisor;

public static class PaperConfigFactory
{
    public static JObject Create(
        string baseConfigPath,
        string overlayPath,
        string algorithmTypeName,
        PromotedStrategy strategy,
        string killSwitchPath,
        string alpacaApiKey,
        string alpacaApiSecret)
    {
        if (string.IsNullOrWhiteSpace(alpacaApiKey) || string.IsNullOrWhiteSpace(alpacaApiSecret))
            throw new InvalidOperationException("Alpaca paper API key and secret must be supplied via environment variables.");
        if (string.IsNullOrWhiteSpace(algorithmTypeName)) throw new ArgumentException("An algorithm type name is required.", nameof(algorithmTypeName));

        var config = JObjectConfig.Read(baseConfigPath);
        var overlay = JObjectConfig.Read(overlayPath);
        config.Merge(overlay, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
        config["environment"] = "live-alpaca";
        config["algorithm-type-name"] = algorithmTypeName;
        config["algorithm-language"] = "CSharp";
        config["alpaca-api-key"] = alpacaApiKey;
        config["alpaca-api-secret"] = alpacaApiSecret;
        config["alpaca-paper-trading"] = true;

        var environment = config["environments"]?["live-alpaca"] as JObject
            ?? throw new InvalidDataException("The live-alpaca environment is missing from the merged LEAN config.");
        if (environment["live-mode"]?.Value<bool>() != true || environment["live-mode-brokerage"]?.Value<string>() != "AlpacaBrokerage")
            throw new InvalidDataException("The selected LEAN environment must be live-mode Alpaca.");

        var parameters = config["parameters"] as JObject;
        if (parameters is null)
        {
            parameters = new JObject();
            config["parameters"] = parameters;
        }
        parameters["discovery.allow-paper-trading"] = "true";
        parameters["discovery.phase"] = "Validation";
        parameters["discovery.family"] = strategy.Genome.Family.ToString();
        parameters["discovery.timeframe"] = strategy.Genome.Timeframe.ToString();
        parameters["discovery.confirmation-timeframe"] = strategy.Genome.ConfirmationTimeframe?.ToString() ?? "None";
        parameters["discovery.fast-period"] = strategy.Genome.FastPeriod.ToString(System.Globalization.CultureInfo.InvariantCulture);
        parameters["discovery.slow-period"] = strategy.Genome.SlowPeriod.ToString(System.Globalization.CultureInfo.InvariantCulture);
        parameters["discovery.entry-tenths"] = strategy.Genome.EntryThresholdTenths.ToString(System.Globalization.CultureInfo.InvariantCulture);
        parameters["discovery.exit-tenths"] = strategy.Genome.ExitThresholdTenths.ToString(System.Globalization.CultureInfo.InvariantCulture);
        parameters["discovery.max-positions"] = strategy.Genome.MaxPositions.ToString(System.Globalization.CultureInfo.InvariantCulture);
        parameters["discovery.max-exposure-percent"] = strategy.Genome.MaxExposurePercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        parameters["discovery.kill-switch-file"] = Path.GetFullPath(killSwitchPath);
        parameters["discovery.drawdown-state-file"] = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(killSwitchPath))!, "portfolio-drawdown-state.json");

        if (config["alpaca-paper-trading"]?.Value<bool>() != true || parameters["discovery.allow-paper-trading"]?.Value<string>() != "true")
            throw new InvalidDataException("Paper-trading guards were not enabled in the generated configuration.");

        return config;
    }

    public static string WriteEphemeralConfig(JObject config)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LeanGeneticPaper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        File.WriteAllText(path, config.ToString(Formatting.Indented));
        return path;
    }

    public static void DeleteEphemeralConfig(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LeanGeneticPaper")) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete a config outside the supervisor's temporary paper-config directory.");
        var directory = Path.GetDirectoryName(fullPath)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

