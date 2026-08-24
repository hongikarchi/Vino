using System.Security.Cryptography;
using System.Text;

namespace Vino.AgentHost.Security;

/// <summary>
/// Per-session secrets for the loopback /mcp endpoint. A Claude CLI session presents its secret
/// as the X-Vino-Secret header on EVERY request; the endpoint maps it back to the owning session
/// and derives the dispatch threadId server-side — the model never supplies (and can never forge)
/// another session's identity.
///
/// Security posture mirrors PanelBootstrapNonceStore (ReadySignalService.cs): only SHA-256 hashes
/// are retained, comparison is fixed-time, and retired hashes are zeroed. Unlike the nonce store,
/// entries are LONG-LIVED (session lifetime, one per session, re-issued on every spawn) and there
/// is no expiry — the store dies with the host process, and every respawn rewrites mcp.json with
/// a freshly issued secret anyway.
/// </summary>
public sealed class McpSessionSecretStore
{
    private const int SecretHexLength = 64; // 32 random bytes, hex-encoded

    private readonly object _gate = new();
    private readonly Dictionary<Guid, byte[]> _hashBySession = [];

    /// <summary>
    /// Issues (or rotates) the secret for one session. The returned plaintext goes into that
    /// session's mcp.json and is never stored here; any previously issued secret for the session
    /// stops resolving immediately.
    /// </summary>
    public string Issue(Guid sessionId)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(secret);
        lock (_gate)
        {
            if (_hashBySession.TryGetValue(sessionId, out var previous))
            {
                CryptographicOperations.ZeroMemory(previous);
            }
            _hashBySession[sessionId] = hash;
        }
        return secret;
    }

    /// <summary>Fixed-time lookup of the session owning <paramref name="secret"/>.</summary>
    public bool TryResolve(string? secret, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        if (secret is null || secret.Length != SecretHexLength)
        {
            return false;
        }
        var suppliedHash = Hash(secret);
        try
        {
            lock (_gate)
            {
                foreach (var (candidate, hash) in _hashBySession)
                {
                    if (CryptographicOperations.FixedTimeEquals(suppliedHash, hash))
                    {
                        sessionId = candidate;
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

    /// <summary>Retires a session's secret (session deleted / backend stopped).</summary>
    public void Revoke(Guid sessionId)
    {
        lock (_gate)
        {
            if (_hashBySession.Remove(sessionId, out var hash))
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
    }

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
