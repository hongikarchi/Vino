namespace Vino.AgentHost.Tests;

/// <summary>
/// Serializes the test classes that redirect <c>CLAUDE_CONFIG_DIR</c> to their own temporary
/// directory. The variable is process-wide, so two such classes running in parallel — xUnit's
/// default across collections — leave one of them probing the other's directory: the conversation
/// JSONL it just wrote appears missing and the client picks <c>--session-id</c> over
/// <c>--resume</c>. Sharing one collection makes them run one after another instead.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ClaudeConfigDirCollection
{
    public const string Name = "claude-config-dir";
}
