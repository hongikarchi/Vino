#requires -Version 5.1
# Live gate for socket removal (log review 2026-08-26, B10).
#
# Until now setComponentIo was append-only: a declaration that dropped a socket was refused outright.
# The stated reason was that a positional reconciliation cannot express WHICH socket goes, and that a
# removal destroys the parameter instance a wire refers to. Both are true of a WIRED socket. Neither
# is true of an unwired one — and 8 of the 17 removals refused in the 07-21..08-26 corpus were
# factory-default sockets ('x','y' / 'out','a') on a component the session had just created.
#
#   K1 remove   : dropping an UNWIRED socket really removes it from the live component.
#   K2 keep     : the sockets that stay keep their names, and the managed console output 'out'
#                 survives even though the declaration never mentions it (the model cannot re-declare
#                 it on C# — 'out' is a reserved keyword).
#   K3 refuse   : dropping a WIRED socket is refused, the wire survives, and the socket stays.
#   K4 no dead-end : that refusal is a clean Failed, never RecoveryRequired — the original incident
#                 was the adapter throwing at execute time, after the same ChangeSet's source write
#                 had already landed.
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
function Names($sockets) { return @($sockets | ForEach-Object { $_.name }) }

$script:results = @()
function Check($id, $ok, $detail) {
    $script:results += [pscustomobject]@{ Id = $id; Ok = [bool]$ok; Detail = "$detail" }
    Write-Host ("  [{0}] {1} — {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $id, $detail)
}

$CSHARP = 'b6ba1144-02d6-4a2d-b53c-ec62e290eeb7'
$SLIDER = '57da07bd-ecab-415d-9d86-af36d7073abc'

Write-Host "gate-socket-removal: run $Run"
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
$sessionId = (Api POST '/sessions' @{ name = 'socket-removal gate' }).id
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

function Set-Schema($objectId, $label, $inputs, $outputs) {
    $resource = @{ kind = 'grasshopperComponentIo'; id = $objectId; field = '*' }
    try {
        return Api POST "/dev/change/$sessionId" @{
            summary    = "gate: schema $label"
            writeSet   = @(@{ resource = $resource; expectedFingerprint = 'gptino:auto' })
            operations = @(@{
                    operationId = "io-$label"; kind = 'setComponentIo'; owner = 'script'
                    reads = @(); writes = @($resource); reversible = $true
                    payload = @{
                        bridgeOperation = 'python.setSchema'
                        arguments       = @{
                            operationId = "io-$label"; componentId = $objectId
                            inputs = $inputs; outputs = $outputs; preserveIncidentWires = $true
                        }
                    }
                })
        }
    }
    catch {
        return [pscustomobject]@{ state = 'rejected'; message = $_.Exception.Message }
    }
}

# --- fixture: a C# script with three inputs, one of them wired to a slider ---------------------
$script = New-Component 'script' $CSHARP
Set-Schema $script 'grow' `
    @(@{ name = 'keep'; access = 'item'; typeHint = 'double'; optional = $true },
      @{ name = 'wired'; access = 'item'; typeHint = 'double'; optional = $true },
      @{ name = 'orphan'; access = 'item'; typeHint = 'double'; optional = $true }) `
    @(@{ name = 'a'; access = 'item' }) | Out-Null
$slider = New-Component 'slider' $SLIDER

$before = Get-Object $script
$wiredSocket = $before.inputs | Where-Object { $_.name -eq 'wired' } | Select-Object -First 1
$sliderObj = Get-Object $slider
$sliderOut = $sliderObj.outputs | Select-Object -First 1
Write-Host ("  script inputs: {0}; outputs: {1}" -f ((Names $before.inputs) -join ','), ((Names $before.outputs) -join ','))

# wire slider -> 'wired'
$wireResource = @{
    kind = 'grasshopperWire'
    id   = ("{0}/{1}>{2}/{3}" -f $slider.Replace('-', ''), $sliderOut.parameterId.Replace('-', ''), $script.Replace('-', ''), $wiredSocket.parameterId.Replace('-', ''))
    field = '*'
}
$wireResult = Api POST "/dev/change/$sessionId" @{
    summary    = 'gate: wire slider'
    writeSet   = @(@{ resource = $wireResource; expectedFingerprint = 'gptino:absent' })
    operations = @(@{
            operationId = 'wire'; kind = 'connectWire'; owner = 'canvas'
            reads = @(); writes = @($wireResource); reversible = $true
            payload = @{
                bridgeOperation = 'canvas.setWire'
                arguments       = @{
                    operationId = 'wire'
                    wire        = @{
                        sourceObjectId = $slider; sourceParameterId = $sliderOut.parameterId
                        targetObjectId = $script; targetParameterId = $wiredSocket.parameterId
                    }
                    action = 'connect'; rejectCycles = $true
                }
            }
        })
}
if ($wireResult.state -ne 'committed') { throw "wire failed: $($wireResult.state) — $($wireResult.message)" }

# --- K1/K2: drop the UNWIRED 'orphan' -----------------------------------------------------------
$shrink = Set-Schema $script 'shrink' `
    @(@{ name = 'keep'; access = 'item'; typeHint = 'double'; optional = $true },
      @{ name = 'wired'; access = 'item'; typeHint = 'double'; optional = $true }) `
    @(@{ name = 'a'; access = 'item' })
$after = Get-Object $script
$afterInputs = Names $after.inputs
$afterOutputs = Names $after.outputs
Check 'K1.remove-unwired' (($shrink.state -eq 'committed') -and ($afterInputs -notcontains 'orphan')) `
    "$($shrink.state); inputs now: $($afterInputs -join ',')"
Check 'K2.keep-rest' (($afterInputs -contains 'keep') -and ($afterInputs -contains 'wired') -and ($afterOutputs -contains 'out')) `
    "inputs: $($afterInputs -join ','); outputs: $($afterOutputs -join ',')"

# --- K3/K4: dropping the WIRED 'wired' is refused, cleanly --------------------------------------
$refused = Set-Schema $script 'cut' `
    @(@{ name = 'keep'; access = 'item'; typeHint = 'double'; optional = $true }) `
    @(@{ name = 'a'; access = 'item' })
$afterRefusal = Get-Object $script
$stillThere = (Names $afterRefusal.inputs) -contains 'wired'
$stillWired = @($afterRefusal.inputs | Where-Object { $_.name -eq 'wired' -and $_.currentSources.Count -gt 0 }).Count -gt 0
Check 'K3.refuse-wired' (($refused.state -ne 'committed') -and $stillThere -and $stillWired) `
    "state=$($refused.state); socket present=$stillThere; wire intact=$stillWired"
Check 'K4.no-dead-end' ($refused.state -ne 'recoveryrequired') `
    "a wired-removal refusal must be a clean failure, got '$($refused.state)'"

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
$out = Join-Path $Run 'gate-socket-removal.json'
$summary | ConvertTo-Json -Depth 8 | Set-Content -Path $out -Encoding utf8
Write-Host ("gate-socket-removal: {0} — {1} passed, {2} failed → {3}" -f $summary.verdict, $summary.passed, $summary.failed, $out)
if ($failed.Count -gt 0) { exit 1 }
