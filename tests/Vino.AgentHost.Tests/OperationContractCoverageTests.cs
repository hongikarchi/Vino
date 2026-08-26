using Vino.Contracts;

namespace Vino.AgentHost.Tests;

/// <summary>
/// docs/operation-contract.md is the developer-side statement of the write contract and has no
/// mechanical tie to the code. Stale entries have misled sessions before: a required field the doc
/// omitted produced waves of rejected jobs, and predicate kinds the doc did not list were treated
/// as unsupported. These tests pin the enumerable surface — every operation and predicate kind must
/// at least appear in the document (reserved values included: the doc declares them reserved).
/// Prose accuracy stays a review/audit concern; this only guards existence drift.
/// </summary>
public sealed class OperationContractCoverageTests
{
    [Fact]
    public void EveryOperationKindAppearsInContractDocument()
    {
        AssertKindsAppear(Enum.GetNames<OperationKind>());
    }

    [Fact]
    public void EveryPredicateKindAppearsInContractDocument()
    {
        AssertKindsAppear(Enum.GetNames<PredicateKind>());
    }

    private static void AssertKindsAppear(IEnumerable<string> enumNames)
    {
        var document = File.ReadAllText(ContractDocumentPath());
        var missing = enumNames
            .Where(name => !document.Contains(ToCamelCase(name), StringComparison.Ordinal) &&
                           !document.Contains(name, StringComparison.Ordinal))
            .ToList();
        Assert.True(missing.Count == 0,
            "docs/operation-contract.md does not mention: " + string.Join(", ", missing) +
            ". Document the new kind (or list it as reserved) in the same change that adds it.");
    }

    private static string ToCamelCase(string name) =>
        char.ToLowerInvariant(name[0]) + name[1..];

    private static string ContractDocumentPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "docs", "operation-contract.md")))
        {
            directory = directory.Parent;
        }
        return directory is not null
            ? Path.Combine(directory.FullName, "docs", "operation-contract.md")
            : throw new DirectoryNotFoundException("Could not locate the repository root (docs/operation-contract.md).");
    }
}
