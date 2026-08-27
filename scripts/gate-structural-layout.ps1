#requires -Version 5.1
# Live gate for structural_layout (P3): secondary beams are an OFFER — the agent proposes
# candidates from the bays, avoids the slab opening, and draws NOTHING without approval.
#
#   ask (spacing + avoid the void) -> structural_layout -> proposal artifact graded against the
#   fixture's closed forms -> the Rhino document must be untouched.
#
# Run with -SessionId from a prior structural gate on a -SceneKind structural-curves run.
#
# NOTE: this file must stay UTF-8 WITH BOM (PS 5.1 ANSI trap).
[CmdletBinding()]
param(
    [string]$Run,
    [Parameter(Mandatory = $true)][string]$SessionId,
    [int]$TimeoutSeconds = 480
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $Run) {
    $Run = (Get-ChildItem (Join-Path $repo 'artifacts\dev-loop') -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'loop-state.json') } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
$state = Get-Content (Join-Path $Run 'loop-state.json') -Raw | ConvertFrom-Json
if ($state.scene3dm -notmatch 'structural-curves') {
    throw "This gate needs a -SceneKind structural-curves run; got $($state.scene3dm)"
}
$base = $state.uiBaseUrl.TrimEnd('/') + '/api/v1'
$headers = @{ 'X-Vino-Token' = $state.token }
function Api($method, $path, $body) {
    $uri = $base + $path
    if ($null -ne $body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 8 -Compress))
        return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -Body $bytes `
            -ContentType 'application/json; charset=utf-8' -TimeoutSec 120
    }
    return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec 120
}
function Send-Turn($sid, $text, $seconds) {
    Api POST "/sessions/$sid/messages" @{ Content = $text; ClientMessageId = [guid]::NewGuid().ToString() } | Out-Null
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        Start-Sleep -Seconds 5
        $s = (Api GET '/runtime').sessions | Where-Object { $_.id -eq $sid }
        $status = if ($s) { $s.status } else { 'gone' }
    } while ($status -eq 'working' -and (Get-Date) -lt $deadline)
    return $status
}
function Read-Artifact($sid, $relative) {
    $sidn = ([guid]$sid).ToString('N')
    $path = Join-Path $Run "runtime\artifacts\$sidn\$relative"
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

$results = [ordered]@{}
if (-not (Read-Artifact $SessionId 'structural\members.json')) { throw 'No extraction artifact - extract first.' }
$objectsBefore = @((Api GET '/dev/rhino-objects').result.objects).Count
$results['0-rhino-objects-before'] = $objectsBefore

# --- 1. ask for candidates: tight spacing so the opening actually bites -----------------------
$results['1-layout-turn'] = Send-Turn $SessionId `
    ('바닥 골조에 작은보를 넣어볼까 하는데, 일단 제안만 해줘 — 간격은 800mm 정도로, ' +
     'Slab 레이어에 슬래브가 없는 개구부 위에는 보가 생기면 안 돼. 아직 모델에 그리지는 말고.') `
    $TimeoutSeconds
$layout = Read-Artifact $SessionId 'structural\layout.json'
if (-not $layout) { throw 'No layout artifact - structural_layout never ran.' }
$results['1-bays'] = $layout.bayCount
$results['1-beams'] = $layout.beamCount
$results['1-bays-found'] = ($layout.bayCount -ge 2)
$results['1-candidates-exist'] = ($layout.beamCount -ge 4)
# The 800x800 opening (x 3000..3800, y 2000..2800) must carry NO candidate point.
$inHole = 0
foreach ($beam in $layout.beams) {
    foreach ($p in @($beam.a, $beam.b)) {
        if ($p[0] -gt 3050 -and $p[0] -lt 3750 -and $p[1] -gt 2050 -and $p[1] -lt 2750) { $inHole++ }
    }
    $mx = ($beam.a[0] + $beam.b[0]) / 2.0
    $my = ($beam.a[1] + $beam.b[1]) / 2.0
    if ($mx -gt 3050 -and $mx -lt 3750 -and $my -gt 2050 -and $my -lt 2750) { $inHole++ }
}
$results['1-hole-candidates'] = $inHole
$results['1-void-respected'] = ($inHole -eq 0)
$results['1-void-trim-reported'] = ($layout.removedByVoidM -gt 0)

# --- 2. nothing drawn: proposal only ----------------------------------------------------------
$objectsAfter = @((Api GET '/dev/rhino-objects').result.objects).Count
$results['2-rhino-objects-after'] = $objectsAfter
$results['2-nothing-drawn'] = ($objectsAfter -eq $objectsBefore)
$lastReply = ((Api GET "/sessions/$SessionId/messages") | Where-Object { $_.role -eq 'assistant' } | Select-Object -Last 1).content
$results['2-presents-counts'] = ($lastReply -match "$($layout.beamCount)")

$pass = $results['1-layout-turn'] -eq 'idle' -and
        $results['1-bays-found'] -and
        $results['1-candidates-exist'] -and
        $results['1-void-respected'] -and
        $results['1-void-trim-reported'] -and
        $results['2-nothing-drawn']
$results['GATE'] = if ($pass) { 'PASS' } else { 'FAIL' }
$results['sessionId'] = $SessionId
[pscustomobject]$results | Format-List
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Run 'gate-structural-layout.json') -Encoding utf8
if (-not $pass) { exit 1 }
