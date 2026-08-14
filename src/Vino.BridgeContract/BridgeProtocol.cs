using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vino.BridgeContract;

public static class BridgeProtocol
{
    // v3: StableTargetKey became path-free so a Save As / rename no longer changes the document target
    // identity. Plugin and AgentHost ship together in one package, so this bump only guards against a
    // stale AgentHost surviving an upgrade; both ends must compute the key identically.
    // v4: CanvasOutputParameterInspection gained SampleValues. JsonOptions disallow unmapped members,
    // so any payload-shape change MUST bump this version or skew fails as an opaque JsonException.
    // v5: SelectionChangedEvent gained GrasshopperObjects (canvas selection discovery hint).
    // v6: CanvasObjectState gained per-domain Structure/Layout/Value fingerprints so a component
    //     move no longer invalidates pending value writes.
    // v7: CanvasObjectState gained BoundsOrigin (bounds top-left) feeding deterministic component
    //     auto-placement. It is EXCLUDED from every fingerprint, so it never churns the revision;
    //     the bump only guards the wire shape against a stale AgentHost, as SampleValues did in v4.
    // v11: DocumentRuntime's Grasshopper half is nullable — a saved Rhino document with no .gh open
    //      is a complete target (document work needs no canvas). rhinoLayerTable resources are now
    //      addressed by the Rhino-scoped ProjectId instead of the bound Grasshopper document id.
    // v12: rhino.structuralExtract read operation (structural member axis extraction). Purely
    //      additive, but a new host calling an old plugin would fail mid-feature with an unknown
    //      operation instead of at connect — the bump keeps mixed installs failing loudly.
    // v13: adapter-owner rename (wireify → script, cordycepsCanvas → canvas, cordycepsRhino →
    //      rhinoScene) changed the BridgeAdapterOwner wire strings; a mixed install would
    //      misroute every operation, so it must fail at connect.
    // v14: canvas.catalog GUID-lookup semantics became load-bearing — the AgentHost's
    //      canvas.create preflight rejects a create when a GUID catalog query returns an
    //      explicit empty Matches list, which only a plugin with the GUID catalog branch can
    //      answer honestly. An older plugin fuzzy-matches the GUID string to zero results
    //      (SUCCESS + empty Matches), which would false-reject every valid create; mixed
    //      installs must fail loudly at connect. AgentHost and plugin ship in one Yak package.
    // v15: layer curation wire shapes — RhinoLayerSummary and UpdateRhinoLayerRequest gained
    //      userText, RhinoAuditFinding gained layerFacts, and audit kind layerSemantics was
    //      added. Additive, but Disallow-unmapped means an old host throws on the new summary
    //      field in EVERY rhino_layers response (not just curation calls), so mixed installs
    //      must fail loudly at connect instead.
    // v16: UpdateRhinoLayerRequest gained renderMaterial (the fill-empty-only plaster template).
    //      Same Disallow-unmapped reasoning as v15 — an old plugin would throw an opaque
    //      JsonException mid-ChangeSet instead of refusing the mixed install at connect.
    // v17: CreateCanvasObjectRequest gained resultOutput — the output socket a createComponent claims
    //      to produce, from which the server auto-attaches outputCountInRange ">=1" (an empty
    //      producing change fails instead of committing green). Disallow-unmapped: an old plugin
    //      throws on the new field, so mixed installs must fail loudly at connect.
    // v18: python.replaceSchema (socket-removal-by-replacement: create a fresh component, rebuild its
    //      sockets from the declared list, copy/set source, rewire the original's connections by
    //      socket name, delete the original, single solve — all one atomic adapter op), and
    //      SetWireRequest gained deferSolve (server-batched rewires solve ONCE at the end of the
    //      run instead of per wire). Disallow-unmapped: an old plugin throws on both, so mixed
    //      installs must fail loudly at connect.
    // v19: EnsureRhinoLayerRequest.argbColor became nullable. A missing colour used to deserialize
    //      to 0 and repaint an existing layer transparent black; null now means "leave the colour
    //      alone". The bytes on the wire are unchanged but their MEANING is not (omitted colour:
    //      repaint-0 vs keep), so mixed installs must fail loudly at connect.
    // v20: focus honesty. FocusObjectsRequest gained ownerToken (a stale surface's automatic
    //      restore is refused once another surface owns the isolation); isolate/lock/restore now
    //      travel as Write access under the document write gate (they mutate visibility attributes
    //      and therefore object fingerprints) while select became a pure read that REPORTS hidden/
    //      locked targets instead of force-showing them; isolate/lock record the targets' own prior
    //      state so restore puts everything back, inside a Rhino undo record. Disallow-unmapped:
    //      an old plugin throws on ownerToken, so mixed installs must fail loudly at connect.
    // v21: Vino rename. The pipe name prefix (vino-), HMAC domain-separation strings
    //      (vino-bridge/*), the VINO_* environment contract, and the vino_v1 dynamic-tool
    //      namespace all changed with the product rename. The bytes of the frame schema are
    //      unchanged, but a pre-rename plugin or host speaks the old identifiers end-to-end,
    //      so mixed installs must fail loudly at connect instead of half-working.
    public const int Version = 21;

    public const int DefaultMaximumFrameBytes = 8 * 1024 * 1024;

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        // Registered before the general enum converter so it wins for AdapterOwner: stored
        // ChangeSets (live-jobs.db) and pre-upgrade sessions may still carry legacy owner names.
        options.Converters.Add(new Vino.Contracts.LegacyAdapterOwnerConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public enum BridgeMessageKind
{
    Hello,
    Challenge,
    Authenticate,
    Authenticated,
    Request,
    Response,
    Event,
    Error,
    Shutdown,
}

public sealed record BridgeFrame
{
    public required int ProtocolVersion { get; init; }

    public required Guid MessageId { get; init; }

    public Guid? CorrelationId { get; init; }

    public required BridgeMessageKind Kind { get; init; }

    public DocumentTarget? Target { get; init; }

    public required string PayloadType { get; init; }

    public required JsonElement Payload { get; init; }

    public string? ErrorCode { get; init; }

    public static BridgeFrame Create<T>(
        BridgeMessageKind kind,
        string payloadType,
        T payload,
        DocumentTarget? target = null,
        Guid? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadType);

        return new BridgeFrame
        {
            ProtocolVersion = BridgeProtocol.Version,
            MessageId = Guid.NewGuid(),
            CorrelationId = correlationId,
            Kind = kind,
            Target = target,
            PayloadType = payloadType,
            Payload = JsonSerializer.SerializeToElement(payload, BridgeProtocol.JsonOptions),
        };
    }

    public T DeserializePayload<T>() =>
        Payload.Deserialize<T>(BridgeProtocol.JsonOptions)
        ?? throw new JsonException($"Payload '{PayloadType}' deserialized to null.");

    public void Validate(bool requireTargetForApplicationMessage = true)
    {
        if (ProtocolVersion != BridgeProtocol.Version)
        {
            throw new BridgeProtocolException(
                "protocol_version",
                $"Unsupported bridge protocol {ProtocolVersion}; expected {BridgeProtocol.Version}.");
        }

        if (MessageId == Guid.Empty)
        {
            throw new BridgeProtocolException("message_id", "MessageId is required.");
        }

        if (string.IsNullOrWhiteSpace(PayloadType))
        {
            throw new BridgeProtocolException("payload_type", "PayloadType is required.");
        }

        var isApplicationMessage = Kind is BridgeMessageKind.Request or
            BridgeMessageKind.Response or BridgeMessageKind.Event;
        if (requireTargetForApplicationMessage && isApplicationMessage && Target is null)
        {
            throw new BridgeProtocolException(
                "target_required",
                "Application bridge messages must carry an explicit document target.");
        }

        Target?.Validate();
    }
}

public sealed class BridgeProtocolException : IOException
{
    public BridgeProtocolException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
