#requires -Version 5.1
# Live gate for volatile-data collection (log review A14, closed-as-not-reproducible 2026-08-27).
# The A14 report said a programmatically created slider / Rhino reference param never fed its
# downstream script ("Verified and committed" with DataCount=0, then the agent asks the human to
# press Recompute). The live probes could NOT reproduce it on the current build — this gate pins
# that state so a regression is caught the day it happens, not six weeks later in a log review.
#
#   V1 fresh-slider : slider created and NEVER touched -> wired -> execute => DataCount=1 (default value)
#   V2 value-write  : slider set to 7 -> execute => output reflects 7
#   V3 reference    : Rhino point -> referenceRhinoObjects param -> wired counter => n=1
#
# No model in the loop. Needs: a dev-loop run (scripts/dev-loop.ps1).
# NOTE: this file must stay UTF-8 WITH BOM (PS 5.1 reads BOM-less .ps1 as ANSI).
[CmdletBinding()]
param(
    [string]$Run,
    [int]$TimeoutSeconds = 120
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
            $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 14 -Compress))
            return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -Body $bytes `
                -ContentType 'application/json; charset=utf-8' -TimeoutSec $TimeoutSeconds
        }
        return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec $TimeoutSeconds
    } catch [Net.WebException] {
        $detail = ''
        if ($_.Exception.Response) {
            $reader = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
            $detail = $reader.ReadToEnd(); $reader.Dispose()
        }
        throw "$method $path -> $($_.Exception.Message) $detail"
    }
}
function Obs($id, $text) { Write-Host ("  [{0}] {1}" -f $id, $text) }


$script:results = @()
function Check($id, $ok, $detail) {
    $script:results += [pscustomobject]@{ Id = $id; Ok = [bool]$ok; Detail = "$detail" }
    Write-Host ("  [{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $id, $detail)
}
Write-Host "gate-volatile-collection: run $Run"
$ready = $false
for ($i = 0; $i -lt 90; $i++) {
    try {
        $probe = Api GET '/dev/snapshot'
        if ($probe.target.hasGrasshopper -and $probe.canvas.grasshopperDocumentId) { $ready = $true; break }
    } catch { }
    Start-Sleep -Seconds 2
}
if (-not $ready) { throw 'Grasshopper never bound a document for this run.' }
$PythonTypeId = '719467e6-7cf5-4848-99b0-c5dd57e5442c'
$CSharpTypeId = 'b6ba1144-02d6-4a2d-b53c-ec62e290eeb7'
$SliderTypeId = '57da07bd-ecab-415d-9d86-af36d7073abc'
$sessionId = (Api POST '/sessions' @{ name = 'volatile gate' }).id
Write-Host "  session $sessionId"

function Change($summary, $writeSet, $operations) {
    return Api POST "/dev/change/$sessionId" @{ summary = $summary; writeSet = $writeSet; operations = $operations }
}
function CompRes($id) { @{ resource = @{ kind = 'grasshopperComponent'; id = $id; field = '*' }; expectedFingerprint = 'gptino:absent' } }
function NewComp($label, $typeId) {
    $objectId = [guid]::NewGuid().ToString('D')
    $r = Change "probe: create $label" @(CompRes $objectId) @(@{
        operationId = "create-$label"; kind = 'createComponent'; owner = 'canvas'
        reads = @(); writes = @(@{ kind = 'grasshopperComponent'; id = $objectId; field = '*' }); reversible = $false
        payload = @{ bridgeOperation = 'canvas.create'; arguments = @{
            operationId = "create-$label"; objectId = $objectId; componentTypeId = $typeId; pivot = 'gptino:auto'; resultOutput = $null } }
    })
    if ($r.state -ne 'committed') { throw ("create {0}: {1} {2}" -f $label, $r.state, $r.message) }
    return $objectId
}
function GetObj($id) { (Api GET '/dev/snapshot').canvas.objects | Where-Object { $_.objectId -eq $id } | Select-Object -First 1 }

# ================================================================== A14: fresh slider, no value write
Write-Host "`n--- A14: volatile of a NEVER-TOUCHED new slider ---"
$script1 = NewComp 'a14-script' $CSharpTypeId
$slider1 = NewComp 'a14-slider' $SliderTypeId
$s1 = GetObj $script1
$ioRes = @{ resource = @{ kind = 'grasshopperComponentIo'; id = $script1; field = '*' }; expectedFingerprint = 'gptino:auto' }
$r = Change 'probe: schema x->a' @($ioRes) @(@{
    operationId = 'a14-io'; kind = 'setComponentIo'; owner = 'script'
    reads = @(); writes = @(@{ kind = 'grasshopperComponentIo'; id = $script1; field = '*' }); reversible = $true
    payload = @{ bridgeOperation = 'python.setSchema'; arguments = @{
        operationId = 'a14-io'; componentId = $script1
        inputs = @(@{ name = 'x'; access = 'item'; optional = $true })
        outputs = @(@{ name = 'a'; access = 'item' })
        preserveIncidentWires = $true } }
})
Note 'A14.io' $r.state
$srcRes = @{ resource = @{ kind = 'grasshopperComponentSource'; id = $script1; field = '*' }; expectedFingerprint = 'gptino:auto' }
$r = Change 'probe: source a=x' @($srcRes) @(@{
    operationId = 'a14-src'; kind = 'updatePythonSource'; owner = 'script'
    reads = @(); writes = @(@{ kind = 'grasshopperComponentSource'; id = $script1; field = '*' }); reversible = $true
    payload = @{ bridgeOperation = 'python.setSource'; arguments = @{
        operationId = 'a14-src'; componentId = $script1; expectedSourceSha256 = 'gptino:auto'
        source = 'a = x;'; runtime = 'csharp'; expireSolution = $false } }
})
Note 'A14.src' $r.state
$sObj = GetObj $script1; $slObj = GetObj $slider1
$inX = ($sObj.inputs | Where-Object { $_.name -eq 'x' } | Select-Object -First 1).parameterId
$outS = ($slObj.outputs | Select-Object -First 1).parameterId
$wireId = "{0}/{1}>{2}/{3}" -f $slider1.Replace('-',''), $outS.Replace('-',''), $script1.Replace('-',''), $inX.Replace('-','')
$r = Change 'probe: wire slider->x' @(@{ resource = @{ kind = 'grasshopperWire'; id = $wireId; field = '*' }; expectedFingerprint = 'gptino:absent' }) @(@{
    operationId = 'a14-wire'; kind = 'connectWire'; owner = 'canvas'
    reads = @(); writes = @(@{ kind = 'grasshopperWire'; id = $wireId; field = '*' }); reversible = $true
    payload = @{ bridgeOperation = 'canvas.setWire'; arguments = @{
        operationId = 'a14-wire'
        wire = @{ sourceObjectId = $slider1; sourceParameterId = $outS; targetObjectId = $script1; targetParameterId = $inX }
        action = 'connect'; rejectCycles = $true } }
})
Note 'A14.wire' $r.state
function ExecScript($label, $recomputeDoc) {
    $valRes = @{ resource = @{ kind = 'grasshopperComponentValue'; id = $script1; field = '*' }; expectedFingerprint = 'gptino:auto' }
    return Change "probe: $label" @($valRes) @(@{
        operationId = $label; kind = 'executePython'; owner = 'script'
        reads = @(); writes = @(@{ kind = 'grasshopperComponentValue'; id = $script1; field = '*' }); reversible = $true
        payload = @{ bridgeOperation = 'python.execute'; arguments = @{
            operationId = $label; componentId = $script1; expireUpstream = $true; recomputeDocument = $false } }
    })
}
$r = ExecScript 'a14-exec1' $false
$outs = Api GET "/dev/grasshopper/$script1/outputs"
$aOut = ($outs.result.outputs | Where-Object { $_.name -eq 'a' } | Select-Object -First 1)
Note 'A14.exec1' ("state={0}; output a DataCount={1} sample={2}" -f $r.state, $aOut.dataCount, ($aOut.sampleValues -join ','))
# now write the slider's value once (the path that expires+solves) and re-execute
$slNow = GetObj $slider1
$r = Change 'probe: slider value 7' @(@{ resource = @{ kind = 'grasshopperComponentValue'; id = $slider1; field = '*' }; expectedFingerprint = $slNow.valueFingerprint }) @(@{
    operationId = 'a14-val'; kind = 'setValue'; owner = 'canvas'
    reads = @(); writes = @(@{ kind = 'grasshopperComponentValue'; id = $slider1; field = '*' }); reversible = $true
    payload = @{ bridgeOperation = 'canvas.setNumberSlider'; arguments = @{
        operationId = 'a14-val'; objectId = $slider1; expectedFingerprint = $slNow.valueFingerprint
        value = 7; minimum = 0; maximum = 100; decimalPlaces = 0 } }
})
$r2 = ExecScript 'a14-exec2' $false
$outs2 = Api GET "/dev/grasshopper/$script1/outputs"
$aOut2 = ($outs2.result.outputs | Where-Object { $_.name -eq 'a' } | Select-Object -First 1)
Note 'A14.exec2(after value write)' ("state={0}; output a DataCount={1} sample={2}" -f $r2.state, $aOut2.dataCount, ($aOut2.sampleValues -join ','))


function Note($id, $text) { Write-Host ("  ({0}) {1}" -f $id, $text) }
Check 'V1.fresh-slider' ($aOut.dataCount -eq 1) ("untouched new slider fed the script: DataCount=$($aOut.dataCount) sample=$($aOut.sampleValues -join ',')")
Check 'V2.value-write' (($aOut2.dataCount -eq 1) -and (($aOut2.sampleValues -join ',') -eq '7')) ("after value write: DataCount=$($aOut2.dataCount) sample=$($aOut2.sampleValues -join ',')")

# ============================================================ A14-ref: live Rhino reference param
Write-Host "`n--- A14-ref: fresh Rhino reference parameter volatile ---"
$ptId = [guid]::NewGuid().ToString('D'); $entId = [guid]::NewGuid().ToString('D')
$r = Change 'probe: rhino point' @(@{ resource = @{ kind = 'rhinoObject'; id = $ptId; field = '*' }; expectedFingerprint = 'gptino:absent' }) @(@{
    operationId = 'ref-pt'; kind = 'createRhinoPrimitive'; owner = 'rhinoBridge'
    reads = @(); writes = @(@{ kind = 'rhinoObject'; id = $ptId; field = '*' }); reversible = $false
    payload = @{ bridgeOperation = 'rhino.createPrimitive'; arguments = @{
        operationId = 'ref-pt'; objectId = $ptId; logicalEntityId = $entId; kind = 'point'
        point = @{ location = @{ x = 1; y = 2; z = 3 } } } }
})
Note 'A14r.point' $r.state
$refParam = [guid]::NewGuid().ToString('D')
$r = Change 'probe: reference param' @(@{ resource = @{ kind = 'grasshopperComponent'; id = $refParam; field = '*' }; expectedFingerprint = 'gptino:absent' }) @(@{
    operationId = 'ref-param'; kind = 'referenceRhinoObjects'; owner = 'canvas'
    reads = @(); writes = @(@{ kind = 'grasshopperComponent'; id = $refParam; field = '*' }); reversible = $false
    payload = @{ bridgeOperation = 'canvas.referenceRhinoObjects'; arguments = @{
        operationId = 'ref-param'; objectId = $refParam; rhinoObjectIds = @($ptId); paramType = 'point'; pivot = 'gptino:auto' } }
})
Note 'A14r.param' $r.state
# script that counts what arrives
$sc = [guid]::NewGuid().ToString('D')
$r = Change 'probe: counter script' @(@{ resource = @{ kind = 'grasshopperComponent'; id = $sc; field = '*' }; expectedFingerprint = 'gptino:absent' }) @(@{
    operationId = 'ref-script'; kind = 'createComponent'; owner = 'canvas'
    reads = @(); writes = @(@{ kind = 'grasshopperComponent'; id = $sc; field = '*' }); reversible = $false
    payload = @{ bridgeOperation = 'canvas.create'; arguments = @{
        operationId = 'ref-script'; objectId = $sc; componentTypeId = $CSharpTypeId; pivot = 'gptino:auto'; resultOutput = $null } }
})
$r = Change 'probe: schema pts->n' @(@{ resource = @{ kind = 'grasshopperComponentIo'; id = $sc; field = '*' }; expectedFingerprint = 'gptino:auto' }) @(@{
    operationId = 'ref-io'; kind = 'setComponentIo'; owner = 'script'
    reads = @(); writes = @(@{ kind = 'grasshopperComponentIo'; id = $sc; field = '*' }); reversible = $true
    payload = @{ bridgeOperation = 'python.setSchema'; arguments = @{
        operationId = 'ref-io'; componentId = $sc
        inputs = @(@{ name = 'pts'; access = 'list'; typeHint = 'point3d'; optional = $true })
        outputs = @(@{ name = 'n'; access = 'item' })
        preserveIncidentWires = $true } }
})
$r = Change 'probe: source n=pts.Count' @(@{ resource = @{ kind = 'grasshopperComponentSource'; id = $sc; field = '*' }; expectedFingerprint = 'gptino:auto' }) @(@{
    operationId = 'ref-src'; kind = 'updatePythonSource'; owner = 'script'
    reads = @(); writes = @(@{ kind = 'grasshopperComponentSource'; id = $sc; field = '*' }); reversible = $true
    payload = @{ bridgeOperation = 'python.setSource'; arguments = @{
        operationId = 'ref-src'; componentId = $sc; expectedSourceSha256 = 'gptino:auto'
        source = 'n = pts == null ? 0 : pts.Count;'; runtime = 'csharp'; expireSolution = $false } }
})
$scObj = GetObj $sc; $rpObj = GetObj $refParam
$inP = ($scObj.inputs | Where-Object { $_.name -eq 'pts' } | Select-Object -First 1).parameterId
$outR = ($rpObj.outputs | Select-Object -First 1).parameterId
$wid = "{0}/{1}>{2}/{3}" -f $refParam.Replace('-',''), $outR.Replace('-',''), $sc.Replace('-',''), $inP.Replace('-','')
$r = Change 'probe: wire ref->pts' @(@{ resource = @{ kind = 'grasshopperWire'; id = $wid; field = '*' }; expectedFingerprint = 'gptino:absent' }) @(@{
    operationId = 'ref-wire'; kind = 'connectWire'; owner = 'canvas'
    reads = @(); writes = @(@{ kind = 'grasshopperWire'; id = $wid; field = '*' }); reversible = $true
    payload = @{ bridgeOperation = 'canvas.setWire'; arguments = @{
        operationId = 'ref-wire'
        wire = @{ sourceObjectId = $refParam; sourceParameterId = $outR; targetObjectId = $sc; targetParameterId = $inP }
        action = 'connect'; rejectCycles = $true } }
})
$r = Change 'probe: exec counter' @(@{ resource = @{ kind = 'grasshopperComponentValue'; id = $sc; field = '*' }; expectedFingerprint = 'gptino:auto' }) @(@{
    operationId = 'ref-exec'; kind = 'executePython'; owner = 'script'
    reads = @(); writes = @(@{ kind = 'grasshopperComponentValue'; id = $sc; field = '*' }); reversible = $true
    payload = @{ bridgeOperation = 'python.execute'; arguments = @{
        operationId = 'ref-exec'; componentId = $sc; expireUpstream = $true; recomputeDocument = $false } }
})
$outs = Api GET "/dev/grasshopper/$sc/outputs"
$nOut = ($outs.result.outputs | Where-Object { $_.name -eq 'n' } | Select-Object -First 1)



Check 'V3.reference' (($nOut.dataCount -eq 1) -and (($nOut.sampleValues -join ',') -eq '1')) ("reference param volatile: n DataCount=$($nOut.dataCount) sample=$($nOut.sampleValues -join ',')")

Write-Host ''
$failed = @($script:results | Where-Object { -not $_.Ok })
$summary = [pscustomobject]@{
    run = $Run; checks = $script:results
    passed = @($script:results | Where-Object { $_.Ok }).Count
    failed = $failed.Count
    verdict = $(if ($failed.Count -eq 0) { 'PASS' } else { 'FAIL' })
}
$out = Join-Path $Run 'gate-volatile-collection.json'
$summary | ConvertTo-Json -Depth 8 | Set-Content -Path $out -Encoding utf8
Write-Host ("gate-volatile-collection: {0} - {1} passed, {2} failed -> {3}" -f $summary.verdict, $summary.passed, $summary.failed, $out)
if ($failed.Count -gt 0) { exit 1 }
