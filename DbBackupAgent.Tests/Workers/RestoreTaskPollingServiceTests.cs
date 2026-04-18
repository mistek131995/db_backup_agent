using DbBackupAgent.Domain;
using DbBackupAgent.Enums;
using DbBackupAgent.Workers;

namespace DbBackupAgent.Tests.Workers;

[TestFixture]
public sealed class RestoreTaskPollingServiceTests
{
    [Test]
    public void CombineResults_DbSuccessFilesSuccess_OverallSuccess()
    {
        var patch = RestoreTaskPollingService.CombineResults(
            DatabaseRestoreResult.Success(),
            FileRestoreResult.Success(3));

        Assert.Multiple(() =>
        {
            Assert.That(patch.Status, Is.EqualTo(RestoreTaskStatus.Success));
            Assert.That(patch.DatabaseStatus, Is.EqualTo(RestoreDatabaseStatus.Success));
            Assert.That(patch.FilesStatus, Is.EqualTo(RestoreFilesStatus.Success));
            Assert.That(patch.ErrorMessage, Is.Null);
            Assert.That(patch.FilesRestoredCount, Is.EqualTo(3));
            Assert.That(patch.FilesFailedCount, Is.Null);
        });
    }

    [Test]
    public void CombineResults_DbSuccessFilesSkipped_OverallSuccess()
    {
        var patch = RestoreTaskPollingService.CombineResults(
            DatabaseRestoreResult.Success(),
            FileRestoreResult.Skipped());

        Assert.Multiple(() =>
        {
            Assert.That(patch.Status, Is.EqualTo(RestoreTaskStatus.Success));
            Assert.That(patch.DatabaseStatus, Is.EqualTo(RestoreDatabaseStatus.Success));
            Assert.That(patch.FilesStatus, Is.EqualTo(RestoreFilesStatus.Skipped));
            Assert.That(patch.ErrorMessage, Is.Null);
            Assert.That(patch.FilesRestoredCount, Is.Null);
            Assert.That(patch.FilesFailedCount, Is.Null);
        });
    }

    [Test]
    public void CombineResults_DbSuccessFilesPartial_OverallPartialWithFilesError()
    {
        var patch = RestoreTaskPollingService.CombineResults(
            DatabaseRestoreResult.Success(),
            FileRestoreResult.Partial(restored: 5, failed: 2, errorMessage: "f-err"));

        Assert.Multiple(() =>
        {
            Assert.That(patch.Status, Is.EqualTo(RestoreTaskStatus.Partial));
            Assert.That(patch.DatabaseStatus, Is.EqualTo(RestoreDatabaseStatus.Success));
            Assert.That(patch.FilesStatus, Is.EqualTo(RestoreFilesStatus.Partial));
            Assert.That(patch.ErrorMessage, Is.EqualTo("f-err"));
            Assert.That(patch.FilesRestoredCount, Is.EqualTo(5));
            Assert.That(patch.FilesFailedCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void CombineResults_DbSuccessFilesFailed_OverallIsPartialBecauseDbRestored()
    {
        var patch = RestoreTaskPollingService.CombineResults(
            DatabaseRestoreResult.Success(),
            FileRestoreResult.Failed("f-err"));

        Assert.Multiple(() =>
        {
            // "files=failed" while the DB was restored is reported as overall=partial,
            // not failed — the DB state is valid and the operator needs to know that.
            Assert.That(patch.Status, Is.EqualTo(RestoreTaskStatus.Partial));
            Assert.That(patch.DatabaseStatus, Is.EqualTo(RestoreDatabaseStatus.Success));
            Assert.That(patch.FilesStatus, Is.EqualTo(RestoreFilesStatus.Failed));
            Assert.That(patch.ErrorMessage, Is.EqualTo("f-err"));
        });
    }

    [Test]
    public void CombineResults_DbFailedFilesSuccess_OverallFailed()
    {
        var patch = RestoreTaskPollingService.CombineResults(
            DatabaseRestoreResult.Failed("db-err"),
            FileRestoreResult.Success(4));

        Assert.Multiple(() =>
        {
            Assert.That(patch.Status, Is.EqualTo(RestoreTaskStatus.Failed));
            Assert.That(patch.DatabaseStatus, Is.EqualTo(RestoreDatabaseStatus.Failed));
            Assert.That(patch.FilesStatus, Is.EqualTo(RestoreFilesStatus.Success));
            Assert.That(patch.ErrorMessage, Is.EqualTo("db-err"));
            Assert.That(patch.FilesRestoredCount, Is.EqualTo(4));
        });
    }

    [Test]
    public void CombineResults_DbFailedFilesFailed_OverallFailedBothMessagesJoined()
    {
        var patch = RestoreTaskPollingService.CombineResults(
            DatabaseRestoreResult.Failed("db-err"),
            FileRestoreResult.Failed("f-err"));

        Assert.Multiple(() =>
        {
            Assert.That(patch.Status, Is.EqualTo(RestoreTaskStatus.Failed));
            Assert.That(patch.DatabaseStatus, Is.EqualTo(RestoreDatabaseStatus.Failed));
            Assert.That(patch.FilesStatus, Is.EqualTo(RestoreFilesStatus.Failed));
            Assert.That(patch.ErrorMessage, Is.EqualTo("db-err\n\nf-err"));
        });
    }

    [Test]
    public void CombineResults_ZeroRestoredCount_EmittedAsNullNotZero()
    {
        var patch = RestoreTaskPollingService.CombineResults(
            DatabaseRestoreResult.Success(),
            FileRestoreResult.Success(0));

        Assert.Multiple(() =>
        {
            // Zero must not overwrite the server-side counter; PATCH sends null.
            Assert.That(patch.FilesRestoredCount, Is.Null);
            Assert.That(patch.FilesFailedCount, Is.Null);
        });
    }

    [Test]
    public void CombineResults_OnlyDbError_UsedAsErrorMessage()
    {
        var patch = RestoreTaskPollingService.CombineResults(
            DatabaseRestoreResult.Failed("only-db"),
            FileRestoreResult.Success(1));

        Assert.That(patch.ErrorMessage, Is.EqualTo("only-db"));
    }
}
