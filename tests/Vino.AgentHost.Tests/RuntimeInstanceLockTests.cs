using Vino.AgentHost.Hosting;

namespace Vino.AgentHost.Tests;

public sealed class RuntimeInstanceLockTests
{
    [Fact]
    public void SameDataRootHasExactlyOneLiveOwner()
    {
        using var directory = new TestDirectory();
        var dataRoot = directory.GetPath("runtime-data");
        var first = RuntimeInstanceLock.Acquire(dataRoot);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                RuntimeInstanceLock.Acquire(dataRoot));
            Assert.Contains("already owns", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            first.Dispose();
        }

        Assert.True(File.Exists(Path.Combine(dataRoot, ".vino-instance.lock")));
        using var replacement = RuntimeInstanceLock.Acquire(dataRoot);
        Assert.Equal(Path.GetFullPath(dataRoot), replacement.DataDirectory);
    }

    [Fact]
    public void DifferentDataRootsCanRunConcurrently()
    {
        using var directory = new TestDirectory();
        using var first = RuntimeInstanceLock.Acquire(directory.GetPath("first"));
        using var second = RuntimeInstanceLock.Acquire(directory.GetPath("second"));

        Assert.NotEqual(first.DataDirectory, second.DataDirectory);
    }
}
