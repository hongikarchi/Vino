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
