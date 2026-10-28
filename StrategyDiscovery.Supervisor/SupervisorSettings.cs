using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Genetic.Supervisor;

public sealed class SupervisorSettings
{
    [JsonProperty("dotnet-executable")] public string DotnetExecutable { get; set; } = "dotnet";
    [JsonProperty("optimizer-launcher-dll")] public string OptimizerLauncherDll { get; set; } = string.Empty;
    [JsonProperty("optimizer-working-directory")] public string OptimizerWorkingDirectory { get; set; } = string.Empty;
    [JsonProperty("optimizer-config")] public string OptimizerConfig { get; set; } = string.Empty;
    [JsonProperty("lean-launcher-dll")] public string LeanLauncherDll { get; set; } = string.Empty;
    [JsonProperty("lean-working-directory")] public string LeanWorkingDirectory { get; set; } = string.Empty;
    [JsonProperty("lean-base-config")] public string LeanBaseConfig { get; set; } = string.Empty;
    [JsonProperty("paper-config-overlay")] public string PaperConfigOverlay { get; set; } = string.Empty;
    [JsonProperty("alpaca-plugin-directory")] public string AlpacaPluginDirectory { get; set; } = string.Empty;
    [JsonProperty("output-directory")] public string OutputDirectory { get; set; } = string.Empty;
    [JsonProperty("kill-switch-file")] public string KillSwitchFile { get; set; } = string.Empty;
    [JsonProperty("reoptimization-interval-hours")] public int ReoptimizationIntervalHours { get; set; } = 168;
    [JsonProperty("minimum-validation-trades-per-window")] public int MinimumValidationTradesPerWindow { get; set; } = 10;
    [JsonProperty("maximum-validation-drawdown")] public double MaximumValidationDrawdown { get; set; } = 0.08;
    [JsonProperty("minimum-robustness-adjusted-fitness")] public double MinimumRobustnessAdjustedFitness { get; set; } = 0;
    [JsonProperty("maximum-consecutive-paper-failures")] public int MaximumConsecutivePaperFailures { get; set; } = 3;
    [JsonProperty("minimum-healthy-paper-run-minutes")] public int MinimumHealthyPaperRunMinutes { get; set; } = 5;
    [JsonProperty("restart-backoff-seconds")] public int RestartBackoffSeconds { get; set; } = 30;
    [JsonIgnore] public int MaximumPaperPositions { get; private set; } = 3;
    [JsonIgnore] public int MaximumPaperExposurePercent { get; private set; } = 30;
    [JsonIgnore] public int RequiredValidationWindowCount { get; private set; } = 1;
    [JsonIgnore] public int RequiredParameterProbeCount { get; private set; } = 1;

    public static SupervisorSettings Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var settings = JsonConvert.DeserializeObject<SupervisorSettings>(File.ReadAllText(fullPath))
            ?? throw new InvalidDataException("Supervisor configuration is empty.");
        var root = Path.GetDirectoryName(fullPath)!;
        settings.DotnetExecutable = string.IsNullOrWhiteSpace(settings.DotnetExecutable) ? "dotnet" : settings.DotnetExecutable;
        settings.OptimizerLauncherDll = Resolve(root, settings.OptimizerLauncherDll);
        settings.OptimizerWorkingDirectory = Resolve(root, settings.OptimizerWorkingDirectory);
        settings.OptimizerConfig = Resolve(root, settings.OptimizerConfig);
        settings.LeanLauncherDll = Resolve(root, settings.LeanLauncherDll);
        settings.LeanWorkingDirectory = Resolve(root, settings.LeanWorkingDirectory);
        settings.LeanBaseConfig = Resolve(root, settings.LeanBaseConfig);
        settings.PaperConfigOverlay = Resolve(root, settings.PaperConfigOverlay);
        settings.AlpacaPluginDirectory = Resolve(root, settings.AlpacaPluginDirectory);
        settings.OutputDirectory = Resolve(root, settings.OutputDirectory);
        settings.KillSwitchFile = Resolve(root, settings.KillSwitchFile);
        settings.Validate();
        return settings;
    }

    public string RankingsFile
    {
        get
        {
            var optimizer = JObjectConfig.Read(OptimizerConfig);
            var resultsFolder = optimizer["results-destination-folder"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(resultsFolder)) throw new InvalidDataException("Optimizer config must set results-destination-folder.");
            return Path.GetFullPath(Path.Combine(OptimizerWorkingDirectory, resultsFolder, "genetic-search-rankings.json"));
        }
    }

    public void Validate()
    {
        foreach (var path in new[] { OptimizerLauncherDll, OptimizerWorkingDirectory, OptimizerConfig, LeanLauncherDll, LeanWorkingDirectory, LeanBaseConfig, PaperConfigOverlay, AlpacaPluginDirectory })
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("All optimizer and LEAN paths must be set.");
        }
        if (ReoptimizationIntervalHours < 1) throw new InvalidDataException("reoptimization-interval-hours must be positive.");
        if (MinimumValidationTradesPerWindow < 0) throw new InvalidDataException("minimum-validation-trades-per-window cannot be negative.");
        if (MaximumConsecutivePaperFailures < 1 || MinimumHealthyPaperRunMinutes < 1 || RestartBackoffSeconds < 1)
            throw new InvalidDataException("Paper restart limits and backoff must be positive.");
        if (!double.IsFinite(MaximumValidationDrawdown) || MaximumValidationDrawdown is <= 0 or >= 1)
            throw new InvalidDataException("maximum-validation-drawdown must be between zero and one.");
        if (!double.IsFinite(MinimumRobustnessAdjustedFitness)) throw new InvalidDataException("minimum-robustness-adjusted-fitness must be finite.");
        foreach (var path in new[] { OptimizerLauncherDll, OptimizerWorkingDirectory, OptimizerConfig, LeanLauncherDll, LeanWorkingDirectory, LeanBaseConfig, PaperConfigOverlay, AlpacaPluginDirectory })
        {
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Configured supervisor path does not exist.", path);
        }
        if (!Directory.Exists(AlpacaPluginDirectory) ||
            !File.Exists(Path.Combine(AlpacaPluginDirectory, "QuantConnect.Brokerages.Alpaca.dll")) ||
            !File.Exists(Path.Combine(AlpacaPluginDirectory, "Alpaca.Markets.dll")))
            throw new InvalidDataException("alpaca-plugin-directory must contain the LEAN Alpaca brokerage assembly and Alpaca.Markets dependency. Run scripts/setup-alpaca-plugin.ps1 first.");

        var optimizer = JObjectConfig.Read(OptimizerConfig);
        var optimizerSettings = optimizer["optimization-strategy-settings"] as JObject
            ?? throw new InvalidDataException("Optimizer config is missing optimization-strategy-settings.");
        var paperParameters = JObjectConfig.Read(PaperConfigOverlay)["parameters"] as JObject
            ?? throw new InvalidDataException("Paper overlay is missing strategy risk parameters.");
        MaximumPaperPositions = ParseInt(paperParameters["discovery.max-positions"])
            ?? throw new InvalidDataException("Paper overlay is missing discovery.max-positions.");
        MaximumPaperExposurePercent = ParseInt(paperParameters["discovery.max-exposure-percent"])
            ?? throw new InvalidDataException("Paper overlay is missing discovery.max-exposure-percent.");
        RequiredValidationWindowCount = optimizerSettings["validation-window-count"]?.Value<int>()
            ?? throw new InvalidDataException("Optimizer config must set validation-window-count.");
        RequiredParameterProbeCount = optimizerSettings["parameter-robustness-probe-count"]?.Value<int>()
            ?? throw new InvalidDataException("Optimizer config must set parameter-robustness-probe-count.");
        var randomBaselineSize = optimizerSettings["random-baseline-size"]?.Value<int>()
            ?? throw new InvalidDataException("Optimizer config must set random-baseline-size.");
        var validationBaselineCount = optimizerSettings["validation-baseline-count"]?.Value<int>()
            ?? throw new InvalidDataException("Optimizer config must set validation-baseline-count.");
        if (RequiredValidationWindowCount < 1 || RequiredParameterProbeCount < 1)
            throw new InvalidDataException("Paper promotion requires at least one validation window and one parameter robustness probe.");
        if (randomBaselineSize < 1 || validationBaselineCount < 1 || validationBaselineCount > randomBaselineSize)
            throw new InvalidDataException("Paper promotion requires one or more validation baselines, bounded by random-baseline-size.");

        RequireEqual(
            optimizerSettings["maximum-positions"]?.Value<int>(),
            ParseInt(paperParameters["discovery.max-positions"]),
            "maximum-positions must match the paper risk limit.");
        RequireEqual(
            optimizerSettings["maximum-exposure-percent"]?.Value<int>(),
            ParseInt(paperParameters["discovery.max-exposure-percent"]),
            "maximum-exposure-percent must match the paper risk limit.");
        RequireEqual(
            optimizerSettings["minimum-spread-bps"]?.Value<decimal>(),
            ParseDecimal(paperParameters["discovery.minimum-spread-bps"]),
            "minimum-spread-bps must match the paper execution-cost setting.");
        RequireEqual(
            optimizerSettings["maximum-correlation"]?.Value<double>(),
            ParseDouble(paperParameters["discovery.max-correlation"]),
            "maximum-correlation must match the paper risk limit.");
        RequireEqual(
            optimizerSettings["maximum-portfolio-drawdown"]?.Value<double>(),
            ParseDouble(paperParameters["discovery.max-drawdown"]),
            "maximum-portfolio-drawdown must match the paper risk limit.");

        var optimizerSymbols = (optimizerSettings["symbols"]?.Value<string>() ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.ToUpperInvariant()).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var paperSymbols = (paperParameters["discovery.symbols"]?.Value<string>() ?? string.Empty)
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.ToUpperInvariant()).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!optimizerSymbols.SequenceEqual(paperSymbols, StringComparer.Ordinal))
            throw new InvalidDataException("Optimizer and paper symbol universes must match exactly.");
        var optimizerUniverseMode = optimizerSettings["universe-mode"]?.Value<string>() ?? "Configured";
        var paperUniverseMode = paperParameters["discovery.universe-mode"]?.Value<string>() ?? "Configured";
        if (!string.Equals(optimizerUniverseMode, "Configured", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(optimizerUniverseMode, "TopDollarVolume", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("universe-mode must be Configured or TopDollarVolume.");
        if (!string.Equals(optimizerUniverseMode, paperUniverseMode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Optimizer and paper universe modes must match.");
        var optimizerUniverseSize = optimizerSettings["universe-size"]?.Value<int>() ?? 20;
        var paperUniverseSize = ParseInt(paperParameters["discovery.universe-size"]) ?? 20;
        if (optimizerUniverseSize is < 1 or > 100 || paperUniverseSize is < 1 or > 100)
            throw new InvalidDataException("universe-size must be between one and one hundred in optimizer and paper settings.");
        if (optimizerUniverseSize != paperUniverseSize)
            throw new InvalidDataException("Optimizer and paper universe sizes must match.");
        if (MaximumValidationDrawdown > ParseDouble(paperParameters["discovery.max-drawdown"]))
            throw new InvalidDataException("maximum-validation-drawdown cannot exceed the live paper drawdown limit.");
    }

    private static int? ParseInt(JToken? token) => token is null ? null : int.Parse(token.ToString(), System.Globalization.CultureInfo.InvariantCulture);

    private static double? ParseDouble(JToken? token) => token is null ? null : double.Parse(token.ToString(), System.Globalization.CultureInfo.InvariantCulture);

    private static decimal? ParseDecimal(JToken? token) => token is null ? null : decimal.Parse(token.ToString(), System.Globalization.CultureInfo.InvariantCulture);

    private static void RequireEqual<T>(T? configured, T? paper, string message) where T : struct, IEquatable<T>
    {
        if (configured is null || paper is null || !configured.Value.Equals(paper.Value)) throw new InvalidDataException(message);
    }

    private static string Resolve(string root, string value) => Path.GetFullPath(Path.Combine(root, value));

}

internal static class JObjectConfig
{
    public static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path)));
        return JObject.Load(reader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
    }
}
