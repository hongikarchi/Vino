using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using GPTino.AgentHost.Api;
using Microsoft.Extensions.Logging;

namespace GPTino.AgentHost.Data;

/// <summary>
/// Turns image URLs pasted into a chat message into local attachments. The AgentHost is NOT the
/// sandboxed Codex process, so it can reach the network: it downloads the referenced images here
/// and hands them to <see cref="AttachmentStore"/>, after which they ride the same guaranteed
/// localImage input channel as a file the user attached with the paperclip. This makes "paste an
/// image link" deterministic instead of depending on whether Codex decides to shell out and fetch
/// it (which the base instructions actively discourage).
/// </summary>
public sealed class ImageUrlAttachmentFetcher
{
    private const int MaxUrlsPerMessage = AttachmentStore.MaxAttachmentsPerMessage;
    private const long MaxTotalBytes = AttachmentStore.MaxTotalDecodedBytes;

    // Only URLs that visibly name an image file are fetched — we never speculatively download every
    // link a user pastes. An optional query string (CDN cache tokens, sizing params) is tolerated.
    private static readonly Regex ImageUrlPattern = new(
        @"https?://[^\s<>""')\]]+\.(?:png|jpe?g|webp|gif)(?:\?[^\s<>""')\]]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient Http = CreateClient();

    private readonly AttachmentStore _attachments;
    private readonly ILogger<ImageUrlAttachmentFetcher>? _logger;

    public ImageUrlAttachmentFetcher(AttachmentStore attachments, ILogger<ImageUrlAttachmentFetcher>? logger = null)
    {
        _attachments = attachments;
        _logger = logger;
    }

    /// <summary>
    /// Extracts the distinct, fetchable image URLs from a message (http/https only, private and
    /// loopback hosts rejected, capped at the per-message attachment limit). Pure and deterministic
    /// so it can be unit-tested without touching the network.
    /// </summary>
    public static IReadOnlyList<string> ExtractImageUrls(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Array.Empty<string>();
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new List<string>();
        foreach (Match match in ImageUrlPattern.Matches(content))
        {
            var url = match.Value;
            if (IsFetchableHttpUrl(url) && seen.Add(url))
            {
                urls.Add(url);
                if (urls.Count >= MaxUrlsPerMessage)
                {
                    break;
                }
            }
        }
        return urls;
    }

    /// <summary>
    /// Best-effort: downloads the image URLs found in <paramref name="content"/> and persists them
    /// as attachments for this session. A URL that fails to fetch, is not an accepted image type, or
    /// would exceed the size budget is skipped silently — a bad link never fails the user's turn.
    /// </summary>
    public async Task<IReadOnlyList<SavedAttachment>> FetchAsync(
        Guid sessionId,
        string? content,
        CancellationToken cancellationToken)
    {
        var urls = ExtractImageUrls(content);
        if (urls.Count == 0)
        {
            return Array.Empty<SavedAttachment>();
        }
        var incoming = new List<IncomingAttachment>();
        long total = 0;
        foreach (var url in urls)
        {
            try
            {
                using var response = await Http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }
                var mediaType = ResolveImageMediaType(response.Content.Headers.ContentType?.MediaType, url);
                if (mediaType is null)
                {
                    continue;
                }
                if (response.Content.Headers.ContentLength is > MaxTotalBytes)
                {
                    continue;
                }
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length == 0 || total + bytes.Length > MaxTotalBytes)
                {
                    continue;
                }
                // Trust the bytes, not the .png in the URL or a spoofable Content-Type: a link that
                // served HTML or arbitrary bytes (e.g. from an internal service) must not be stored and
                // shown to the model as an image.
                if (!IsRecognizedImage(bytes))
                {
                    continue;
                }
                total += bytes.Length;
                incoming.Add(new IncomingAttachment(FileNameFor(url, mediaType), mediaType, Convert.ToBase64String(bytes)));
            }
            catch (Exception exception)
            {
                _logger?.LogDebug(exception, "Skipped unfetchable image URL {Url}", url);
            }
        }
        if (incoming.Count == 0)
        {
            return Array.Empty<SavedAttachment>();
        }
        try
        {
            return await _attachments.SaveAsync(sessionId, incoming, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Persisting fetched URL images failed; continuing without them.");
            return Array.Empty<SavedAttachment>();
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            // The literal-host check in IsFetchableHttpUrl cannot see where a HOSTNAME actually
            // resolves, nor where a redirect goes. Validate the real address at connect time — for the
            // first request AND every redirect hop — and connect only to a vetted public address, so a
            // name that resolves into loopback/private space (DNS rebinding) or a 302 to an internal IP
            // is refused before any bytes flow.
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var resolved = IPAddress.TryParse(host, out var literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                var allowed = resolved.Where(address => !IsBlockedAddress(address)).ToArray();
                if (allowed.Length == 0)
                {
                    throw new IOException($"Refusing to connect to a non-public address for host '{host}'.");
                }
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    // Connect to the vetted IPs directly so the socket cannot re-resolve to a blocked one.
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxTotalBytes + 1,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GPTino/0.1 (+image-attachment-fetch)");
        return client;
    }

    /// <summary>
    /// True for any address a pasted link must never reach: loopback, RFC1918 private space, the
    /// 169.254/16 link-local range (incl. the cloud metadata endpoint), 0.0.0.0/8, and the IPv6
    /// equivalents. Checked against the RESOLVED address at connect time, so it also stops a hostname
    /// that resolves into that space and a redirect that lands there.
    /// </summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
        {
            return true;
        }
        var ipv4 = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (ipv4.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = ipv4.GetAddressBytes();
            return octets[0] switch
            {
                0 => true,
                10 => true,
                127 => true,
                169 when octets[1] == 254 => true,
                172 when octets[1] >= 16 && octets[1] <= 31 => true,
                192 when octets[1] == 168 => true,
                _ => false,
            };
        }
        return false;
    }

    /// <summary>
    /// True when the bytes begin with a recognized image signature (PNG/JPEG/GIF/WEBP). Guards
    /// against a link that ends in .png but serves HTML or arbitrary bytes from an internal service —
    /// neither the URL extension nor the Content-Type is trusted for what is actually stored and shown.
    /// </summary>
    public static bool IsRecognizedImage(ReadOnlySpan<byte> bytes) =>
        (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) ||
        (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) ||
        (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38) ||
        (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50);

    private static string? ResolveImageMediaType(string? headerMediaType, string url)
    {
        var normalized = headerMediaType?.Trim().ToLowerInvariant();
        if (normalized is "image/png" or "image/jpeg" or "image/webp" or "image/gif")
        {
            return normalized;
        }
        // Fall back to the URL extension when the server sent no / a generic content type.
        var extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null,
        };
    }

    private static string FileNameFor(string url, string mediaType)
    {
        var candidate = Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var extension = mediaType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".img",
            };
            candidate = $"linked-image{extension}";
        }
        return candidate;
    }

    private static bool IsFetchableHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // Fast pre-filter for a literal-IP host (loopback, link-local incl. the cloud metadata
        // endpoint, or RFC1918 private space). A HOSTNAME is not resolved here — that check happens
        // at connect time in CreateClient's ConnectCallback, which also covers redirects and rebinding.
        if (IPAddress.TryParse(host, out var ip) && IsBlockedAddress(ip))
        {
            return false;
        }
        return true;
    }
}
