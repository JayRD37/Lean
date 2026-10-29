using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
var repositoryRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("LEAN_DISCOVERY_ROOT") ?? Path.Combine(builder.Environment.ContentRootPath, ".."));
var outputDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, "supervisor-output"));
var settingsFile = Path.Combine(repositoryRoot, "examples", "genetic-discovery", "supervisor.json");
var settings = File.Exists(settingsFile) ? JsonNode.Parse(await File.ReadAllTextAsync(settingsFile)) : null;
var configuredKillSwitch = settings?["kill-switch-file"]?.GetValue<string>();
var killSwitchPath = Path.GetFullPath(Path.IsPathRooted(configuredKillSwitch ?? "")
    ? configuredKillSwitch!
    : Path.Combine(repositoryRoot, "examples", "genetic-discovery", configuredKillSwitch ?? "emergency-stop"));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", () => Results.Json(ReadStatus(outputDirectory, killSwitchPath)));
app.MapGet("/api/activity", (int? limit) => Results.Json(ReadActivity(outputDirectory, Math.Clamp(limit ?? 80, 1, 300))));
app.MapGet("/api/strategies", () => Results.Json(ReadStrategies(outputDirectory)));
app.MapGet("/api/portfolio", () => Results.Json(ReadPortfolio(outputDirectory)));
app.MapGet("/api/campaign", () => Results.Json(ReadCampaign(repositoryRoot, settings)));
app.MapGet("/api/research", () => Results.Json(ReadResearch(repositoryRoot, settingsFile, settings, outputDirectory)));
app.MapPost("/api/emergency-stop", (HttpContext context) =>
{
    if (!string.Equals(context.Request.Headers.Origin, "http://127.0.0.1:5178", StringComparison.Ordinal))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var pid = GetActivePaperPid(outputDirectory);
    if (!IsProcessAlive(pid)) return Results.Conflict(new { error = "No active paper process is recorded." });
    Directory.CreateDirectory(Path.GetDirectoryName(killSwitchPath)!);
    File.WriteAllText(killSwitchPath, $"Dashboard emergency stop {DateTimeOffset.UtcNow:O}{Environment.NewLine}");
    AppendActivity(outputDirectory, "dashboard_emergency_stop_requested", new { source = "local dashboard" });
    return Results.Accepted("/api/status", new { requested = true, killSwitchPath });
});
app.MapFallbackToFile("index.html");
await app.RunAsync("http://127.0.0.1:5178");

static object ReadStatus(string outputDirectory, string killSwitchPath)
{
    var sessionFile = Path.Combine(outputDirectory, "active-paper-session.json");
    JsonObject? session = File.Exists(sessionFile) ? JsonNode.Parse(File.ReadAllText(sessionFile)) as JsonObject : null;
    var pid = session?["ProcessId"]?.GetValue<int>() ?? 0;
    var running = IsProcessAlive(pid);
    var latest = Directory.Exists(outputDirectory)
        ? Directory.GetDirectories(outputDirectory, "paper-results-*").OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault()
        : null;
    var resultFile = latest is null ? null : Path.Combine(latest, "GeneticDiscoveryAlgorithm.json");
    JsonObject? result = resultFile is not null && File.Exists(resultFile) ? JsonNode.Parse(File.ReadAllText(resultFile)) as JsonObject : null;
    var runtime = result?["runtimeStatistics"] as JsonObject;
    var state = result?["state"] as JsonObject;
    var charts = result?["charts"]?["Strategy Equity"]?["series"] as JsonObject;
    var equityPoints = ReadEquityPoints(charts);
    var pointCount = equityPoints.Count;
    var audits = ReadAudit(outputDirectory);
    var lastEvent = audits.LastOrDefault();
    var processStart = session?["StartedAtUtc"]?.GetValue<DateTimeOffset?>( );
    return new
    {
        generatedAtUtc = DateTimeOffset.UtcNow,
        supervisor = File.Exists(Path.Combine(outputDirectory, "supervisor-audit.jsonl")) ? "connected" : "not found",
        paperProcessRunning = running,
        paperProcessId = running ? pid : (int?)null,
        paperStartedAtUtc = processStart,
        emergencyStopPresent = File.Exists(killSwitchPath),
        killSwitchPath,
        genome = session?["Genome"],
        fitness = session?["Fitness"],
        population = session?["Population"],
        validationWindowCount = session?["ValidationWindowCount"],
        equity = runtime?["Equity"]?.GetValue<string>() ?? "—",
        netProfit = runtime?["Net Profit"]?.GetValue<string>() ?? "—",
        returnValue = runtime?["Return"]?.GetValue<string>() ?? "—",
        holdings = (result?["holdings"] as JsonObject)?.Count ?? 0,
        orderCount = ParseInt(state?["OrderCount"]?.GetValue<string>()),
        equityPoints,
        equityPointCount = pointCount,
        dataState = pointCount > 0 ? "equity points received" : running ? "no equity points yet" : "no active paper run",
        leanStatus = state?["Status"]?.GetValue<string>() ?? "No run result",
        latestRunDirectory = latest is null ? null : Path.GetFileName(latest),
        lastEvent = lastEvent is null ? null : new { lastEvent.Utc, lastEvent.Event }
    };
}

static List<object> ReadEquityPoints(JsonObject? series)
{
    var values = series?.AsObject().SelectMany(pair => pair.Value?["values"]?.AsArray() ?? []).ToArray() ?? [];
    return values.Select(point =>
        {
            var x = point?["x"]?.ToString() ?? point?["time"]?.ToString() ?? "";
            var yNode = point?["y"] ?? point?["value"];
            return double.TryParse(yNode?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                ? (object)new { x, y }
                : null;
        })
        .Where(point => point is not null)
        .Cast<object>()
        .ToList();
}

static int GetActivePaperPid(string outputDirectory)
{
    var sessionFile = Path.Combine(outputDirectory, "active-paper-session.json");
    if (!File.Exists(sessionFile)) return 0;
    try { return JsonNode.Parse(File.ReadAllText(sessionFile))?["ProcessId"]?.GetValue<int>() ?? 0; }
    catch (JsonException) { return 0; }
}

static object ReadStrategies(string outputDirectory)
{
    return ReadAudit(outputDirectory)
        .Where(item => item.Event is "strategy_candidate_approved_for_paper")
        .Reverse()
        .Select(item => new { item.Utc, item.Event, details = item.Details })
        .ToArray();
}

static object ReadPortfolio(string outputDirectory)
{
    var latest = Directory.Exists(outputDirectory)
        ? Directory.GetDirectories(outputDirectory, "paper-results-*").OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault()
        : null;
    var resultFile = latest is null ? null : Path.Combine(latest, "GeneticDiscoveryAlgorithm.json");
    var result = resultFile is not null && File.Exists(resultFile) ? JsonNode.Parse(File.ReadAllText(resultFile)) as JsonObject : null;
    var holdings = result?["holdings"] as JsonObject;
    var items = holdings?.Select(pair => new
    {
        symbol = pair.Key,
        quantity = pair.Value?["quantity"]?.ToString() ?? pair.Value?["Quantity"]?.ToString() ?? "—",
        mark = pair.Value?["price"]?.ToString() ?? pair.Value?["Price"]?.ToString() ?? "—",
        value = pair.Value?["marketValue"]?.ToString() ?? pair.Value?["MarketValue"]?.ToString() ?? "—"
    }).ToArray() ?? [];
    return new { latestRunDirectory = latest is null ? null : Path.GetFileName(latest), holdings = items };
}

static object ReadCampaign(string repositoryRoot, JsonNode? supervisorSettings)
{
    var configPath = Path.Combine(repositoryRoot, "examples", "genetic-discovery", "optimizer.json");
    var optimizer = File.Exists(configPath) ? JsonNode.Parse(File.ReadAllText(configPath)) : null;
    var options = optimizer?["optimization-strategy-settings"];
    var criterion = optimizer?["optimization-criterion"];
    return new
    {
        source = "examples/genetic-discovery/optimizer.json",
        objective = criterion?["target"]?.GetValue<string>() ?? "—",
        populationSize = options?["population-size"]?.GetValue<int>() ?? 0,
        generationCount = options?["generation-count"]?.GetValue<int>() ?? 0,
        randomBaselineSize = options?["random-baseline-size"]?.GetValue<int>() ?? 0,
        eliteCount = options?["elite-count"]?.GetValue<int>() ?? 0,
        tournamentSize = options?["tournament-size"]?.GetValue<int>() ?? 0,
        mutationRate = options?["mutation-rate"]?.GetValue<double>() ?? 0,
        monteCarloSimulationCount = options?["monte-carlo-simulation-count"]?.GetValue<int>() ?? 0,
        validationWindowCount = options?["validation-window-count"]?.GetValue<int>() ?? 0,
        validationFinalistCount = options?["validation-finalist-count"]?.GetValue<int>() ?? 0,
        robustnessProbeCount = options?["parameter-robustness-probe-count"]?.GetValue<int>() ?? 0,
        maximumConcurrentBacktests = optimizer?["maximum-concurrent-backtests"]?.GetValue<int>() ?? 0,
        allowedTimeframes = options?["allowed-timeframes"]?.GetValue<string>() ?? "—",
        symbols = options?["symbols"]?.GetValue<string>() ?? "—",
        trainingStartDate = options?["training-start-date"]?.GetValue<string>() ?? "—",
        trainingEndDate = options?["training-end-date"]?.GetValue<string>() ?? "—",
        validationStartDate = options?["validation-start-date"]?.GetValue<string>() ?? "—",
        validationEndDate = options?["validation-end-date"]?.GetValue<string>() ?? "—",
        confirmationStartDate = options?["confirmation-start-date"]?.GetValue<string>() ?? "—",
        confirmationEndDate = options?["confirmation-end-date"]?.GetValue<string>() ?? "—",
        reoptimizationIntervalHours = supervisorSettings?["reoptimization-interval-hours"]?.GetValue<double>() ?? 0
    };
}

static object ReadResearch(string repositoryRoot, string settingsFile, JsonNode? supervisorSettings, string outputDirectory)
{
    var audit = ReadAudit(outputDirectory);
    var latestStart = audit.LastOrDefault(item => item.Event == "optimization_started")?.Utc;
    var settingsDirectory = Path.GetDirectoryName(settingsFile)!;
    var optimizerConfigPath = Path.GetFullPath(Path.Combine(settingsDirectory,
        supervisorSettings?["optimizer-config"]?.GetValue<string>() ?? "optimizer.json"));
    var optimizerWorkingDirectory = Path.GetFullPath(Path.Combine(settingsDirectory,
        supervisorSettings?["optimizer-working-directory"]?.GetValue<string>() ?? "..\\..\\Optimizer.Launcher\\bin\\Debug"));
    var optimizer = File.Exists(optimizerConfigPath) ? JsonNode.Parse(File.ReadAllText(optimizerConfigPath)) : null;
    var resultsDirectorySetting = optimizer?["results-destination-folder"]?.GetValue<string>();
    var resultsDirectory = string.IsNullOrWhiteSpace(resultsDirectorySetting)
        ? null
        : Path.GetFullPath(Path.Combine(optimizerWorkingDirectory, resultsDirectorySetting));
    var historyPath = resultsDirectory is null ? null : Path.Combine(resultsDirectory, "genetic-search-history.jsonl");
    var rankingPath = resultsDirectory is null ? null : Path.Combine(resultsDirectory, "genetic-search-rankings.json");
    var rows = new List<ResearchRecord>();
    var malformedRows = 0;

    if (historyPath is not null && File.Exists(historyPath))
    {
        foreach (var line in File.ReadLines(historyPath))
        {
            try
            {
                var row = JsonNode.Parse(line) as JsonObject;
                if (row is null) { malformedRows++; continue; }
                var utc = DateTimeOffset.TryParse(row["utc"]?.ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedUtc)
                    ? parsedUtc
                    : (DateTimeOffset?)null;
                if (utc is null || (latestStart is not null && utc < latestStart)) continue;
                var generation = ReadInt(row["Generation"]);
                var id = ReadInt(row["Id"]);
                var population = ReadPopulation(row["Population"]);
                var phase = ReadPhase(row["phase"]);
                var fitness = ReadDouble(row["Fitness"]);
                if (generation is null || id is null || population is null || phase is null || fitness is null) { malformedRows++; continue; }
                rows.Add(new ResearchRecord(utc.Value, id.Value, generation.Value, population, phase,
                    fitness.Value, row["Genome"]?.DeepClone() as JsonObject, row["Metrics"]?.DeepClone() as JsonObject));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            {
                malformedRows++;
            }
        }
    }

    var training = rows.Where(item => item.Phase == "Training").ToArray();
    var generations = training
        .GroupBy(item => new { item.Generation, item.Population })
        .OrderBy(group => group.Key.Generation)
        .ThenBy(group => group.Key.Population)
        .Select(group =>
        {
            var scores = group.Where(item => item.Metrics is not null && double.IsFinite(item.Fitness))
                .Select(item => item.Fitness).OrderBy(value => value).ToArray();
            var best = group.Where(item => item.Metrics is not null && double.IsFinite(item.Fitness))
                .OrderByDescending(item => item.Fitness).ThenBy(item => item.Id).FirstOrDefault();
            return new
            {
                generation = group.Key.Generation,
                population = group.Key.Population,
                candidateCount = group.Count(),
                scoredCandidateCount = scores.Length,
                bestFitness = scores.Length == 0 ? (double?)null : scores[^1],
                medianFitness = scores.Length == 0 ? (double?)null : Median(scores),
                bestGenome = best?.Genome,
                bestCandidateId = best?.Id
            };
        }).ToArray();

    var populationNames = new Dictionary<string, string>
    {
        ["Growth"] = "Growth",
        ["Consistency"] = "Consistency",
        ["RandomBaseline"] = "Random baseline"
    };
    var landscape = training
        .Where(item => item.Genome is not null && item.Metrics is not null)
        .GroupBy(item => new { item.Generation, item.Population, GenomeKey = item.Genome!.ToJsonString() })
        .Select(group =>
        {
            var items = group.OrderBy(item => item.Id).ToArray();
            var scores = items.Select(item => item.Fitness).Where(double.IsFinite).OrderBy(value => value).ToArray();
            var best = items.OrderByDescending(item => item.Fitness).ThenBy(item => item.Id).First();
            return new
            {
                generation = group.Key.Generation,
                population = group.Key.Population,
                genome = best.Genome,
                fitness = best.Fitness,
                candidateCount = items.Length,
                fastPeriod = ReadInt(best.Genome?["FastPeriod"]),
                slowPeriod = ReadInt(best.Genome?["SlowPeriod"]),
                family = best.Genome?["Family"]?.ToString(),
                timeframe = best.Genome?["Timeframe"]?.ToString(),
                bestCandidateId = best.Id,
                medianFitness = scores.Length == 0 ? (double?)null : Median(scores)
            };
        })
        .OrderBy(item => item.generation).ThenBy(item => item.population).ThenBy(item => item.fastPeriod)
        .ToArray();

    var latestGeneration = training.Select(item => (int?)item.Generation).Max();
    var profiles = latestGeneration is null ? Array.Empty<object>() : training
        .Where(item => item.Generation == latestGeneration && item.Metrics is not null)
        .GroupBy(item => item.Population)
        .Select(group => group.OrderByDescending(item => item.Fitness).ThenBy(item => item.Id).First())
        .Select(item => (object)new
        {
            candidateId = item.Id,
            generation = item.Generation,
            population = item.Population,
            fitness = item.Fitness,
            genome = item.Genome,
            metrics = item.Metrics is null ? null : new
            {
                annualReturn = ReadDouble(item.Metrics["AnnualReturn"]),
                sharpeRatio = ReadDouble(item.Metrics["SharpeRatio"]),
                drawdown = ReadDouble(item.Metrics["Drawdown"]),
                winRate = ReadDouble(item.Metrics["WinRate"]),
                tradeCount = ReadInt(item.Metrics["TradeCount"]),
                tradeReturns = item.Metrics["TradeReturns"]?.DeepClone()
            }
        }).ToArray();

    var recent = rows.OrderByDescending(item => item.Utc).Take(16).Select(item => new
    {
        utc = item.Utc,
        candidateId = item.Id,
        generation = item.Generation,
        population = item.Population,
        phase = item.Phase,
        fitness = item.Fitness,
        annualReturn = ReadDouble(item.Metrics?["AnnualReturn"]),
        sharpeRatio = ReadDouble(item.Metrics?["SharpeRatio"]),
        drawdown = ReadDouble(item.Metrics?["Drawdown"]),
        tradeCount = ReadInt(item.Metrics?["TradeCount"]),
        genome = item.Genome
    }).ToArray();

    return new
    {
        available = historyPath is not null && File.Exists(historyPath),
        campaignStartedAtUtc = latestStart,
        historyUpdatedAtUtc = historyPath is not null && File.Exists(historyPath) ? File.GetLastWriteTimeUtc(historyPath) : (DateTime?)null,
        resultSource = historyPath,
        generationCount = generations.Select(item => item.generation).Distinct().Count(),
        evaluationCount = rows.Count,
        malformedRows,
        populationLabels = populationNames,
        generations,
        landscape,
        latestGeneration,
        latestGenerationProfiles = profiles,
        recentEvaluations = recent,
        validationRankingAvailable = rankingPath is not null && File.Exists(rankingPath)
    };
}

static int? ReadInt(JsonNode? node) => int.TryParse(node?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

static double? ReadDouble(JsonNode? node) => double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

static string? ReadPopulation(JsonNode? node) => node?.ToString() switch
{
    "0" or "Growth" => "Growth",
    "1" or "Consistency" => "Consistency",
    "2" or "RandomBaseline" => "RandomBaseline",
    _ => null
};

static string? ReadPhase(JsonNode? node) => node?.ToString() switch
{
    "0" or "Training" => "Training",
    "1" or "Validation" => "Validation",
    "2" or "Confirmation" => "Confirmation",
    _ => null
};

static double Median(IReadOnlyList<double> sortedValues) => sortedValues.Count % 2 == 0
    ? (sortedValues[sortedValues.Count / 2 - 1] + sortedValues[sortedValues.Count / 2]) / 2d
    : sortedValues[sortedValues.Count / 2];

static object ReadActivity(string outputDirectory, int limit) => ReadAudit(outputDirectory).TakeLast(limit).Reverse().ToArray();

static List<AuditEntry> ReadAudit(string outputDirectory)
{
    var path = Path.Combine(outputDirectory, "supervisor-audit.jsonl");
    if (!File.Exists(path)) return [];
    var entries = new List<AuditEntry>();
    foreach (var line in File.ReadLines(path))
    {
        try
        {
            var row = JsonNode.Parse(line);
            entries.Add(new AuditEntry(row?["utc"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.MinValue,
                row?["event"]?.GetValue<string>() ?? "unknown", row?["details"]?.DeepClone()));
        }
        catch (JsonException) { }
    }
    return entries;
}

static void AppendActivity(string outputDirectory, string eventName, object details)
{
    Directory.CreateDirectory(outputDirectory);
    var line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, @event = eventName, details });
    File.AppendAllText(Path.Combine(outputDirectory, "supervisor-audit.jsonl"), line + Environment.NewLine);
}

static bool IsProcessAlive(int pid)
{
    if (pid <= 0) return false;
    try { return !Process.GetProcessById(pid).HasExited; }
    catch (ArgumentException) { return false; }
    catch (InvalidOperationException) { return false; }
}

static int ParseInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

sealed record AuditEntry(DateTimeOffset Utc, string Event, JsonNode? Details);
sealed record ResearchRecord(DateTimeOffset Utc, int Id, int Generation, string Population, string Phase, double Fitness, JsonObject? Genome, JsonObject? Metrics);
