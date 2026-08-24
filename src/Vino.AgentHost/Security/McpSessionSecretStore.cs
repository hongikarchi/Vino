using System.Security.Cryptography;
using System.Text;

namespace Vino.AgentHost.Security;

/// <summary>
/// Per-conversation secrets for the loopback /mcp endpoint. A Claude CLI child presents its
/// secret as the X-Vino-Secret header on EVERY request; the endpoint maps it back to the owning
/// CONVERSATION (thread) id and resolves the session from that, entirely server-side — the model
/// never supplies (and can never forge) another session's identity. Keying by conversation id
/// rather than session id is deliberate: the visual-review judge runs on a thread that is bound
/// to NO session, so its secret resolves to a conversation no session owns and every tool call is
/// refused — the judge gets captures and a goal, never tools — and two threads of one session
/// (main + judge) can never rotate each other's secrets.
///
/// Security posture mirrors PanelBootstrapNonceStore (ReadySignalService.cs): only SHA-256 hashes
/// are retained, comparison is fixed-time, and retired hashes are zeroed. Unlike the nonce store,
/// entries are LONG-LIVED (thread lifetime, re-issued on every spawn) and there is no expiry —
/// the store dies with the host process, and every respawn rewrites mcp.json with a freshly
/// issued secret anyway.
/// </summary>
public sealed class McpSessionSecretStore
{
    private const int SecretHexLength = 64; // 32 random bytes, hex-encoded

    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _hashByConversation = new(StringComparer.Ordinal);

    /// <summary>
    /// Issues (or rotates) the secret for one conversation. The returned plaintext goes into that
    /// thread's mcp.json and is never stored here; any previously issued secret for the thread
    /// stops resolving immediately.
    /// </summary>
    public string Issue(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(secret);
        lock (_gate)
        {
            if (_hashByConversation.TryGetValue(conversationId, out var previous))
            {
                CryptographicOperations.ZeroMemory(previous);
            }
            _hashByConversation[conversationId] = hash;
        }
        return secret;
    }

    /// <summary>Fixed-time lookup of the conversation owning <paramref name="secret"/>.</summary>
    public bool TryResolve(string? secret, out string conversationId)
    {
        conversationId = string.Empty;
        if (secret is null || secret.Length != SecretHexLength)
        {
            return false;
        }
        var suppliedHash = Hash(secret);
        try
        {
            lock (_gate)
            {
                foreach (var (candidate, hash) in _hashByConversation)
                {
                    if (CryptographicOperations.FixedTimeEquals(suppliedHash, hash))
                    {
                        conversationId = candidate;
                        return true;
                    }
                }
            }
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedHash);
        }
    }

    /// <summary>Retires a conversation's secret (session deleted / thread stopped).</summary>
    public void Revoke(string conversationId)
    {
        lock (_gate)
        {
            if (_hashByConversation.Remove(conversationId, out var hash))
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
    }

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
