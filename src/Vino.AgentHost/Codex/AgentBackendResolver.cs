using Vino.AgentHost.Api;

namespace Vino.AgentHost.Codex;

/// <summary>
/// One registered agent backend: the session client that drives turns, the catalog that lists its
/// models, and a backend-private ModelSelector (per-backend instance = per-backend catalog cache
/// for free — ModelSelector itself stays backend-unaware).
/// </summary>
public interface IAgentBackend
{
    /// <summary>Backend id (AgentBackends vocabulary, e.g. "codex").</summary>
    string Id { get; }

    IAgentSessionClient Client { get; }

    IModelCatalog Catalog { get; }

    ModelSelector Models { get; }
}

/// <summary>
/// Resolves a session's stored backend id to its registered backend. The orchestrator resolves per
/// turn (sessions are fixed to one backend at creation, but the orchestrator itself serves all of
/// them); /models aggregates over <see cref="All"/>.
/// </summary>
public interface IAgentBackendResolver
{
    /// <summary>
    /// Null/blank resolve to the default backend (codex). Unknown ids throw — the caller sits on
    /// the turn path where the existing unhandled-failure handling turns this into a Failed
    /// session with a system message, which is exactly the honest outcome for a session whose
    /// backend is not installed in this build.
    /// </summary>
    IAgentBackend Resolve(string? backendId);

    bool TryResolve(string? backendId, out IAgentBackend backend);

    /// <summary>Registered backends in registration order (codex first).</summary>
    IReadOnlyList<IAgentBackend> All { get; }
}

public sealed class AgentBackend(
    string id,
    IAgentSessionClient client,
    IModelCatalog catalog,
    ModelSelector models) : IAgentBackend
{
    public string Id { get; } = id;
    public IAgentSessionClient Client { get; } = client;
    public IModelCatalog Catalog { get; } = catalog;
    public ModelSelector Models { get; } = models;
}

public sealed class AgentBackendRegistry : IAgentBackendResolver
{
    private readonly Dictionary<string, IAgentBackend> _byId;
    private readonly IReadOnlyList<IAgentBackend> _all;

    public AgentBackendRegistry(IEnumerable<IAgentBackend> backends)
    {
        _all = backends.ToArray();
        if (_all.Count == 0)
        {
            throw new InvalidOperationException("At least one agent backend must be registered.");
        }
        _byId = new Dictionary<string, IAgentBackend>(StringComparer.OrdinalIgnoreCase);
        foreach (var backend in _all)
        {
            if (!_byId.TryAdd(backend.Id, backend))
            {
                throw new InvalidOperationException($"Duplicate agent backend id '{backend.Id}'.");
            }
        }
    }

    public IReadOnlyList<IAgentBackend> All => _all;

    public IAgentBackend Resolve(string? backendId)
    {
        if (TryResolve(backendId, out var backend))
        {
            return backend;
        }
        throw new InvalidOperationException(
            $"Agent backend '{backendId}' is not registered in this build " +
            $"(registered: {string.Join(", ", _all.Select(entry => entry.Id))}).");
    }

    public bool TryResolve(string? backendId, out IAgentBackend backend)
    {
        var id = string.IsNullOrWhiteSpace(backendId) ? AgentBackends.Codex : backendId.Trim();
        if (_byId.TryGetValue(id, out var found))
        {
            backend = found;
            return true;
        }
        backend = _all[0];
        return false;
    }
}
