using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Genetic.Supervisor;

public sealed class SupervisorHost(SupervisorSettings settings)
{
    private readonly string _auditFile = Path.Combine(settings.OutputDirectory, "supervisor-audit.jsonl");
    private readonly string _paperSessionStateFile = Path.Combine(settings.OutputDirectory, "active-paper-session.json");
    private readonly SemaphoreSlim _auditLock = new(1, 1);
    private readonly PaperRestartPolicy _restartPolicy = new(
        settings.MaximumConsecutivePaperFailures,
        TimeSpan.FromMinutes(settings.MinimumHealthyPaperRunMinutes),
        TimeSpan.FromSeconds(settings.RestartBackoffSeconds));
    private bool _ownsKillSwitch;
    private string? _ownedKillSwitchMarker;
    private readonly HashSet<string> _knownPaperOutcomeIds = new(StringComparer.Ordinal);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settings.OutputDirectory);
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runToken = sessionCancellation.Token;
        var stopAtUtc = settings.PaperSessionDurationHours > 0
            ? DateTimeOffset.UtcNow.AddHours(settings.PaperSessionDurationHours)
            : (DateTimeOffset?)null;
        if (stopAtUtc is { } scheduledStop)
        {
            sessionCancellation.CancelAfter(TimeSpan.FromHours(settings.PaperSessionDurationHours));
            await AuditAsync("paper_session_duration_set", new
            {
                stopAtUtc = scheduledStop,
                durationHours = settings.PaperSessionDurationHours,
                capitalLimit = settings.PaperSessionCapitalLimit
            }).ConfigureAwait(false);
        }
        foreach (var outcome in PaperTradeOutcomeJournal.Read(settings.PaperTradeOutcomesFile))
            _knownPaperOutcomeIds.Add(outcome.OutcomeId);
        using var instanceLock = SupervisorInstanceLock.Acquire(settings.OutputDirectory);
        PaperSession? active = null;
        PromotedStrategy? activeStrategy = null;
        try
        {
            var orphan = PaperProcessRecoveryGuard.FindOrphan(_paperSessionStateFile, PaperProcessRecoveryGuard.GetProcessStartTimeUtc);
            if (orphan is not null)
            {
                if (!PaperConfigFactory.IsRecoverablePaperConfig(orphan.ConfigPath, settings.KillSwitchFile, settings.OutputDirectory))
                {
                    await AuditAsync("orphaned_paper_process_detected_fail_closed", new { orphan.ProcessId, orphan.StartedAtUtc }).ConfigureAwait(false);
                    throw new InvalidOperationException($"A previous LEAN paper process is still running (PID {orphan.ProcessId}, started {orphan.StartedAtUtc:O}) but its session metadata cannot be verified. Inspect and reconcile it before restarting the supervisor.");
                }

                active = AdoptPaper(orphan);
                if (orphan.Genome is not null && orphan.Fitness is { } fitness && orphan.Population is { } population)
                    activeStrategy = new PromotedStrategy(orphan.Genome, fitness, population, orphan.ValidationWindowCount);
                await AuditAsync("orphaned_paper_process_adopted", new { orphan.ProcessId, orphan.StartedAtUtc, genome = orphan.Genome }).ConfigureAwait(false);
            }

            if (File.Exists(settings.KillSwitchFile))
            {
                if (active is null)
                    throw new InvalidOperationException($"Emergency stop is already present at '{settings.KillSwitchFile}'. Clear it only after investigating the stop request.");

                await AuditAsync("adopted_paper_process_stopping_for_existing_emergency_stop", new { processId = active.Process.Id }).ConfigureAwait(false);
                await WaitForPaperShutdownAsync(active, "existing emergency stop").ConfigureAwait(false);
                await active.DisposeAsync().ConfigureAwait(false);
                active = null;
                return;
            }

            while (!runToken.IsCancellationRequested)
            {
                if (active is null)
                {
                    _ = RequirePaperCredentials();
                    var next = await OptimizeAndSelectAsync(runToken).ConfigureAwait(false);
                    activeStrategy = next;
                    if (!await PaperStartAllowedAsync("initial paper start after optimization").ConfigureAwait(false)) return;
                    active = StartPaper(next);
                    continue;
                }

                var exited = active.Process.WaitForExitAsync();
                var nextOptimizationAt = DateTime.UtcNow.AddHours(settings.ReoptimizationIntervalHours);
                var externalStopDetected = false;
                var processExited = false;
                while (DateTime.UtcNow < nextOptimizationAt)
                {
                    var remaining = nextOptimizationAt - DateTime.UtcNow;
                    var pollInterval = TimeSpan.FromSeconds(settings.EmergencyStopPollSeconds);
                    var pollDelay = Task.Delay(remaining < pollInterval ? remaining : pollInterval, runToken);
                    var completed = await Task.WhenAny(pollDelay, exited).ConfigureAwait(false);
                    if (completed == exited)
                    {
                        processExited = true;
                        break;
                    }

                    await pollDelay.ConfigureAwait(false);
                    CollectPaperTradeOutcomes(active);
                    if (File.Exists(settings.KillSwitchFile))
                    {
                        externalStopDetected = true;
                        break;
                    }
                }

                if (externalStopDetected)
                {
                    await AuditAsync("external_emergency_stop_detected", new { }).ConfigureAwait(false);
                    await WaitForPaperShutdownAsync(active, "external emergency stop").ConfigureAwait(false);
                    CollectPaperTradeOutcomes(active);
                    await active.DisposeAsync().ConfigureAwait(false);
                    active = null;
                    return;
                }

                if (processExited)
                {
                    await exited.ConfigureAwait(false);
                    CollectPaperTradeOutcomes(active);
                    var exitCode = active.Process.ExitCode;
                    var uptime = active.Uptime;
                    await AuditAsync("paper_process_exited", new { exitCode, genome = activeStrategy?.Genome }).ConfigureAwait(false);
                    var externallyStopped = File.Exists(settings.KillSwitchFile) && !_ownsKillSwitch;
                    await active.DisposeAsync().ConfigureAwait(false);
                    active = null;
                    if (externallyStopped)
                    {
                        await AuditAsync("external_emergency_stop_honored", new { }).ConfigureAwait(false);
                        return;
                    }

                    var restartDelay = _restartPolicy.RegisterExit(exitCode, uptime);
                    await AuditAsync("paper_process_restart_scheduled", new { exitCode, consecutiveFailures = _restartPolicy.ConsecutiveFailures, delaySeconds = restartDelay.TotalSeconds }).ConfigureAwait(false);
                    if (restartDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(restartDelay, cancellationToken).ConfigureAwait(false);
                        if (activeStrategy is not null)
                        {
                            if (!await PaperStartAllowedAsync("paper restart after backoff").ConfigureAwait(false)) return;
                            active = StartPaper(activeStrategy);
                            continue;
                        }
                    }

                    // A naturally retired or unexpectedly exited process triggers immediate research before restart.
                    continue;
                }

                if (File.Exists(settings.KillSwitchFile))
                {
                    await AuditAsync("external_emergency_stop_detected", new { }).ConfigureAwait(false);
                    await WaitForPaperShutdownAsync(active, "external emergency stop").ConfigureAwait(false);
                    CollectPaperTradeOutcomes(active);
                    await active.DisposeAsync().ConfigureAwait(false);
                    active = null;
                    return;
                }

                PromotedStrategy nextStrategy;
                try
                {
                    using var optimizationCancellation = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                    var optimization = OptimizeAndSelectAsync(optimizationCancellation.Token);
                    var emergencyStop = WaitForEmergencyStopAsync(optimizationCancellation.Token);
                    var completed = await Task.WhenAny(optimization, emergencyStop).ConfigureAwait(false);
                    if (completed == emergencyStop && await emergencyStop.ConfigureAwait(false))
                    {
                        optimizationCancellation.Cancel();
                        try { await optimization.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!runToken.IsCancellationRequested) { }
                        catch (OperationCanceledException) when (runToken.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        {
                            await AuditAsync("optimization_aborted_for_emergency_stop", new { exception = exception.Message }).ConfigureAwait(false);
                        }
                        await AuditAsync("external_emergency_stop_during_optimization_honored", new { }).ConfigureAwait(false);
                        await WaitForPaperShutdownAsync(active, "external emergency stop during optimization").ConfigureAwait(false);
                        await active.DisposeAsync().ConfigureAwait(false);
                        active = null;
                        return;
                    }

                    optimizationCancellation.Cancel();
                    nextStrategy = await optimization.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await AuditAsync("optimization_failed_keep_current_paper_strategy", new { exception = exception.Message }).ConfigureAwait(false);
                    continue;
                }

                if (active.Process.HasExited)
                {
                    var unexpectedExitCode = active.Process.ExitCode;
                    var uptime = active.Uptime;
                    await active.DisposeAsync().ConfigureAwait(false);
                    active = null;
                    if (File.Exists(settings.KillSwitchFile))
                    {
                        await AuditAsync("external_emergency_stop_during_optimization_honored", new { unexpectedExitCode }).ConfigureAwait(false);
                        return;
                    }
                    await AuditAsync("paper_process_exited_during_optimization", new { unexpectedExitCode }).ConfigureAwait(false);
                    var restartDelay = _restartPolicy.RegisterExit(unexpectedExitCode, uptime);
                    if (restartDelay > TimeSpan.Zero) await Task.Delay(restartDelay, cancellationToken).ConfigureAwait(false);
                    if (!await PaperStartAllowedAsync("paper restart after optimization").ConfigureAwait(false)) return;
                    activeStrategy = nextStrategy;
                    active = StartPaper(nextStrategy);
                    continue;
                }

                if (nextStrategy.Genome == activeStrategy?.Genome)
                {
                    await AuditAsync("promotion_unchanged_keep_current_paper_strategy", new { fitness = nextStrategy.Fitness }).ConfigureAwait(false);
                    continue;
                }

                if (!TryCreateKillSwitch("scheduled strategy promotion"))
                {
                    await AuditAsync("emergency_stop_raced_with_promotion", new { }).ConfigureAwait(false);
                    await WaitForPaperShutdownAsync(active, "emergency stop raced with promotion").ConfigureAwait(false);
                    await active.DisposeAsync().ConfigureAwait(false);
                    active = null;
                    return;
                }

                await AuditAsync("graceful_paper_strategy_shutdown_requested", new { oldGenome = activeStrategy?.Genome, newGenome = nextStrategy.Genome }).ConfigureAwait(false);
                var stoppedCleanly = await WaitForPaperShutdownAsync(active, "scheduled strategy promotion").ConfigureAwait(false);
                CollectPaperTradeOutcomes(active);
                await active.DisposeAsync().ConfigureAwait(false);
                active = null;
                if (!stoppedCleanly) return;
                if (runToken.IsCancellationRequested) return;
                DeleteOwnedKillSwitch();
                if (!await PaperStartAllowedAsync("scheduled strategy promotion").ConfigureAwait(false)) return;
                activeStrategy = nextStrategy;
                active = StartPaper(nextStrategy);
            }
        }
        finally
        {
            if (stopAtUtc is not null && !cancellationToken.IsCancellationRequested && runToken.IsCancellationRequested)
                await AuditAsync("paper_session_scheduled_stop_reached", new { stopAtUtc }).ConfigureAwait(false);
            if (active is not null)
            {
                if (!active.Process.HasExited && !File.Exists(settings.KillSwitchFile))
                {
                    TryCreateKillSwitch("supervisor shutdown");
                    await AuditAsync("graceful_paper_shutdown_requested", new { genome = activeStrategy?.Genome }).ConfigureAwait(false);
                }
                if (!active.Process.HasExited) await WaitForPaperShutdownAsync(active, "supervisor shutdown").ConfigureAwait(false);
                CollectPaperTradeOutcomes(active);
                await active.DisposeAsync().ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<bool> WaitForPaperShutdownAsync(PaperSession session, string reason)
    {
        if (session.Process.HasExited) return true;
        try
        {
            await session.Process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(settings.PaperShutdownTimeoutSeconds))
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            await AuditAsync("paper_shutdown_timeout_force_terminated", new
            {
                processId = session.Process.Id,
                timeoutSeconds = settings.PaperShutdownTimeoutSeconds,
                reason
            }).ConfigureAwait(false);
            try
            {
                if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            await session.Process.WaitForExitAsync().ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> WaitForEmergencyStopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (File.Exists(settings.KillSwitchFile)) return true;
                await Task.Delay(TimeSpan.FromSeconds(settings.EmergencyStopPollSeconds), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        return false;
    }

    private async Task<PromotedStrategy> OptimizeAndSelectAsync(CancellationToken cancellationToken)
    {
        EnsurePortfolioDrawdownNotTripped();
        var rankingsPath = settings.RankingsFile;
        var previousWriteTime = File.Exists(rankingsPath) ? File.GetLastWriteTimeUtc(rankingsPath) : DateTime.MinValue;
        var started = DateTime.UtcNow;
        var searchSeed = unchecked((int)(started.Ticks ^ (started.Ticks >> 32)));
        var experience = PaperTradeOutcomeJournal.Read(settings.PaperTradeOutcomesFile);
        var seeds = PaperTradeOutcomeJournal.GetSeedGenomes(experience, maximumCount: 2, minimumTrades: 8);
        var feedback = PaperTradeOutcomeJournal.Summarize(experience);
        await AuditAsync("optimization_started", new
        {
            paperTradeCount = feedback.TradeCount,
            paperWins = feedback.Wins,
            paperLosses = feedback.Losses,
            searchSeed,
            seededGenomeCount = seeds.Count,
            paperFeedback = "Exact genome seeds require at least eight closed paper trades and positive net return after fees."
        }).ConfigureAwait(false);
        var runConfigPath = WriteOptimizerFeedbackConfig(settings.OptimizerConfig, seeds, searchSeed, started);
        int exitCode;
        try
        {
            exitCode = await RunChildAsync(
                settings.OptimizerLauncherDll,
                settings.OptimizerWorkingDirectory,
                runConfigPath,
                Path.Combine(settings.OutputDirectory, $"optimizer-{started:yyyyMMdd-HHmmss}.log"),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!string.Equals(runConfigPath, settings.OptimizerConfig, StringComparison.OrdinalIgnoreCase) && File.Exists(runConfigPath))
                File.Delete(runConfigPath);
        }
        if (exitCode != 0) throw new InvalidOperationException($"LEAN optimizer exited with code {exitCode}.");
        if (!File.Exists(rankingsPath) || File.GetLastWriteTimeUtc(rankingsPath) <= previousWriteTime || File.GetLastWriteTimeUtc(rankingsPath) < started.AddSeconds(-2))
            throw new InvalidDataException("Optimizer did not produce a fresh rankings file.");

        var selected = PromotionSelector.SelectBest(rankingsPath, settings);
        await AuditAsync("strategy_candidate_approved_for_paper", new { selected.Genome, selected.Fitness, selected.Population, selected.ValidationWindowCount }).ConfigureAwait(false);
        return selected;
    }

    private PaperSession StartPaper(PromotedStrategy strategy)
    {
        var credentials = RequirePaperCredentials();
        EnsurePortfolioDrawdownNotTripped();
        if (File.Exists(settings.KillSwitchFile)) throw new InvalidOperationException("Cannot start a paper process while the emergency stop file exists.");
        var optimizer = JObjectConfig.Read(settings.OptimizerConfig);
        var algorithmType = optimizer["algorithm-type-name"]?.Value<string>()
            ?? throw new InvalidDataException("Optimizer config does not specify algorithm-type-name.");
        var started = DateTime.UtcNow;
        var sessionId = started.ToString("yyyyMMdd-HHmmss-fffffff", System.Globalization.CultureInfo.InvariantCulture);
        var resultsDirectory = Path.Combine(settings.OutputDirectory, $"paper-results-{sessionId}");
        Directory.CreateDirectory(resultsDirectory);
        var config = PaperConfigFactory.Create(
            settings.LeanBaseConfig,
            settings.PaperConfigOverlay,
            algorithmType,
            strategy,
            resultsDirectory,
            settings.AlpacaPluginDirectory,
            settings.KillSwitchFile,
            credentials.AlpacaKey,
            credentials.AlpacaSecret,
            credentials.QuantConnectUserId,
            credentials.QuantConnectApiToken);
        if (settings.PaperSessionCapitalLimit > 0)
        {
            var parameters = config["parameters"] as JObject
                ?? throw new InvalidDataException("Generated paper config is missing algorithm parameters.");
            parameters["discovery.paper-capital-limit"] = settings.PaperSessionCapitalLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var configPath = PaperConfigFactory.WriteEphemeralConfig(config);
        Process? process = null;
        try
        {
            var logPath = Path.Combine(settings.OutputDirectory, $"paper-{sessionId}.log");
            process = StartProcess(
                settings.LeanLauncherDll,
                settings.LeanWorkingDirectory,
                configPath,
                logPath);
            var identity = new PaperProcessIdentity(
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime()),
                configPath,
                strategy.Genome,
                strategy.Fitness,
                strategy.Population,
                strategy.ValidationWindowCount);
            PaperProcessRecoveryGuard.Save(_paperSessionStateFile, identity);
            AuditAsync("paper_process_started", new { strategy.Genome, strategy.Fitness, config = "ephemeral" }).GetAwaiter().GetResult();
            return new PaperSession(process, configPath, DateTimeOffset.UtcNow, _paperSessionStateFile, identity, logPath);
        }
        catch
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
                finally { process.Dispose(); }
            }
            PaperConfigFactory.DeleteEphemeralConfig(configPath);
            throw;
        }
    }

    private static PaperCredentials RequirePaperCredentials()
    {
        var apiKey = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_KEY");
        var apiSecret = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_SECRET");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            throw new InvalidOperationException("Set ALGORY_ALPACA_API_KEY and ALGORY_ALPACA_API_SECRET before starting paper trading.");
        var quantConnectUserId = Environment.GetEnvironmentVariable("ALGORY_QUANTCONNECT_USER_ID");
        var quantConnectApiToken = Environment.GetEnvironmentVariable("ALGORY_QUANTCONNECT_API_TOKEN");
        if (string.IsNullOrWhiteSpace(quantConnectUserId) || string.IsNullOrWhiteSpace(quantConnectApiToken))
            throw new InvalidOperationException("Set ALGORY_QUANTCONNECT_USER_ID and ALGORY_QUANTCONNECT_API_TOKEN before starting the Alpaca paper integration. LEAN's official Alpaca plugin uses these credentials to validate its subscription entitlement.");
        return new PaperCredentials(apiKey, apiSecret, quantConnectUserId, quantConnectApiToken);
    }

    private async Task<bool> PaperStartAllowedAsync(string reason)
    {
        if (!File.Exists(settings.KillSwitchFile)) return true;
        await AuditAsync("paper_start_blocked_by_emergency_stop", new { reason }).ConfigureAwait(false);
        return false;
    }

    private void EnsurePortfolioDrawdownNotTripped()
    {
        var statePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.KillSwitchFile))!, "portfolio-drawdown-state.json");
        var state = PortfolioDrawdownGuard.LoadState(statePath);
        if (state?.Tripped == true)
            throw new InvalidOperationException("The persistent account drawdown limit has tripped. Automatic optimization and paper restarts are disabled until an operator reviews the account and risk-state file.");
    }

    private PaperSession AdoptPaper(PaperProcessIdentity identity)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
            var actualStartTime = new DateTimeOffset(process.StartTime.ToUniversalTime());
            if (process.HasExited || !PaperProcessRecoveryGuard.MatchesStartTime(identity, actualStartTime))
                throw new InvalidOperationException("The recorded LEAN process changed while the supervisor was recovering it.");

            var config = JObjectConfig.Read(identity.ConfigPath!);
            var resultsDirectory = config["results-destination-folder"]?.Value<string>();
            var sessionId = string.IsNullOrWhiteSpace(resultsDirectory) ? "recovered" : Path.GetFileName(resultsDirectory).Replace("paper-results-", string.Empty, StringComparison.OrdinalIgnoreCase);
            var logPath = Path.Combine(settings.OutputDirectory, $"paper-{sessionId}.log");
            return new PaperSession(process, identity.ConfigPath!, identity.StartedAtUtc, _paperSessionStateFile, identity, logPath);
        }
        catch
        {
            process?.Dispose();
            throw;
        }
    }

    private async Task<int> RunChildAsync(string launcherDll, string workingDirectory, string configPath, string logPath, CancellationToken cancellationToken)
    {
        using var process = StartProcess(launcherDll, workingDirectory, configPath, logPath);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw;
        }
    }

    private Process StartProcess(string launcherDll, string workingDirectory, string configPath, string logPath)
    {
        var info = new ProcessStartInfo(settings.DotnetExecutable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(launcherDll);
        info.ArgumentList.Add("--config");
        info.ArgumentList.Add(configPath);

        var writerLock = new object();
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => WriteProcessLine(logPath, writerLock, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => WriteProcessLine(logPath, writerLock, eventArgs.Data);
        if (!process.Start()) throw new InvalidOperationException($"Unable to start LEAN child process '{launcherDll}'.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void WriteProcessLine(string path, object writerLock, string? line)
    {
        if (line is null) return;
        lock (writerLock) File.AppendAllText(path, line + Environment.NewLine);
    }

    private string WriteOptimizerFeedbackConfig(string sourcePath, IReadOnlyList<StrategyGenome> seeds, int searchSeed, DateTime started)
    {
        var config = JObjectConfig.Read(sourcePath);
        var settingsNode = config["optimization-strategy-settings"] as JObject
            ?? throw new InvalidDataException("Optimizer config is missing optimization-strategy-settings.");
        settingsNode["seed"] = searchSeed;
        if (seeds.Count > 0) settingsNode["seed-genomes"] = JArray.FromObject(seeds);
        else settingsNode.Remove("seed-genomes");
        var output = Path.Combine(settings.OutputDirectory, $"optimizer-feedback-{started:yyyyMMdd-HHmmss-fffffff}.json");
        File.WriteAllText(output, config.ToString(Formatting.Indented));
        return output;
    }

    private void CollectPaperTradeOutcomes(PaperSession session)
    {
        if (!File.Exists(session.LogPath)) return;
        try
        {
            foreach (var line in File.ReadLines(session.LogPath))
            {
                var marker = line.IndexOf("PAPER_TRADE_OUTCOME|", StringComparison.Ordinal);
                if (marker < 0) continue;
                var json = line[(marker + "PAPER_TRADE_OUTCOME|".Length)..].Trim();
                if (!PaperTradeOutcomeJournal.TryParse(json, out var outcome) || outcome is null) continue;
                PaperTradeOutcomeJournal.TryAppendUnique(settings.PaperTradeOutcomesFile, outcome, _knownPaperOutcomeIds);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private bool TryCreateKillSwitch(string reason)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settings.KillSwitchFile)!);
        try
        {
            using var stream = new FileStream(settings.KillSwitchFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            _ownedKillSwitchMarker = $"{DateTimeOffset.UtcNow:O} {Guid.NewGuid():N} {reason}";
            writer.WriteLine(_ownedKillSwitchMarker);
            _ownsKillSwitch = true;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void DeleteOwnedKillSwitch()
    {
        if (!_ownsKillSwitch) return;
        if (_ownedKillSwitchMarker is null || !File.Exists(settings.KillSwitchFile) || File.ReadAllText(settings.KillSwitchFile).Trim() != _ownedKillSwitchMarker)
        {
            _ownsKillSwitch = false;
            _ownedKillSwitchMarker = null;
            return;
        }
        File.Delete(settings.KillSwitchFile);
        _ownsKillSwitch = false;
        _ownedKillSwitchMarker = null;
    }

    private async Task AuditAsync(string eventName, object details)
    {
        await _auditLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var row = new JObject
            {
                ["utc"] = DateTimeOffset.UtcNow,
                ["event"] = eventName,
                ["details"] = JToken.FromObject(details)
            };
            await File.AppendAllTextAsync(_auditFile, row.ToString(Formatting.None) + Environment.NewLine).ConfigureAwait(false);
        }
        finally
        {
            _auditLock.Release();
        }
    }

    private sealed class PaperSession(
        Process process,
        string configPath,
        DateTimeOffset startedAt,
        string statePath,
        PaperProcessIdentity identity,
        string logPath) : IAsyncDisposable
    {
        public Process Process { get; } = process;
        public string LogPath { get; } = logPath;
        public TimeSpan Uptime => DateTimeOffset.UtcNow - startedAt;

        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited) await Process.WaitForExitAsync().ConfigureAwait(false);
            try { PaperProcessRecoveryGuard.ClearIfOwned(statePath, identity); }
            finally
            {
                Process.Dispose();
                PaperConfigFactory.DeleteEphemeralConfig(configPath);
            }
        }
    }

    private sealed record PaperCredentials(string AlpacaKey, string AlpacaSecret, string QuantConnectUserId, string QuantConnectApiToken);
}
