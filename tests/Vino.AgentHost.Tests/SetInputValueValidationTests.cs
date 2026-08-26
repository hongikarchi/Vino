using System.Text.Json;
using Vino.AgentHost.Api;
using Vino.BridgeContract;
using Vino.Contracts;

namespace Vino.AgentHost.Tests;

/// <summary>
/// Submit-time validation for <c>canvas.setInputValue</c> — the operation that closed the largest
/// tool gap of its class. A Value List's items, a Boolean Toggle, a Panel's text and a Button's
/// expressions were all things a person could set and the agent could not, so the agent asked the
/// user to do it by hand (measured across the 07-21..08-26 corpus, still happening on 08-25).
///
/// <para>
/// Every rejection here is raised at SUBMIT, before the operation is dispatched, so a malformed
/// payload can never leave a half-set control on the user's canvas.
/// </para>
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class SetInputValueValidationTests
{
    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);

    /// <summary>
    /// Submits one setInputValue operation and returns the rejection message, or null when the
    /// submit was accepted. Validation runs before any bridge traffic, so no responder is needed.
    /// </summary>
    private static async Task<string?> SubmitAsync(object extra)
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Input value"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            $"input-value-{Guid.NewGuid():N}.json",
            new
            {
                bridgeOperation = "canvas.setInputValue",
                arguments = Arguments(harness.CanvasObjectId, extra),
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "set-input",
                OperationKind.SetInputValue,
                AdapterOwner.Canvas,
                [],
                [resource],
                Reversible: true,
                artifact),
            [new ResourceExpectation(resource, "value-fingerprint")]);

        try
        {
            await harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, $"input-value-{Guid.NewGuid():N}"),
                CancellationToken.None);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
    }

    private static JsonElement Submission(ChangeSet changeSet, string snapshotId, string idempotencyKey) =>
        JsonSerializer.SerializeToElement(
            new
            {
                changeSet,
                expectedSnapshotId = snapshotId,
                idempotencyKey,
                summary = "set an input value",
                wait = false,
            },
            BridgeProtocol.JsonOptions);

    private static object Arguments(Guid objectId, object extra)
    {
        var values = new Dictionary<string, object?>
        {
            ["operationId"] = "set-input",
            ["objectId"] = objectId,
            ["expectedFingerprint"] = "value-fingerprint",
        };
        foreach (var property in extra.GetType().GetProperties())
        {
            values[property.Name] = property.GetValue(extra);
        }
        return values;
    }

    [Fact]
    public async Task AcceptsEachKindWithItsOwnField()
    {
        Assert.Null(await SubmitAsync(new { kind = "booleanToggle", toggle = true }));
        Assert.Null(await SubmitAsync(new { kind = "panel", text = "note" }));
        Assert.Null(await SubmitAsync(new { kind = "button", expressionNormal = "false" }));
        Assert.Null(await SubmitAsync(new
        {
            kind = "valueList",
            items = new[]
            {
                new { name = "update", expression = "\"replace\"" },
                new { name = "overlap", expression = "\"append\"" },
            },
        }));
        // Re-selecting without rewriting the list is legal.
        Assert.Null(await SubmitAsync(new { kind = "valueList", selectedIndex = 1 }));
    }

    [Theory]
    [InlineData("booleanToggle", "toggle")]
    [InlineData("panel", "text")]
    [InlineData("button", "expressionNormal or expressionPressed")]
    public async Task RejectsAKindThatCarriesNoValue(string kind, string missing)
    {
        var message = await SubmitAsync(new { kind });

        // The message must name the field, or the model has to guess which one it forgot.
        Assert.NotNull(message);
        Assert.Contains(missing, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAValueListThatSetsNeitherItemsNorSelection()
    {
        var message = await SubmitAsync(new { kind = "valueList" });

        Assert.NotNull(message);
        Assert.Contains("items or selectedIndex", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAnEmptyValueList()
    {
        // A Value List with no items emits nothing — the silent-empty state this operation exists to
        // end. Catch it at submit rather than committing a control that produces no data.
        var message = await SubmitAsync(new { kind = "valueList", items = Array.Empty<object>() });

        Assert.NotNull(message);
        Assert.Contains("emits nothing", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAValueListItemWithNoNameOrExpression()
    {
        var message = await SubmitAsync(new
        {
            kind = "valueList",
            items = new[] { new { name = "update", expression = "  " } },
        });

        Assert.NotNull(message);
        Assert.Contains("empty", message, StringComparison.Ordinal);
    }
}
