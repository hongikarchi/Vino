#requires -Version 5.1
# Live gate for structural_size (P4): "가장 가벼운 통과 단면" must come from the ladder walk, under
# the REAL loads, and the sized state must become THE results artifact.
#
# Grading is artifact-first: sizing.json carries the trace (up jumps + down sweep), results.json
# must be all-pass with a steel mass no heavier than the user's earlier hand-picked sections.
# Run with -SessionId from a session that already extracted + (ideally) ran structural_loads.
#
# NOTE: this file must stay UTF-8 WITH BOM (PS 5.1 ANSI trap).
[CmdletBinding()]
param(
    [string]$Run,
    [Parameter(Mandatory = $true)][string]$SessionId,
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
$before = Read-Artifact $SessionId 'structural\results.json'
if (-not $before) { throw 'No prior results - solve (with loads) before sizing.' }
$results['0-mass-before-kg'] = $before.steelMassKg
$results['0-loads-present'] = ($null -ne (Read-Artifact $SessionId 'structural\loads.json'))

# --- 1. one ask: minimum passing sections under the same loads --------------------------------
$results['1-size-turn'] = Send-Turn $SessionId `
    '지금 하중 그대로 두고, 모든 검토를 통과하는 것 중에 가장 가벼운 단면으로 다시 골라줘.' `
    $TimeoutSeconds
$sizing = Read-Artifact $SessionId 'structural\sizing.json'
if (-not $sizing) { throw 'No sizing artifact - structural_size never ran.' }
$after = Read-Artifact $SessionId 'structural\results.json'
$results['1-solves'] = $sizing.solves
$results['1-chosen'] = ($sizing.chosen.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '
$results['1-all-pass'] = ($after.memberChecks.failed -eq 0)
$results['1-mass-after-kg'] = $after.steelMassKg
$results['1-not-heavier'] = ($after.steelMassKg -le $before.steelMassKg * 1.001)
$results['1-walked'] = ($sizing.solves -ge 3)
# Down-minimality evidence: the trace must show at least one attempted step (up or down).
$results['1-trace-nonempty'] = (@($sizing.iterations).Count -ge 1)
$lastReply = ((Api GET "/sessions/$SessionId/messages") | Where-Object { $_.role -eq 'assistant' } | Select-Object -Last 1).content
$results['1-reports-mass'] = ($lastReply -match 'kg')
$results['1-names-screening'] = ($lastReply -match '스크리닝|screen|설계가 아|좌굴')

$pass = $results['1-size-turn'] -eq 'idle' -and
        $results['1-all-pass'] -and
        $results['1-not-heavier'] -and
        $results['1-walked'] -and
        $results['1-trace-nonempty'] -and
        $results['1-reports-mass']
$results['GATE'] = if ($pass) { 'PASS' } else { 'FAIL' }
$results['sessionId'] = $SessionId
[pscustomobject]$results | Format-List
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Run 'gate-structural-size.json') -Encoding utf8
if (-not $pass) { exit 1 }
