using BackupsterAgent.Configuration;

namespace BackupsterAgent.Tests.Configuration;

[TestFixture]
public sealed class AgentVersionTests
{
    [Test]
    public void Current_IsNotEmpty()
    {
        Assert.That(AgentVersion.Current, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Current_DoesNotContainBuildMetadata()
    {
        Assert.That(AgentVersion.Current, Does.Not.Contain("+"));
    }

    [Test]
    public void Current_MatchesCsprojDefault()
    {
        Assert.That(AgentVersion.Current, Does.StartWith("0.1.0"));
    }
}
