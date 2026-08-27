#requires -Version 5.1
# Live gate for the whole-canvas restore (log review 2026-08-26, B05 v2).
#
# v1 restored component POSITIONS. v2 added the wire set and input-control values. v3 adds SCRIPT
# SOURCE: a snapshot only ever stored a source fingerprint, so each provenance commit now writes the
# text itself (sources/<id>.txt), plus the pre-Vino original the first time a component is edited
# (sources-baseline/<id>.txt). C6 and C7 are the two paths that has to cover.
#
#   C1 wire-back   : a wire the user cut is reconnected.
#   C2 wire-removed: a wire added after the restore point is removed.
#   C3 value-back  : a changed slider value is put back.
#   C4 additive    : a component created after the restore point is NOT deleted — restoring must
#                    never look like a deletion — and is reported in componentsAddedSinceThen.
#   C5 honest      : the result names what it could not restore (sourceNotRestored), so a caller
#                    never reads a partial restore as a complete one.
#   C6 source-back : a script edited twice comes back to the FIRST text — the ordinary undo, served
#                    from sources/<id>.txt at the restore revision.
#   C7 pre-vino    : a script Vino has edited exactly once comes back to the text it had BEFORE that
#                    edit — served from sources-baseline/<id>.txt, which is the only thing that
#                    makes a hand-authored script's first edit undoable.
#
# C7 uses a PYTHON component on purpose. A fresh C# script component's default source is the SDK
# class template (public class Script_Instance : GH_ScriptInstance), and Vino refuses to WRITE that
# shape into a script component — so a C# component that has never been authored has a pre-Vino text
# that cannot be written back, and the restore reports it in sourceNotRestored instead of pretending.
# A fresh Python component's default is ordinary script-mode source, so it round-trips.
#
# The expectations compare against the text the component actually HOLDS, read back after the write:
# the adapter normalises the language directive on the way in, so the installed text is the payload
# plus a directive line, and that installed text is what the history stores and the restore returns.
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
function Get-Source($componentId) {
    $read = Api GET "/dev/grasshopper/$componentId/python"
    $inspection = @($read.inspections | Where-Object { $_.scope -like 'script:*' })[0]
    return $inspection.result.source
}
# A restore returns the CODE, not the bytes: every write goes through the adapter, which stamps the
# language directive if it is missing and normalises line endings. Compare what the author would
# recognise as their script, which is what the feature actually promises.
function Normalize-Source($text) {
    if ($null -eq $text) { return '' }
    $normalized = $text -replace "`r`n", "`n"
    return ($normalized -replace "^(?:#! python 3|// #! csharp)`n", '')
}
function Set-Source($label, $componentId, $text, $runtime = 'csharp') {
    $resource = @{ kind = 'grasshopperComponentSource'; id = $componentId; field = '*' }
    $result = Api POST "/dev/change/$sessionId" @{
        summary    = "gate: $label"
        writeSet   = @(@{ resource = $resource; expectedFingerprint = 'gptino:auto' })
        operations = @(@{
                operationId = $label; kind = 'updatePythonSource'; owner = 'script'
                reads = @(); writes = @($resource); reversible = $true
                payload = @{
                    bridgeOperation = 'python.setSource'
                    arguments       = @{
                        operationId = $label; componentId = $componentId
                        expectedSourceSha256 = 'gptino:auto'
                        source = $text; runtime = $runtime; expireSolution = $false
                    }
                }
            })
    }
    if ($result.state -ne 'committed') { throw "$label failed: $($result.state) — $($result.message)" }
    return $result
}

$script:results = @()
function Check($id, $ok, $detail) {
    $script:results += [pscustomobject]@{ Id = $id; Ok = [bool]$ok; Detail = "$detail" }
    Write-Host ("  [{0}] {1} — {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $id, $detail)
}

$CSharpTypeId = 'b6ba1144-02d6-4a2d-b53c-ec62e290eeb7'
$Python3TypeId = '719467e6-7cf5-4848-99b0-c5dd57e5442c'
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
# A second script, kept aside for C7: Vino writes it exactly ONCE, after the restore point, so the
# only text that can bring it back is the pre-Vino original captured at that first write.
$virgin = New-Component 'virgin' $Python3TypeId
$virginOriginal = Get-Source $virgin
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

# The script is edited ONCE before the restore point, so `sources/` holds this text at the baseline.
$SourceV1 = "// gate v1`nvar a = 1;"
$SourceV2 = "// gate v2`nvar a = 2;`nvar b = 3;"
Set-Source 'source-v1' $script $SourceV1 | Out-Null
# What the component HOLDS after that write — the payload plus the directive the adapter adds. This,
# not the payload, is what the history stores and what a restore must return.
$InstalledV1 = Get-Source $script

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
# Both source paths are damaged after the restore point: an edited script gets a second edit, and a
# never-edited script gets its first.
Set-Source 'source-v2' $script $SourceV2 | Out-Null
Set-Source 'virgin-first' $virgin "# written after the restore point`na = 9" 'cpython3' | Out-Null
Write-Host ("  damaged: wires into script={0}; slider={1}" -f (Wire-Count $script), (Get-Object $slider).valueJson)

# --- restore -------------------------------------------------------------------------------------
$restored = Api POST '/dev/rewind-layout' @{
    sessionId = $sessionId; sha = $baselineSha; restoreStateBefore = $false; scope = 'canvas'
}
Start-Sleep -Seconds 3
$finalWires = Wire-Count $script
$finalValue = (Get-Object $slider).valueJson
$latecomerAlive = $null -ne (Get-Object $latecomer)
$finalSource = Get-Source $script
$finalVirgin = Get-Source $virgin

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
Check 'C6.source-back' ($finalSource -eq $InstalledV1) `
    "script source restored to the v1 the component held (len $($finalSource.Length), expected $($InstalledV1.Length)); sourcesRestored=$($restored.sourcesRestored)"
Check 'C7.pre-vino' ((Normalize-Source $finalVirgin) -eq (Normalize-Source $virginOriginal)) `
    "a first-ever edit was undone from the captured pre-Vino text (restored $($finalVirgin.Length)->$((Normalize-Source $finalVirgin).Length) chars vs original $($virginOriginal.Length)->$((Normalize-Source $virginOriginal).Length); compared after the adapter's directive/line-ending normalisation)"

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
