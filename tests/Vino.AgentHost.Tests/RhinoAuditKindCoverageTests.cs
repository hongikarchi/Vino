using System.Text.Json;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Hosting;
using Vino.CanvasSceneAdapter;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The audit-kind vocabulary is maintained by hand in several model-facing texts. The tool-spec
/// enum and the adapter's unknown-kind error already share RhinoAuditKinds.All; these tests tie
/// the remaining hand-written surfaces (house rules, the rhino_audit tool description) to the
/// same canonical list, so adding a kind in the adapter switch but forgetting an instruction
/// site fails the build instead of silently hiding the kind from the model.
/// </summary>
public sealed class RhinoAuditKindCoverageTests
{
    [Fact]
    public void EveryAuditKindAppearsInTheHouseRules()
    {
        foreach (var kind in RhinoAuditKinds.All)
        {
            Assert.Contains(kind, HouseRules.DefaultText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryAuditKindAppearsInTheRhinoAuditToolSpec()
    {
        // The whole tool list, serialized the way the provider sees it: covers both the enum
        // (built from RhinoAuditKinds.All) and the prose description naming each kind.
        var serialized = JsonSerializer.Serialize(DynamicToolSpecs.Create());
        foreach (var kind in RhinoAuditKinds.All)
        {
            var occurrences = CountOccurrences(serialized, kind);
            Assert.True(
                occurrences >= 2,
                $"Audit kind '{kind}' appears {occurrences}x in the tool specs — expected it in " +
                "both the rhino_audit enum and its description prose.");
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
