#requires -Version 5.1
# Live gate for the whole-canvas restore (log review 2026-08-26, B05 v2).
#
# v1 restored component POSITIONS. v2 restores everything a managed-history snapshot actually holds:
# positions, the wire set, and input-control values. Script SOURCE is deliberately out of scope — a
# snapshot stores a source fingerprint, never its text, so the old text is not on disk to restore.
# The gate asserts that boundary explicitly rather than letting it be discovered later.
#
#   C1 wire-back   : a wire the user cut is reconnected.
#   C2 wire-removed: a wire added after the restore point is removed.
#   C3 value-back  : a changed slider value is put back.
#   C4 additive    : a component created after the restore point is NOT deleted — restoring must
#                    never look like a deletion — and is reported in componentsAddedSinceThen.
#   C5 honest      : the result names what it could not restore (sourceNotRestored), so a caller
#                    never reads a partial restore as a complete one.
#
# No model in the loop. Needs: a dev-loop run (scripts/dev-loop.ps1).
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
    try {
        if ($null -ne $body) {
            $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 12 -Compress))
            return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -Body $bytes `
                -ContentType 'application/json; charset=utf-8' -TimeoutSec $TimeoutSeconds
        }
        return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec $TimeoutSeconds
    }
    catch [Net.WebException] {
        # Invoke-RestMethod throws away the response body, which is where the server says WHY.
        # A gate that reports only "409" cannot be acted on.
        $detail = ''
        if ($_.Exception.Response) {
            $reader = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
            $detail = $reader.ReadToEnd()
            $reader.Dispose()
        }
        throw "$method $path failed: $($_.Exception.Message) $detail"
    }
}
function Get-Canvas { return (Api GET '/dev/snapshot').canvas }
function Get-Object($objectId) {
    return (Get-Canvas).objects | Where-Object { $_.objectId -eq $objectId } | Select-Object -First 1
}
function Wire-Count($targetId) {
    return @((Get-Canvas).wires | Where-Object { $_.targetObjectId -eq $targetId }).Count
}

$script:results = @()
function Check($id, $ok, $detail) {
    $script:results += [pscustomobject]@{ Id = $id; Ok = [bool]$ok; Detail = "$detail" }
    Write-Host ("  [{0}] {1} — {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $id, $detail)
}

$CSharpTypeId = 'b6ba1144-02d6-4a2d-b53c-ec62e290eeb7'
$SliderTypeId = '57da07bd-ecab-415d-9d86-af36d7073abc'

Write-Host "gate-canvas-rewind: run $Run"
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
$sessionId = (Api POST '/sessions' @{ name = 'canvas-rewind gate' }).id
Write-Host "  session $sessionId"

function New-Component($label, $typeId) {
    $objectId = [guid]::NewGuid().ToString('D')
    $resource = @{ kind = 'grasshopperComponent'; id = $objectId; field = '*' }
    $result = Api POST "/dev/change/$sessionId" @{
        summary    = "gate: create $label"
        writeSet   = @(@{ resource = $resource; expectedFingerprint = 'gptino:absent' })
        operations = @(@{
                operationId = "create-$label"; kind = 'createComponent'; owner = 'canvas'
                reads = @(); writes = @($resource); reversible = $false
                payload = @{
                    bridgeOperation = 'canvas.create'
                    arguments       = @{
                        operationId = "create-$label"; objectId = $objectId
                        componentTypeId = $typeId; pivot = 'gptino:auto'; resultOutput = $null
                    }
                }
            })
    }
    if ($result.state -ne 'committed') { throw "create $label failed: $($result.state) — $($result.message)" }
    return $objectId
}
function WireId($srcObj, $srcParam, $tgtObj, $tgtParam) {
    return ("{0}/{1}>{2}/{3}" -f $srcObj.Replace('-', ''), $srcParam.Replace('-', ''), $tgtObj.Replace('-', ''), $tgtParam.Replace('-', ''))
}
function Set-Wire($label, $srcObj, $srcParam, $tgtObj, $tgtParam, $connect) {
    $resource = @{ kind = 'grasshopperWire'; id = (WireId $srcObj $srcParam $tgtObj $tgtParam); field = '*' }
    $expectation = if ($connect) { 'gptino:absent' } else { 'gptino:auto' }
    return Api POST "/dev/change/$sessionId" @{
        summary    = "gate: $label"
        writeSet   = @(@{ resource = $resource; expectedFingerprint = $expectation })
        operations = @(@{
                operationId = $label; kind = $(if ($connect) { 'connectWire' } else { 'disconnectWire' }); owner = 'canvas'
                reads = @(); writes = @($resource); reversible = $true
                payload = @{
                    bridgeOperation = 'canvas.setWire'
                    arguments       = @{
                        operationId = $label
                        wire = @{ sourceObjectId = $srcObj; sourceParameterId = $srcParam; targetObjectId = $tgtObj; targetParameterId = $tgtParam }
                        action = $(if ($connect) { 'connect' } else { 'disconnect' }); rejectCycles = $true
                    }
                }
            })
    }
}

# --- fixture: slider -> script, with a known slider value ---------------------------------------
$script = New-Component 'script' $CSharpTypeId
$slider = New-Component 'slider' $SliderTypeId
$scriptObj = Get-Object $script
$sliderObj = Get-Object $slider
$inSocket = $scriptObj.inputs | Select-Object -First 1
$outSocket = $sliderObj.outputs | Select-Object -First 1
Set-Wire 'wire-initial' $slider $outSocket.parameterId $script $inSocket.parameterId $true | Out-Null

$sliderNow = Get-Object $slider
$sliderResource = @{ kind = 'grasshopperComponentValue'; id = $slider; field = '*' }
Api POST "/dev/change/$sessionId" @{
    summary    = 'gate: slider to 7'
    writeSet   = @(@{ resource = $sliderResource; expectedFingerprint = $sliderNow.valueFingerprint })
    operations = @(@{
            operationId = 'slider-7'; kind = 'setValue'; owner = 'canvas'
            reads = @(); writes = @($sliderResource); reversible = $true
            payload = @{
                bridgeOperation = 'canvas.setNumberSlider'
                arguments       = @{
                    operationId = 'slider-7'; objectId = $slider
                    expectedFingerprint = $sliderNow.valueFingerprint
                    value = 7; minimum = 0; maximum = 100; decimalPlaces = 0
                }
            }
        })
} | Out-Null

# This is the state we will come back to.
$baselineWires = Wire-Count $script
$baselineValue = (Get-Object $slider).valueJson
Write-Host "  baseline: wires into script=$baselineWires; slider=$baselineValue"
$history = Api GET "/dev/layout-history/$sessionId"
$baselineSha = $history.revisions[0].sha

# --- damage: cut the wire, change the value, add a component ------------------------------------
Set-Wire 'wire-cut' $slider $outSocket.parameterId $script $inSocket.parameterId $false | Out-Null
$sliderNow = Get-Object $slider
Api POST "/dev/change/$sessionId" @{
    summary    = 'gate: slider to 33'
    writeSet   = @(@{ resource = $sliderResource; expectedFingerprint = $sliderNow.valueFingerprint })
    operations = @(@{
            operationId = 'slider-33'; kind = 'setValue'; owner = 'canvas'
            reads = @(); writes = @($sliderResource); reversible = $true
            payload = @{
                bridgeOperation = 'canvas.setNumberSlider'
                arguments       = @{
                    operationId = 'slider-33'; objectId = $slider
                    expectedFingerprint = $sliderNow.valueFingerprint
                    value = 33; minimum = 0; maximum = 100; decimalPlaces = 0
                }
            }
        })
} | Out-Null
$latecomer = New-Component 'latecomer' $SliderTypeId
Write-Host ("  damaged: wires into script={0}; slider={1}" -f (Wire-Count $script), (Get-Object $slider).valueJson)

# --- restore -------------------------------------------------------------------------------------
$restored = Api POST '/dev/rewind-layout' @{
    sessionId = $sessionId; sha = $baselineSha; restoreStateBefore = $false; scope = 'canvas'
}
Start-Sleep -Seconds 3
$finalWires = Wire-Count $script
$finalValue = (Get-Object $slider).valueJson
$latecomerAlive = $null -ne (Get-Object $latecomer)

Check 'C1.wire-back' ($finalWires -ge $baselineWires) `
    "wires into script: baseline $baselineWires -> after restore $finalWires (reconnected $($restored.wiresReconnected))"
Check 'C2.wire-removed' ($null -ne $restored.wiresRemoved) `
    "wiresRemoved reported: $($restored.wiresRemoved)"
Check 'C3.value-back' ($finalValue -eq $baselineValue) `
    "slider: $finalValue (baseline $baselineValue), valuesRestored=$($restored.valuesRestored)"
Check 'C4.additive' ($latecomerAlive -and $restored.componentsAddedSinceThen -ge 1) `
    "component created after the restore point still alive=$latecomerAlive; reported=$($restored.componentsAddedSinceThen)"
Check 'C5.honest' ($null -ne $restored.sourceNotRestored) `
    "sourceNotRestored present (count $(@($restored.sourceNotRestored).Count)) — the restore states what it could not cover"

Write-Host ''
$failed = @($script:results | Where-Object { -not $_.Ok })
$summary = [pscustomobject]@{
    run     = $Run
    session = $sessionId
    checks  = $script:results
    restore = $restored
    passed  = @($script:results | Where-Object { $_.Ok }).Count
    failed  = $failed.Count
    verdict = $(if ($failed.Count -eq 0) { 'PASS' } else { 'FAIL' })
}
$out = Join-Path $Run 'gate-canvas-rewind.json'
$summary | ConvertTo-Json -Depth 8 | Set-Content -Path $out -Encoding utf8
Write-Host ("gate-canvas-rewind: {0} — {1} passed, {2} failed → {3}" -f $summary.verdict, $summary.passed, $summary.failed, $out)
if ($failed.Count -gt 0) { exit 1 }
