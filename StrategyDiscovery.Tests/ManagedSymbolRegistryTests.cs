using NUnit.Framework;
using QuantConnect.Genetic;

namespace QuantConnect.Genetic.Tests;

[TestFixture]
public sealed class ManagedSymbolRegistryTests
{
    [Test]
    public void RemovedInvestedUniverseMemberRemainsManagedUntilItsPositionCloses()
    {
        var registry = new ManagedSymbolRegistry<string>();
        const string anchor = "SPY";
        const string member = "AAPL";
        registry.AddAnchor(anchor);
        registry.AddUniverseMember(member);

        registry.RemoveUniverseMember(member, invested: true);
        Assert.That(registry.Contains(member), Is.True);
        Assert.That(registry.OnPositionStateChanged(member, invested: true), Is.False);
        Assert.That(registry.OnPositionStateChanged(member, invested: false), Is.True);
        Assert.That(registry.Contains(member), Is.False);

        registry.RemoveUniverseMember(anchor, invested: false);
        Assert.That(registry.Contains(anchor), Is.True);
        Assert.That(registry.IsAnchor(anchor), Is.True);
    }

    [Test]
    public void FlatRemovedUniverseMemberIsReleasedAndReaddedMembersBecomeManagedAgain()
    {
        var registry = new ManagedSymbolRegistry<string>();
        const string member = "IWM";

        registry.AddUniverseMember(member);
        registry.RemoveUniverseMember(member, invested: false);
        Assert.That(registry.Contains(member), Is.False);

        registry.AddUniverseMember(member);
        Assert.That(registry.Contains(member), Is.True);
        Assert.That(registry.OnPositionStateChanged(member, invested: false), Is.False);
        Assert.That(registry.Contains(member), Is.True);
    }
}
