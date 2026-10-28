using System.Diagnostics;
using System.Globalization;
using NUnit.Framework;
using QuantConnect.Genetic.Supervisor;

namespace QuantConnect.Tests.StrategyDiscovery;

[TestFixture]
public class SupervisorRecoveryTests
{
    [Test]
    public void PaperProcessRecoveryGuardBlocksLiveOrphansAndClearsStaleOrReusedProcessIds()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lean-paper-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var statePath = Path.Combine(folder, "active-paper-session.json");
            var identity = new PaperProcessIdentity(1234, DateTimeOffset.Parse("2026-10-28T12:00:00Z"));
            PaperProcessRecoveryGuard.Save(statePath, identity);
            Assert.That(PaperProcessRecoveryGuard.FindOrphan(statePath, _ => identity.StartedAtUtc), Is.EqualTo(identity));
            Assert.That(File.Exists(statePath), Is.True);

            Assert.That(PaperProcessRecoveryGuard.FindOrphan(statePath, _ => null), Is.Null);
            Assert.That(File.Exists(statePath), Is.False);

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
