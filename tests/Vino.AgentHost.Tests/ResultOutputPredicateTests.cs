using System.Text.Json;
using Vino.AgentHost.Runtime;
using Vino.Contracts;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The W2 server-side enforcement: a createComponent that declares a resultOutput gets an
/// outputCountInRange "&gt;=1" predicate injected, so an empty producing change fails instead of
/// committing green. A norm alone (verified live) was ignored by the model, so intent is forced
/// through the required resultOutput field and turned into a predicate here.
/// </summary>
public sealed class ResultOutputPredicateTests
{
    private static readonly Guid ObjectId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void CreateComponentWithResultOutputAttachesOutputCountPredicate()
    {
        var args = Args($$"""{"objectId":"{{ObjectId}}","resultOutput":"Points"}""");

        var predicate = LiveDocumentBackend.BuildResultOutputPredicate(
            OperationKind.CreateComponent, args, "op-1");

        Assert.NotNull(predicate);
        Assert.Equal(PredicateKind.OutputCountInRange, predicate!.Kind);
        Assert.Equal("Points:1:*", predicate.ExpectedValue);
        Assert.NotNull(predicate.Resource);
        Assert.Equal(ResourceKind.GrasshopperComponent, predicate.Resource!.Kind);
        Assert.Equal(ObjectId.ToString("D"), predicate.Resource.Id);
        // The injected spec must actually parse as a >=1 range the verifier can evaluate.
        Assert.True(LiveDocumentBackend.TryParseOutputCountRange(predicate.ExpectedValue, out var range));
        Assert.Equal("Points", range.OutputName);
        Assert.Equal(1, range.Min);
        Assert.Equal(int.MaxValue, range.Max);
    }

    [Theory]
    [InlineData("""{"objectId":"bbbbbbbb-0000-0000-0000-000000000002","resultOutput":null}""")] // scaffolding
    [InlineData("""{"objectId":"bbbbbbbb-0000-0000-0000-000000000002"}""")]                     // absent
    [InlineData("""{"objectId":"bbbbbbbb-0000-0000-0000-000000000002","resultOutput":""}""")]   // blank
    [InlineData("""{"objectId":"bbbbbbbb-0000-0000-0000-000000000002","resultOutput":"  "}""")] // whitespace
    public void NoPredicateWhenNoOutputIsClaimed(string json)
    {
        Assert.Null(LiveDocumentBackend.BuildResultOutputPredicate(
            OperationKind.CreateComponent, Args(json), "op-1"));
    }

    [Fact]
    public void NonCreateComponentNeverAttaches()
    {
        // Same payload shape, but a non-create op must never grow an output predicate.
        var args = Args($$"""{"objectId":"{{ObjectId}}","resultOutput":"Points"}""");
        Assert.Null(LiveDocumentBackend.BuildResultOutputPredicate(
            OperationKind.SetLayout, args, "op-1"));
    }

    [Fact]
    public void MissingOrUnparseableObjectIdYieldsNoPredicate()
    {
        Assert.Null(LiveDocumentBackend.BuildResultOutputPredicate(
            OperationKind.CreateComponent, Args("""{"resultOutput":"Points"}"""), "op-1"));
        Assert.Null(LiveDocumentBackend.BuildResultOutputPredicate(
            OperationKind.CreateComponent, Args("""{"objectId":"not-a-guid","resultOutput":"Points"}"""), "op-1"));
    }

    [Fact]
    public void ReplaceComponentIoAttachesThePredicateOnTheNewComponent()
    {
        // A replacement is a producing create in disguise: the claimed output lives on the NEW
        // component, never on the replaced one (which is deleted by the same op).
        var newComponentId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        var args = Args($$"""
            {"componentId":"{{ObjectId}}","newComponentId":"{{newComponentId}}","resultOutput":"Panels"}
            """);

        var predicate = LiveDocumentBackend.BuildResultOutputPredicate(
            OperationKind.ReplaceComponentIo, args, "op-replace");

        Assert.NotNull(predicate);
        Assert.Equal(PredicateKind.OutputCountInRange, predicate!.Kind);
        Assert.Equal("Panels:1:*", predicate.ExpectedValue);
        Assert.Equal(newComponentId.ToString("D"), predicate.Resource!.Id);
    }

    [Fact]
    public void ReplaceComponentIoWithNullResultOutputAttachesNothing()
    {
        var args = Args($$"""
            {"componentId":"{{ObjectId}}","newComponentId":"{{Guid.NewGuid()}}","resultOutput":null}
            """);
        Assert.Null(LiveDocumentBackend.BuildResultOutputPredicate(
            OperationKind.ReplaceComponentIo, args, "op-replace"));
    }
}
