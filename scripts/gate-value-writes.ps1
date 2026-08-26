#requires -Version 5.1
# Live gate for the value-write and layout-rewind work (log review 2026-08-26).
#
#   V1 read    : a Value List, Boolean Toggle, Panel and Button each report a valueJson AND a
#                valueFingerprint in the snapshot. Before this change only a Number Slider did, so
#                those controls had no concurrency guard and the model could not read them at all.
#   V2 write   : canvas.setInputValue sets the three WRITABLE controls for real, and the LIVE
#                document reads back exactly what was asked for. A Button is read-only by design:
#                assigning its expressions opens Grasshopper's breakpoint modal and blocks the
#                bridge past its budget — found by this gate on 2026-08-26, twice.
#   V3 refuse  : a payload aimed at the wrong control type is refused and the document is unchanged.
#   R1 rewind  : after an arrange moves components, rewind_layout with restoreStateBefore puts every
#                one of them back to its exact pre-move pivot.
#
# No model in the loop: every step drives /dev endpoints, so the gate asserts exactly what it grades
# and costs no subscription quota.
#
# Needs: a dev-loop run (scripts/dev-loop.ps1).
# NOTE: this file must stay UTF-8 WITH BOM (PS 5.1 reads BOM-less .ps1 as ANSI).
[CmdletBinding()]
param(
    [string]$Run,
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $Run) {
    $Run = (Get-ChildItem (Join-Path $repo 'artifacts\dev-loop') -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'loop-state.json') } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $Run) { throw 'No dev-loop run found. Launch scripts/dev-loop.ps1 first.' }
$state = Get-Content (Join-Path $Run 'loop-state.json') -Raw | ConvertFrom-Json
$base = $state.uiBaseUrl.TrimEnd('/') + '/api/v1'
$headers = @{ 'X-Vino-Token' = $state.token }

function Api($method, $path, $body) {
    $uri = $base + $path
    if ($null -ne $body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 12 -Compress))
        return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -Body $bytes `
            -ContentType 'application/json; charset=utf-8' -TimeoutSec $TimeoutSeconds
    }
    return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec $TimeoutSeconds
}
function Get-Canvas { return (Api GET '/dev/snapshot').canvas }
function Get-Object($objectId) {
    return (Get-Canvas).objects | Where-Object { $_.objectId -eq $objectId } | Select-Object -First 1
}
function Value-Resource($objectId) { return @{ kind = 'grasshopperComponentValue'; id = $objectId; field = '*' } }

$script:results = @()
function Check($id, $ok, $detail) {
    $script:results += [pscustomobject]@{ Id = $id; Ok = [bool]$ok; Detail = "$detail" }
    Write-Host ("  [{0}] {1} — {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $id, $detail)
}
function Short($text, $n = 90) {
    if (-not $text) { return '<none>' }
    if ($text.Length -le $n) { return $text }
    return $text.Substring(0, $n)
}

# Well-known Grasshopper type GUIDs for the four controls under test.
$TYPE = [ordered]@{
    ValueList     = '00027467-0d24-4fa7-b178-8dc0ac5f42ec'
    BooleanToggle = '2e78987b-9dfb-42a2-8b76-3923ac8bd91a'
    Panel         = '59e0b89a-e487-49f8-bab8-b5bab16be14c'
    Button        = 'a8b97322-2d53-47cd-905e-b932c3ccd74e'
}

Write-Host "gate-value-writes: run $Run"
# dev-loop writes loop-state.json as soon as the AgentHost endpoint answers, but Grasshopper opens
# afterwards over a chained /runscript. Creating a component before the GH document registers fails
# with "No Grasshopper document is open" — wait for the canvas rather than racing it.
# dev-loop writes loop-state.json when the AgentHost endpoint answers, but Grasshopper opens
# afterwards over a chained /runscript — and /dev/snapshot returns a canvas envelope before the
# document is bound, so "canvas is not null" is not readiness. Wait for the binding itself.
$ready = $false
for ($i = 0; $i -lt 90; $i++) {
    try {
        $probe = Api GET '/dev/snapshot'
        if ($probe.target.hasGrasshopper -and $probe.canvas.grasshopperDocumentId) { $ready = $true; break }
    }
    catch { }
    Start-Sleep -Seconds 2
}
if (-not $ready) { throw 'Grasshopper never bound a document for this run.' }
Write-Host "  canvas ready"

$sessionId = (Api POST '/sessions' @{ name = 'value-write gate' }).id
Write-Host "  session $sessionId"

# --- fixtures: one of each control ------------------------------------------------------------
$created = [ordered]@{}
foreach ($kind in $TYPE.Keys) {
    $objectId = [guid]::NewGuid().ToString('D')
    $resource = @{ kind = 'grasshopperComponent'; id = $objectId; field = '*' }
    $result = Api POST "/dev/change/$sessionId" @{
        summary    = "gate: create $kind"
        writeSet   = @(@{ resource = $resource; expectedFingerprint = 'gptino:absent' })
        operations = @(@{
                operationId = "create-$kind"
                kind        = 'createComponent'
                owner       = 'canvas'
                reads       = @()
                writes      = @($resource)
                reversible  = $false
                payload     = @{
                    bridgeOperation = 'canvas.create'
                    arguments       = @{
                        operationId     = "create-$kind"
                        objectId        = $objectId
                        componentTypeId = $TYPE[$kind]
                        pivot           = 'gptino:auto'
                        resultOutput    = $null
                    }
                }
            })
    }
    if ($result.state -ne 'committed') {
        throw "Could not create the $kind fixture: $($result.state) — $($result.message)"
    }
    $created[$kind] = $objectId
}
Write-Host ("  created: {0}" -f ($created.Keys -join ', '))

# --- V1: every control now reports a readable value -------------------------------------------
foreach ($kind in $created.Keys) {
    $obj = Get-Object $created[$kind]
    Check "V1.$kind" ($obj -and $obj.valueJson -and $obj.valueFingerprint) (Short $obj.valueJson 70)
}

# --- V2: set each value for real ---------------------------------------------------------------
$writes = @(
    @{ Kind = 'BooleanToggle'; Extra = @{ kind = 'booleanToggle'; toggle = $true }; Expect = 'true' },
    @{ Kind = 'Panel'; Extra = @{ kind = 'panel'; text = 'gate-text' }; Expect = 'gate-text' },
    @{ Kind = 'ValueList'; Extra = @{
            kind          = 'valueList'
            items         = @(@{ name = 'update'; expression = '"replace"' }, @{ name = 'overlap'; expression = '"append"' })
            selectedIndex = 1
        }; Expect = 'append'
    }
)
foreach ($write in $writes) {
    $objectId = $created[$write.Kind]
    $obj = Get-Object $objectId
    $opId = "set-$($write.Kind)"
    $arguments = @{ operationId = $opId; objectId = $objectId; expectedFingerprint = $obj.valueFingerprint }
    foreach ($pair in $write.Extra.GetEnumerator()) { $arguments[$pair.Key] = $pair.Value }
    $resource = Value-Resource $objectId
    $result = Api POST "/dev/change/$sessionId" @{
        summary    = "gate: set $($write.Kind)"
        writeSet   = @(@{ resource = $resource; expectedFingerprint = $obj.valueFingerprint })
        operations = @(@{
                operationId = $opId
                kind        = 'setInputValue'
                owner       = 'canvas'
                reads       = @()
                writes      = @($resource)
                reversible  = $true
                payload     = @{ bridgeOperation = 'canvas.setInputValue'; arguments = $arguments }
            })
    }
    $after = Get-Object $objectId
    $ok = ($result.state -eq 'committed') -and $after.valueJson -and $after.valueJson.Contains($write.Expect)
    Check "V2.$($write.Kind)" $ok "$($result.state); value=$(Short $after.valueJson)"
}

# --- V3: a payload aimed at the wrong control type is refused, document untouched --------------
$panelId = $created['Panel']
$panel = Get-Object $panelId
$beforeValue = $panel.valueJson
$resource = Value-Resource $panelId
$refused = $null
try {
    $refused = Api POST "/dev/change/$sessionId" @{
        summary    = 'gate: wrong kind'
        writeSet   = @(@{ resource = $resource; expectedFingerprint = $panel.valueFingerprint })
        operations = @(@{
                operationId = 'wrong-kind'
                kind        = 'setInputValue'
                owner       = 'canvas'
                reads       = @()
                writes      = @($resource)
                reversible  = $true
                payload     = @{
                    bridgeOperation = 'canvas.setInputValue'
                    arguments       = @{
                        operationId = 'wrong-kind'; objectId = $panelId
                        expectedFingerprint = $panel.valueFingerprint
                        kind = 'booleanToggle'; toggle = $true
                    }
                }
            })
    }
}
catch {
    $refused = [pscustomobject]@{ state = 'rejected'; message = $_.Exception.Message }
}
$afterPanel = Get-Object $panelId
$unchanged = ($afterPanel.valueJson -eq $beforeValue)
Check 'V3.wrong-kind' (($refused.state -ne 'committed') -and $unchanged) `
    "state=$($refused.state); panel unchanged=$unchanged"

# --- R1: rewind restores the pre-move pivots ---------------------------------------------------
$beforePivots = @{}
foreach ($obj in (Get-Canvas).objects) { $beforePivots[$obj.objectId] = @($obj.pivot.x, $obj.pivot.y) }
Api POST "/dev/arrange/$sessionId" @{ seedComponentIds = @($created.Values); wait = $true } | Out-Null
Start-Sleep -Seconds 3

function Count-Displaced($pivots) {
    $n = 0
    foreach ($obj in (Get-Canvas).objects) {
        if ($pivots.ContainsKey($obj.objectId)) {
            $was = $pivots[$obj.objectId]
            if ([Math]::Abs($obj.pivot.x - $was[0]) -gt 0.5 -or [Math]::Abs($obj.pivot.y - $was[1]) -gt 0.5) { $n++ }
        }
    }
    return $n
}
$movedCount = Count-Displaced $beforePivots
$history = Api GET "/dev/layout-history/$sessionId"
$arrangeRevision = $history.revisions | Where-Object { $_.movedLayout } | Select-Object -First 1
if ($movedCount -eq 0 -or -not $arrangeRevision) {
    Check 'R1.rewind' $false "nothing to rewind (moved=$movedCount, arrangeRevision=$([bool]$arrangeRevision))"
}
else {
    $rewound = Api POST '/dev/rewind-layout' @{
        sessionId = $sessionId; sha = $arrangeRevision.sha; restoreStateBefore = $true
    }
    Start-Sleep -Seconds 3
    $stillOff = Count-Displaced $beforePivots
    Check 'R1.rewind' ($stillOff -eq 0) "moved $movedCount, restored $($rewound.moved), still off $stillOff"
}

Write-Host ''
$failed = @($script:results | Where-Object { -not $_.Ok })
$summary = [pscustomobject]@{
    run     = $Run
    session = $sessionId
    checks  = $script:results
    passed  = @($script:results | Where-Object { $_.Ok }).Count
    failed  = $failed.Count
    verdict = $(if ($failed.Count -eq 0) { 'PASS' } else { 'FAIL' })
}
$out = Join-Path $Run 'gate-value-writes.json'
$summary | ConvertTo-Json -Depth 8 | Set-Content -Path $out -Encoding utf8
Write-Host ("gate-value-writes: {0} — {1} passed, {2} failed → {3}" -f $summary.verdict, $summary.passed, $summary.failed, $out)
if ($failed.Count -gt 0) { exit 1 }
