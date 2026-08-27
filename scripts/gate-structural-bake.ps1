#requires -Version 5.1
# Live gate for the diagnosis bake (P1 persistence): one ask must put structural_bake.py on the
# canvas VERBATIM, trigger it through a Toggle (never a Button), and leave verdict-colored axes
# as REAL Rhino objects on Vino::Structural band layers with the bake-family identity.
#
# Grading reads the Rhino document (/dev/rhino-objects) and the canvas (/dev/snapshot +
# /dev/grasshopper/{id}/python), never the prose. Needs a session that already solved —
# pass -SessionId from a prior gate. Fixture: -SceneKind structural-curves.
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
function Normalize($text) { return ($text -replace "`r`n", "`n").TrimEnd() }

$results = [ordered]@{}
$report = Read-Artifact $SessionId 'structural\results.json'
if (-not $report) { throw 'No results artifact - nothing to bake; solve first.' }
$expectedEdges = $report.edgesSolved
$before = @((Api GET '/dev/rhino-objects').result.objects | Where-Object { $_.name -match '^structural-diag-' }).Count
$results['0-diag-objects-before'] = $before
$results['0-edges-solved'] = $expectedEdges

# --- 1. one ask: persist the diagnosis ---------------------------------------------------------
$results['1-bake-turn'] = Send-Turn $SessionId `
    '지금 진단을 라이노 모델에도 남겨줘. 변형 없이 축선 상태로, Vino::Structural 아래에 정리해서.' `
    $TimeoutSeconds

# --- 2. the Rhino document is the proof --------------------------------------------------------
$objects = @((Api GET '/dev/rhino-objects').result.objects)
$diag = @($objects | Where-Object { $_.name -match '^structural-diag-' })
$results['2-diag-objects'] = $diag.Count
$results['2-count-matches-edges'] = ($diag.Count -ge [math]::Max(10, $expectedEdges - 3) -and $diag.Count -le $expectedEdges + 6)
$onBandLayers = @($diag | Where-Object { $_.layerFullPath -match '^Vino::Structural::' })
$results['2-on-band-layers'] = $onBandLayers.Count
$results['2-all-under-root'] = ($onBandLayers.Count -eq $diag.Count -and $diag.Count -gt 0)
$results['2-band-layers-used'] = (@($diag | ForEach-Object { $_.layerFullPath } | Sort-Object -Unique)).Count

# --- 3. the payload on the canvas is the shipped one, triggered by a Toggle -------------------
$snapshot = Api GET '/dev/snapshot'
$objectsOnCanvas = @($snapshot.canvas.objects)
$shipped = Normalize (Get-Content (Join-Path $repo 'assets\skills\structural_bake.py') -Raw -Encoding UTF8)
$bakeComponent = $null
foreach ($obj in @($objectsOnCanvas | Where-Object { $_.componentTypeId -eq '719467e6-7cf5-4848-99b0-c5dd57e5442c' })) {
    try {
        $py = Api GET "/dev/grasshopper/$($obj.objectId)/python"
        foreach ($inspection in @($py.inspections)) {
            $r = $inspection.result
            if ($null -ne $r -and $r.PSObject.Properties['source'] -and $r.source) {
                if ((Normalize ([string]$r.source)) -eq $shipped) { $bakeComponent = $obj }
            }
        }
    } catch { }
}
$results['3-payload-verbatim'] = ($null -ne $bakeComponent)
# A Boolean Toggle must exist (Button writes are impossible for Vino and the recipe says so).
$results['3-has-toggle'] = [bool]@($objectsOnCanvas | Where-Object { $_.componentTypeId -eq '2e78987b-9dfb-42a2-8b76-3923ac8bd91a' })

$pass = $results['1-bake-turn'] -eq 'idle' -and
        $results['2-count-matches-edges'] -and
        $results['2-all-under-root'] -and
        $results['2-band-layers-used'] -ge 2 -and
        $results['3-payload-verbatim'] -and
        $results['3-has-toggle']
$results['GATE'] = if ($pass) { 'PASS' } else { 'FAIL' }
$results['sessionId'] = $SessionId
[pscustomobject]$results | Format-List
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Run 'gate-structural-bake.json') -Encoding utf8
if (-not $pass) { exit 1 }
