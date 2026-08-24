#requires -Version 5.1
# B1/B2 live gate for the Claude backend: a backend=claude session must draw a REAL canvas.
#
#   B1 canvas-green : grid+circles task -> /dev/snapshot shows components + wires >= 1 and the
#                     document solves without runtime errors. Server-observed only — the agent's
#                     prose is never trusted (same discipline as every other gate).
#   R12 interrupt   : a second turn is interrupted mid-flight (pause kills the CLI process tree),
#                     then a third turn must prove the conversation SURVIVED the kill (--resume
#                     integrity after an interrupted spawn) by recalling turn-1 content.
#   B2 no-dead-ends : across the whole run, live jobs show >= 1 committed ChangeSet and ZERO
#                     recoveryRequired (self-repair may fire, but never a dead end).
#
# Needs: a dev-loop run (scripts/dev-loop.ps1) with the default scene, Claude CLI logged in.
# Consumes a small amount of the user's Claude subscription quota.
#
# NOTE: this file must stay UTF-8 WITH BOM (PS 5.1 reads BOM-less .ps1 as ANSI).
[CmdletBinding()]
param(
    [string]$Run,
    [int]$TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $Run) {
    $Run = (Get-ChildItem (Join-Path $repo 'artifacts\dev-loop') -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'loop-state.json') } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
$state = Get-Content (Join-Path $Run 'loop-state.json') -Raw | ConvertFrom-Json
$base = $state.uiBaseUrl.TrimEnd('/') + '/api/v1'
$headers = @{ 'X-Vino-Token' = $state.token }

function Api($method, $path, $body) {
    $uri = $base + $path
    if ($null -ne $body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 8 -Compress))
        return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -Body $bytes `
            -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
    }
    return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec 60
}
function Get-Session($sessionId) {
    return (Api GET '/runtime').sessions | Where-Object { $_.id -eq $sessionId }
}
function Send-Turn($sessionId, $text, $seconds) {
    Api POST "/sessions/$sessionId/messages" @{ Content = $text; ClientMessageId = [guid]::NewGuid().ToString() } | Out-Null
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        Start-Sleep -Seconds 5
        $s = Get-Session $sessionId
        $status = if ($s) { $s.status } else { 'gone' }
    } while ($status -in @('working', 'drafting', 'queued', 'verifying') -and (Get-Date) -lt $deadline)
    return $status
}
function Get-AssistantText($sessionId) {
    $messages = @(Api GET "/sessions/$sessionId/messages?limit=250")
    return @($messages | Where-Object { $_.role -eq 'assistant' } | ForEach-Object { $_.content }) -join "`n"
}

$results = [ordered]@{}

# --- readiness: the bridge answers reads a while after document.register (measured in the bench
# harness: prep turns died 4-in-a-row to GrasshopperDocumentUnavailable) — demand TWO consecutive
# snapshot successes before spending any quota.
$consecutive = 0
$readyDeadline = (Get-Date).AddSeconds(120)
while ($consecutive -lt 2) {
    if ((Get-Date) -gt $readyDeadline) { throw 'The bridge never served two consecutive snapshots.' }
    try {
        [void] (Api GET '/dev/snapshot')
        $consecutive++
    }
    catch {
        $consecutive = 0
        Start-Sleep -Seconds 5
    }
}

# --- session: backend=claude, fullAuto (no human to answer cards) -------------------------------
# No GrasshopperDoc: the sole open document is the default binding (the loop-state path is NOT
# a docKey — passing it binds an unregistered document and every read is refused).
$session = Api POST '/sessions' @{
    Name = 'claude-canvas-gate'; ModelProfile = 'xhigh'; Backend = 'claude'
}
$sessionId = $session.id
Api PUT "/sessions/$sessionId/permission" @{ Mode = 'fullAuto' } | Out-Null
$results['session'] = "$sessionId (backend=$($session.backend))"
if ($session.backend -ne 'claude') { throw "Session backend is '$($session.backend)', expected claude." }

# --- B1: canvas-green ---------------------------------------------------------------------------
# Absolute judgment (not a delta): this gate expects a FRESH dev-loop scene. On a warm canvas a
# well-behaved model verifies the existing definition instead of duplicating it — honest agent
# behavior that a delta check misreads as failure (observed live, attempt 3).
$snapshotBefore = Api GET '/dev/snapshot'
$objectsBefore = @($snapshotBefore.canvas.objects).Count
if ($objectsBefore -gt 2) {
    throw "The canvas already has $objectsBefore objects - run this gate against a fresh dev-loop run."
}
$b1Prompt = 'Grasshopper 캔버스에 다음을 만들어줘: 정수 슬라이더 2개(X Count=4, Y Count=3)와 ' +
    '5 간격의 직사각 그리드 포인트들, 그리고 각 포인트에 반지름 1.5의 원. ' +
    '슬라이더가 그리드 개수를 구동해야 한다. 완료 후 한 줄로 보고해줘.'
$b1Status = Send-Turn $sessionId $b1Prompt $TimeoutSeconds
$results['b1-final-status'] = $b1Status
if ($b1Status -ne 'idle') { throw "B1 turn ended '$b1Status', expected idle." }

$snapshot = Api GET '/dev/snapshot'
$objects = @($snapshot.canvas.objects)
$wires = @($snapshot.canvas.wires)
$results['b1-components'] = $objects.Count
$results['b1-wires'] = $wires.Count
if ($objects.Count -lt 3) {
    throw "B1 left only $($objects.Count) component(s) on the canvas, expected >= 3."
}
if ($wires.Count -lt 1) { throw 'B1 produced no wires.' }
$runtimeErrors = @($objects | Where-Object {
        $_.runtimeMessages -and (@($_.runtimeMessages | Where-Object { $_.level -eq 'error' }).Count -gt 0)
    })
$results['b1-runtime-errors'] = $runtimeErrors.Count
if ($runtimeErrors.Count -gt 0) {
    throw "B1 left $($runtimeErrors.Count) component(s) with runtime errors."
}

# --- R12: interrupt mid-turn, then prove --resume continuity ------------------------------------
Api POST "/sessions/$sessionId/messages" @{
    Content = '지금 캔버스의 모든 컴포넌트를 하나씩 아주 자세히 설명해줘.'
    ClientMessageId = [guid]::NewGuid().ToString()
} | Out-Null
Start-Sleep -Seconds 12   # let the CLI spawn and get to work before the kill
Api PUT "/sessions/$sessionId/pause" @{ Paused = $true } | Out-Null
Start-Sleep -Seconds 5
# Un-pause via the same PUT (POST /resume is the recovery-halt endpoint and 409s here).
Api PUT "/sessions/$sessionId/pause" @{ Paused = $false } | Out-Null
$results['r12-interrupted'] = 'paused mid-turn, resumed'

$r12Status = Send-Turn $sessionId ('첫 번째 요청에서 만들어 달라고 한 도형이 뭐였지? ' +
    '원이면 CONTINUITY-OK-CIRCLES 라고만, 아니면 CONTINUITY-LOST 라고만 답해줘.') $TimeoutSeconds
if ($r12Status -ne 'idle') { throw "R12 continuity turn ended '$r12Status', expected idle." }
$transcript = Get-AssistantText $sessionId
if ($transcript -notmatch 'CONTINUITY-OK-CIRCLES') {
    throw 'R12: the conversation did not survive the interrupted spawn (continuity sentinel missing).'
}
$results['r12-continuity'] = 'CONTINUITY-OK-CIRCLES'

# --- B2: committed work, zero dead ends (live-jobs.db read directly, dev-drive 'jobs' pattern) --
$pkg = Join-Path $env:APPDATA 'McNeel\Rhinoceros\packages\8.0\Vino'
$sqlite = Get-ChildItem $pkg -Recurse -Filter 'Microsoft.Data.Sqlite.dll' -File | Select-Object -First 1
if (-not $sqlite) { throw 'Microsoft.Data.Sqlite.dll not found in the installed package.' }
Add-Type -Path $sqlite.FullName
$dbPath = Join-Path $state.runtime 'live-jobs.db'
$conn = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$dbPath;Mode=ReadOnly;Cache=Shared")
$conn.Open()
$jobRows = @()
try {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = 'SELECT state, phase FROM live_jobs WHERE session_id=$sid'
    [void] $cmd.Parameters.AddWithValue('$sid', $sessionId)
    $reader = $cmd.ExecuteReader()
    while ($reader.Read()) {
        $jobRows += [pscustomobject]@{ State = $reader.GetString(0); Phase = $reader.GetString(1) }
    }
    $reader.Close()
}
finally { $conn.Close() }
$committed = @($jobRows | Where-Object { $_.State -eq 'committed' })
$recovery = @($jobRows | Where-Object { $_.State -match 'recovery' -or $_.Phase -match 'recovery' })
$results['b2-jobs'] = $jobRows.Count
$results['b2-committed'] = $committed.Count
$results['b2-recovery-required'] = $recovery.Count
if ($committed.Count -lt 1) { throw 'B2: no committed ChangeSet was observed.' }
if ($recovery.Count -gt 0) { throw "B2: $($recovery.Count) job(s) required recovery (dead end)." }

Write-Host ''
Write-Host '=== gate-claude-canvas PASS ==='
$results.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-24} {1}" -f $_.Key, $_.Value) }
