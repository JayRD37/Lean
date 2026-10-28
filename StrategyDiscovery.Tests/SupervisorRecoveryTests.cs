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
            Assert.That(PaperProcessRecoveryGuard.FindOrphan(statePath, _ => identity.StartedAtUtc.AddSeconds(1)), Is.Null);
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
