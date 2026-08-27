using System.Text;
using Vino.History;

namespace Vino.History.Tests;

public sealed class ManagedHistoryRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        FindRepositoryRoot(),
        "artifacts",
        "test-temp",
        "history",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Baseline_and_verified_change_advance_history()
    {
        var repository = new ManagedHistoryRepository(_root);
        var baseline = await repository.InitializeBaselineAsync(
            Files(("manifest.json", "{\"revision\":0}")), Guid.NewGuid());

        var metadata = Metadata(1);
        var next = await repository.CommitAsync(new HistoryCommitRequest(
            baseline.Head,
            Files(("manifest.json", "{\"revision\":1}"), ("grasshopper/definition.ghx", "<Archive />")),
            metadata));

        Assert.True(next.CreatedCommit);
        Assert.NotEqual(baseline.Head, next.Head);
        Assert.Equal(next.Head, repository.ReadHead());
        Assert.True(repository.Verify().IsValid);
    }

    [Fact]
    public async Task Commit_rejects_stale_expected_head()
    {
        var repository = new ManagedHistoryRepository(_root);
        await repository.InitializeBaselineAsync(Files(("manifest.json", "{}")), Guid.NewGuid());

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => repository.CommitAsync(
            new HistoryCommitRequest("stale", Files(("manifest.json", "{\"x\":1}")), Metadata(1))));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("/absolute.txt")]
    [InlineData(".git")]
    [InlineData(".git.")]
    [InlineData("nested/.git ")]
    [InlineData(".git/config")]
    [InlineData("nested/.GIT/config")]
    [InlineData("safe/../secret.txt")]
    public async Task Baseline_rejects_unsafe_paths(string path)
    {
        var repository = new ManagedHistoryRepository(_root);
        await Assert.ThrowsAsync<HistoryPathException>(() => repository.InitializeBaselineAsync(
            Files((path, "no")), Guid.NewGuid()));
    }

    [Fact]
    public async Task Baseline_validates_entire_file_set_before_initializing_or_writing()
    {
        var repository = new ManagedHistoryRepository(_root);

        await Assert.ThrowsAsync<HistoryPathException>(() => repository.InitializeBaselineAsync(
            Files(("safe.txt", "must not be written"), ("nested/.git/config", "unsafe")),
            Guid.NewGuid()));

        Assert.False(repository.IsInitialized);
        Assert.False(File.Exists(Path.Combine(_root, "safe.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".git")));
    }

    [Fact]
    public async Task Commit_validates_entire_file_set_before_mutating_worktree()
    {
        var repository = new ManagedHistoryRepository(_root);
        var baseline = await repository.InitializeBaselineAsync(
            Files(("safe.txt", "before")), Guid.NewGuid());

        await Assert.ThrowsAsync<HistoryPathException>(() => repository.CommitAsync(
            new HistoryCommitRequest(
                baseline.Head,
                Files(("safe.txt", "after"), (".git/config", "unsafe")),
                Metadata(1))));

        Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(_root, "safe.txt")));
        Assert.Equal(baseline.Head, repository.ReadHead());
        Assert.True(repository.Verify().IsValid);
    }

    [Fact]
    public async Task Baseline_rejects_existing_reparse_point_before_writing()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "actual");
        var link = Path.Combine(_root, "linked");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var repository = new ManagedHistoryRepository(_root);
        await Assert.ThrowsAsync<HistoryPathException>(() => repository.InitializeBaselineAsync(
            Files(("linked/unsafe.txt", "no")), Guid.NewGuid()));

        Assert.False(repository.IsInitialized);
        Assert.False(File.Exists(Path.Combine(target, "unsafe.txt")));
    }

    [Fact]
    public void Pair_fingerprint_is_case_insensitive_on_windows()
    {
        var lower = ProjectHomeLayout.StablePairFingerprint("c:/models/a.3dm", "c:/models/a.gh");
        var upper = ProjectHomeLayout.StablePairFingerprint("C:/MODELS/A.3DM", "C:/MODELS/A.GH");
        Assert.Equal(lower, upper);
    }

    private static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files(params (string Path, string Text)[] files) =>
        files.ToDictionary(
            item => item.Path,
            item => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(item.Text),
            StringComparer.Ordinal);

    [Fact]
    public async Task History_can_be_read_back_commit_by_commit()
    {
        // The repository recorded a commit per verified job and could not read one back — so the
        // pre-change state of every edit was on disk and unreachable, and a relayout's old
        // coordinates were reported as "not recorded" when they were sitting right there.
        var repository = new ManagedHistoryRepository(_root);
        var baseline = await repository.InitializeBaselineAsync(
            Files(("state/snapshot.json", "{\"pivots\":\"baseline\"}")), Guid.NewGuid());
        var first = await repository.CommitAsync(new HistoryCommitRequest(
            baseline.Head,
            Files(("state/snapshot.json", "{\"pivots\":\"before\"}")),
            Metadata(1)));
        var second = await repository.CommitAsync(new HistoryCommitRequest(
            first.Head,
            Files(("state/snapshot.json", "{\"pivots\":\"after\"}")),
            Metadata(2)));

        var revisions = repository.ListRevisions();

        // Newest first, with the revision number and summary parsed out of the commit message.
        Assert.Equal(3, revisions.Count);
        Assert.Equal(second.Head, revisions[0].Sha);
        Assert.Equal(2, revisions[0].Revision);
        Assert.Equal("test change", revisions[0].Summary);
        Assert.Equal("r2", revisions[0].SnapshotId);
        Assert.NotEqual(Guid.Empty, revisions[0].SessionId);

        // The point of the whole exercise: the state BEFORE a commit is recoverable from it.
        var parent = repository.FindParent(second.Head!);
        Assert.NotNull(parent);
        Assert.Equal(first.Head, parent!.Sha);
        var before = repository.ReadFileAt(parent.Sha, "state/snapshot.json");
        Assert.NotNull(before);
        Assert.Equal("{\"pivots\":\"before\"}", Encoding.UTF8.GetString(before!.Value.Span));

        // Reading never disturbs the present state.
        Assert.Equal(second.Head, repository.ReadHead());
        Assert.True(repository.Verify().IsValid);
    }

    [Fact]
    public async Task Reading_an_unknown_commit_or_path_returns_null()
    {
        var repository = new ManagedHistoryRepository(_root);
        var baseline = await repository.InitializeBaselineAsync(
            Files(("state/snapshot.json", "{}")), Guid.NewGuid());

        Assert.Null(repository.ReadFileAt(baseline.Head!, "state/missing.json"));
        Assert.Null(repository.ReadFileAt(new string('0', 40), "state/snapshot.json"));
        Assert.Null(repository.FindParent(baseline.Head!));
    }

    /// <summary>
    /// A commit writes only the paths it lists and inherits the rest of the parent tree. The whole
    /// script-source capture rests on this: a source stored once must stay readable at every later
    /// revision, or a rewind would only ever reach the job that happened to touch that component.
    /// </summary>
    [Fact]
    public async Task Files_not_listed_in_a_commit_carry_forward_from_the_parent()
    {
        var repository = new ManagedHistoryRepository(_root);
        var baseline = await repository.InitializeBaselineAsync(
            Files(("state/snapshot.json", "{}")), Guid.NewGuid());

        var first = await repository.CommitAsync(new HistoryCommitRequest(
            baseline.Head,
            Files(("state/snapshot.json", "{\"r\":1}"), ("sources/aaaa.txt", "print(1)")),
            Metadata(1)));
        // The second commit does not mention the source at all.
        var second = await repository.CommitAsync(new HistoryCommitRequest(
            first.Head,
            Files(("state/snapshot.json", "{\"r\":2}")),
            Metadata(2)));

        Assert.Equal("print(1)", ReadText(repository, second.Head, "sources/aaaa.txt"));
        Assert.Equal("print(1)", ReadText(repository, first.Head, "sources/aaaa.txt"));
        Assert.True(repository.Verify().IsValid);
    }

    /// <summary>
    /// The rewind enumerates a revision's stored sources without knowing the component ids in
    /// advance, and reads each one AS OF that revision — the older text, not the newer one.
    /// </summary>
    [Fact]
    public async Task Stored_sources_are_listed_and_read_at_the_revision_that_held_them()
    {
        var repository = new ManagedHistoryRepository(_root);
        var baseline = await repository.InitializeBaselineAsync(
            Files(("state/snapshot.json", "{}")), Guid.NewGuid());

        var first = await repository.CommitAsync(new HistoryCommitRequest(
            baseline.Head,
            Files(
                ("state/snapshot.json", "{\"r\":1}"),
                ("sources/aaaa.txt", "version one"),
                ("sources-baseline/aaaa.txt", "the author's own original")),
            Metadata(1)));
        var second = await repository.CommitAsync(new HistoryCommitRequest(
            first.Head,
            Files(("state/snapshot.json", "{\"r\":2}"), ("sources/aaaa.txt", "version two"), ("sources/bbbb.txt", "another")),
            Metadata(2)));

        Assert.Equal(["sources/aaaa.txt"], repository.ListFilesAt(first.Head, "sources"));
        Assert.Equal(
            ["sources/aaaa.txt", "sources/bbbb.txt"],
            repository.ListFilesAt(second.Head, "sources").OrderBy(path => path, StringComparer.Ordinal));

        // The point of the whole exercise: the older revision still yields the older text.
        Assert.Equal("version one", ReadText(repository, first.Head, "sources/aaaa.txt"));
        Assert.Equal("version two", ReadText(repository, second.Head, "sources/aaaa.txt"));
        // And the pre-Vino original survives the second edit untouched, because nothing rewrote it.
        Assert.Equal("the author's own original", ReadText(repository, second.Head, "sources-baseline/aaaa.txt"));

        Assert.Equal([], repository.ListFilesAt(second.Head, "sources-does-not-exist"));
    }

    /// <summary>
    /// The pre-Vino source is written once and never again, so the capture asks whether it is
    /// already there. Answering wrongly would overwrite the author's original on the second edit.
    /// </summary>
    [Fact]
    public async Task Head_membership_is_reported_per_path()
    {
        var repository = new ManagedHistoryRepository(_root);
        var baseline = await repository.InitializeBaselineAsync(
            Files(("state/snapshot.json", "{}")), Guid.NewGuid());
        await repository.CommitAsync(new HistoryCommitRequest(
            baseline.Head,
            Files(("state/snapshot.json", "{\"r\":1}"), ("sources-baseline/aaaa.txt", "original")),
            Metadata(1)));

        Assert.True(repository.HasFileAtHead("sources-baseline/aaaa.txt"));
        Assert.False(repository.HasFileAtHead("sources-baseline/bbbb.txt"));
    }

    private static string? ReadText(ManagedHistoryRepository repository, string sha, string path) =>
        repository.ReadFileAt(sha, path) is { } bytes ? Encoding.UTF8.GetString(bytes.Span) : null;

    private static HistoryCommitMetadata Metadata(int revision) =>
        new(revision, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"r{revision}", "sha256:change", "Standard", "test change");

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Vino.sln")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Vino repository root for test artifacts.");
    }

    public void Dispose()
    {
        // Local verification artifacts are evidence and are intentionally preserved.
    }
}
