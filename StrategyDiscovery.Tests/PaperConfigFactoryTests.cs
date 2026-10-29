using System.Security.AccessControl;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Genetic.Supervisor;
using WindowsSecurityIdentifier = System.Security.Principal.SecurityIdentifier;

namespace QuantConnect.Tests.StrategyDiscovery;

[TestFixture]
public sealed class PaperConfigFactoryTests
{
    [Test]
    public void EphemeralPaperConfigDirectoryIsRestrictedToCurrentUser()
    {
        var path = PaperConfigFactory.WriteEphemeralConfig(new JObject { ["alpaca-api-secret"] = "test-secret" });
        var directory = Path.GetDirectoryName(path)!;
        try
        {
            Assert.That(File.Exists(path), Is.True);
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                var owner = security.GetOwner(typeof(WindowsSecurityIdentifier)) as WindowsSecurityIdentifier;
                var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(WindowsSecurityIdentifier))
                    .Cast<FileSystemAccessRule>()
                    .Where(rule => rule.AccessControlType == AccessControlType.Allow)
                    .Select(rule => rule.IdentityReference.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                Assert.That(security.AreAccessRulesProtected, Is.True);
                Assert.That(owner, Is.Not.Null);
                Assert.That(rules, Is.EquivalentTo(new[] { owner!.Value }));
            }
            else
            {
                Assert.That(File.GetUnixFileMode(directory), Is.EqualTo(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
                Assert.That(File.GetUnixFileMode(path), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
            }
        }
        finally
        {
            PaperConfigFactory.DeleteEphemeralConfig(path);
        }
    }
}
