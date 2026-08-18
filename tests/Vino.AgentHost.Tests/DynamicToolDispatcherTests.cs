using System.Text.Json;
using Vino.AgentHost.Api;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Data;
using Vino.AgentHost.Hosting;
using Vino.AgentHost.Runtime;
using Vino.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vino.AgentHost.Tests;

public sealed class DynamicToolDispatcherTests
{
    [Fact]
    public async Task ArtifactWriteAndReadRoundTripWithinManagedStorage()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory);
        var session = await BindSessionAsync(store, "thread");

        var written = await dispatcher.DispatchAsync(
            Call("artifact_write", """{"path":"drafts/component.py","content":"print('ok')"}"""),
            CancellationToken.None);
        var read = await dispatcher.DispatchAsync(
            Call("artifact_read", """{"path":"drafts/component.py"}"""),
            CancellationToken.None);

        Assert.True(written.Success, written.Text);
        Assert.True(read.Success, read.Text);
        using var writePayload = JsonDocument.Parse(written.Text);
        using var readPayload = JsonDocument.Parse(read.Text);
        Assert.Equal("drafts/component.py", writePayload.RootElement.GetProperty("path").GetString());
        Assert.False(writePayload.RootElement.GetProperty("liveDocumentChanged").GetBoolean());
        Assert.Equal("drafts/component.py", readPayload.RootElement.GetProperty("path").GetString());
        Assert.Equal("print('ok')", readPayload.RootElement.GetProperty("content").GetString());
        Assert.Equal(
            "print('ok')",
            await File.ReadAllTextAsync(directory.GetPath(
                $"data/artifacts/{session.Id:N}/drafts/component.py")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    public async Task ArtifactWriteRejectsTraversalWithoutCreatingOutsideFile(string path)
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory);
        await BindSessionAsync(store, "thread");
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path, content = "escape" }));

        var result = await dispatcher.DispatchAsync(
            new DynamicToolCall("call", "thread", "turn", "vino_v1", "artifact_write", arguments.RootElement.Clone()),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("escapes managed storage", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(directory.GetPath("outside.txt")));
    }

    [Theory]
    [InlineData(".vino-reserved/jobs/abc/operations/0000.json")]
    [InlineData("drafts/../.vino-reserved/payload.json")]
    public async Task ArtifactWriteRejectsBrokerReservedNamespace(string path)
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory);
        await BindSessionAsync(store, "thread");
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path, content = "{}" }));

        var result = await dispatcher.DispatchAsync(
            new DynamicToolCall("call", "thread", "turn", "vino_v1", "artifact_write", arguments.RootElement.Clone()),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("reserved", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Session roles and plan mode are gone; pause is the only thing between a session and the
    /// broker. This pins that the write path still HAS a gate (the removal must not have taken the
    /// surviving one with it) and that the gate reverses.
    /// </summary>
    [Theory]
    [InlineData("change_submit")]
    [InlineData("arrange_layout")]
    public async Task WriteToolsAreGatedByPauseAlone(string tool)
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Modeling"));
        await store.SetThreadIdAsync(session.Id, "write-thread");

        var allowed = await dispatcher.DispatchAsync(
            Call(tool, """{"summary":"Move point"}""", threadId: "write-thread"),
            CancellationToken.None);
        Assert.True(allowed.Success, allowed.Text);

        await store.SetSessionStateAsync(session.Id, SessionStates.Paused);
        var paused = await dispatcher.DispatchAsync(
            Call(tool, """{"summary":"Move point"}""", threadId: "write-thread"),
            CancellationToken.None);
        Assert.False(paused.Success);
        Assert.Contains("paused", paused.Text, StringComparison.OrdinalIgnoreCase);

        await store.SetSessionStateAsync(session.Id, SessionStates.Idle);
        var resumed = await dispatcher.DispatchAsync(
            Call(tool, """{"summary":"Move point"}""", threadId: "write-thread"),
            CancellationToken.None);
        Assert.True(resumed.Success, resumed.Text);
    }

    [Theory]
    [InlineData("change_submit")]
    [InlineData("arrange_layout")]
    [InlineData("consolidate_stages")]
    public async Task ReviewModeRefusesWriteTools(string tool)
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Reviewer"));
        await store.SetThreadIdAsync(session.Id, "review-thread");
        await store.SetPermissionModeAsync(session.Id, PermissionModes.Review);

        var refused = await dispatcher.DispatchAsync(
            Call(tool, """{"summary":"Move point"}""", threadId: "review-thread"),
            CancellationToken.None);

        Assert.False(refused.Success);
        Assert.Contains("review-only", refused.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backend.SubmitCount);
    }

    [Fact]
    public async Task FullAutoSubmitsWithAutoApproveAndStandardDoesNot()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        await store.SetThreadIdAsync(session.Id, "auto-thread");

        var standard = await dispatcher.DispatchAsync(
            Call("change_submit", """{"summary":"Move point"}""", threadId: "auto-thread"),
            CancellationToken.None);
        Assert.True(standard.Success, standard.Text);
        Assert.Equal(false, backend.SubmittedAutoApprove);

        await store.SetPermissionModeAsync(session.Id, PermissionModes.FullAuto);
        var auto = await dispatcher.DispatchAsync(
            Call("change_submit", """{"summary":"Move point"}""", threadId: "auto-thread"),
            CancellationToken.None);
        Assert.True(auto.Success, auto.Text);
        Assert.Equal(true, backend.SubmittedAutoApprove);
    }

    [Fact]
    public async Task StandingConsentSubmitsWithAutoApprove()
    {
        using var directory = new TestDirectory();
        var standing = new StandingApprovals();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory, standingApprovals: standing);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        await store.SetThreadIdAsync(session.Id, "standing-thread");
        standing.Grant(session.Id);

        var result = await dispatcher.DispatchAsync(
            Call("change_submit", """{"summary":"Move point"}""", threadId: "standing-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        Assert.Equal(true, backend.SubmittedAutoApprove);
    }

    [Fact]
    public async Task FullAutoApprovalRequestAutoGrantsWithoutACard()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        await store.SetThreadIdAsync(session.Id, "grant-thread");
        await store.SetPermissionModeAsync(session.Id, PermissionModes.FullAuto);

        var objectId = Guid.NewGuid();
        var result = await dispatcher.DispatchAsync(
            Call(
                "approval_request",
                $$"""
                {"summary":"Delete two struts","items":[{"id":"i1","label":"Struts",
                 "targets":[{"objectId":"{{objectId}}","fingerprint":"fp-1"}]}]}
                """,
                threadId: "grant-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        Assert.Contains("autoGranted", result.Text, StringComparison.Ordinal);
        var minted = Assert.Single(backend.MintedGrantItems!);
        Assert.Equal(objectId, minted.ObjectId);
        Assert.Equal("fp-1", minted.Fingerprint);
        // The card IS stored — already GRANTED, so nothing interrupts the user — because
        // ComposeApprovalBlock is what carries the grantId into the next turn's input for a
        // model that never echoed (and so never saw) this tool result.
        var reloaded = await store.FindSessionAsync(session.Id);
        Assert.NotNull(reloaded!.ApprovalCard);
        Assert.Contains("\"granted\"", reloaded.ApprovalCard, StringComparison.Ordinal);
        Assert.Contains("test-grant", reloaded.ApprovalCard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullAutoAskUserIsAutoResolved()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        await store.SetThreadIdAsync(session.Id, "ask-thread");
        await store.SetPermissionModeAsync(session.Id, PermissionModes.FullAuto);

        // Observed live (bench A-T2-r1): a full-auto session asked which near-duplicate to keep
        // and idled on an answer that could never arrive. The question must resolve in-turn.
        var result = await dispatcher.DispatchAsync(
            Call(
                "ask_user",
                """
                {"question":"Which duplicate should stay?","options":[
                  {"id":"keep-a","label":"Keep first"},
                  {"id":"keep-b","label":"Keep second"}]}
                """,
                threadId: "ask-thread"),
            CancellationToken.None);

        // Steer channel: success=false is what makes the code-mode harness print the text even
        // when the exec script never echoes the result (the Ok channel is invisible unechoed).
        Assert.False(result.Success);
        Assert.Contains("autoResolved", result.Text, StringComparison.Ordinal);
        Assert.Contains("not an error", result.Text, StringComparison.Ordinal);
        Assert.Contains("continue the work NOW", result.Text, StringComparison.Ordinal);
        // No card was stored: nothing parks the session, nobody was interrupted.
        var reloaded = await store.FindSessionAsync(session.Id);
        Assert.Null(reloaded!.AskCard);
    }

    [Fact]
    public async Task FullAutoGoalProposalIsAutoConfirmed()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        await store.SetThreadIdAsync(session.Id, "goal-thread");
        await store.SetPermissionModeAsync(session.Id, PermissionModes.FullAuto);

        var result = await dispatcher.DispatchAsync(
            Call("goal_propose", """{"objective":"Fix the duplicate"}""", threadId: "goal-thread"),
            CancellationToken.None);

        // Steer channel (success=false): the auto-confirm instruction must print unechoed.
        Assert.False(result.Success);
        Assert.Contains("autoConfirmed", result.Text, StringComparison.Ordinal);
        Assert.Contains("not an error", result.Text, StringComparison.Ordinal);
        var reloaded = await store.FindSessionAsync(session.Id);
        Assert.Contains("\"confirmed\"", reloaded!.GoalCard, StringComparison.Ordinal);

        // A goal WITH options must resolve the choice too — a confirmed-without-choice card
        // still read as a pending decision and the agent parked the turn (shakedown A-T1-r3).
        var withOptions = await dispatcher.DispatchAsync(
            Call(
                "goal_propose",
                """
                {"objective":"Panelize","options":[
                  {"id":"opt-full","label":"Full build"},
                  {"id":"opt-min","label":"Minimal build"}]}
                """,
                threadId: "goal-thread"),
            CancellationToken.None);
        Assert.False(withOptions.Success);
        Assert.Contains("autoConfirmed", withOptions.Text, StringComparison.Ordinal);
        Assert.Contains("opt-full", withOptions.Text, StringComparison.Ordinal);
        reloaded = await store.FindSessionAsync(session.Id);
        Assert.Contains("\"chosenOption\":\"opt-full\"", reloaded!.GoalCard, StringComparison.Ordinal);
        // Standing consent alone must NOT auto-confirm goals — it only covers approvals.
        await store.SetPermissionModeAsync(session.Id, PermissionModes.Standard);
        var standing = new StandingApprovals();
        standing.Grant(session.Id);
        var proposing = await dispatcher.DispatchAsync(
            Call("goal_propose", """{"objective":"Second framing"}""", threadId: "goal-thread"),
            CancellationToken.None);
        Assert.True(proposing.Success, proposing.Text);
        Assert.Contains("awaiting_user_confirmation", proposing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangeSubmitForwardsBoundSession()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        await store.SetThreadIdAsync(session.Id, "modeler-thread");

        var result = await dispatcher.DispatchAsync(
            Call("change_submit", """{"summary":"Move point"}""", threadId: "modeler-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        Assert.Equal(1, backend.SubmitCount);
        Assert.Equal(session.Id, backend.SubmittedSession?.Id);
    }

    [Theory]
    [InlineData("component_catalog", "{\"query\":\"point\"}", "matches")]
    [InlineData("rhino_list", "{\"limit\":10}", "objects")]
    public async Task ReadOnlyDiscoveryToolsForwardToLiveBackend(
        string tool,
        string arguments,
        string expectedProperty)
    {
        using var directory = new TestDirectory();
        var (dispatcher, _, _) = await CreateDispatcherAsync(directory);

        var result = await dispatcher.DispatchAsync(
            Call(tool, arguments, threadId: "unbound-read-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        using var payload = JsonDocument.Parse(result.Text);
        Assert.True(payload.RootElement.TryGetProperty(expectedProperty, out _));
    }

    [Fact]
    public async Task SnapshotReadReturnsCallingSessionIdentityForChangeSetBinding()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory);
        var session = await BindSessionAsync(store, "snapshot-thread");

        var result = await dispatcher.DispatchAsync(
            Call("snapshot_read", "{}", threadId: "snapshot-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        using var payload = JsonDocument.Parse(result.Text);
        Assert.Equal(
            session.Id,
            payload.RootElement.GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task FullAutoAutoResolveMarksContinuationNudge()
    {
        using var directory = new TestDirectory();
        var continuation = new Vino.AgentHost.Runtime.FullAutoContinuation();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory, continuation: continuation);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
        await store.SetThreadIdAsync(session.Id, "nudge-thread");
        await store.SetPermissionModeAsync(session.Id, PermissionModes.FullAuto);

        await dispatcher.DispatchAsync(
            Call("goal_propose", """{"objective":"Fix the duplicate"}""", threadId: "nudge-thread"),
            CancellationToken.None);

        // A turn that ends right after the auto-resolve is a park — one nudge is available.
        Assert.True(continuation.TryConsumeNudge("nudge-thread"));
        // The mark is one-shot: consuming it again without a new auto-resolve yields nothing.
        Assert.False(continuation.TryConsumeNudge("nudge-thread"));

        // Progress after an auto-resolve clears the mark — the model kept going, no nudge.
        continuation.MarkAutoResolved("nudge-thread");
        continuation.MarkProgress("nudge-thread");
        Assert.False(continuation.TryConsumeNudge("nudge-thread"));
    }

    private static async Task<(DynamicToolDispatcher Dispatcher, SessionStore Store, FakeLiveDocumentBackend Backend)>
        CreateDispatcherAsync(
            TestDirectory directory,
            DataLibrary? data = null,
            IStructuralSolver? solver = null,
            StandingApprovals? standingApprovals = null,
            Vino.AgentHost.Runtime.FullAutoContinuation? continuation = null)
    {
        var store = new SessionStore(directory.GetPath("state.db"));
        await store.InitializeAsync();
        var backend = new FakeLiveDocumentBackend();
        var options = new AgentHostOptions { DataDirectory = directory.GetPath("data") };
        var problems = new ProblemLog(options, NullLogger<ProblemLog>.Instance);
        return (
            new DynamicToolDispatcher(
                store, backend, options, problems: problems, data: data, structuralSolver: solver,
                standingApprovals: standingApprovals, continuation: continuation),
            store,
            backend);
    }

    private sealed class FakeStructuralSolver : IStructuralSolver
    {
        public string? LastInputJson { get; private set; }

        public Task<string> SolveAsync(string inputJson, CancellationToken cancellationToken)
        {
            LastInputJson = inputJson;
            return Task.FromResult("""
                {
                  "solveSeconds": 0.1, "membersIn": 1, "edgesSolved": 1, "islandEdgesDropped": 0,
                  "islandMembers": [], "nodes": 2, "supports": 2, "snappedFreeEnds": 0,
                  "tJunctionSplits": 0, "repairedFreeEnds": 0, "freeEndsRemaining": [],
                  "missingSectionMarks": {}, "totalLoadKn": 2.8, "sumReactionsFzKn": 2.8,
                  "equilibriumErrorPercent": 0.0, "maxDisplacementMm": 0.2,
                  "maxDisplacementXyzMm": [1500.0, 0.0, 3000.0], "deflectionLimit": "L/250",
                  "memberChecks": { "checked": 1, "passed": 0, "failed": 1 },
                  "failedMembers": [{ "mark": "SC1", "ratio": 1.4, "sourceObjectIds": ["a0b1c2d3-0001-4e4e-9f9f-000000000001"] }],
                  "checks": [], "viz": { "nodes": {}, "edges": [] }
                }
                """);
        }
    }

    /// <summary>
    /// structural_solve composes the solver input from the extraction artifact (endpoint records
    /// → arrays), injects the FULL KS catalog rows and the section guesses, threads the user's
    /// answers through, writes the results artifact, and returns worst members WITH source ids.
    /// </summary>
    [Fact]
    public async Task StructuralSolveComposesInputFromArtifactAndReturnsVerdictSummary()
    {
        using var directory = new TestDirectory();
        var dataRoot = directory.GetPath("shipped-data");
        Directory.CreateDirectory(Path.Combine(dataRoot, "structural"));
        await File.WriteAllTextAsync(
            Path.Combine(dataRoot, "structural", "sections-ks.json"),
            """
            {"sections":[
              {"name":"H-300x300x10x15","H":300,"B":300,"tw":10,"tf":15,"A":119.8,"Ix":20400,"Iy":6750}
            ]}
            """);
        var solver = new FakeStructuralSolver();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory, new DataLibrary(dataRoot), solver);
        var session = await BindSessionAsync(store, "solve-thread");

        // The extraction step ran first: run the real structural_extract path to plant the artifact.
        var extract = await dispatcher.DispatchAsync(
            Call("structural_extract", "{}", threadId: "solve-thread"),
            CancellationToken.None);
        Assert.True(extract.Success, extract.Text);

        var result = await dispatcher.DispatchAsync(
            Call(
                "structural_solve",
                """{"answers":{"repairFreeEnds":true,"cantileverPoints":[[0.0,0.0,3000.0]]}}""",
                threadId: "solve-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        using var input = JsonDocument.Parse(solver.LastInputJson!);
        var inputRoot = input.RootElement;
        // Endpoint records became arrays, and the catalog row went in whole.
        Assert.Equal(0.0, inputRoot.GetProperty("members")[0].GetProperty("a")[0].GetDouble());
        Assert.Equal(3000.0, inputRoot.GetProperty("members")[0].GetProperty("b")[2].GetDouble());
        Assert.Equal(
            20400.0,
            inputRoot.GetProperty("sections").GetProperty("H-300x300x10x15").GetProperty("Ix").GetDouble());
        // The extraction's section guess became the mark mapping.
        Assert.Equal(
            "H-300x300x10x15",
            inputRoot.GetProperty("markSections").GetProperty("SC1").GetString());
        // The variant mark resolved by PREFIX to the same section instead of falling to the
        // default — the real-model regression the gate caught.
        Assert.Equal(
            "H-300x300x10x15",
            inputRoot.GetProperty("markSections").GetProperty("SC1 (Bracing)").GetString());
        // The user's answers reached the solver options verbatim.
        Assert.True(inputRoot.GetProperty("options").GetProperty("repairFreeEnds").GetBoolean());
        Assert.Equal(
            3000.0,
            inputRoot.GetProperty("options").GetProperty("cantileverPoints")[0][2].GetDouble());

        using var payload = JsonDocument.Parse(result.Text);
        var summary = payload.RootElement;
        Assert.Equal(1, summary.GetProperty("memberChecks").GetProperty("failed").GetInt32());
        Assert.Equal(
            "a0b1c2d3-0001-4e4e-9f9f-000000000001",
            summary.GetProperty("worstMembers")[0].GetProperty("sourceObjectIds")[0].GetString());
        Assert.Equal("structural/results.json", summary.GetProperty("resultsArtifact").GetString());
        Assert.True(File.Exists(directory.GetPath($"data/artifacts/{session.Id:N}/structural/results.json")));
    }

    /// <summary>
    /// structural_extract composes the model-facing SUMMARY: full member list to a session
    /// artifact (never the tool result), section identity matched dispatcher-side against the
    /// shipped KS catalog (÷1.02 of the prototype outer dims), and free ends carried WITH their
    /// source object ids because they are the ask-back items.
    /// </summary>
    [Fact]
    public async Task StructuralExtractSummarizesMatchesSectionsAndWritesTheMembersArtifact()
    {
        using var directory = new TestDirectory();
        var dataRoot = directory.GetPath("shipped-data");
        Directory.CreateDirectory(Path.Combine(dataRoot, "structural"));
        await File.WriteAllTextAsync(
            Path.Combine(dataRoot, "structural", "sections-ks.json"),
            """
            {"sections":[
              {"name":"H-300x300x10x15","H":300,"B":300},
              {"name":"H-400x400x13x21","H":400,"B":408},
              {"name":"H-414x405x18x28","H":414,"B":405},
              {"name":"H-400x200x8x13","H":400,"B":200}
            ]}
            """);
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory, new DataLibrary(dataRoot));
        var session = await BindSessionAsync(store, "structural-thread");

        var result = await dispatcher.DispatchAsync(
            Call("structural_extract", """{"layerFilter":"철골"}""", threadId: "structural-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        using var payload = JsonDocument.Parse(result.Text);
        var root = payload.RootElement;
        Assert.Equal(2, root.GetProperty("memberCount").GetInt32());
        Assert.Equal(2, root.GetProperty("mergedDuplicateAxes").GetInt32());
        // 306 / 1.02 = 300 exactly → the H-300x300 row wins with zero error.
        var guess = root.GetProperty("sectionGuesses").GetProperty("SC1");
        Assert.Equal("H-300x300x10x15", guess.GetProperty("section").GetString());
        Assert.Equal(0.0, guess.GetProperty("errorMm").GetDouble());
        // 405×414 is H-414x405 at EXACT dims: the scale-1.0 hypothesis must win over the
        // ÷1.02 reading that lands on the neighboring H-400x400.
        var exact = root.GetProperty("sectionGuesses").GetProperty("SC6");
        Assert.Equal("H-414x405x18x28", exact.GetProperty("section").GetString());
        Assert.Equal(0.0, exact.GetProperty("errorMm").GetDouble());
        // The free end arrives with its source object id — the ask-back needs a focusable target.
        var freeEnd = Assert.Single(root.GetProperty("freeEnds").EnumerateArray().ToArray());
        Assert.Equal(
            "a0b1c2d3-0001-4e4e-9f9f-000000000001",
            freeEnd.GetProperty("sourceObjectIds")[0].GetString());
        // The artifact holds the FULL extraction; the summary only points at it.
        var artifactPath = root.GetProperty("membersArtifact").GetString();
        Assert.Equal("structural/members.json", artifactPath);
        var stored = await File.ReadAllTextAsync(
            directory.GetPath($"data/artifacts/{session.Id:N}/structural/members.json"));
        using var artifact = JsonDocument.Parse(stored);
        Assert.Equal(
            "SC1",
            artifact.RootElement.GetProperty("extraction").GetProperty("members")[0].GetProperty("mark").GetString());
        Assert.DoesNotContain("\"members\":", result.Text.Replace(" ", ""));
    }

    [Fact]
    public async Task RecoveryResumeForwardsBoundSessionAndJobId()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory);
        var session = await BindSessionAsync(store, "resume-thread");
        var jobId = Guid.NewGuid().ToString("D");

        var result = await dispatcher.DispatchAsync(
            Call("recovery_resume", $$"""{"jobId":"{{jobId}}"}""", threadId: "resume-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        Assert.Equal(session.Id, backend.ResumedSession?.Id);
        Assert.Equal(jobId, backend.ResumedJobId);
    }

    /// <summary>
    /// W3 SHARED CONTRACT: approval_request targets carry optional label/role/impact strings that
    /// must round-trip into the stored ApprovalCard JSON (the panel renders them per target).
    /// </summary>
    [Fact]
    public async Task ApprovalRequestRoundTripsRoleAndImpactIntoTheStoredCard()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory);
        var session = await BindSessionAsync(store, "approval-thread");
        var componentId = Guid.NewGuid();

        var result = await dispatcher.DispatchAsync(
            Call(
                "approval_request",
                $$"""
                {
                  "summary": "정리: 살아있는 컴포넌트 1개 삭제 승인 요청",
                  "items": [{
                    "id": "cleanup-1",
                    "label": "구형 패널 스크립트 삭제",
                    "targets": [{
                      "objectId": "{{componentId:D}}",
                      "fingerprint": "fp-live-1",
                      "label": "PanelStage",
                      "role": "격자 곡면을 패널로 분할하는 단계",
                      "impact": "PanelStage → Bake 와이어가 끊기고 새 C# 체인이 대체합니다"
                    }]
                  }]
                }
                """,
                threadId: "approval-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        var stored = (await store.FindSessionAsync(session.Id))!.ApprovalCard;
        Assert.NotNull(stored);
        // Observation 1: the raw stored JSON carries the new fields (what the panel deserializes).
        // Non-ASCII values are \u-escaped in storage, so assert on the property names.
        Assert.Contains("\"role\":", stored, StringComparison.Ordinal);
        Assert.Contains("\"impact\":", stored, StringComparison.Ordinal);
        Assert.Contains("\"label\":\"PanelStage\"", stored, StringComparison.Ordinal);
        // Observation 2: the typed card round-trips them on the exact target.
        var card = JsonSerializer.Deserialize<ApprovalCard>(
            stored!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var target = Assert.Single(Assert.Single(card.Items).Targets);
        Assert.Equal(componentId, target.ObjectId);
        Assert.Equal("fp-live-1", target.Fingerprint);
        Assert.Equal("PanelStage", target.Label);
        Assert.Equal("격자 곡면을 패널로 분할하는 단계", target.Role);
        Assert.Equal("PanelStage → Bake 와이어가 끊기고 새 C# 체인이 대체합니다", target.Impact);
    }

    /// <summary>
    /// W3 Finding 8: model-authored target display strings are clamped to 300 chars (ellipsis) at
    /// intake, so a runaway generation can never flood the stored card or the approval UI.
    /// </summary>
    [Fact]
    public async Task ApprovalRequestClampsOversizedTargetDisplayStrings()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory);
        var session = await BindSessionAsync(store, "clamp-thread");
        var oversized = new string('R', 400);

        var result = await dispatcher.DispatchAsync(
            Call(
                "approval_request",
                $$"""
                {
                  "summary": "clamp",
                  "items": [{
                    "id": "clamp-1",
                    "label": "clamp target strings",
                    "targets": [{
                      "objectId": "{{Guid.NewGuid():D}}",
                      "fingerprint": "fp-clamp",
                      "label": "short label",
                      "role": "{{oversized}}",
                      "impact": "{{oversized}}"
                    }]
                  }]
                }
                """,
                threadId: "clamp-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        var stored = (await store.FindSessionAsync(session.Id))!.ApprovalCard;
        var card = JsonSerializer.Deserialize<ApprovalCard>(
            stored!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var target = Assert.Single(Assert.Single(card.Items).Targets);
        // The short string is untouched; the oversized ones are cut to exactly 300 with ellipsis.
        Assert.Equal("short label", target.Label);
        Assert.Equal(300, target.Role!.Length);
        Assert.EndsWith("…", target.Role, StringComparison.Ordinal);
        Assert.Equal(300, target.Impact!.Length);
        Assert.EndsWith("…", target.Impact, StringComparison.Ordinal);
        // The binding pair is never clamped.
        Assert.Equal("fp-clamp", target.Fingerprint);
    }

    /// <summary>Legacy cards (pre-W3, no label/role/impact on targets) must keep deserializing.</summary>
    [Fact]
    public void LegacyApprovalCardWithoutRoleAndImpactStillLoads()
    {
        var legacy = """
            {
              "status": "granted",
              "summary": "Fix a near-miss pair.",
              "items": [{
                "id": "gap",
                "label": "Close a 0.005 mm endpoint gap",
                "measure": "0.005 mm",
                "targets": [{ "objectId": "3f2f7a44-8f5e-4f5c-9b0a-1c2d3e4f5a6b", "fingerprint": "fp-old" }]
              }],
              "grantId": "grant-legacy",
              "approvedItemIds": ["gap"]
            }
            """;

        var card = JsonSerializer.Deserialize<ApprovalCard>(
            legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(card);
        var target = Assert.Single(Assert.Single(card!.Items).Targets);
        Assert.Equal("fp-old", target.Fingerprint);
        Assert.Null(target.Label);
        Assert.Null(target.Role);
        Assert.Null(target.Impact);
    }

    private static async Task<SessionRecord> BindSessionAsync(SessionStore store, string threadId)
    {
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Artifacts"));
        await store.SetThreadIdAsync(session.Id, threadId);
        return session;
    }

    private static DynamicToolCall Call(string tool, string arguments, string threadId = "thread")
    {
        using var document = JsonDocument.Parse(arguments);
        return new DynamicToolCall(
            Guid.NewGuid().ToString("N"),
            threadId,
            "turn",
            "vino_v1",
            tool,
            document.RootElement.Clone());
    }

    private sealed class FakeLiveDocumentBackend : ILiveDocumentBackend
    {
        public bool IsConnected => true;

        public DocumentRuntime? CurrentTarget => null;

        public int QueueLength => 0;

        public string? WriterSessionId => null;

        public int SubmitCount { get; private set; }

        public SessionRecord? SubmittedSession { get; private set; }

        public Task<object> ReadSnapshotAsync(
            SessionRecord session,
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { sessionId = session.Id, snapshotId = "snapshot-1" });

        public Task<object> SearchComponentCatalogAsync(
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { matches = Array.Empty<object>() });

        public Task<object> ListRhinoObjectsAsync(
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { objects = Array.Empty<object>() });

        public Task<object> InspectCanvasOutputsAsync(
            SessionRecord session,
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { outputs = Array.Empty<object>(), sessionId = session.Id });

        public Task<object> InspectCanvasOutputsAsync(
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { outputs = Array.Empty<object>() });

        public Task<object> SubmitChangeAsync(
            SessionRecord session,
            JsonElement arguments,
            bool autoApprove,
            CancellationToken cancellationToken)
        {
            SubmitCount++;
            SubmittedSession = session;
            SubmittedAutoApprove = autoApprove;
            return Task.FromResult<object>(new { jobId = "job-1" });
        }

        public bool? SubmittedAutoApprove { get; private set; }

        public IReadOnlyList<(Guid ObjectId, string Fingerprint)>? MintedGrantItems { get; private set; }

        public ApprovalGrantMint MintApprovalGrant(IReadOnlyList<(Guid ObjectId, string Fingerprint)> items)
        {
            MintedGrantItems = items;
            return new ApprovalGrantMint("test-grant", DateTimeOffset.UtcNow.AddMinutes(15));
        }

        public Task<object> ArrangeLayoutAsync(
            SessionRecord session,
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { status = "already-tidy", moved = 0 });

        public Task<object> ConsolidateStagesAsync(
            SessionRecord session,
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { status = "plan", action = "merge" });

        public Task<object> ReadJobAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { state = "queued" });

        public SessionRecord? ResumedSession { get; private set; }

        public string? ResumedJobId { get; private set; }

        public Task<object> ResumeSessionAsync(
            SessionRecord session,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            ResumedSession = session;
            ResumedJobId = arguments.TryGetProperty("jobId", out var jobId) ? jobId.GetString() : null;
            return Task.FromResult<object>(new { resumed = true });
        }

        public Task<object> ReadDataFlowAsync(SessionRecord session, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { docId = "test", references = new { }, bakes = new { } });

        public object RhinoAuditResponse { get; set; } =
            new { kind = "purgeCandidates", findings = Array.Empty<object>() };

        public Task<object> ReadRhinoAuditAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult(RhinoAuditResponse);

        // Mirrors the backend's { result, fingerprint, diagnostics } bridge-read wrapper with one
        // instance member whose prototype dims are KS nominal × 1.02 (H-300x300 → 306) and one
        // free end, so the dispatcher's section matching and summary composition are exercised.
        public Task<object> ReadStructuralExtractAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new
            {
                result = new
                {
                    docUnits = "Millimeters",
                    scannedObjects = 3,
                    members = new object[]
                    {
                        new
                        {
                            mark = "SC1",
                            layer = "철골::SC1",
                            a = new { x = 0.0, y = 0.0, z = 0.0 },
                            b = new { x = 0.0, y = 0.0, z = 3000.0 },
                            length = 3000.0,
                            kind = "instance",
                            sourceObjectIds = new[] { "a0b1c2d3-0001-4e4e-9f9f-000000000001" },
                            fingerprints = new[] { "fp-sc1" },
                        },
                        // A VARIANT mark: members on "SC1 (Bracing)" are SC1s, and the real-model
                        // gate caught them falling to the default section (38 braces solving 90%
                        // too heavy) when only exact-mark lookups existed.
                        new
                        {
                            mark = "SC1 (Bracing)",
                            layer = "철골::SC1 (Bracing)",
                            a = new { x = 0.0, y = 0.0, z = 3000.0 },
                            b = new { x = 6000.0, y = 0.0, z = 0.0 },
                            length = 6708.2,
                            kind = "pca",
                            sourceObjectIds = new[] { "a0b1c2d3-0002-4e4e-9f9f-000000000002" },
                            fingerprints = new[] { "fp-sc1-brace" },
                        },
                    },
                    prototypes = new object[]
                    {
                        // Both drawing conventions from the real file: SC1 at nominal × 1.02,
                        // SC6 at EXACT nominal dims of a section whose catalog neighbor sits ~2%
                        // away (a fixed ÷1.02 read SC6 as H-400x400 — the live gate caught it).
                        new { layer = "철골::SC1", mark = "SC1", outerX = 306.0, outerY = 306.0 },
                        new { layer = "철골::SC6", mark = "SC6", outerX = 405.0, outerY = 414.0 },
                    },
                    freeEnds = new object[]
                    {
                        new
                        {
                            memberIndex = 0,
                            end = 1,
                            point = new { x = 0.0, y = 0.0, z = 3000.0 },
                            sourceObjectIds = new[] { "a0b1c2d3-0001-4e4e-9f9f-000000000001" },
                        },
                    },
                    mergedDuplicateAxes = 2,
                    obliqueExactAxes = 0,
                    skippedByReason = new Dictionary<string, int> { ["skipped:Mesh"] = 1 },
                    truncated = false,
                    fingerprint = "extract-fp",
                },
                fingerprint = "extract-fp",
                diagnostics = Array.Empty<object>(),
            });

        public Task<object> ReadRhinoLayersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { layers = Array.Empty<object>(), namedLayerStates = Array.Empty<string>() });

        public Task StopCurrentAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

