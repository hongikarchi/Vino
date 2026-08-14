using Vino.BridgeContract;

namespace Vino.BridgeContract.Tests;

public sealed class VinoBackupPathsTests
{
    [Fact]
    public void PruneKeepsTheMostRecentFoldersAndDeletesTheRest()
    {
        using var temp = new TempDir();
        // Six folders with increasing write times; folder-5 is the newest.
        for (var index = 0; index < 6; index++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(temp.Path, $"folder-{index}"));
            File.WriteAllText(Path.Combine(dir.FullName, "model.3dm"), "x");
            dir.LastWriteTimeUtc = new DateTime(2026, 1, 1, 0, 0, index, DateTimeKind.Utc);
        }

        var removed = VinoBackupPaths.PruneToMostRecent(temp.Path, keep: 2);

        Assert.Equal(4, removed);
        var survivors = Directory.GetDirectories(temp.Path).Select(Path.GetFileName).OrderBy(name => name).ToArray();
        Assert.Equal(new[] { "folder-4", "folder-5" }, survivors);
    }

    [Fact]
    public void PruneIsANoOpWhenWithinTheLimitOrTheRootIsMissing()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "only"));
        Assert.Equal(0, VinoBackupPaths.PruneToMostRecent(temp.Path, keep: 6));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "only")));

        Assert.Equal(0, VinoBackupPaths.PruneToMostRecent(Path.Combine(temp.Path, "does-not-exist"), keep: 3));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vino-backup-prune-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp dir is harmless.
            }
        }
    }
}
