#requires -Version 5.1
# Live gate for the structural diagnosis viewer (P1): after a curve-input solve, one ask puts the
# vetted structural_viewer.py payload on the canvas — VERBATIM — wired to a Panel (results path),
# a Number Slider (displacement scale), and a Custom Preview.
#
# Verification never trusts prose: the payload source is byte-compared against the shipped asset
# via /dev/grasshopper/{id}/python, the wiring is counted from /dev/snapshot, and the component
# must carry no runtime errors. Run it after gate-structural-curves.ps1 on the same run, passing
# -SessionId from that gate's output so the solve artifact already exists; without -SessionId it
# runs the two solve turns itself (same fixture contract: -SceneKind structural-curves).
#
# NOTE: this file must stay UTF-8 WITH BOM — PS 5.1 reads a BOM-less .ps1 in the ANSI codepage and
# turns the Korean prompts into mojibake the agent then answers instead of the real question.
[CmdletBinding()]
param(
    [string]$Run,
    [string]$SessionId,
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
            -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
    }
    return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec 60
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
function Normalize($text) { return ($text -replace "`r`n", "`n").TrimEnd() }

$results = [ordered]@{}

# --- 0. a solved session: reuse the curves gate's, or run the two solve turns here -------------
if (-not $SessionId) {
    $SessionId = (Api POST '/sessions' @{ Name = 'structural-viewer-gate'; ModelProfile = 'xhigh' }).id
    $results['0-extract-turn'] = Send-Turn $SessionId `
        'Structure 레이어의 선들로 구조 해석을 해줘. 모델에 없는 정보는 해석 전에 한 번에 물어봐.' $TimeoutSeconds
    $results['0-solve-turn'] = Send-Turn $SessionId `
        ('튀어나온 보는 의도한 캔틸레버야. Supports 레이어 점 4개가 고정 지점이야(아치 발도 고정). ' +
         '기둥 H-300x300x10x15, 보 H-400x200x8x13, 아치 H-200x200x8x12. 보에 활하중 5 kN/m.') $TimeoutSeconds
}
if (-not (Read-Artifact $SessionId 'structural\results.json')) {
    throw 'No results artifact in the session — the viewer has nothing to show; solve first.'
}
$before = @((Api GET '/dev/snapshot').canvas.objects).Count
$results['0-canvas-objects-before'] = $before

# --- 1. ask for the diagnosis view ------------------------------------------------------------
$PythonTypeId = '719467e6-7cf5-4848-99b0-c5dd57e5442c'
$SliderTypeId = '57da07bd-ecab-415d-9d86-af36d7073abc'
$CustomPreviewTypeId = '537b0419-bbc2-4ff4-bf08-afe526367b2c'
$results['1-viewer-turn'] = Send-Turn $SessionId `
    '지금 해석 결과를 캔버스에서 진단으로 보여줘. 부담이 큰 부재일수록 빨갛게, 변형 배율 슬라이더도 달아줘.' `
    $TimeoutSeconds

$snapshot = Api GET '/dev/snapshot'
$objects = @($snapshot.canvas.objects)
$wires = @($snapshot.canvas.wires)
$results['1-canvas-objects-after'] = $objects.Count
$results['1-new-objects'] = $objects.Count - $before
$results['1-wires'] = $wires.Count

# --- 2. the payload must be on the canvas VERBATIM --------------------------------------------
# /dev/grasshopper/{id}/python wraps the read: inspections[] carries python.readSource (result
# .source) and python.runtimeMessages (result.messages) — grade both from the same call.
# -Encoding UTF8 is load-bearing: PS 5.1 reads BOM-less files in the ANSI codepage, and the
# payload's UTF-8 punctuation then never matches the canvas source (the first rerun's FAIL).
$shipped = Normalize (Get-Content (Join-Path $repo 'assets\skills\structural_viewer.py') -Raw -Encoding UTF8)
$viewerId = $null
$viewerErrors = -1
foreach ($obj in @($objects | Where-Object { $_.componentTypeId -eq $PythonTypeId })) {
    try {
        $py = Api GET "/dev/grasshopper/$($obj.objectId)/python"
        $source = $null
        $messages = @()
        foreach ($inspection in @($py.inspections)) {
            $r = $inspection.result
            if ($null -eq $r) { continue }
            if ($r.PSObject.Properties['source'] -and $r.source) { $source = [string]$r.source }
            if ($r.PSObject.Properties['messages']) { $messages += @($r.messages) }
        }
        if ($source -and (Normalize $source) -eq $shipped) {
            $viewerId = $obj.objectId
            $viewerErrors = @($messages | Where-Object { "$_" -match '(?i)error' }).Count
            break
        }
    } catch { }
}
$results['2-payload-verbatim'] = ($null -ne $viewerId)
if ($viewerId) {
    $viewerWires = @($wires | Where-Object {
        $_.sourceObjectId -eq $viewerId -or $_.targetObjectId -eq $viewerId })
    $results['2-viewer-wires'] = $viewerWires.Count
    $results['2-viewer-runtime-errors'] = $viewerErrors
}

# --- 3. the supporting cast, by component TYPE (display names are the agent's to choose) ------
$results['3-has-slider'] = [bool]@($objects | Where-Object { $_.componentTypeId -eq $SliderTypeId })
$results['3-has-custom-preview'] = [bool]@($objects | Where-Object { $_.componentTypeId -eq $CustomPreviewTypeId })

$pass = $results['1-viewer-turn'] -eq 'idle' -and
        $results['1-new-objects'] -ge 3 -and
        $results['2-payload-verbatim'] -and
        $results['2-viewer-wires'] -ge 3 -and
        $results['2-viewer-runtime-errors'] -eq 0 -and
        $results['3-has-slider'] -and
        $results['3-has-custom-preview']
$results['GATE'] = if ($pass) { 'PASS' } else { 'FAIL' }
$results['sessionId'] = $SessionId
[pscustomobject]$results | Format-List
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Run 'gate-structural-viewer.json') -Encoding utf8
if (-not $pass) { exit 1 }
