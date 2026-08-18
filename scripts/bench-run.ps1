#requires -Version 5.1
# Bench cell runner (rubric v1, docs/bench-rubric.md). Runs ONE cell = arm x task x rep:
#
#   bench-run.ps1 -Arm C -Task T1 -Rep 1
#
# Flow: boot dev-loop (task-mapped fixture) -> arm-agnostic prep turn places the Cordyceps
# MCP host component (excluded from scoring diffs) -> baseline snapshot/audits -> the arm
# executes the shared task prompt -> post snapshot/audits, viewport capture, blind copy,
# scores.csv append -> teardown. Vino is always the OBSERVER: agent prose is never trusted,
# only /dev/snapshot, /dev/rhino-objects and /dev/audit.
#
# Arms: A = Vino session (codex, fullAuto so unattended parity with card-less baselines)
#       B = codex exec + mcp-remote bridge to Cordyceps (stdio->http)
#       C = Claude Code headless (-p) + Cordyceps HTTP MCP
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('A', 'B', 'C')][string]$Arm,
    [Parameter(Mandatory)][ValidateSet('T1', 'T2')][string]$Task,
    [int]$Rep = 1,
    [int]$TimeoutSeconds = 1500,
    [string]$Round = (Get-Date -Format 'yyyyMMdd'),
    [switch]$KeepRhino
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$cellId = "$Arm-$Task-r$Rep"
$benchRoot = Join-Path $repo "artifacts\bench\$Round"
$cellDir = Join-Path $benchRoot $cellId
$blindDir = Join-Path $benchRoot 'blind'
New-Item -ItemType Directory -Force -Path $cellDir, $blindDir | Out-Null

# --- 1. boot ------------------------------------------------------------------------
$sceneKind = @{ T1 = 'paneling'; T2 = 'hygiene' }[$Task]
& (Join-Path $PSScriptRoot 'dev-loop.ps1') -SceneKind $sceneKind -GhTemplate 'bench-definition.gh' | Out-Null
$run = (Get-ChildItem (Join-Path $repo 'artifacts\dev-loop') -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'loop-state.json') } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$state = Get-Content (Join-Path $run 'loop-state.json') -Raw | ConvertFrom-Json
$base = $state.uiBaseUrl.TrimEnd('/') + '/api/v1'
$headers = @{ 'X-Vino-Token' = $state.token }
function Api($method, $path, $body) {
    $uri = $base + $path
    if ($null -ne $body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 8 -Compress))
        return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -Body $bytes `
            -ContentType 'application/json; charset=utf-8' -TimeoutSec 90
    }
    return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec 90
}
function Wait-SessionIdle($sessionId, $seconds) {
    $active = @('working', 'drafting', 'queued', 'verifying')
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        Start-Sleep -Seconds 6
        $s = (Api GET '/runtime').sessions | Where-Object { $_.id -eq $sessionId }
        $status = if ($s) { $s.status } else { 'gone' }
        if ($status -notin $active) {
            # Inter-turn idle is not DONE: a full-auto continuation nudge starts its follow-up
            # turn 1-2s after a parked turn ends (A-T2 4th attempt died to exactly this — the
            # runner tore down mid-repair). Only a status that stays quiet through a 12s
            # confirmation window counts as final.
            Start-Sleep -Seconds 12
            $s = (Api GET '/runtime').sessions | Where-Object { $_.id -eq $sessionId }
            $confirm = if ($s) { $s.status } else { 'gone' }
            if ($confirm -in $active) { $status = $confirm }
        }
    } while ($status -in $active -and (Get-Date) -lt $deadline)
    return $status
}

# GH pairing heals asynchronously after boot: bridge reads throw
# GrasshopperDocumentUnavailable for a stretch even AFTER document.register lands (observed
# 08-18: four consecutive prep turns raced the heal and reported "no Grasshopper document").
# Gate on a twice-readable canvas before any turn is sent. Product-side note: reads should
# queue during registration instead of failing — bench works around, does not fix.
$canvasReady = 0
foreach ($i in 1..40) {
    try { Api GET '/dev/snapshot' | Out-Null; $canvasReady++ } catch { $canvasReady = 0 }
    if ($canvasReady -ge 2) { break }
    Start-Sleep -Seconds 3
}
if ($canvasReady -lt 2) { throw 'Canvas never became readable after boot (GH pairing did not heal).' }

# --- 2. arm-agnostic prep: Cordyceps MCP host on canvas ------------------------------
# The bench GH template ships with Cordyceps pre-placed, so the prep turn (a codex call!)
# only runs when the component is somehow absent. This keeps arm C runnable on exhausted
# codex quota and stops burning codex for every cell of every arm.
$prepSnap = Api GET '/dev/snapshot'
if (@($prepSnap.canvas.objects | Where-Object { $_.name -match 'Cordyceps' }).Count -eq 0) {
    $prepId = (Api POST '/sessions' @{ Name = 'bench-prep'; ModelProfile = 'medium' }).id
    Api POST "/sessions/$prepId/messages" @{
        Content = 'component_catalog에서 Cordyceps를 검색해서 그 컴포넌트를 캔버스 (900,50) 위치에 하나만 놓아줘. 다른 작업은 하지 마.'
        ClientMessageId = [guid]::NewGuid().ToString()
    } | Out-Null
    $prepStatus = Wait-SessionIdle $prepId 360
    if ($prepStatus -ne 'idle') { throw "Cordyceps prep turn ended '$prepStatus' - cannot start the cell." }
}
$mcpUp = $false
foreach ($i in 1..20) {
    if (netstat -ano | Select-String ':26929.*LISTENING') { $mcpUp = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $mcpUp) { throw 'Cordyceps MCP server did not come up on :26929.' }

$snap0 = Api GET '/dev/snapshot'
$baselineIds = @{}; foreach ($o in $snap0.canvas.objects) { $baselineIds[$o.objectId] = $true }
$wires0 = @($snap0.canvas.wires).Count
$objects0 = Api GET '/dev/rhino-objects'
$fp0 = @{}; foreach ($o in $objects0.result.objects) { $fp0[$o.objectId] = $o.fingerprint }
$gaps0 = -1; $dups0 = -1
if ($Task -eq 'T2') {
    $gaps0 = (Api GET '/dev/audit?kind=nearMissEndpoints').result.findings.Count
    $dups0 = (Api GET '/dev/audit?kind=nearDuplicates').result.findings.Count
}

# --- 3. execute the arm --------------------------------------------------------------
$prompt = (Get-Content (Join-Path $PSScriptRoot "bench\tasks\$Task.txt") -Raw -Encoding UTF8).Trim()
$transcript = Join-Path $cellDir 'transcript.txt'
$mcpConfig = Join-Path $PSScriptRoot 'bench\mcp-cordyceps.json'
$sw = [Diagnostics.Stopwatch]::StartNew()
$status = 'unknown'; $exitCode = 0; $armSessionId = $null
switch ($Arm) {
    'A' {
        $armSessionId = (Api POST '/sessions' @{ Name = "bench-$cellId"; ModelProfile = 'xhigh' }).id
        # fullAuto: unattended parity - the baseline arms have no cards at all.
        Api PUT "/sessions/$armSessionId/permission" @{ mode = 'fullAuto' } | Out-Null
        Api POST "/sessions/$armSessionId/messages" @{
            Content = $prompt; ClientMessageId = [guid]::NewGuid().ToString()
        } | Out-Null
        $status = Wait-SessionIdle $armSessionId $TimeoutSeconds
        $messages = Api GET "/sessions/$armSessionId/messages"
        ($messages | ForEach-Object { "== [$($_.role)] $($_.createdAt)`n$($_.content)`n" }) |
            Set-Content $transcript -Encoding utf8
    }
    'C' {
        Push-Location $cellDir
        # Native tools write progress to stderr; under Stop that one line aborts the cell
        # (killed arm B in shakedown r2). Locally relax for the invocation only.
        $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try {
            & claude -p $prompt --mcp-config $mcpConfig --strict-mcp-config `
                --allowedTools 'mcp__cordyceps__*' --model sonnet 2>&1 |
                Tee-Object -FilePath $transcript | Out-Null
            $exitCode = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $eap; Pop-Location }
        $status = if ($exitCode -eq 0) { 'idle' } else { "exit-$exitCode" }
    }
    'B' {
        Push-Location $cellDir
        $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try {
            # PS 5.1 native-arg quoting: the inner TOML quotes must reach codex as \" — probed
            # working via `codex mcp list`. Plain exec: MCP tool calls are not shell commands,
            # so no sandbox flag is needed (escalate only if the transcript shows approval stalls).
            & codex exec `
                -c "mcp_servers.cordyceps.command=`"npx`"" `
                -c "mcp_servers.cordyceps.args=[\`"-y\`",\`"mcp-remote\`",\`"http://127.0.0.1:26929/mcp\`"]" `
                $prompt 2>&1 |
                Tee-Object -FilePath $transcript | Out-Null
            $exitCode = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $eap; Pop-Location }
        $status = if ($exitCode -eq 0) { 'idle' } else { "exit-$exitCode" }
    }
}
$sw.Stop()

# --- 4. observe (never the agent's prose) --------------------------------------------
$snap1 = Api GET '/dev/snapshot'
$added = @($snap1.canvas.objects | Where-Object { -not $baselineIds.ContainsKey($_.objectId) })
$wiresAdded = @($snap1.canvas.wires).Count - $wires0
# Geometry liveness: sample added components' output sockets. componentsAdded alone lied in
# shakedown r1 — a 49-component canvas produced zero geometry because nothing was referenced.
# Two-pass max: a single sample can land inside a recompute window right after the arm's
# final slider/verification step and read near-zero on a LIVE definition (A-T1-r1 sampled 6
# while its clean capture showed 300 solids). Max of two samples 6s apart.
$outputData = 0
foreach ($samplePass in 1..2) {
    $sample = 0
    foreach ($component in ($added | Select-Object -Last 12)) {
        try {
            $outs = (Api GET "/dev/grasshopper/$($component.objectId)/outputs").result.outputs
            foreach ($socket in $outs) { $sample += [int]$socket.dataCount }
        }
        catch { }
    }
    if ($sample -gt $outputData) { $outputData = $sample }
    if ($samplePass -eq 1) { Start-Sleep -Seconds 6 }
}
# The whole definition, values and wires included, as an analyzable artifact (axis 3) — plus
# the .gh itself in case the arm saved it.
$snap1 | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $cellDir 'definition.json') -Encoding utf8
if (Test-Path $state.sceneGh) { Copy-Item $state.sceneGh (Join-Path $cellDir 'definition.gh') -Force }

# --- 4b. harness-side definition + scene archival ------------------------------------
# The live canvas is usually UNSAVED (arm A has no save affordance at all; shakedown lost
# A-r4's verified definition this way), so the sceneGh copy above archives a stale template
# whenever the arm didn't save. Every cell carries the Cordyceps component (prep turn,
# excluded from scoring diffs) whose MCP endpoint exposes a save tool — the harness calls
# it directly so all arms' definitions are retrieved identically, independent of arm whim.
# Cordyceps 1.4.12 is stateless over streamable-http: tools/call works without a session
# handshake. Save lives at gh_document {action:'save', path} (live-verified 2026-08-18).
function Invoke-CordycepsCall {
    param([string]$Tool, [hashtable]$Arguments)
    $body = @{ jsonrpc = '2.0'; id = 9; method = 'tools/call'
        params = @{ name = $Tool; arguments = $Arguments } } | ConvertTo-Json -Depth 6
    $resp = Invoke-WebRequest -Uri 'http://127.0.0.1:26929/mcp' -Method Post -TimeoutSec 30 `
        -UseBasicParsing -ContentType 'application/json' `
        -Headers @{ Accept = 'application/json, text/event-stream' } -Body $body
    $raw = $resp.Content
    if ($raw -match 'data:') {
        $raw = ($raw -split "`r?`n" | Where-Object { $_ -like 'data:*' } |
            ForEach-Object { $_.Substring(5).Trim() }) | Select-Object -Last 1
    }
    $parsed = $raw | ConvertFrom-Json
    if ($parsed.error) { throw "tools/call error: $($parsed.error.message)" }
    $parsed
}
try {
    $ghTarget = Join-Path $cellDir 'definition.gh'
    Invoke-CordycepsCall 'gh_document' @{ action = 'save'; path = $ghTarget } | Out-Null
    if (-not (Test-Path -LiteralPath $ghTarget)) { throw "save reported ok but $ghTarget missing" }
}
catch {
    # Harness archival failure, NOT an arm error — record and keep the sceneGh fallback copy.
    Add-Content (Join-Path $cellDir 'archive-note.txt') `
        "cordyceps gh save failed: $($_.Exception.Message)" -Encoding utf8
}
# Scene fixture snapshot: dev-loop runs are pruned (keep-10), which orphans the definition's
# object references; reopen-verification (bench-recap.py) needs scene + definition together.
Get-ChildItem (Join-Path $run 'scene*.3dm') -ErrorAction SilentlyContinue |
    Copy-Item -Destination $cellDir -Force
$gaps1 = -1; $dups1 = -1; $foreignTouched = -1
if ($Task -eq 'T2') {
    $gaps1 = (Api GET '/dev/audit?kind=nearMissEndpoints').result.findings.Count
    $dups1 = (Api GET '/dev/audit?kind=nearDuplicates').result.findings.Count
    # Safety: how many PRE-EXISTING rhino objects changed or vanished. The fixes are expected to
    # touch the finding objects; the axis-5 review compares this count against the finding sets.
    $objects1 = Api GET '/dev/rhino-objects'
    $fp1 = @{}; foreach ($o in $objects1.result.objects) { $fp1[$o.objectId] = $o.fingerprint }
    $foreignTouched = @($fp0.Keys | Where-Object {
            (-not $fp1.ContainsKey($_)) -or ($fp1[$_] -ne $fp0[$_]) }).Count
}
$armErrors = -1
if ($Arm -eq 'A' -and $armSessionId) {
    $problemLog = Join-Path $run 'runtime\problem-log.jsonl'
    if (Test-Path $problemLog) {
        $armErrors = @(Get-Content $problemLog -Encoding UTF8 | ForEach-Object {
                try { $_ | ConvertFrom-Json } catch { $null }
            } | Where-Object { $_ -and $_.kind -eq 'job-state' -and $_.sessionId -eq $armSessionId -and
                $_.state -in @('Failed', 'Blocked', 'RecoveryRequired') }).Count
    }
}

# --- 5. viewport capture (whole Rhino window; arm-agnostic, no plugin dependency) ----
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BenchCapture {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@ -ReferencedAssemblies System.Drawing -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.Drawing
$capturePath = Join-Path $cellDir 'capture.png'
# The BENCH Rhino by pid, never "first Rhino" — a concurrent user session must neither be
# captured nor touched.
$rhinoProc = Get-Process -Id $state.rhinoPid -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -eq 'Rhino' }
if ($rhinoProc -and $rhinoProc.MainWindowHandle -ne [IntPtr]::Zero) {
    $rect = New-Object BenchCapture+RECT
    [BenchCapture]::GetWindowRect($rhinoProc.MainWindowHandle, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    if ($w -gt 0 -and $h -gt 0) {
        $bmp = New-Object Drawing.Bitmap($w, $h)
        $g = [Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [BenchCapture]::PrintWindow($rhinoProc.MainWindowHandle, $hdc, 2) | Out-Null
        $g.ReleaseHdc($hdc); $g.Dispose()
        $bmp.Save($capturePath, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    }
}
# Clean viewport render (no window chrome) alongside the PrintWindow shot. The PrintWindow
# capture stays the blind-pool image (guaranteed present for every cell); this one is the
# comparison-page asset when it succeeds.
try {
    Invoke-CordycepsCall 'gh_document' @{ action = 'capture_viewport'; view = 'Perspective'
        path = (Join-Path $cellDir 'capture-clean.png'); width = 1600; height = 1000 } | Out-Null
}
catch {
    Add-Content (Join-Path $cellDir 'archive-note.txt') `
        "cordyceps viewport capture failed: $($_.Exception.Message)" -Encoding utf8
}
$blindName = $null
if (Test-Path $capturePath) {
    # Blind pool for axis-2 human scoring: random name, mapping kept separately.
    $blindName = [guid]::NewGuid().ToString('N').Substring(0, 12) + '.png'
    Copy-Item $capturePath (Join-Path $blindDir $blindName)
    Add-Content (Join-Path $benchRoot 'blind-map.csv') "$blindName,$cellId" -Encoding utf8
}

# --- 6. score row + metrics ----------------------------------------------------------
$scores = Join-Path $benchRoot 'scores.csv'
if (-not (Test-Path $scores)) {
    Set-Content $scores 'at,cell,arm,task,rep,status,durationSec,componentsAdded,wiresAdded,outputData,gapsBefore,gapsAfter,dupsBefore,dupsAfter,rhinoObjectsTouched,armErrors,blind' -Encoding utf8
}
$row = @(
    (Get-Date -Format 'o'), $cellId, $Arm, $Task, $Rep, $status,
    [math]::Round($sw.Elapsed.TotalSeconds), $added.Count, $wiresAdded, $outputData,
    $gaps0, $gaps1, $dups0, $dups1, $foreignTouched, $armErrors, $blindName
) -join ','
Add-Content $scores $row -Encoding utf8
[ordered]@{
    cell = $cellId; run = $run; status = $status; durationSec = [math]::Round($sw.Elapsed.TotalSeconds)
    componentsAdded = $added.Count; wiresAdded = $wiresAdded; outputData = $outputData
    addedComponents = @($added | ForEach-Object { @{ id = $_.objectId; name = $_.name } })
    gaps = @($gaps0, $gaps1); dups = @($dups0, $dups1); rhinoObjectsTouched = $foreignTouched
    armErrors = $armErrors
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $cellDir 'metrics.json') -Encoding utf8
Write-Output $row

# --- 7. teardown ---------------------------------------------------------------------
if (-not $KeepRhino) {
    # PID-scoped kill. The old name-based sweep force-closed a USER Rhino session that was
    # opened mid-cell (764MB model -> stale .3dm.rhl -> read-only reopen, 08-18 postmortem).
    # Only the Rhino this cell's dev-loop booted (loop-state.json rhinoPid) is ever stopped.
    $benchRhino = Get-Process -Id $state.rhinoPid -ErrorAction SilentlyContinue
    if ($benchRhino -and $benchRhino.ProcessName -eq 'Rhino') {
        Stop-Process -Id $benchRhino.Id -Force -Confirm:$false
        $benchRhino.WaitForExit()
    }
}
