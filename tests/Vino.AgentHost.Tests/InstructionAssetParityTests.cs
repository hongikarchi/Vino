using Vino.AgentHost.Hosting;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The model-facing instruction text lives in markdown assets (the runtime source of truth) and the
/// build embeds the same files into the assembly as the broken-install fallback. These tests pin the
/// embedding: the resource must exist under the exact name InstructionAssets looks up (a csproj
/// LogicalName typo would otherwise surface as a startup crash on the first damaged install) and
/// must carry the asset's current content.
/// </summary>
public sealed class InstructionAssetParityTests
{
    // payload-guide.md left this list on 2026-08-27: it moved to assets/skills (served on demand
    // via skill_read like every other reference) and is no longer embedded — the change_submit
    // description carries only the measured-trap core.
    [Theory]
    [InlineData("house-rules.md")]
    public void EmbeddedCopyMatchesAsset(string assetFileName)
    {
        var assetPath = Path.Combine(RepoRoot(), "assets", "instructions", assetFileName);
        Assert.True(File.Exists(assetPath), $"Instruction asset not found: {assetPath}");
        Assert.Equal(
            Normalize(File.ReadAllText(assetPath)),
            Normalize(InstructionAssets.ReadEmbedded(assetFileName)));
    }

    [Fact]
    public void LooseFileWinsOverTheEmbeddedCopy()
    {
        // The deployed file is the tuning surface: whatever it says must beat the embedded copy.
        var assetDirectory = Path.Combine(RepoRoot(), "assets", "instructions");
        Assert.Equal(
            Normalize(File.ReadAllText(Path.Combine(assetDirectory, "house-rules.md"))),
            Normalize(InstructionAssets.LoadOrFallback("house-rules.md", assetDirectory)));
    }

    [Fact]
    public void MissingLooseFileServesTheEmbeddedCopyAndReportsIt()
    {
        var messages = new List<string>();
        var previousSink = InstructionAssets.DiagnosticSink;
        InstructionAssets.DiagnosticSink = message =>
        {
            lock (messages)
            {
                messages.Add(message);
            }
        };
        try
        {
            // A path that cannot exist — nothing is created, so no cleanup can be skipped.
            var absentDirectory = Path.Combine(
                Path.GetTempPath(), "vino-absent-instructions-" + Guid.NewGuid().ToString("n"));

            var text = InstructionAssets.LoadOrFallback("house-rules.md", absentDirectory);

            Assert.Equal(Normalize(InstructionAssets.ReadEmbedded("house-rules.md")), Normalize(text));
            lock (messages)
            {
                Assert.Contains(messages, message =>
                    message.Contains("house-rules.md", StringComparison.Ordinal) &&
                    message.Contains("missing", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            InstructionAssets.DiagnosticSink = previousSink;
        }
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).TrimEnd();

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "assets", "instructions", "house-rules.md")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root (assets/instructions).");
    }
}
