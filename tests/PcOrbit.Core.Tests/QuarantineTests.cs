using PcOrbit.Adapters.Windows;
using PcOrbit.Core.Cleanup;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The quarantine store, which is the only reason cleanup was allowed to ship.
/// </summary>
/// <remarks>
/// Undo everywhere else in this product restores the value a step read before it. A deleted file
/// has no such value, so cleanup had to bring its own undo — and an undo nobody has tested is not
/// one (ADR 0004, ADR 0005). Everything here runs against a scratch folder, never the real one.
/// </remarks>
public sealed class QuarantineTests : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(),
        "pcorbit-tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _source;
    private readonly string _store;

    public QuarantineTests()
    {
        _source = Path.Combine(_work, "source");
        _store = Path.Combine(_work, "store");

        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_store);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, recursive: true);
            }
        }
        catch (IOException)
        {
            // A scratch folder we could not remove is not a test failure.
        }
    }

    /// <summary>
    /// A location table pointing at this test's scratch folder.
    /// </summary>
    /// <remarks>
    /// The store now only touches folders named in the table it was given, which is why a test has
    /// to supply one: a candidate carrying a path the table does not know is refused, and that
    /// refusal is the point — it is what stops a caller pointing the deleter anywhere it likes.
    /// </remarks>
    private IReadOnlyList<CleanupLocation> Table(CleanupTrust trust) =>
        [new CleanupLocation("temp.test", CleanupCategory.TemporaryFiles, trust, [_source])];

    private WindowsQuarantineStore NewStore(
        FakeClock? clock = null,
        TimeSpan? retention = null,
        CleanupTrust trust = CleanupTrust.Reclaimable) =>
        new(_store, clock ?? new FakeClock(), new SequentialIds(), retention, Table(trust));

    private string WriteFile(string name, string content = "scratch")
    {
        string path = Path.Combine(_source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private CleanupCandidate Candidate(
        CleanupTrust trust = CleanupTrust.Reclaimable,
        int fileCount = 1,
        long bytes = 7) =>
        new(
            "temp.test",
            CleanupCategory.TemporaryFiles,
            _source,
            bytes,
            fileCount,
            NewestItem: null,
            trust,
            new Evidence(EvidenceSourceKind.Win32Api, "test", Confidence.High));

    // ---------------------------------------------------------------- the round trip

    [Fact]
    public async Task AQuarantinedFileLeavesItsOriginalPlaceAndComesBackToIt()
    {
        string file = WriteFile("old.tmp", "the original bytes");

        WindowsQuarantineStore store = NewStore();
        QuarantineBatch batch = await store.QuarantineAsync([Candidate()]);

        Assert.False(File.Exists(file), "the file should have moved out of its original place");
        Assert.Single(batch.Files);

        QuarantineRestore restore = await store.RestoreAsync(batch.Id);

        Assert.True(restore.IsComplete);
        Assert.Equal(1, restore.Restored);
        Assert.True(File.Exists(file), "the file should be back where it came from");
        Assert.Equal("the original bytes", File.ReadAllText(file));
    }

    [Fact]
    public async Task RestoreRebuildsFoldersThatWereEmptiedOut()
    {
        string file = WriteFile(Path.Combine("nested", "deep", "old.tmp"));

        WindowsQuarantineStore store = NewStore();
        QuarantineBatch batch = await store.QuarantineAsync([Candidate()]);

        Directory.Delete(Path.Combine(_source, "nested"), recursive: true);

        QuarantineRestore restore = await store.RestoreAsync(batch.Id);

        Assert.True(restore.IsComplete);
        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// Something recreated the file while it was in quarantine. Overwriting it would be a second,
    /// unannounced data loss, so both copies survive and the restore says what it did not do.
    /// </summary>
    [Fact]
    public async Task RestoreWillNotOverwriteAFileThatCameBackOnItsOwn()
    {
        string file = WriteFile("old.tmp", "original");

        WindowsQuarantineStore store = NewStore();
        QuarantineBatch batch = await store.QuarantineAsync([Candidate()]);

        File.WriteAllText(file, "something newer");

        QuarantineRestore restore = await store.RestoreAsync(batch.Id);

        Assert.False(restore.IsComplete);
        Assert.Equal(0, restore.Restored);
        Assert.Equal("something newer", File.ReadAllText(file));
        Assert.Contains(restore.Failures, f => f.Contains("exists again", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- refusals

    /// <summary>
    /// The store is the last line, not the only one. A caller that hands it a category this
    /// product does not remove gets a refusal rather than a favour.
    /// </summary>
    [Theory]
    [InlineData(CleanupTrust.ReportOnly)]
    [InlineData(CleanupTrust.Protected)]
    [InlineData(CleanupTrust.Regenerable)]
    public async Task QuarantineRefusesEverythingThatIsNotReclaimable(CleanupTrust trust)
    {
        string file = WriteFile("keep.dat");

        QuarantineBatch batch = await NewStore(trust: trust).QuarantineAsync([Candidate(trust)]);

        Assert.Empty(batch.Files);
        Assert.True(File.Exists(file), "a refused candidate must be left exactly where it was");
        Assert.NotEmpty(batch.Failures);
    }

    // ---------------------------------------------------------------- the delete route

    /// <summary>
    /// The reason the disk actually shrinks. A browser cache is rebuilt by the next page load, so
    /// holding it for thirty days would leave the space spoken for while the user reads a number
    /// saying otherwise.
    /// </summary>
    [Fact]
    public async Task ARegenerableCacheIsDeletedAndTheSpaceIsFreedAtOnce()
    {
        string file = WriteFile("chunk.bin", new string('x', 4096));

        CleanupDeletion deletion = await NewStore(trust: CleanupTrust.Regenerable)
            .DeleteRegenerableAsync([Candidate(CleanupTrust.Regenerable)]);

        Assert.Equal(4096, deletion.Freed);
        Assert.Equal(1, deletion.Removed);
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData(CleanupTrust.Reclaimable)]
    [InlineData(CleanupTrust.ReportOnly)]
    [InlineData(CleanupTrust.Protected)]
    public async Task DeletingRefusesAnythingThatIsNotRegenerable(CleanupTrust trust)
    {
        string file = WriteFile("keep.dat");

        CleanupDeletion deletion = await NewStore(trust: trust).DeleteRegenerableAsync([Candidate(trust)]);

        Assert.Equal(0, deletion.Freed);
        Assert.True(File.Exists(file), "only a regenerable cache may be deleted outright");
        Assert.NotEmpty(deletion.Failures);
    }

    /// <summary>
    /// A candidate naming a location the table does not know is refused. Without this the deleter
    /// would go wherever its caller pointed it.
    /// </summary>
    [Fact]
    public async Task ALocationTheTableDoesNotKnowIsRefused()
    {
        string file = WriteFile("keep.dat");

        CleanupCandidate stranger = Candidate(CleanupTrust.Regenerable) with { Id = "somewhere.else" };

        CleanupDeletion deletion = await NewStore(trust: CleanupTrust.Regenerable)
            .DeleteRegenerableAsync([stranger]);

        Assert.Equal(0, deletion.Freed);
        Assert.True(File.Exists(file));
        Assert.NotEmpty(deletion.Failures);
    }

    /// <summary>The user asking for their disk back before the window closes.</summary>
    [Fact]
    public async Task PurgingEmptiesTheWholeStoreWhateverItsRetentionSays()
    {
        WriteFile("old.tmp", new string('x', 2048));

        WindowsQuarantineStore store = NewStore(retention: TimeSpan.FromDays(30));
        await store.QuarantineAsync([Candidate()]);

        long released = await store.PurgeAllAsync();

        Assert.Equal(2048, released);
        Assert.Empty(await store.ListAsync());
    }

    // ---------------------------------------------------------------- retention

    [Fact]
    public async Task ABatchIsNotPurgedWhileItIsStillInsideItsWindow()
    {
        WriteFile("old.tmp");

        var clock = new FakeClock();
        WindowsQuarantineStore store = NewStore(clock, TimeSpan.FromDays(30));
        QuarantineBatch batch = await store.QuarantineAsync([Candidate()]);

        long released = await store.PurgeExpiredAsync(clock.Now.AddDays(29));

        Assert.Equal(0, released);
        Assert.Single(await store.ListAsync());
        Assert.False(batch.IsExpired(clock.Now.AddDays(29)));
    }

    /// <summary>
    /// The one place in this product that deletes anything for real, and it only happens once the
    /// window the user was told about has closed.
    /// </summary>
    [Fact]
    public async Task ABatchPastItsWindowIsPurgedAndTheSpaceIsReallyReleased()
    {
        WriteFile("old.tmp", new string('x', 512));

        var clock = new FakeClock();
        WindowsQuarantineStore store = NewStore(clock, TimeSpan.FromDays(30));
        QuarantineBatch batch = await store.QuarantineAsync([Candidate()]);

        long released = await store.PurgeExpiredAsync(clock.Now.AddDays(31));

        Assert.Equal(512, released);
        Assert.Empty(await store.ListAsync());
        Assert.True(batch.IsExpired(clock.Now.AddDays(31)));
    }

    // ---------------------------------------------------------------- bookkeeping

    [Fact]
    public async Task AStoredBatchCanBeListedBackWithItsOriginalPathsIntact()
    {
        string file = WriteFile("old.tmp");

        WindowsQuarantineStore store = NewStore();
        QuarantineBatch created = await store.QuarantineAsync([Candidate()]);

        QuarantineBatch listed = Assert.Single(await store.ListAsync());

        Assert.Equal(created.Id, listed.Id);
        Assert.Equal(file, Assert.Single(listed.Files).OriginalPath);
    }

    [Fact]
    public async Task RestoringABatchThatDoesNotExistSaysSoRatherThanThrowing()
    {
        QuarantineRestore restore = await NewStore().RestoreAsync("qtn-nope");

        Assert.False(restore.IsComplete);
        Assert.Equal(0, restore.Restored);
    }

    [Fact]
    public async Task AnEmptyStoreListsNothing()
    {
        Assert.Empty(await NewStore().ListAsync());
    }
}
