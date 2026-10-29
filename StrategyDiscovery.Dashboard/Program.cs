using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuantConnect.Genetic;
using QuantConnect.Indicators;

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
app.MapGet("/api/preview", (string? asOf, decimal? equity) => Results.Json(ReadDecisionPreview(repositoryRoot, outputDirectory, asOf, equity ?? 100_000m)));
app.MapGet("/api/outcomes", () => Results.Json(ReadPaperOutcomes(outputDirectory)));
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

static object ReadDecisionPreview(string repositoryRoot, string outputDirectory, string? asOfText, decimal equity)
{
    var overlayPath = Path.Combine(repositoryRoot, "examples", "genetic-discovery", "paper-config.overlay.json");
    var optimizerPath = Path.Combine(repositoryRoot, "examples", "genetic-discovery", "optimizer.json");
    var overlay = File.Exists(overlayPath) ? JsonNode.Parse(File.ReadAllText(overlayPath)) : null;
    var optimizer = File.Exists(optimizerPath) ? JsonNode.Parse(File.ReadAllText(optimizerPath)) : null;
    var settings = optimizer?["optimization-strategy-settings"];
    var confirmationStart = DateOnly.TryParse(settings?["confirmation-start-date"]?.ToString(), CultureInfo.InvariantCulture, out var parsedStart)
        ? parsedStart
        : new DateOnly(2025, 1, 1);
    var symbols = (overlay?["parameters"]?["discovery.symbols"]?.ToString() ?? "SPY|QQQ|IWM")
        .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var maximumCorrelation = ReadDouble(overlay?["parameters"]?["discovery.max-correlation"]) ?? 0.80;
    var maximumDrawdown = ReadDouble(overlay?["parameters"]?["discovery.max-drawdown"]) ?? 0.08;
    var latestApproval = ReadAudit(outputDirectory).LastOrDefault(item => item.Event == "strategy_candidate_approved_for_paper");
    var details = latestApproval?.Details as JsonObject;
    var genomeNode = (details?["Genome"] ?? details?["genome"]) as JsonObject;
    var latestDataDate = (DateOnly?)null;
    var empty = new
    {
        available = false,
        reason = "No approved candidate or local daily price archives are available.",
        confirmationStartDate = confirmationStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        latestDataDate = (string?)null
    };
    if (genomeNode is null) return empty;
    if (equity is < 1_000m or > 100_000_000m)
        return new { available = false, reason = "Enter a hypothetical account equity between $1,000 and $100,000,000.", confirmationStartDate = confirmationStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), latestDataDate = (string?)null };

    var family = ParseGenomeEnum<StrategyFamily>(genomeNode["Family"] ?? genomeNode["family"]);
    var timeframe = ParseGenomeEnum<StrategyTimeframe>(genomeNode["Timeframe"] ?? genomeNode["timeframe"]);
    var confirmationTimeframe = ParseGenomeEnum<StrategyTimeframe>(genomeNode["ConfirmationTimeframe"] ?? genomeNode["confirmationTimeframe"]);
    if (family is null || timeframe is null)
        return new { available = false, reason = "The approved candidate record does not contain a supported strategy genome.", confirmationStartDate = confirmationStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), latestDataDate = (string?)null };
    if (timeframe != StrategyTimeframe.Daily || confirmationTimeframe is not null)
        return new { available = false, reason = "This preview currently supports daily candidates without a second confirmation timeframe.", confirmationStartDate = confirmationStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), latestDataDate = (string?)null };

    var fastPeriod = ReadInt(genomeNode["FastPeriod"] ?? genomeNode["fastPeriod"]) ?? 10;
    var slowPeriod = ReadInt(genomeNode["SlowPeriod"] ?? genomeNode["slowPeriod"]) ?? 40;
    var entryTenths = ReadInt(genomeNode["EntryThresholdTenths"] ?? genomeNode["entryThresholdTenths"]) ?? 12;
    var exitTenths = ReadInt(genomeNode["ExitThresholdTenths"] ?? genomeNode["exitThresholdTenths"]) ?? 4;
    var maxPositions = ReadInt(genomeNode["MaxPositions"] ?? genomeNode["maxPositions"]) ?? 3;
    var maxExposurePercent = ReadInt(genomeNode["MaxExposurePercent"] ?? genomeNode["maxExposurePercent"]) ?? 30;
    if (fastPeriod <= 0 || slowPeriod <= fastPeriod || entryTenths <= exitTenths || exitTenths <= 0 || maxPositions <= 0 || maxExposurePercent is <= 0 or > 100)
        return new { available = false, reason = "The approved candidate genome has invalid periods, thresholds, or risk limits.", confirmationStartDate = confirmationStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), latestDataDate = (string?)null };

    var archiveDirectory = Path.Combine(repositoryRoot, "Data", "equity", "usa", "daily");
    var histories = new Dictionary<string, SortedDictionary<DateOnly, PreviewBar>>(StringComparer.OrdinalIgnoreCase);
    foreach (var ticker in symbols)
    {
        var path = Path.Combine(archiveDirectory, ticker.ToLowerInvariant() + ".zip");
        if (!File.Exists(path)) continue;
        histories[ticker] = DailyArchiveCache.Read(path, ReadDailyArchive);
    }
    if (histories.Count != symbols.Length || symbols.Length == 0)
        return new { available = false, reason = "A configured symbol is missing its local LEAN daily archive.", confirmationStartDate = confirmationStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), latestDataDate = (string?)null };

    var commonDates = histories.Values.Select(history => history.Keys.AsEnumerable()).Aggregate((left, right) => left.Intersect(right).Order().ToArray());
    latestDataDate = commonDates.Select(date => (DateOnly?)date).LastOrDefault();
    if (latestDataDate is null)
        return new { available = false, reason = "The configured symbol archives do not have a common trading date.", confirmationStartDate = confirmationStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), latestDataDate = (string?)null };
    var requestedDate = DateOnly.TryParse(asOfText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var requested) ? requested : latestDataDate.Value;
    var asOfDate = commonDates.Where(date => date <= requestedDate).Select(date => (DateOnly?)date).LastOrDefault();
    var latestDateText = latestDataDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    if (asOfDate is null || asOfDate < confirmationStart)
        return new { available = false, reason = $"Choose a trading date on or after the candidate confirmation start ({confirmationStart:yyyy-MM-dd}) and on or before the local archive end ({latestDateText}).", confirmationStartDate = confirmationStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), latestDataDate = latestDateText };

    var states = symbols.ToDictionary(ticker => ticker, _ => new PreviewSymbolState(fastPeriod, slowPeriod, entryTenths / 10d), StringComparer.OrdinalIgnoreCase);
    foreach (var date in commonDates.Where(date => date <= asOfDate.Value))
    {
        var allowSignals = date >= confirmationStart;
        foreach (var ticker in symbols)
        {
            var bar = histories[ticker][date];
            var state = states[ticker];
            if (date == asOfDate.Value) state.Bar = bar;
            var time = date.ToDateTime(TimeOnly.MinValue);
            state.Fast.Update(time, bar.Close);
            state.Slow.Update(time, bar.Close);
            state.Volatility.Update(time, bar.Close);
            state.Returns.Update(time, (double)bar.Close);
            state.Breakout.Update(time, bar.High, bar.Low, bar.Close, allowSignals);
            if (date != asOfDate.Value || !allowSignals || !state.Fast.IsReady || !state.Slow.IsReady || !state.Volatility.IsReady || state.Volatility.Current.Value <= 0)
                continue;
            var signal = StrategySignalEvaluator.Evaluate(family.Value, state.TrendEntered, state.MeanReversionEntered,
                entryTenths / 10d, exitTenths / 10d, bar.Close, state.Fast.Current.Value, state.Slow.Current.Value,
                state.Volatility.Current.Value, state.Breakout.Current);
            state.TrendEntered = signal.TrendEntered;
            state.MeanReversionEntered = signal.MeanReversionEntered;
            state.Evaluation = signal;
        }
    }

    var riskBudget = PortfolioRiskBudget.Calculate(maxPositions, maxExposurePercent / 100m, equity, Array.Empty<decimal>());
    var candidates = states.Where(pair => pair.Value.Evaluation is { Strength: > 0 })
        .OrderByDescending(pair => pair.Value.Evaluation!.Value.Strength).ThenBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
    var selected = new List<string>();
    var decisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var correlations = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
    foreach (var candidate in candidates)
    {
        if (selected.Count >= riskBudget.AvailablePositions)
        {
            decisions[candidate.Key] = "Position limit reached";
            correlations[candidate.Key] = null;
            continue;
        }
        var correlation = selected.Count == 0 ? null : SynchronizedReturnHistory.Correlation(candidate.Value.Returns, states[selected[0]].Returns);
        correlations[candidate.Key] = correlation;
        if (selected.Any(existing => SynchronizedReturnHistory.Correlation(candidate.Value.Returns, states[existing].Returns) is not { } value || Math.Abs(value) >= maximumCorrelation))
        {
            decisions[candidate.Key] = "Filtered: correlation unavailable or above limit";
            continue;
        }
        selected.Add(candidate.Key);
        decisions[candidate.Key] = "Hypothetical target";
    }
    var targetWeight = selected.Count == 0 ? 0m : riskBudget.AvailableGrossExposureFraction / selected.Count;
    var rows = symbols.Select(ticker =>
    {
        var state = states[ticker];
        var signal = state.Evaluation;
        var isSelected = selected.Contains(ticker, StringComparer.OrdinalIgnoreCase);
        var decision = decisions.TryGetValue(ticker, out var status) ? status : "No signal";
        return new
        {
            symbol = ticker,
            close = state.Bar?.Close,
            fastAverage = state.Fast.IsReady ? state.Fast.Current.Value : (decimal?)null,
            slowAverage = state.Slow.IsReady ? state.Slow.Current.Value : (decimal?)null,
            standardDeviation = state.Volatility.IsReady ? state.Volatility.Current.Value : (decimal?)null,
            spreadPercent = signal is null ? (double?)null : signal.Value.Spread * 100d,
            zScore = signal?.ZScore,
            signalStrength = signal?.Strength,
            hasSignal = signal is { Strength: > 0 },
            selected = isSelected,
            status = decision,
            correlationToSelected = correlations.GetValueOrDefault(ticker),
            targetWeight = isSelected ? targetWeight : 0m,
            targetValue = isSelected ? decimal.Round(equity * targetWeight, 2) : 0m
        };
    }).ToArray();
    var targetExposure = targetWeight * selected.Count;
    return new
    {
        available = true,
        asOfDate = asOfDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        requestedDate = requestedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        latestDataDate = latestDateText,
        dataSource = "Local LEAN daily equity archives (Data/equity/usa/daily)",
        candidateId = ReadInt(details?["CandidateId"] ?? details?["Id"]),
        candidateFitness = ReadDouble(details?["Fitness"] ?? details?["fitness"]),
        population = ReadPopulation(details?["Population"] ?? details?["population"]),
        genome = genomeNode,
        confirmationStartDate = confirmationStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        equityAssumption = equity,
        accountAssumption = "Flat account; no existing holdings are included. Allocations are hypothetical estimates.",
        maxCorrelation = maximumCorrelation,
        maxDrawdown = maximumDrawdown,
        maxPositions,
        maxExposurePercent,
        targetExposurePercent = targetExposure * 100m,
        selectedCount = selected.Count,
        ordersSubmitted = false,
        historicalConfirmationOverlap = asOfDate >= confirmationStart,
        warning = $"Historical-only dry run. Local price data ends {latestDateText}. This candidate was confirmed using data from {confirmationStart:yyyy-MM-dd}; these bars overlap that confirmation period. No current quotes, recommendation, or orders.",
        symbols = rows
    };
}

static SortedDictionary<DateOnly, PreviewBar> ReadDailyArchive(string path)
{
    var bars = new SortedDictionary<DateOnly, PreviewBar>();
    using var archive = ZipFile.OpenRead(path);
    var entry = archive.Entries.FirstOrDefault(item => item.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
    if (entry is null) return bars;
    using var reader = new StreamReader(entry.Open());
    while (reader.ReadLine() is { } line)
    {
        var fields = line.Split(',');
        if (fields.Length < 6 || !DateOnly.TryParseExact(fields[0].AsSpan(0, Math.Min(8, fields[0].Length)), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
        if (!decimal.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var high) ||
            !decimal.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var low) ||
            !decimal.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var close)) continue;
        bars[date] = new PreviewBar(high / 10_000m, low / 10_000m, close / 10_000m);
    }
    return bars;
}

static TEnum? ParseGenomeEnum<TEnum>(JsonNode? node) where TEnum : struct, Enum
{
    if (node is null) return null;
    return Enum.TryParse<TEnum>(node.ToString(), true, out var value) ? value
        : int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && Enum.IsDefined(typeof(TEnum), numeric)
            ? (TEnum)Enum.ToObject(typeof(TEnum), numeric)
            : null;
}

static object ReadPaperOutcomes(string outputDirectory)
{
    var outcomes = PaperTradeOutcomeJournal.Read(Path.Combine(outputDirectory, "paper-trade-outcomes.jsonl"));
    var summary = PaperTradeOutcomeJournal.Summarize(outcomes);
    var seeds = PaperTradeOutcomeJournal.GetSeedGenomes(outcomes, maximumCount: 2, minimumTrades: 8);
    var factor = summary.ProfitFactor;
    var byStrategy = outcomes.GroupBy(item => item.Genome)
        .Select(group => new
        {
            family = group.Key.Family.ToString(),
            timeframe = group.Key.Timeframe.ToString(),
            genome = group.Key,
            summary = PaperTradeOutcomeJournal.Summarize(group)
        })
        .OrderByDescending(item => item.summary.NetProfitLoss)
        .ToArray();
    return new
    {
        available = outcomes.Count > 0,
        source = Path.Combine(outputDirectory, "paper-trade-outcomes.jsonl"),
        tradeCount = summary.TradeCount,
        wins = summary.Wins,
        losses = summary.Losses,
        breakEven = summary.BreakEven,
        winRate = summary.WinRate,
        netProfitLoss = summary.NetProfitLoss,
        averageNetReturnPercent = summary.AverageNetReturnPercent,
        profitFactor = factor is { } value && double.IsFinite(value) ? value : (double?)null,
        unboundedProfitFactor = summary.Losses == 0 && summary.NetProfitLoss > 0,
        exactGenomesEligibleForSearchSeeding = seeds.Count,
        tradesRequiredForGenomeSeeding = 8,
        strategyHistory = byStrategy,
        recentTrades = outcomes.OrderByDescending(item => item.ExitTime).Take(100).ToArray()
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
sealed record PreviewBar(decimal High, decimal Low, decimal Close);
sealed class PreviewSymbolState(int fastPeriod, int slowPeriod, double entryThreshold)
{
    public SimpleMovingAverage Fast { get; } = new(fastPeriod);
    public SimpleMovingAverage Slow { get; } = new(slowPeriod);
    public StandardDeviation Volatility { get; } = new(slowPeriod);
    public BreakoutChannel Breakout { get; } = new(fastPeriod, slowPeriod, entryThreshold);
    public SynchronizedReturnHistory Returns { get; } = new(60);
    public bool TrendEntered { get; set; }
    public bool MeanReversionEntered { get; set; }
    public StrategySignalEvaluation? Evaluation { get; set; }
    public PreviewBar? Bar { get; set; }
}
static class DailyArchiveCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime ModifiedUtc, SortedDictionary<DateOnly, PreviewBar> Bars)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SortedDictionary<DateOnly, PreviewBar> Read(string path, Func<string, SortedDictionary<DateOnly, PreviewBar>> loader)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (Cache.TryGetValue(fullPath, out var cached) && cached.Length == info.Length && cached.ModifiedUtc == info.LastWriteTimeUtc)
            return cached.Bars;
        var updated = (info.Length, info.LastWriteTimeUtc, loader(fullPath));
        Cache[fullPath] = updated;
        return updated.Item3;
    }
}
