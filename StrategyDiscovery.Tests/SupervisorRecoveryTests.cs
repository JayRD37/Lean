using System.Diagnostics;
using System.Globalization;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Genetic;
using QuantConnect.Genetic.Supervisor;

namespace QuantConnect.Tests.StrategyDiscovery;

[TestFixture]
public class SupervisorRecoveryTests
{
    [Test]
    public async Task SupervisorCliRequiresExplicitPaperAuthorizationBeforeLoadingConfig()
    {
        var supervisorAssembly = Path.Combine(TestContext.CurrentContext.TestDirectory, "QuantConnect.Genetic.Supervisor.dll");
        Assert.That(File.Exists(supervisorAssembly), Is.True, $"Supervisor assembly was not copied to {TestContext.CurrentContext.TestDirectory}");

        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var startInfo = new ProcessStartInfo(string.IsNullOrWhiteSpace(dotnetHost) ? "dotnet" : dotnetHost)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(supervisorAssembly);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(Path.Combine(Path.GetTempPath(), $"missing-supervisor-{Guid.NewGuid():N}.json"));
        startInfo.Environment.Remove("ALGORY_ALPACA_API_KEY");
        startInfo.Environment.Remove("ALGORY_ALPACA_API_SECRET");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the supervisor CLI authorization test.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(process.ExitCode, Is.EqualTo(2));
        Assert.That(await standardError, Does.Contain("explicit --enable-paper"));
        Assert.That(await standardOutput, Is.Empty);
    }

    [Test]
    public void PaperProcessRecoveryGuardBlocksLiveOrphansAndClearsStaleOrReusedProcessIds()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lean-paper-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var statePath = Path.Combine(folder, "active-paper-session.json");
            var configPath = PaperConfigFactory.WriteEphemeralConfig(new JObject());
            var identity = new PaperProcessIdentity(1234, DateTimeOffset.Parse("2026-10-28T12:00:00Z"), configPath);
            PaperProcessRecoveryGuard.Save(statePath, identity);
            Assert.That(PaperProcessRecoveryGuard.FindOrphan(statePath, _ => identity.StartedAtUtc), Is.EqualTo(identity));
            Assert.That(File.Exists(statePath), Is.True);
            Assert.That(File.Exists(configPath), Is.True);

            Assert.That(PaperProcessRecoveryGuard.FindOrphan(statePath, _ => null), Is.Null);
            Assert.That(File.Exists(statePath), Is.False);
            Assert.That(File.Exists(configPath), Is.False, "A stale marker should remove its orphaned credential config.");

            PaperProcessRecoveryGuard.Save(statePath, identity);
            Assert.That(PaperProcessRecoveryGuard.FindOrphan(statePath, _ => identity.StartedAtUtc.AddSeconds(1)), Is.EqualTo(identity));
            Assert.That(File.Exists(statePath), Is.True, "Small OS-reported process start-time differences must keep a live session marker.");

            PaperProcessRecoveryGuard.Save(statePath, identity);
            Assert.That(PaperProcessRecoveryGuard.FindOrphan(statePath, _ => identity.StartedAtUtc.AddSeconds(3)), Is.Null);
            Assert.That(File.Exists(statePath), Is.False);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public void PaperProcessRecoveryGuardFailsClosedOnCorruptSessionState()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lean-paper-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var statePath = Path.Combine(folder, "active-paper-session.json");
            File.WriteAllText(statePath, "{broken");
            Assert.Throws<InvalidDataException>(() => PaperProcessRecoveryGuard.FindOrphan(statePath, _ => null));
            Assert.That(File.Exists(statePath), Is.True);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public void EphemeralPaperCredentialsAreOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix permission bits are not available on Windows.");

        var configPath = PaperConfigFactory.WriteEphemeralConfig(new JObject { ["alpaca-api-key"] = "paper-test-key" });
        try
        {
            var ownerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            var ownerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var directoryPermissions = File.GetUnixFileMode(Path.GetDirectoryName(configPath)!);
            var filePermissions = File.GetUnixFileMode(configPath);
            var otherUserPermissions = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.That(directoryPermissions & otherUserPermissions, Is.EqualTo((UnixFileMode)0));
            Assert.That(filePermissions & otherUserPermissions, Is.EqualTo((UnixFileMode)0));
            Assert.That(directoryPermissions & ownerOnlyDirectory, Is.EqualTo(ownerOnlyDirectory));
            Assert.That(filePermissions & ownerOnlyFile, Is.EqualTo(ownerOnlyFile));
        }
        finally { PaperConfigFactory.DeleteEphemeralConfig(configPath); }
    }

    [Test]
    public async Task SupervisorStartupFailsClosedWhenSessionMarkerMatchesALiveProcess()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lean-paper-live-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var statePath = Path.Combine(folder, "active-paper-session.json");
        var startedAtUtc = PaperProcessRecoveryGuard.GetProcessStartTimeUtc(Environment.ProcessId);
        Assert.That(startedAtUtc, Is.Not.Null);
        PaperProcessRecoveryGuard.Save(statePath, new PaperProcessIdentity(Environment.ProcessId, startedAtUtc!.Value));

        var previousApiKey = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_KEY");
        var previousApiSecret = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_KEY", "recovery-test-key");
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_SECRET", "recovery-test-secret");
            var settings = new SupervisorSettings
            {
                OutputDirectory = folder,
                KillSwitchFile = Path.Combine(folder, "emergency-stop")
            };

            var exception = Assert.ThrowsAsync<InvalidOperationException>(() => new SupervisorHost(settings).RunAsync(CancellationToken.None));
            Assert.That(exception!.Message, Does.Contain($"PID {Environment.ProcessId}"));
            Assert.That(File.ReadAllText(Path.Combine(folder, "supervisor-audit.jsonl")), Does.Contain("orphaned_paper_process_detected_fail_closed"));
            Assert.That(File.Exists(statePath), Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_KEY", previousApiKey);
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_SECRET", previousApiSecret);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task RestartedSupervisorFailsClosedWhenPaperChildOutlivesItsLaunchingProcess()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lean-paper-process-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previousApiKey = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_KEY");
        var previousApiSecret = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_SECRET");
        Process? orphanedChild = null;
        try
        {
            var fixtureAssembly = Path.Combine(TestContext.CurrentContext.TestDirectory, "QuantConnect.Genetic.ProcessFixture.dll");
            Assert.That(File.Exists(fixtureAssembly), Is.True, $"Process fixture was not copied to {TestContext.CurrentContext.TestDirectory}");
            var childMetadataPath = Path.Combine(folder, "child-process.txt");
            var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            var launcher = new ProcessStartInfo(string.IsNullOrWhiteSpace(dotnetHost) ? "dotnet" : dotnetHost)
            {
                UseShellExecute = false
            };
            launcher.ArgumentList.Add(fixtureAssembly);
            launcher.ArgumentList.Add("--launch-child-and-exit");
            launcher.ArgumentList.Add(childMetadataPath);

            using (var parent = Process.Start(launcher) ?? throw new InvalidOperationException("Unable to start the supervisor-crash fixture."))
            {
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.That(parent.ExitCode, Is.Zero);

                Assert.That(File.Exists(childMetadataPath), Is.True, "The launching process did not write the paper-child metadata.");
                var fields = File.ReadAllText(childMetadataPath).Trim().Split('|');
                Assert.That(fields, Has.Length.EqualTo(2));
                var processId = int.Parse(fields[0], CultureInfo.InvariantCulture);
                var startedAtUtc = DateTimeOffset.Parse(fields[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                orphanedChild = Process.GetProcessById(processId);
                Assert.That(orphanedChild.HasExited, Is.False, "The simulated LEAN child did not survive its launching process.");

                var statePath = Path.Combine(folder, "active-paper-session.json");
                PaperProcessRecoveryGuard.Save(statePath, new PaperProcessIdentity(processId, startedAtUtc));
            }

            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_KEY", "recovery-test-key");
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_SECRET", "recovery-test-secret");
            var settings = new SupervisorSettings
            {
                OutputDirectory = folder,
                KillSwitchFile = Path.Combine(folder, "emergency-stop")
            };

            var exception = Assert.ThrowsAsync<InvalidOperationException>(() => new SupervisorHost(settings).RunAsync(CancellationToken.None));
            Assert.That(exception!.Message, Does.Contain($"PID {orphanedChild!.Id}"));
            Assert.That(File.ReadAllText(Path.Combine(folder, "supervisor-audit.jsonl")), Does.Contain("orphaned_paper_process_detected_fail_closed"));
            Assert.That(orphanedChild.HasExited, Is.False, "Recovery detection must leave the orphan untouched for explicit reconciliation.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_KEY", previousApiKey);
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_SECRET", previousApiSecret);
            if (orphanedChild is not null)
            {
                try
                {
                    if (!orphanedChild.HasExited) orphanedChild.Kill(entireProcessTree: true);
                    await orphanedChild.WaitForExitAsync();
                }
                catch (InvalidOperationException) { }
                finally { orphanedChild.Dispose(); }
            }
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task RestartedSupervisorAdoptsVerifiedPaperChildAndStopsItOnShutdown()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lean-paper-adoption-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previousApiKey = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_KEY");
        var previousApiSecret = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_SECRET");
        Process? adoptedChild = null;
        string? configPath = null;
        try
        {
            var fixtureAssembly = Path.Combine(TestContext.CurrentContext.TestDirectory, "QuantConnect.Genetic.ProcessFixture.dll");
            Assert.That(File.Exists(fixtureAssembly), Is.True);
            var statePath = Path.Combine(folder, "active-paper-session.json");
            var killSwitchPath = Path.Combine(folder, "emergency-stop");
            var childMetadataPath = Path.Combine(folder, "child-process.txt");
            var donePath = Path.Combine(folder, "child-stopped.txt");
            var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            var launcher = new ProcessStartInfo(string.IsNullOrWhiteSpace(dotnetHost) ? "dotnet" : dotnetHost)
            {
                UseShellExecute = false
            };
            launcher.ArgumentList.Add(fixtureAssembly);
            launcher.ArgumentList.Add("--launch-recoverable-child-and-exit");
            launcher.ArgumentList.Add(childMetadataPath);
            launcher.ArgumentList.Add(killSwitchPath);
            launcher.ArgumentList.Add(donePath);
            using (var parent = Process.Start(launcher) ?? throw new InvalidOperationException("Unable to start the recoverable-child fixture."))
            {
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.That(parent.ExitCode, Is.Zero);
            }

            var fields = File.ReadAllText(childMetadataPath).Trim().Split('|');
            Assert.That(fields, Has.Length.EqualTo(2));
            var processId = int.Parse(fields[0], CultureInfo.InvariantCulture);
            var startedAtUtc = DateTimeOffset.Parse(fields[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            adoptedChild = Process.GetProcessById(processId);
            Assert.That(adoptedChild.HasExited, Is.False);

            var paperResultsDirectory = Path.Combine(folder, "paper-results-fixture");
            Directory.CreateDirectory(paperResultsDirectory);
            var alpacaPluginDirectory = Path.Combine(folder, "alpaca-plugin-fixture");
            Directory.CreateDirectory(alpacaPluginDirectory);
            File.WriteAllText(Path.Combine(alpacaPluginDirectory, "QuantConnect.Brokerages.Alpaca.dll"), string.Empty);
            File.WriteAllText(Path.Combine(alpacaPluginDirectory, "Alpaca.Markets.dll"), string.Empty);
            configPath = PaperConfigFactory.WriteEphemeralConfig(new JObject
            {
                ["alpaca-paper-trading"] = true,
                ["log-handler"] = "QuantConnect.Logging.FileLogHandler",
                ["plugin-directory"] = alpacaPluginDirectory,
                ["results-destination-folder"] = paperResultsDirectory,
                ["environments"] = new JObject
                {
                    ["live-alpaca"] = new JObject
                    {
                        ["live-mode"] = true,
                        ["live-mode-brokerage"] = "AlpacaBrokerage"
                    }
                },
                ["parameters"] = new JObject
                {
                    ["discovery.allow-paper-trading"] = "true",
                    ["discovery.kill-switch-file"] = Path.GetFullPath(killSwitchPath)
                }
            });
            Assert.That(PaperConfigFactory.IsRecoverablePaperConfig(configPath, killSwitchPath, folder), Is.True);
            File.Delete(Path.Combine(alpacaPluginDirectory, "Alpaca.Markets.dll"));
            Assert.That(PaperConfigFactory.IsRecoverablePaperConfig(configPath, killSwitchPath, folder), Is.False);
            File.WriteAllText(Path.Combine(alpacaPluginDirectory, "Alpaca.Markets.dll"), string.Empty);
            Assert.That(PaperConfigFactory.IsRecoverablePaperConfig(configPath, Path.Combine(folder, "different-stop"), folder), Is.False);
            Assert.That(PaperConfigFactory.IsRecoverablePaperConfig(configPath, killSwitchPath, Path.Combine(folder, "different-output")), Is.False);
            var genome = new StrategyGenome(StrategyFamily.TrendFollowing, StrategyTimeframe.Daily, 10, 40, 12, 4, 3, 30);
            PaperProcessRecoveryGuard.Save(statePath, new PaperProcessIdentity(
                processId,
                startedAtUtc,
                configPath,
                genome,
                0.12,
                StrategyPopulation.Growth,
                2));

            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_KEY", "recovery-test-key");
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_SECRET", "recovery-test-secret");
            var settings = new SupervisorSettings
            {
                OutputDirectory = folder,
                KillSwitchFile = killSwitchPath,
                ReoptimizationIntervalHours = 1
            };
            using var cancellation = new CancellationTokenSource();
            var host = new SupervisorHost(settings);
            var run = host.RunAsync(cancellation.Token);
            var adoptionObserved = await WaitForFileContentAsync(
                Path.Combine(folder, "supervisor-audit.jsonl"),
                "orphaned_paper_process_adopted",
                TimeSpan.FromSeconds(15));
            if (!adoptionObserved)
            {
                cancellation.Cancel();
                try { await run.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception) { }
            }
            Assert.That(adoptionObserved, Is.True, "The supervisor did not adopt the verified surviving paper child.");
            cancellation.Cancel();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Fail("The supervisor should stop when cancellation is requested.");
            }
            catch (OperationCanceledException) { }

            Assert.That(adoptedChild.HasExited, Is.True, "The adopted child should exit after the supervisor's emergency-stop request.");
            Assert.That(File.Exists(killSwitchPath), Is.True, "A shutdown stop marker should remain for operator review.");
            Assert.That(File.Exists(statePath), Is.False, "The exited paper session marker should be cleared.");
            Assert.That(File.Exists(donePath), Is.True, "The child should acknowledge that it observed the stop request.");
            Assert.That(configPath, Is.Not.Null);
            Assert.That(File.Exists(configPath!), Is.False, "The adopted session's temporary credential config should be deleted after exit.");
            Assert.That(File.ReadAllText(Path.Combine(folder, "supervisor-audit.jsonl")), Does.Contain("orphaned_paper_process_adopted"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_KEY", previousApiKey);
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_SECRET", previousApiSecret);
            if (adoptedChild is not null)
            {
                try
                {
                    if (!adoptedChild.HasExited) adoptedChild.Kill(entireProcessTree: true);
                    await adoptedChild.WaitForExitAsync();
                }
                catch (InvalidOperationException) { }
                finally { adoptedChild.Dispose(); }
            }
            if (configPath is not null && File.Exists(configPath)) PaperConfigFactory.DeleteEphemeralConfig(configPath);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task SupervisorRestartsAnUnexpectedlyExitedLeanChildAndStopsAtTheEmergencyFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lean-paper-unexpected-exit-" + Guid.NewGuid().ToString("N"));
        var optimizerDirectory = Path.Combine(folder, "optimizer");
        var leanDirectory = Path.Combine(folder, "lean");
        var pluginDirectory = Path.Combine(folder, "alpaca-plugin");
        var outputDirectory = Path.Combine(folder, "output");
        Directory.CreateDirectory(optimizerDirectory);
        Directory.CreateDirectory(leanDirectory);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "QuantConnect.Brokerages.Alpaca.dll"), string.Empty);
        File.WriteAllText(Path.Combine(pluginDirectory, "Alpaca.Markets.dll"), string.Empty);

        var fixtureAssembly = Path.Combine(TestContext.CurrentContext.TestDirectory, "QuantConnect.Genetic.ProcessFixture.dll");
        Assert.That(File.Exists(fixtureAssembly), Is.True, $"Process fixture was not copied to {TestContext.CurrentContext.TestDirectory}");
        var statePath = Path.Combine(folder, "fixture-events.txt");
        var resultsDirectory = Path.Combine(optimizerDirectory, "results");
        var optimizerConfigPath = Path.Combine(optimizerDirectory, "optimizer.json");
        File.WriteAllText(optimizerConfigPath, new JObject
        {
            ["algorithm-type-name"] = "GeneticDiscoveryAlgorithm",
            ["results-destination-folder"] = resultsDirectory,
            ["fixture-mode"] = "optimizer",
            ["fixture-state-file"] = statePath
        }.ToString());

        var baseConfigPath = Path.Combine(folder, "base.json");
        File.WriteAllText(baseConfigPath, new JObject
        {
            ["environment"] = "backtesting",
            ["algorithm-type-name"] = "GeneticDiscoveryAlgorithm",
            ["fixture-mode"] = "paper",
            ["fixture-state-file"] = statePath,
            ["environments"] = new JObject
            {
                ["live-alpaca"] = new JObject
                {
                    ["live-mode"] = true,
                    ["live-mode-brokerage"] = "AlpacaBrokerage"
                }
            }
        }.ToString());
        var overlayPath = Path.Combine(folder, "overlay.json");
        File.WriteAllText(overlayPath, new JObject
        {
            ["alpaca-paper-trading"] = true,
            ["parameters"] = new JObject
            {
                ["discovery.max-positions"] = "3",
                ["discovery.max-exposure-percent"] = "30",
                ["discovery.minimum-spread-bps"] = "5",
                ["discovery.max-correlation"] = "0.80",
                ["discovery.max-drawdown"] = "0.08",
                ["discovery.symbols"] = "SPY|QQQ|IWM"
            }
        }.ToString());

        var previousApiKey = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_KEY");
        var previousApiSecret = Environment.GetEnvironmentVariable("ALGORY_ALPACA_API_SECRET");
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var settings = new SupervisorSettings
        {
            DotnetExecutable = string.IsNullOrWhiteSpace(dotnetHost) ? "dotnet" : dotnetHost,
            OptimizerLauncherDll = fixtureAssembly,
            OptimizerWorkingDirectory = optimizerDirectory,
            OptimizerConfig = optimizerConfigPath,
            LeanLauncherDll = fixtureAssembly,
            LeanWorkingDirectory = leanDirectory,
            LeanBaseConfig = baseConfigPath,
            PaperConfigOverlay = overlayPath,
            AlpacaPluginDirectory = pluginDirectory,
            OutputDirectory = outputDirectory,
            KillSwitchFile = Path.Combine(folder, "emergency-stop"),
            ReoptimizationIntervalHours = 1,
            MaximumConsecutivePaperFailures = 3,
            MinimumHealthyPaperRunMinutes = 5,
            RestartBackoffSeconds = 1
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task? run = null;
        int? secondChildId = null;
        Process? secondChildProcess = null;
        string? secondConfigPath = null;
        try
        {
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_KEY", "restart-test-paper-key");
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_SECRET", "restart-test-paper-secret");
            var host = new SupervisorHost(settings);
            run = host.RunAsync(cancellation.Token);

            Assert.That(await WaitForFileContentAsync(statePath, "paper|1|", TimeSpan.FromSeconds(15)), Is.True,
                "The first paper child did not start.");
            var firstChildId = ReadPaperChildIds(statePath)[0];
            using (var failedChild = Process.GetProcessById(firstChildId))
            {
                failedChild.Kill(entireProcessTree: true);
                await failedChild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.That(await WaitForFileContentAsync(statePath, "paper|2|", TimeSpan.FromSeconds(15)), Is.True,
                "The supervisor did not restart the paper child after its unexpected exit.");
            secondChildId = ReadPaperChildIds(statePath)[1];
            secondChildProcess = Process.GetProcessById(secondChildId.Value);
            var sessionPath = Path.Combine(outputDirectory, "active-paper-session.json");
            Assert.That(await WaitForFileContentAsync(sessionPath, "\"ProcessId\"", TimeSpan.FromSeconds(5)), Is.True);
            secondConfigPath = JObject.Parse(File.ReadAllText(sessionPath))["ConfigPath"]?.Value<string>();
            Assert.That(secondConfigPath, Is.Not.Null);
            Assert.That(File.Exists(secondConfigPath), Is.True);

            var audit = Path.Combine(outputDirectory, "supervisor-audit.jsonl");
            Assert.That(await WaitForFileContentAsync(audit, "paper_process_restart_scheduled", TimeSpan.FromSeconds(5)), Is.True);
            File.WriteAllText(settings.KillSwitchFile, "operator-requested paper stop");
            await run.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.That(secondChildProcess.HasExited, Is.True);
            Assert.That(File.Exists(sessionPath), Is.False, "The stopped paper-session marker should be cleared.");
            Assert.That(File.Exists(secondConfigPath!), Is.False, "The stopped session's ephemeral credential config should be deleted.");
            var events = File.ReadAllLines(statePath);
            Assert.That(events.Count(line => line.StartsWith("optimizer|", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(events.Count(line => line.StartsWith("paper|", StringComparison.Ordinal)), Is.EqualTo(2));
            Assert.That(File.ReadAllText(audit), Does.Contain("external_emergency_stop_honored"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_KEY", previousApiKey);
            Environment.SetEnvironmentVariable("ALGORY_ALPACA_API_SECRET", previousApiSecret);
            if (run is not null && !run.IsCompleted)
            {
                File.WriteAllText(settings.KillSwitchFile, "test cleanup");
                cancellation.Cancel();
                try { await run.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception) { }
            }
            if (secondChildProcess is not null)
            {
                try
                {
                    if (!secondChildProcess.HasExited) secondChildProcess.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException) { }
                finally { secondChildProcess.Dispose(); }
            }
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static int[] ReadPaperChildIds(string path) => File.ReadAllLines(path)
        .Where(line => line.StartsWith("paper|", StringComparison.Ordinal))
        .Select(line => int.Parse(line.Split('|')[2], CultureInfo.InvariantCulture))
        .ToArray();

    private static async Task<bool> WaitForFileContentAsync(string path, string content, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(path) && File.ReadAllText(path).Contains(content, StringComparison.Ordinal)) return true;
            await Task.Delay(20);
        }
        return false;
    }

    [Test]
    public void PaperRestartPolicyBacksOffResetsAfterHealthySessionAndStopsAfterFailureLimit()
    {
        var policy = new PaperRestartPolicy(3, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
        Assert.That(policy.RegisterExit(1, TimeSpan.FromMinutes(1)), Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(policy.RegisterExit(1, TimeSpan.FromMinutes(1)), Is.EqualTo(TimeSpan.FromSeconds(20)));
        Assert.That(policy.ConsecutiveFailures, Is.EqualTo(2));

        Assert.That(policy.RegisterExit(0, TimeSpan.FromMinutes(5)), Is.EqualTo(TimeSpan.Zero));
        Assert.That(policy.ConsecutiveFailures, Is.Zero);
        Assert.That(policy.RegisterExit(0, TimeSpan.FromMinutes(1)), Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(policy.RegisterExit(1, TimeSpan.FromMinutes(1)), Is.EqualTo(TimeSpan.FromSeconds(20)));
        Assert.Throws<InvalidOperationException>(() => policy.RegisterExit(1, TimeSpan.FromMinutes(1)));
    }

    [Test]
    public void SupervisorInstanceLockAllowsOnlyOneOwnerPerOutputDirectory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lean-supervisor-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var first = SupervisorInstanceLock.Acquire(folder);
            Assert.Throws<InvalidOperationException>(() => SupervisorInstanceLock.Acquire(folder));
            first.Dispose();

            using var restarted = SupervisorInstanceLock.Acquire(folder);
            Assert.That(File.Exists(Path.Combine(folder, "supervisor.lock")), Is.True);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
