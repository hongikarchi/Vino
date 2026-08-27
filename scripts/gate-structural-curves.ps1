#requires -Version 5.1
# Live gate for the CURVE-INPUT structural workflow — the way a designer actually hands Vino a
# structure: axis lines on one ordinary layer, a ring beam drawn as a single closed polyline, a
# secondary landing mid-span, one deliberate cantilever, point objects at the supports, an arch.
#
#   structural_extract (polyline explosion, roles, point objects)
#     -> ask-back in ONE message (cantilever? sections per role? supports? loads?)
#     -> answers -> structural_solve (roleSections, supportType, lineLoads, G/Q combos)
#     -> verify from the results artifact, never from prose
#
# Needs a dev-loop run booted with -SceneKind structural-curves. The fixture is graded server-side
# first (/dev/structural-extract): 17 members, 7 free ends, 4 point objects — a mismatch means the
# gate is running against the wrong scene and would prove nothing.
#
# NOTE: this file must stay UTF-8 WITH BOM — PS 5.1 reads a BOM-less .ps1 in the ANSI codepage and
# turns the Korean prompts into mojibake the agent then answers instead of the real question.
[CmdletBinding()]
param(
    [string]$Run,
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
function Send-Turn($sessionId, $text, $seconds) {
    Api POST "/sessions/$sessionId/messages" @{ Content = $text; ClientMessageId = [guid]::NewGuid().ToString() } | Out-Null
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        Start-Sleep -Seconds 5
        $s = (Api GET '/runtime').sessions | Where-Object { $_.id -eq $sessionId }
        $status = if ($s) { $s.status } else { 'gone' }
    } while ($status -eq 'working' -and (Get-Date) -lt $deadline)
    return $status
}
function Read-Artifact($sessionId, $relative) {
    $sidn = ([guid]$sessionId).ToString('N')
    $path = Join-Path $Run "runtime\artifacts\$sidn\$relative"
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
function Last-Reply($sessionId) {
    return ((Api GET "/sessions/$sessionId/messages") | Where-Object { $_.role -eq 'assistant' } | Select-Object -Last 1).content
}
function Ask-Text($sessionId) {
    # The ask can travel as prose OR as an ask card (no assistant message at all) — grade both.
    $text = [string](Last-Reply $sessionId)
    $s = (Api GET '/runtime').sessions | Where-Object { $_.id -eq $sessionId }
    if ($s -and $s.askCard) { $text += ' ' + [string]$s.askCard }
    return $text
}

$results = [ordered]@{}

# --- 0. grade the extraction server-side, before any model sees the document ----
$probe = (Api GET '/dev/structural-extract').result
$results['0-members'] = $probe.members.Count
$results['0-free-ends'] = $probe.freeEnds.Count
$results['0-points'] = $probe.pointObjects.Count
$roles = @{}
foreach ($m in $probe.members) { $roles[$m.role] = 1 + $(if ($roles.ContainsKey($m.role)) { $roles[$m.role] } else { 0 }) }
$results['0-roles'] = ($roles.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '
$chords = @($probe.members | Where-Object { $_.kind -eq 'curve-discretized' }).Count
$results['0-arch-chords'] = $chords
if ($probe.members.Count -ne 17 -or $probe.freeEnds.Count -ne 7 -or $probe.pointObjects.Count -ne 4 -or $chords -ne 7) {
    throw "Fixture mismatch: expected 17 members / 7 free ends / 4 points / 7 chords, got " +
        "$($probe.members.Count) / $($probe.freeEnds.Count) / $($probe.pointObjects.Count) / $chords. " +
        "A gate graded against the wrong fixture proves nothing."
}
# The polyline ring must have become FOUR members: the frame has exactly 4 columns and, at the
# roof, 4 ring segments + the secondary + the cantilever = 6 beams (arch chords add their own).
if ($roles['column'] -lt 4 -or $roles['beam'] -lt 6) {
    throw "Role mismatch: expected >=4 columns and >=6 beams, got $($results['0-roles'])."
}
# The deliberate cantilever tip is the only elevated free end that is not an arch foot.
$tip = $probe.freeEnds | Where-Object { $_.point.z -gt 100 } | Select-Object -First 1
if (-not $tip) { throw 'The fixture lost its deliberate elevated free end.' }
$tipPoint = @($tip.point.x, $tip.point.y, $tip.point.z)

# --- 1. hand over the curves; the agent must extract and STOP to ask -----------
$sessionId = (Api POST '/sessions' @{ Name = 'structural-curves-gate'; ModelProfile = 'xhigh' }).id
$results['1-extract-turn'] = Send-Turn $sessionId `
    'Structure와 Arch 레이어에 그려둔 선들이 철골 골조 축선이야. 이 선들로 구조 해석을 해줘. 해석에 필요한데 모델에 없는 정보가 있으면 해석 전에 한 번에 물어봐.' `
    $TimeoutSeconds
$results['1-members-artifact'] = ($null -ne (Read-Artifact $sessionId 'structural\members.json'))
$results['1-no-premature-solve'] = ($null -eq (Read-Artifact $sessionId 'structural\results.json'))
$askReply = Ask-Text $sessionId
# Chips in prose, or real object ids inside the ask card — either way the question POINTS.
$results['1-asked-with-focus-chips'] = ($askReply -match '\[\[focus:[0-9a-fA-F-]{36}' -or
    $askReply -match '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
# The ask must cover what the model cannot know: sections (단면), supports (지점), loads (하중).
$results['1-asked-sections'] = ($askReply -match '단면|H-\d{3}')
$results['1-asked-supports'] = ($askReply -match '지점|지지')
$results['1-asked-loads'] = ($askReply -match '하중')

# --- 2. answer everything at once; the agent must solve with the answers --------
$results['2-solve-turn'] = Send-Turn $sessionId `
    ('튀어나온 보는 의도한 캔틸레버야. Supports 레이어의 점 4개가 지점이고 기초에 고정이야(아치 발도 고정). ' +
     '단면은 기둥 H-300x300x10x15, 보 H-400x200x8x13, 아치는 H-200x200x8x12로 해줘. ' +
     '보에는 활하중으로 5 kN/m 선하중을 얹고, 하중조합은 KDS 1.2G+1.6Q로 해줘.') `
    $TimeoutSeconds
$report = Read-Artifact $sessionId 'structural\results.json'
if (-not $report) { throw 'No results artifact — structural_solve never ran.' }
$results['2-equilibrium-ok'] = ([math]::Abs($report.equilibriumErrorPercent) -lt 0.5)
$results['2-supports'] = $report.supports
$results['2-support-type-fixed'] = ($report.supportDetail.type -eq 'fixed')
$results['2-components'] = $report.componentsSolved
$results['2-no-unapproved-repair'] = ($report.repairedFreeEnds -eq 0)
# Turn 2 names the arch: nothing the user described may be silently dropped from the solve.
$results['2-no-islands'] = ($report.islandEdgesDropped -eq 0)
$confirmedTip = $report.freeEndsRemaining | Where-Object {
    $_.confirmedCantilever -and
    ([math]::Abs($_.xyzMm[0] - $tipPoint[0]) -lt 400) -and
    ([math]::Abs($_.xyzMm[1] - $tipPoint[1]) -lt 400) -and
    ([math]::Abs($_.xyzMm[2] - $tipPoint[2]) -lt 400)
}
$results['2-cantilever-answer-threaded'] = (@($confirmedTip).Count -ge 1)
# Sections by ROLE reached the solver: columns on H-300, beams on H-400 (the ring polyline
# segments included), arch chords on H-200.
$sections = @{}
foreach ($p in $report.sectionsUsed.PSObject.Properties) { $sections[$p.Name] = $p.Value }
$results['2-sections-used'] = ($sections.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '
$results['2-role-sections-threaded'] = ($sections['H-300x300x10x15'] -ge 4 -and $sections['H-400x200x8x13'] -ge 6 -and $sections['H-200x200x8x12'] -ge 1)
$results['2-live-load-applied'] = ($report.loads.lineLoadKn.Q -gt 0)
$results['2-kds-factors'] = ($report.loads.combos.ULS -match '1\.2G \+ 1\.6Q')
$results['2-utilization-reported'] = ($null -ne $report.maxUtilization)
$results['2-warnings'] = ($report.warnings -join ' | ')
$results['2-checks'] = "$($report.memberChecks.passed)/$($report.memberChecks.checked) passed"
$finalReply = Last-Reply $sessionId
$results['2-verdict-points-at-objects'] = ($finalReply -match '\[\[focus:[0-9a-fA-F-]{36}')
# The report must name its assumptions: support type and the screen's scope (no design check).
$results['2-names-support-assumption'] = ($finalReply -match '고정|지점')
$results['2-names-screen-scope'] = ($finalReply -match '스크리닝|screen|설계 검토가 아|좌굴')

$pass = $results['1-extract-turn'] -eq 'idle' -and
        $results['2-solve-turn'] -eq 'idle' -and
        $results['1-members-artifact'] -and
        $results['1-no-premature-solve'] -and
        $results['1-asked-with-focus-chips'] -and
        $results['1-asked-sections'] -and
        $results['1-asked-supports'] -and
        $results['1-asked-loads'] -and
        $results['2-equilibrium-ok'] -and
        $results['2-support-type-fixed'] -and
        $results['2-no-unapproved-repair'] -and
        $results['2-no-islands'] -and
        $results['2-cantilever-answer-threaded'] -and
        $results['2-role-sections-threaded'] -and
        $results['2-live-load-applied'] -and
        $results['2-kds-factors'] -and
        $results['2-utilization-reported'] -and
        $results['2-verdict-points-at-objects'] -and
        $results['2-names-support-assumption']
$results['GATE'] = if ($pass) { 'PASS' } else { 'FAIL' }
$results['sessionId'] = $sessionId
[pscustomobject]$results | Format-List
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Run 'gate-structural-curves.json') -Encoding utf8
if (-not $pass) { exit 1 }
