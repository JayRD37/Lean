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

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_KEY");
        var apiSecret = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_SECRET");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            throw new InvalidOperationException("Set ALGORY_ALPACA_API_KEY and ALGORY_ALPACA_API_SECRET before starting paper trading.");

        Directory.CreateDirectory(settings.OutputDirectory);
        using var instanceLock = SupervisorInstanceLock.Acquire(settings.OutputDirectory);
        var orphan = PaperProcessRecoveryGuard.FindOrphan(_paperSessionStateFile, PaperProcessRecoveryGuard.GetProcessStartTimeUtc);
        if (orphan is not null)
        {
            await AuditAsync("orphaned_paper_process_detected_fail_closed", new { orphan.ProcessId, orphan.StartedAtUtc }).ConfigureAwait(false);
            throw new InvalidOperationException($"A previous LEAN paper process is still running (PID {orphan.ProcessId}, started {orphan.StartedAtUtc:O}). Inspect and stop or reconcile it before restarting the supervisor.");
        }
        if (File.Exists(settings.KillSwitchFile))
            throw new InvalidOperationException($"Emergency stop is already present at '{settings.KillSwitchFile}'. Clear it only after investigating the stop request.");

        PaperSession? active = null;
        PromotedStrategy? activeStrategy = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (active is null)
                {
                    var next = await OptimizeAndSelectAsync(cancellationToken).ConfigureAwait(false);
                    activeStrategy = next;
                    active = StartPaper(next, apiKey, apiSecret);
                    continue;
                }

                var delay = Task.Delay(TimeSpan.FromHours(settings.ReoptimizationIntervalHours), cancellationToken);
                var exited = active.Process.WaitForExitAsync();
                var completed = await Task.WhenAny(delay, exited).ConfigureAwait(false);
                if (completed == exited)
                {
                    await exited.ConfigureAwait(false);
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
                            active = StartPaper(activeStrategy, apiKey, apiSecret);
                            continue;
                        }
                    }

                    // A naturally retired or unexpectedly exited process triggers immediate research before restart.
                    continue;
                }

                await delay.ConfigureAwait(false);
                if (File.Exists(settings.KillSwitchFile))
                {
                    await AuditAsync("external_emergency_stop_detected", new { }).ConfigureAwait(false);
                    await active.Process.WaitForExitAsync().ConfigureAwait(false);
                    await active.DisposeAsync().ConfigureAwait(false);
                    active = null;
                    return;
                }

                PromotedStrategy nextStrategy;
                try
                {
                    nextStrategy = await OptimizeAndSelectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
                    activeStrategy = nextStrategy;
                    active = StartPaper(nextStrategy, apiKey, apiSecret);
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
                    await active.Process.WaitForExitAsync().ConfigureAwait(false);
                    await active.DisposeAsync().ConfigureAwait(false);
                    active = null;
                    return;
                }

                await AuditAsync("graceful_paper_strategy_shutdown_requested", new { oldGenome = activeStrategy?.Genome, newGenome = nextStrategy.Genome }).ConfigureAwait(false);
                await active.Process.WaitForExitAsync().ConfigureAwait(false);
                await active.DisposeAsync().ConfigureAwait(false);
                active = null;
                if (cancellationToken.IsCancellationRequested) return;
                DeleteOwnedKillSwitch();
                activeStrategy = nextStrategy;
                active = StartPaper(nextStrategy, apiKey, apiSecret);
            }
        }
        finally
        {
            if (active is not null)
            {
                if (!active.Process.HasExited && !File.Exists(settings.KillSwitchFile))
                {
                    TryCreateKillSwitch("supervisor shutdown");
                    await AuditAsync("graceful_paper_shutdown_requested", new { genome = activeStrategy?.Genome }).ConfigureAwait(false);
                }
                if (!active.Process.HasExited) await active.Process.WaitForExitAsync().ConfigureAwait(false);
                await active.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<PromotedStrategy> OptimizeAndSelectAsync(CancellationToken cancellationToken)
    {
        EnsurePortfolioDrawdownNotTripped();
        var rankingsPath = settings.RankingsFile;
        var previousWriteTime = File.Exists(rankingsPath) ? File.GetLastWriteTimeUtc(rankingsPath) : DateTime.MinValue;
        var started = DateTime.UtcNow;
        await AuditAsync("optimization_started", new { }).ConfigureAwait(false);
        var exitCode = await RunChildAsync(
            settings.OptimizerLauncherDll,
            settings.OptimizerWorkingDirectory,
            settings.OptimizerConfig,
            Path.Combine(settings.OutputDirectory, $"optimizer-{started:yyyyMMdd-HHmmss}.log"),
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0) throw new InvalidOperationException($"LEAN optimizer exited with code {exitCode}.");
        if (!File.Exists(rankingsPath) || File.GetLastWriteTimeUtc(rankingsPath) <= previousWriteTime || File.GetLastWriteTimeUtc(rankingsPath) < started.AddSeconds(-2))
            throw new InvalidDataException("Optimizer did not produce a fresh rankings file.");

        var selected = PromotionSelector.SelectBest(rankingsPath, settings);
        await AuditAsync("strategy_candidate_approved_for_paper", new { selected.Genome, selected.Fitness, selected.Population, selected.ValidationWindowCount }).ConfigureAwait(false);
        return selected;
    }

    private PaperSession StartPaper(PromotedStrategy strategy, string apiKey, string apiSecret)
    {
        EnsurePortfolioDrawdownNotTripped();
        if (File.Exists(settings.KillSwitchFile)) throw new InvalidOperationException("Cannot start a paper process while the emergency stop file exists.");
        var optimizer = JObjectConfig.Read(settings.OptimizerConfig);
        var algorithmType = optimizer["algorithm-type-name"]?.Value<string>()
            ?? throw new InvalidDataException("Optimizer config does not specify algorithm-type-name.");
        var config = PaperConfigFactory.Create(
            settings.LeanBaseConfig,
            settings.PaperConfigOverlay,
            algorithmType,
            strategy,
            settings.KillSwitchFile,
            apiKey,
            apiSecret);
        var configPath = PaperConfigFactory.WriteEphemeralConfig(config);
        Process? process = null;
        try
        {
            var started = DateTime.UtcNow;
            var logPath = Path.Combine(settings.OutputDirectory, $"paper-{started:yyyyMMdd-HHmmss}.log");
            process = StartProcess(
                settings.LeanLauncherDll,
                settings.LeanWorkingDirectory,
                configPath,
                logPath);
            var identity = new PaperProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
            PaperProcessRecoveryGuard.Save(_paperSessionStateFile, identity);
            AuditAsync("paper_process_started", new { strategy.Genome, strategy.Fitness, config = "ephemeral" }).GetAwaiter().GetResult();
            return new PaperSession(process, configPath, DateTimeOffset.UtcNow, _paperSessionStateFile, identity);
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

    private void EnsurePortfolioDrawdownNotTripped()
    {
        var statePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.KillSwitchFile))!, "portfolio-drawdown-state.json");
        var state = PortfolioDrawdownGuard.LoadState(statePath);
        if (state?.Tripped == true)
            throw new InvalidOperationException("The persistent account drawdown limit has tripped. Automatic optimization and paper restarts are disabled until an operator reviews the account and risk-state file.");
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
        PaperProcessIdentity identity) : IAsyncDisposable
    {
        public Process Process { get; } = process;
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
}
