using Microsoft.Extensions.Logging.Abstractions;
using Vino.AgentHost.Api;
using Vino.AgentHost.Claude;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Hosting;

namespace Vino.AgentHost.Tests;

/// <summary>
/// Phase 3c infrastructure: the ASCII home planner, the CLAUDE.md scaffolder, the executable
/// resolver's authoritative-config rule, and the static catalog under ModelSelector clamping.
/// </summary>
public sealed class ClaudeBackendInfrastructureTests
{
    // ---------------------------------------------------------------- workspace planner

    [Fact]
    public void PlannerUsesTheDataDirectoryWhenItIsAscii()
    {
        var planner = new ClaudeWorkspacePlanner(_ => throw new InvalidOperationException("no 8.3 lookup expected"));
        var (path, tier) = planner.Plan(@"C:\Users\dev\AppData\Local\Vino\projects\abcd1234");
        Assert.Equal(ClaudeHomeTier.DataDirectory, tier);
        Assert.Equal(@"C:\Users\dev\AppData\Local\Vino\projects\abcd1234\claude", path);
        Assert.True(ClaudeWorkspacePlanner.IsAscii(path));
    }

    [Fact]
    public void PlannerFallsBackToTheShortPathForANonAsciiProfile()
    {
        var planner = new ClaudeWorkspacePlanner(_ => @"C:\Users\HONG~1\AppData\Local\Vino\projects\abcd1234");
        var (path, tier) = planner.Plan(@"C:\Users\홍길동\AppData\Local\Vino\projects\abcd1234");
        Assert.Equal(ClaudeHomeTier.ShortPath, tier);
        Assert.Equal(@"C:\Users\HONG~1\AppData\Local\Vino\projects\abcd1234\claude", path);
        Assert.True(ClaudeWorkspacePlanner.IsAscii(path));
    }

    [Theory]
    [InlineData(null)] // 8.3 disabled on the volume: no short name at all
    [InlineData(@"C:\Users\홍길동\AppData\Local\Vino\projects\abcd1234")] // lookup echoes the long path
    public void PlannerFallsBackToProgramDataWhenNoAsciiAliasExists(string? shortPath)
    {
        var planner = new ClaudeWorkspacePlanner(_ => shortPath);
        var (path, tier) = planner.Plan(@"C:\Users\홍길동\AppData\Local\Vino\projects\abcd1234");
        Assert.Equal(ClaudeHomeTier.ProgramData, tier);
        Assert.True(ClaudeWorkspacePlanner.IsAscii(path));
        Assert.Contains(@"\Vino\claude-homes\", path, StringComparison.OrdinalIgnoreCase);

        // Deterministic per (user, project): the same inputs land in the same home.
        var (again, _) = planner.Plan(@"C:\Users\홍길동\AppData\Local\Vino\projects\abcd1234");
        Assert.Equal(path, again);
        // ...and a different project gets a different home.
        var (other, _) = planner.Plan(@"C:\Users\홍길동\AppData\Local\Vino\projects\ffff9999");
        Assert.NotEqual(path, other);
    }

    // ---------------------------------------------------------------- scaffolder

    [Fact]
    public void ScaffolderRefreshesTheManagedBlockAndPreservesUserText()
    {
        using var directory = new TestDirectory();
        var scaffolder = new ClaudeHomeScaffolder();
        var home = directory.GetPath("home");

        var path = scaffolder.ScaffoldSessionHome(home, "FIRST instructions");
        File.AppendAllText(path, "\nMy own notes below the managed block.\n");

        // Re-scaffold (every spawn does): the block refreshes, user text survives.
        scaffolder.ScaffoldSessionHome(home, "SECOND instructions");
        var content = File.ReadAllText(path);
        Assert.Contains("SECOND instructions", content, StringComparison.Ordinal);
        Assert.DoesNotContain("FIRST instructions", content, StringComparison.Ordinal);
        Assert.Contains("My own notes below the managed block.", content, StringComparison.Ordinal);
        Assert.Single(SplitOccurrences(content, ClaudeHomeScaffolder.BeginMarker));

        // A large composed payload (the real ~38KB shape) round-trips intact.
        var big = new string('x', 40_000);
        scaffolder.ScaffoldSessionHome(home, big);
        Assert.Contains(big, File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ScaffolderRecoversWhenMarkersWereHandEdited()
    {
        using var directory = new TestDirectory();
        var scaffolder = new ClaudeHomeScaffolder();
        var home = directory.GetPath("home");
        var path = Path.Combine(home, "CLAUDE.md");
        Directory.CreateDirectory(home);
        File.WriteAllText(path, "A hand-written file with no markers.\n");

        scaffolder.ScaffoldSessionHome(home, "managed text");
        var content = File.ReadAllText(path);
        Assert.StartsWith(ClaudeHomeScaffolder.BeginMarker, content, StringComparison.Ordinal);
        Assert.Contains("A hand-written file with no markers.", content, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- executable resolver

    [Fact]
    public void ExplicitExecutableSettingIsAuthoritativeWithNoFallback()
    {
        using var directory = new TestDirectory();
        // The configured path does not exist -> the ONLY candidate fails -> empty result. Falling
        // back to a discovered binary would break UI-probe/execution convergence.
        var missing = new AgentHostOptions { ClaudeExecutable = directory.GetPath("nope\\claude.exe") };
        Assert.Empty(ClaudeExecutableResolver.EnumerateCandidates(missing));
        Assert.False(ClaudeExecutableResolver.TryResolve(missing, out _));

        // An existing configured claude.exe is the single candidate.
        var exePath = directory.GetPath("bin\\claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllBytes(exePath, [0x4D, 0x5A]);
        var configured = new AgentHostOptions { ClaudeExecutable = exePath };
        var candidate = Assert.Single(ClaudeExecutableResolver.EnumerateCandidates(configured));
        Assert.Equal("app setting", candidate.Source);
        Assert.Equal(Path.GetFullPath(exePath), candidate.Path);
    }

    [Fact]
    public void OnlyANativeClaudeExeIsLaunchable()
    {
        using var directory = new TestDirectory();
        // npm shims (claude.cmd) have no native exe to map to — they must not surface.
        var shim = directory.GetPath("npm\\claude.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(shim)!);
        File.WriteAllText(shim, "@echo off");
        var options = new AgentHostOptions { ClaudeExecutable = shim };
        Assert.Empty(ClaudeExecutableResolver.EnumerateCandidates(options));
    }

    // ---------------------------------------------------------------- catalog + selector

    [Fact]
    public async Task CatalogModelsAreClaudeProviderWithFableDefault()
    {
        var models = await new ClaudeModelCatalog().ListModelsAsync();
        Assert.All(models, model => Assert.Equal(AgentBackends.Claude, model.Provider));
        Assert.Equal("claude-fable-5", Assert.Single(models, model => model.IsDefault).Model);
        // The codex-only "ultra" rung never appears in a Claude ladder.
        Assert.All(models, model => Assert.DoesNotContain("ultra", model.ReasoningEfforts));
    }

    [Fact]
    public async Task SelectorClampsEffortsAgainstTheStaticCatalog()
    {
        var selector = new ModelSelector(new ClaudeModelCatalog(), NullLogger<ModelSelector>.Instance);

        // No pin -> fable default; "ultra" (codex-only) clamps down to fable's max.
        var defaulted = await selector.ResolveDirectAsync("ultra", pinnedModel: null, CancellationToken.None);
        Assert.Equal("claude-fable-5", defaulted.Model);
        Assert.Equal("max", defaulted.Effort);

        // Haiku hard-rejects xhigh/max (spike-measured): xhigh clamps to high.
        var haiku = await selector.ResolveDirectAsync("xhigh", "claude-haiku-4-5", CancellationToken.None);
        Assert.Equal("claude-haiku-4-5", haiku.Model);
        Assert.Equal("high", haiku.Effort);
    }

    private static IEnumerable<int> SplitOccurrences(string haystack, string needle)
    {
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            yield return index;
        }
    }
}
