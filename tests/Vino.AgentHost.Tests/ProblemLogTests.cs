using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vino.AgentHost.Hosting;
using Vino.AgentHost.Runtime;

namespace Vino.AgentHost.Tests;

public class ProblemLogTests
{
    [Fact]
    public void RecordSnapshotReadAppendsScopeAndSizeFields()
    {
        using var directory = new TestDirectory();
        var options = new AgentHostOptions { DataDirectory = directory.GetPath("data") };
        Directory.CreateDirectory(options.ResolveDataDirectory());
        var log = new ProblemLog(options, NullLogger<ProblemLog>.Instance);
        var sessionId = Guid.NewGuid();

        log.RecordSnapshotRead(
            sessionId,
            meta: true,
            index: true,
            componentsRequested: 3,
            wires: false,
            groups: false,
            canvas: false,
            inspections: 2,
            unchanged: false,
            truncated: true,
            responseBytes: 12_345);

        var lines = File.ReadAllLines(Path.Combine(options.ResolveDataDirectory(), "problem-log.jsonl"));
        var record = JsonDocument.Parse(Assert.Single(lines)).RootElement;
        Assert.Equal("snapshot-read", record.GetProperty("kind").GetString());
        Assert.Equal(sessionId, record.GetProperty("sessionId").GetGuid());
        Assert.True(record.GetProperty("meta").GetBoolean());
        Assert.True(record.GetProperty("index").GetBoolean());
        Assert.Equal(3, record.GetProperty("componentsRequested").GetInt32());
        Assert.False(record.GetProperty("canvas").GetBoolean());
        Assert.Equal(2, record.GetProperty("inspections").GetInt32());
        Assert.False(record.GetProperty("unchanged").GetBoolean());
        Assert.True(record.GetProperty("truncated").GetBoolean());
        Assert.Equal(12_345, record.GetProperty("responseBytes").GetInt32());
    }
}
