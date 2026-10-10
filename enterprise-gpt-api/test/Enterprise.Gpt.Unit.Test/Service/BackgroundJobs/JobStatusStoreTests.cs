using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service.BackgroundJobs;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.BackgroundJobs;

public sealed class JobStatusStoreTests : IDisposable
{
    private static readonly Guid _ownerId = Guid.NewGuid();
    private static readonly JobTarget Target = new(JobTargetKind.Conversation, Guid.NewGuid());

    private readonly JobStatusStore _store = new(
        new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
        NullLogger<JobStatusStore>.Instance);

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Get_UnknownJob_ReturnsNull()
    {
        Assert.Null(_store.Get("missing"));
    }

    [Fact]
    public void Register_NewJob_RecordsQueuedAtZeroPercentForTheOwner()
    {
        _store.Register("job-1", _ownerId, Target);

        var snapshot = _store.Get("job-1");

        Assert.NotNull(snapshot);
        Assert.Equal(_ownerId, snapshot.UserId);
        Assert.Equal(JobStatus.Queued, snapshot.Status);
        Assert.Equal(0, snapshot.Progress);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Message));
    }

    [Fact]
    public void Report_RecordsStageProgressMessageAndUnitCounts()
    {
        _store.Register("job-1", _ownerId, Target);

        _store.Report("job-1", JobStatus.Embedding, 60, "Embedded 12 of 40 chunk(s)", 12, 40);

        var snapshot = _store.Get("job-1");

        Assert.NotNull(snapshot);
        Assert.Equal(JobStatus.Embedding, snapshot.Status);
        Assert.Equal(60, snapshot.Progress);
        Assert.Equal("Embedded 12 of 40 chunk(s)", snapshot.Message);
        Assert.Equal(12, snapshot.CompletedUnits);
        Assert.Equal(40, snapshot.TotalUnits);
    }

    [Fact]
    public void Report_PreservesTheOwner()
    {
        _store.Register("job-1", _ownerId, Target);

        _store.Report("job-1", JobStatus.Embedding, 60);

        Assert.Equal(_ownerId, _store.Get("job-1")!.UserId);
    }

    [Fact]
    public void Report_LowerProgressThanAlreadyRecorded_KeepsTheHigherValue()
    {
        // Extraction heartbeats race with stage transitions, and a percentage that went backwards would
        // read to the user as the job restarting.
        _store.Register("job-1", _ownerId, Target);
        _store.Report("job-1", JobStatus.Embedding, 70);

        _store.Report("job-1", JobStatus.Embedding, 40, "late heartbeat");

        var snapshot = _store.Get("job-1");

        Assert.NotNull(snapshot);
        Assert.Equal(70, snapshot.Progress);
        Assert.Equal("late heartbeat", snapshot.Message);
    }

    [Fact]
    public void Report_WithoutAMessage_KeepsThePreviousOne()
    {
        _store.Register("job-1", _ownerId, Target);
        _store.Report("job-1", JobStatus.Extracting, 20, "Recognizing text");

        _store.Report("job-1", JobStatus.Extracting, 25);

        Assert.Equal("Recognizing text", _store.Get("job-1")!.Message);
    }

    [Fact]
    public void Report_WithoutUnitCounts_KeepsThePreviousOnes()
    {
        _store.Register("job-1", _ownerId, Target);
        _store.Report("job-1", JobStatus.Embedding, 60, "Embedded 12 of 40 chunk(s)", 12, 40);

        _store.Report("job-1", JobStatus.Embedding, 65, "still going");

        var snapshot = _store.Get("job-1");

        Assert.Equal(12, snapshot!.CompletedUnits);
        Assert.Equal(40, snapshot.TotalUnits);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(150, 100)]
    public void Report_ProgressOutsideTheValidRange_IsClamped(int reported, int expected)
    {
        _store.Register("job-1", _ownerId, Target);

        _store.Report("job-1", JobStatus.Extracting, reported);

        Assert.Equal(expected, _store.Get("job-1")!.Progress);
    }

    [Fact]
    public void Report_UnknownJob_CreatesASnapshotOwnedByNobody()
    {
        // A report for a job that was never registered has no owner to attribute it to, and Guid.Empty
        // matches no user — the snapshot exists for diagnostics but is not readable through the API.
        _store.Report("job-unregistered", JobStatus.Extracting, 20, "Reading");

        var snapshot = _store.Get("job-unregistered");

        Assert.Equal(JobStatus.Extracting, snapshot!.Status);
        Assert.Equal(Guid.Empty, snapshot.UserId);
    }

    [Theory]
    [InlineData(JobStatus.Processed)]
    [InlineData(JobStatus.Failed)]
    public void Report_AfterTheJobReachedATerminalState_IsIgnored(JobStatus terminalStatus)
    {
        // Reviving a terminal snapshot would also make it permanently un-evictable, because eviction only
        // considers terminal states.
        _store.Register("job-1", _ownerId, Target);

        if (terminalStatus == JobStatus.Processed)
        {
            _store.Complete("job-1", Guid.NewGuid(), "done");
        }
        else
        {
            _store.Fail("job-1", "failed");
        }

        _store.Report("job-1", JobStatus.Embedding, 50, "a late report");

        var snapshot = _store.Get("job-1");

        Assert.Equal(terminalStatus, snapshot!.Status);
        Assert.NotEqual("a late report", snapshot.Message);
    }

    [Fact]
    public void Complete_RecordsProcessedAtFullProgressWithTheDocumentId()
    {
        var documentId = Guid.NewGuid();
        _store.Register("job-1", _ownerId, Target);
        _store.Report("job-1", JobStatus.Persisting, 92);

        _store.Complete("job-1", documentId, "Ready — 40 chunk(s) indexed");

        var snapshot = _store.Get("job-1");

        Assert.NotNull(snapshot);
        Assert.Equal(_ownerId, snapshot.UserId);
        Assert.Equal(JobStatus.Processed, snapshot.Status);
        Assert.Equal(100, snapshot.Progress);
        Assert.Equal(documentId, snapshot.DocumentId);
        Assert.Null(snapshot.ErrorMessage);
    }

    [Fact]
    public void Fail_RecordsTheErrorAndLeavesProgressWhereItStopped()
    {
        // Progress is where the failure happened, which tells the user which stage broke; jumping it to
        // 100 would read as a completion.
        _store.Register("job-1", _ownerId, Target);
        _store.Report("job-1", JobStatus.Embedding, 60);

        _store.Fail("job-1", "Processing failed.");

        var snapshot = _store.Get("job-1");

        Assert.NotNull(snapshot);
        Assert.Equal(_ownerId, snapshot.UserId);
        Assert.Equal(JobStatus.Failed, snapshot.Status);
        Assert.Equal(60, snapshot.Progress);
        Assert.Equal("Processing failed.", snapshot.ErrorMessage);
        Assert.Equal("Processing failed.", snapshot.Message);
    }

    [Fact]
    public void Fail_UnknownJob_CreatesATerminalSnapshot()
    {
        _store.Fail("job-unregistered", "Could not be queued.");

        var snapshot = _store.Get("job-unregistered");

        Assert.NotNull(snapshot);
        Assert.Equal(JobStatus.Failed, snapshot.Status);
        Assert.Equal("Could not be queued.", snapshot.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_BlankJobId_Throws(string jobId)
    {
        Assert.Throws<ArgumentException>(() => _store.Register(jobId, _ownerId, Target));
    }

    [Fact]
    public void Report_ConcurrentReports_NeverLosesTheHighestProgress()
    {
        // Every value stays under 100 and the highest is reported first, so a last-writer-wins
        // implementation fails deterministically rather than roughly half the time.
        _store.Register("job-1", _ownerId, Target);
        _store.Report("job-1", JobStatus.Embedding, 90);

        Parallel.For(0, 200, i => _store.Report("job-1", JobStatus.Embedding, i % 90));

        Assert.Equal(90, _store.Get("job-1")!.Progress);
    }

    [Fact]
    public void Cancel_UnknownJob_ReturnsNullAndCreatesNothing()
    {
        Assert.Null(_store.Cancel("missing", "Cancelled"));

        // The difference from Fail: a cancel naming an id nobody registered must not mint an
        // ownerless snapshot that no one can read and retention still has to carry.
        Assert.Null(_store.Get("missing"));
    }

    [Fact]
    public void Cancel_RunningJob_RecordsCancelledAsTerminal()
    {
        _store.Register("job-1", _ownerId, Target);
        _store.Report("job-1", JobStatus.Embedding, 60);

        var outcome = _store.Cancel("job-1", "Cancelled");

        Assert.NotNull(outcome);
        Assert.Equal(JobStatus.Cancelled, outcome.Status);
        Assert.Equal("Cancelled", outcome.Message);
        Assert.Equal(_ownerId, outcome.UserId);

        // Terminal, so a heartbeat still unwinding cannot revive it.
        _store.Report("job-1", JobStatus.Extracting, 20);
        Assert.Equal(JobStatus.Cancelled, _store.Get("job-1")!.Status);
    }

    [Fact]
    public void Cancel_JobThatAlreadySucceeded_ReturnsItWithItsDocumentIntact()
    {
        var documentId = Guid.NewGuid();
        _store.Register("job-1", _ownerId, Target);
        _store.Complete("job-1", documentId, "Ready");

        var outcome = _store.Cancel("job-1", "Cancelled");

        // The caller reads the document off this to remove it, so the id has to survive.
        Assert.NotNull(outcome);
        Assert.Equal(JobStatus.Processed, outcome.Status);
        Assert.Equal(documentId, outcome.DocumentId);
        Assert.Equal(Target, outcome.Target);
    }

    [Fact]
    public void Complete_AfterACancelClaimedTheTerminalSlot_ReportsTheLoss()
    {
        _store.Register("job-1", _ownerId, Target);
        _store.Cancel("job-1", "Cancelled");

        Assert.False(_store.Complete("job-1", Guid.NewGuid(), "Ready"));

        var snapshot = _store.Get("job-1");
        Assert.NotNull(snapshot);
        Assert.Equal(JobStatus.Cancelled, snapshot.Status);
        // Never published: the loser undoes its own work, and a second party must not be invited
        // to delete the same rows.
        Assert.Null(snapshot.DocumentId);
    }

    [Fact]
    public void Complete_WhenNothingBeatIt_ReportsTheWin()
    {
        _store.Register("job-1", _ownerId, Target);

        Assert.True(_store.Complete("job-1", Guid.NewGuid(), "Ready"));
    }

    [Fact]
    public void Fail_AfterACancel_IsIgnored()
    {
        _store.Register("job-1", _ownerId, Target);
        _store.Cancel("job-1", "Cancelled");

        // The cancellation the caller asked for produces a failure as the pipeline unwinds; that
        // must not overwrite the outcome already published.
        _store.Fail("job-1", "Processing failed.");

        Assert.Equal(JobStatus.Cancelled, _store.Get("job-1")!.Status);
    }

    [Fact]
    public void Register_RecordsTheTargetSoACancelKnowsWhatToUndo()
    {
        _store.Register("job-1", _ownerId, Target);

        Assert.Equal(Target, _store.Get("job-1")!.Target);
    }
}
