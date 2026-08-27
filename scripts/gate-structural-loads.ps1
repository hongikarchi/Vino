#requires -Version 5.1
# Live gate for structural_loads (P2): modeled load geometry -> confirmed densities ->
# per-member line loads -> re-solve. Two layers of proof, neither trusting prose:
#
#   0. MODEL-FREE: /dev/structural-load-sample must reproduce the fixture's closed-form volumes
#      (slab (12 - 0.64) x 0.15 = 1.704 m3 with the 800x800 opening EMPTY; soil 0.9 m3).
#   1. One turn hands the agent materials and use; the loads artifact must carry the right
#      totals, the unassigned drops, and the re-solve must apply them (loads in results.json).
#
# Run after gate-structural-curves.ps1 with its -SessionId (needs the extraction + a solve);
# without -SessionId it runs the solve turns itself. Fixture: -SceneKind structural-curves.
#
# NOTE: this file must stay UTF-8 WITH BOM (PS 5.1 ANSI trap).
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

# --- 0. model-free sampler grading against closed-form volumes --------------------------------
function Probe-Volume($layer) {
    $probe = (Api GET "/dev/structural-load-sample?layerFilter=$layer&gridSpacing=250").result
    $entry = $probe.sources[0]
    $volume = 0.0
    foreach ($sample in $entry.samples) { $volume += $sample.thickness * $entry.cellArea }
    return @{ volumeM3 = $volume / 1e9; cells = @($entry.samples | Where-Object { $_.thickness -gt 0 }).Count
              cellArea = $entry.cellArea; samples = $entry.samples }
}
$slab = Probe-Volume 'Slab'
$soil = Probe-Volume 'Landscape'
$results['0-slab-volume-m3'] = [math]::Round($slab.volumeM3, 3)
$results['0-soil-volume-m3'] = [math]::Round($soil.volumeM3, 3)
if ([math]::Abs($slab.volumeM3 - 1.704) / 1.704 -gt 0.05) { throw "Slab sampled volume $($slab.volumeM3) m3 is off the closed form 1.704 m3." }
if ([math]::Abs($soil.volumeM3 - 0.9) / 0.9 -gt 0.05) { throw "Soil sampled volume $($soil.volumeM3) m3 is off the closed form 0.9 m3." }
# The opening must be EMPTY: no loaded sample may sit inside the 800x800 hole (edge cells aside).
$inHole = @($slab.samples | Where-Object {
    $_.thickness -gt 0 -and $_.x -gt 3050 -and $_.x -lt 3750 -and $_.y -gt 2050 -and $_.y -lt 2750 })
$results['0-hole-samples'] = $inHole.Count
if ($inHole.Count -gt 0) { throw "The slab opening carries $($inHole.Count) loaded samples - the void is not void." }

# --- 1. a solved session --------------------------------------------------------------------
if (-not $SessionId) {
    $SessionId = (Api POST '/sessions' @{ Name = 'structural-loads-gate'; ModelProfile = 'xhigh' }).id
    $results['1-extract-turn'] = Send-Turn $SessionId `
        'Structure와 Arch 레이어에 그려둔 선들이 철골 골조 축선이야. 이 선들로 구조 해석을 해줘. 모델에 없는 정보는 해석 전에 한 번에 물어봐.' $TimeoutSeconds
    $results['1-solve-turn'] = Send-Turn $SessionId `
        ('튀어나온 보는 의도한 캔틸레버야. Supports 레이어 점 4개가 고정 지점이야(아치 발도 고정). ' +
         '기둥 H-300x300x10x15, 보 H-400x200x8x13, 아치 H-200x200x8x12. 하중조합은 KDS 1.2G+1.6Q.') $TimeoutSeconds
}
if (-not (Read-Artifact $SessionId 'structural\members.json')) { throw 'No extraction artifact - nothing to load.' }

# --- 2. hand over materials and use; the loads must land and the solve must apply them --------
$results['2-loads-turn'] = Send-Turn $SessionId `
    ('이제 위에 올라가는 하중도 반영하자. Slab 레이어는 철근콘크리트 150mm 슬래브이고 옥상정원 용도야(활하중은 KDS 표의 값으로). ' +
     'Landscape 레이어는 조경토(습윤)이고 토심은 모델 그대로야. 밀도는 표준값을 쓰면 돼. 하중 산정해서 해석을 다시 돌려줘.') `
    $TimeoutSeconds
$loads = Read-Artifact $SessionId 'structural\loads.json'
# The house rules DEMAND a confirmation of the table values before computing — an ask card
# here is the designed behavior, not a failure. Confirm once and let the turn finish.
if (-not $loads) {
    $pending = (Api GET '/runtime').sessions | Where-Object { $_.id -eq $SessionId }
    if ($pending -and $pending.askCard -and ([string]$pending.askCard) -match '"status":\s*"asking"') {
        $results['2-confirm-card-shown'] = $true
        $results['2-confirm-turn'] = Send-Turn $SessionId '응, 그 표준값 그대로 적용해서 계속 진행해줘.' $TimeoutSeconds
        $loads = Read-Artifact $SessionId 'structural\loads.json'
    }
}
if (-not $loads) { throw 'No loads artifact - structural_loads never ran (even after confirming).' }
$deadKn = 0.0; $liveKn = 0.0
foreach ($s in $loads.sources) { $deadKn += $s.deadKn; $liveKn += $s.liveKn }
$results['2-dead-kn'] = [math]::Round($deadKn, 1)
$results['2-live-kn'] = [math]::Round($liveKn, 1)
# Closed form: 24 x 1.704 + 18 x 0.9 = 57.1 kN dead; roof-garden 5.0 x 11.36 m2 = 56.8 kN live.
$results['2-dead-matches'] = ([math]::Abs($deadKn - 57.1) / 57.1 -lt 0.07)
$results['2-live-matches'] = ([math]::Abs($liveKn - 56.8) / 56.8 -lt 0.07)
$results['2-unassigned-small'] = (($loads.unassigned.deadKn + $loads.unassigned.liveKn) -lt 0.1 * ($deadKn + $liveKn))
$report = Read-Artifact $SessionId 'structural\results.json'
if (-not $report) { throw 'No results artifact after the loads turn.' }
$applied = $report.loads.lineLoadKn.G + $report.loads.lineLoadKn.Q
$results['2-solve-applied-kn'] = [math]::Round($applied, 1)
$assigned = $deadKn + $liveKn - $loads.unassigned.deadKn - $loads.unassigned.liveKn
$results['2-solve-carries-loads'] = ($applied -gt 0.8 * $assigned)
# The stand-in 5 kN/m from the earlier turn must be REPLACED, not stacked: the applied line
# loads may not meaningfully exceed what the distribution produced (first live run: 212 kN
# applied against 114 kN distributed - the stand-in and the slab live load were both in).
$results['2-no-double-count'] = ($applied -lt 1.15 * $assigned)
$results['2-equilibrium-ok'] = ([math]::Abs($report.equilibriumErrorPercent) -lt 0.5)
$results['2-checks'] = "$($report.memberChecks.passed)/$($report.memberChecks.checked) passed"
$lastReply = [string]((Api GET "/sessions/$SessionId/messages") | Where-Object { $_.role -eq 'assistant' } | Select-Object -Last 1).content
$sessionState = (Api GET '/runtime').sessions | Where-Object { $_.id -eq $SessionId }
if ($sessionState -and $sessionState.askCard) { $lastReply += ' ' + [string]$sessionState.askCard }
$results['2-names-table-values'] = ($lastReply -match '24' -and $lastReply -match '18|조경토' -and $lastReply -match '5')

$pass = $results['2-loads-turn'] -eq 'idle' -and
        $results['2-no-double-count'] -and
        $results['2-dead-matches'] -and
        $results['2-live-matches'] -and
        $results['2-unassigned-small'] -and
        $results['2-solve-carries-loads'] -and
        $results['2-equilibrium-ok'] -and
        $results['2-names-table-values']
$results['GATE'] = if ($pass) { 'PASS' } else { 'FAIL' }
$results['sessionId'] = $SessionId
[pscustomobject]$results | Format-List
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Run 'gate-structural-loads.json') -Encoding utf8
if (-not $pass) { exit 1 }
