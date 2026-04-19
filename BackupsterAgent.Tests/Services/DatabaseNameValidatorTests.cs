using BackupsterAgent.Services.Common;

namespace BackupsterAgent.Tests.Services;

[TestFixture]
public sealed class DatabaseNameValidatorTests
{
    [TestCase("mydb")]
    [TestCase("my_db")]
    [TestCase("my-db")]
    [TestCase("my.db")]
    [TestCase("MyDb123")]
    [TestCase("a")]
    [TestCase("Users_2026-04-19")]
    public void IsValid_AcceptsRegularNames(string name)
    {
        Assert.That(DatabaseNameValidator.IsValid(name, out var reason), Is.True);
        Assert.That(reason, Is.Null);
    }

    [TestCase(null)]
    [TestCase("")]
    public void IsValid_RejectsNullOrEmpty(string? name)
    {
        Assert.That(DatabaseNameValidator.IsValid(name, out var reason), Is.False);
        Assert.That(reason, Does.Contain("пустое"));
    }

    [Test]
    public void IsValid_RejectsTooLong()
    {
        var name = new string('a', DatabaseNameValidator.MaxLength + 1);
        Assert.That(DatabaseNameValidator.IsValid(name, out var reason), Is.False);
        Assert.That(reason, Does.Contain("длина"));
    }

    [TestCase("foo..bar")]
    [TestCase("..evil")]
    [TestCase("trail..")]
    public void IsValid_RejectsDoubleDot(string name)
    {
        Assert.That(DatabaseNameValidator.IsValid(name, out var reason), Is.False);
        Assert.That(reason, Does.Contain("две точки"));
    }

    [TestCase("foo bar")]
    [TestCase("foo\nbar")]
    [TestCase("foo\tbar")]
    [TestCase("foo'; DROP")]
    [TestCase("foo\"bar")]
    [TestCase("foo/bar")]
    [TestCase("foo\\bar")]
    [TestCase("foo\0bar")]
    [TestCase("привет")]
    [TestCase("foo;bar")]
    public void IsValid_RejectsDisallowedChars(string name)
    {
        Assert.That(DatabaseNameValidator.IsValid(name, out var reason), Is.False);
        Assert.That(reason, Does.Contain("недопустимый символ"));
    }
}
