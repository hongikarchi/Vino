using System.Text.Json;
using GPTino.BridgeContract;
using GPTino.ScriptAdapter;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// The bridge deserializes operation arguments with Disallow-unmapped, so every field the server's
/// payload contract REQUIRES must exist on the adapter-side request record. replaceComponentIo
/// requires resultOutput; the record initially lacked the member, which made every dispatched
/// python.replaceSchema die in DeserializeArguments before reaching the adapter.
/// </summary>
public sealed class ReplaceSchemaPayloadContractTests
{
    [Fact]
    public void FullRequiredPayloadDeserializesIntoTheAdapterRecord()
    {
        var json = """
            {
              "operationId": "replace-1",
              "componentId": "11111111-1111-1111-1111-111111111111",
              "newComponentId": "22222222-2222-2222-2222-222222222222",
              "inputs": [
                { "parameterId": "00000000-0000-0000-0000-000000000000", "name": "a", "nickName": "a", "typeHint": "float", "access": "item", "optional": false }
              ],
              "outputs": [
                { "parameterId": "00000000-0000-0000-0000-000000000000", "name": "Sum", "nickName": "Sum", "typeHint": "object", "access": "item", "optional": false }
              ],
              "source": null,
              "socketMap": { "old": "a" },
              "resultOutput": "Sum"
            }
            """;

        var request = JsonSerializer.Deserialize<ReplaceParameterSchemaRequest>(
            json, BridgeProtocol.JsonOptions);

        Assert.NotNull(request);
        Assert.Equal("replace-1", request!.OperationId);
        Assert.Equal("Sum", request.ResultOutput);
        Assert.Equal("a", request.SocketMap!["old"]);
        Assert.Single(request.Inputs);
        Assert.Single(request.Outputs);
    }
}
