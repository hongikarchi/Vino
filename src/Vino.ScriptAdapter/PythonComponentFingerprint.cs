using System.Security.Cryptography;
using System.Text.Json;
using Vino.BridgeContract;

namespace Vino.ScriptAdapter;

/// <summary>
/// Produces the bridge concurrency fingerprint for the AUTHORED state of a Python component:
/// source, sockets, typing, runtime — deliberately NOT its runtime messages. The record-shaped
/// payload has stable property ordering and contains no dictionaries, so the bridge JSON
/// representation is deterministic for a given state.
///
/// Runtime messages were originally part of the hash and were the largest remaining source of
/// false "drifted" refusals (constraint audit 2026-08-19: 20 of 26 post-fix auto-drifted blocks):
/// a solve triggered by the session's OWN execute — or by a sibling session's slider — changes the
/// warnings without touching anything anyone authored, which made the session's own committed
/// write look like a manual edit. Concurrency only needs to detect AUTHORED changes; messages are
/// reported separately in state payloads. States that carried no messages hash identically to the
/// old algorithm; rows recorded while warnings were present are re-baselined once by the
/// resource-ledger fingerprint_algo migration.
/// </summary>
public static class PythonComponentFingerprint
{
    public static string Compute(PythonComponentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var authored = state with { RuntimeMessages = Array.Empty<ComponentRuntimeMessage>() };
        var canonicalJson = JsonSerializer.SerializeToUtf8Bytes(authored, BridgeProtocol.JsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonicalJson)).ToLowerInvariant();
    }
}
