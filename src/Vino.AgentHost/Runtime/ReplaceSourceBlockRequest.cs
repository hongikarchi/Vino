namespace Vino.AgentHost.Runtime;

/// <summary>
/// The model-facing payload of <c>python.replaceBlock</c> (OperationKind.ReplaceSourceBlock). This
/// operation never crosses the bridge: after validation the server reads the component's current
/// stored source, splices the block via <see cref="CSharpStageMerger.ReplaceBlock"/>, and rewrites
/// the dispatched operation into an ordinary <c>python.setSource</c> carrying the recomposed full
/// text — which is why the record lives here and not in the bridge contract.
/// </summary>
public sealed record ReplaceSourceBlockRequest(
    string OperationId,
    Guid ComponentId,
    // "gptino:auto" (typical) or a concrete stored-source sha256 to assert a specific prior text.
    string ExpectedSourceSha256,
    string BlockId,
    // The block's replacement statements only — no markers, no seams, no meta header.
    string Source,
    bool ExpireSolution);
