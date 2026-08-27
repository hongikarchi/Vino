#requires -Version 5.1
# Live gate for the member solids (P7, Profile->Brep): one ask must put structural_profile.py on
# the canvas VERBATIM with a Toggle, and the baked Rhino solids must reconcile with A x L —
# geometry is graded by volume, never by prose. Curved members must be SWEPT (their group
# reports swept >= 1), because a chorded arch baked as segments would be a fabrication lie.
#
# Run with -SessionId from a solved session on a -SceneKind structural-curves run.
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
function Normalize($text) { return ($text -replace "`r`n", "`n").TrimEnd() }

$results = [ordered]@{}
$report = Read-Artifact $SessionId 'structural\results.json'
if (-not $report) { throw 'No results artifact - nothing to build; solve first.' }
if (-not $report.sectionsUsedDetail) { throw 'results.json has no sectionsUsedDetail - deploy the new solver first.' }

# --- 1. one ask: real member solids ------------------------------------------------------------
$results['1-brep-turn'] = Send-Turn $SessionId `
    '이제 부재들을 실제 형강 솔리드로 만들어서 모델에 넣어줘. 아치는 곡선 그대로 휘어진 형강으로.' `
    $TimeoutSeconds

# --- 2. the payload, verbatim, with its self-check read from the CANVAS ------------------------
$snapshot = Api GET '/dev/snapshot'
$objects = @($snapshot.canvas.objects)
$shipped = Normalize (Get-Content (Join-Path $repo 'assets\skills\structural_profile.py') -Raw -Encoding UTF8)
$componentId = $null
foreach ($obj in @($objects | Where-Object { $_.componentTypeId -eq '719467e6-7cf5-4848-99b0-c5dd57e5442c' })) {
    try {
        $py = Api GET "/dev/grasshopper/$($obj.objectId)/python"
        foreach ($inspection in @($py.inspections)) {
            $r = $inspection.result
            if ($null -ne $r -and $r.PSObject.Properties['source'] -and $r.source) {
                if ((Normalize ([string]$r.source)) -eq $shipped) { $componentId = $obj.objectId }
            }
        }
    } catch { }
}
$results['2-payload-verbatim'] = ($null -ne $componentId)
$results['2-has-toggle'] = [bool]@($objects | Where-Object { $_.componentTypeId -eq '2e78987b-9dfb-42a2-8b76-3923ac8bd91a' })
$selfCheck = $null
if ($componentId) {
    $outputs = Api GET "/dev/grasshopper/$componentId/outputs"
    $raw = ($outputs | ConvertTo-Json -Depth 12 -Compress)
    if ($raw -match '\{\\?"groups\\?":.*?\\?"assumptions\\?":.*?\}') {
        $selfCheck = ($Matches[0] -replace '\\"', '"') | ConvertFrom-Json
    }
}
if ($selfCheck) {
    $results['3-groups'] = $selfCheck.groups
    $results['3-swept'] = $selfCheck.swept
    $results['3-expected-m3'] = $selfCheck.expectedVolumeM3
    $results['3-actual-m3'] = $selfCheck.actualVolumeM3
    $results['3-curved-swept'] = ($selfCheck.swept -ge 1)
    $results['3-volume-reconciles'] = ($selfCheck.expectedVolumeM3 -gt 0 -and
        [math]::Abs($selfCheck.actualVolumeM3 - $selfCheck.expectedVolumeM3) / $selfCheck.expectedVolumeM3 -lt 0.08)
} else {
    $results['3-selfcheck-readable'] = $false
}

# --- 4. real solids in the document ------------------------------------------------------------
$solids = @((Api GET '/dev/rhino-objects').result.objects | Where-Object { $_.name -match '^structural-member-' })
$results['4-solids'] = $solids.Count
$results['4-solids-exist'] = ($solids.Count -ge 6)
$onLayer = @($solids | Where-Object { $_.layerFullPath -match '^Vino::Structural::' })
$results['4-on-vino-layer'] = ($onLayer.Count -eq $solids.Count -and $solids.Count -gt 0)

$pass = $results['1-brep-turn'] -eq 'idle' -and
        $results['2-payload-verbatim'] -and
        $results['2-has-toggle'] -and
        $null -ne $selfCheck -and
        $results['3-curved-swept'] -and
        $results['3-volume-reconciles'] -and
        $results['4-solids-exist'] -and
        $results['4-on-vino-layer']
$results['GATE'] = if ($pass) { 'PASS' } else { 'FAIL' }
$results['sessionId'] = $SessionId
[pscustomobject]$results | Format-List
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Run 'gate-structural-brep.json') -Encoding utf8
if (-not $pass) { exit 1 }
