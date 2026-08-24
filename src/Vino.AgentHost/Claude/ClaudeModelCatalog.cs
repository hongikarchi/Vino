using Vino.AgentHost.Api;
using Vino.AgentHost.Codex;

namespace Vino.AgentHost.Claude;

/// <summary>
/// The Claude backend's model catalog. Static by necessity — the CLI has no model/list RPC — and
/// measured, not guessed: ids, the [1m] long-context variant, and the per-model effort sets come
/// from the 2026-08-19 spike (haiku hard-rejects xhigh/max; the 5-generation takes low..max).
/// "ultra" never appears here: it is a codex-only rung, and ModelSelector.ClampEffort already
/// walks an unsupported request down to the model's highest advertised effort (max).
/// claude-fable-5 is the default on bench evidence: T5/T6 form quality won on
/// `--model fable --effort xhigh` (fable3-20260821: F 35 vs V 22).
/// </summary>
public sealed class ClaudeModelCatalog : IModelCatalog
{
    private static readonly string[] FullEffortLadder = ["low", "medium", "high", "xhigh", "max"];
    private static readonly string[] HaikuEffortLadder = ["low", "medium", "high"];

    private static readonly IReadOnlyList<ModelView> Models =
    [
        new ModelView(
            "claude-fable-5",
            "claude-fable-5",
            "Claude Fable 5",
            "Strongest form-making and reasoning (bench winner on facade/shell tasks)",
            IsDefault: true,
            FullEffortLadder,
            Provider: AgentBackends.Claude),
        new ModelView(
            "claude-fable-5[1m]",
            "claude-fable-5[1m]",
            "Claude Fable 5 (1M context)",
            "Fable with the long-context window for very large definitions",
            IsDefault: false,
            FullEffortLadder,
            Provider: AgentBackends.Claude),
        new ModelView(
            "claude-opus-5",
            "claude-opus-5",
            "Claude Opus 5",
            "Frontier reasoning tier below Fable",
            IsDefault: false,
            FullEffortLadder,
            Provider: AgentBackends.Claude),
        new ModelView(
            "claude-sonnet-5",
            "claude-sonnet-5",
            "Claude Sonnet 5",
            "Balanced speed and capability",
            IsDefault: false,
            FullEffortLadder,
            Provider: AgentBackends.Claude),
        new ModelView(
            "claude-haiku-4-5",
            "claude-haiku-4-5",
            "Claude Haiku 4.5",
            "Fast reads and simple typed operations",
            IsDefault: false,
            HaikuEffortLadder,
            Provider: AgentBackends.Claude),
    ];

    public Task<IReadOnlyList<ModelView>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Models);
}
