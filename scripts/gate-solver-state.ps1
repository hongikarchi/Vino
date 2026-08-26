#requires -Version 5.1
# Live gate for the solver-state reporting fix (log review 2026-08-26, B04/A14).
#
# The defect: EnsureSolverEnabled returned "did I flip a switch", not "what is the state", and the
# caller only spoke up when it returned true. Worse, GH_Document.EnableSolutions has an asymmetric
# accessor pair — the getter is (m_enableSolutions && CanSolve) while the setter compares
# m_enableSolutions alone — so with Rhino momentarily busy we "re-enabled" an already-true flag and
# told the user their solver had been off. It had not been. A user answered that with
# "enable solver 되어있는데 무슨 소리야", and they were right.
#
#   S1 was-enabled : a normal execute reports POSITIVE confirmation that the solver was on and a
#                    solution ran. Without this the model only ever hears about the solver when
#                    something is wrong, so an empty output had no evidence against the
#                    "the user disabled the solver" story — and the model told that story.
#   S2 re-enabled  : with the solver genuinely OFF (a script component turns it off during its own
#                    solve), the next execute reports that Vino re-enabled it — and only then.
#   S3 no false    : the S1 execute must NOT claim a re-enable. This is the regression that
#                    produced the false narrative.
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

$script:results = @()
function Check($id, $ok, $detail) {
    $script:results += [pscustomobject]@{ Id = $id; Ok = [bool]$ok; Detail = "$detail" }
    Write-Host ("  [{0}] {1} — {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $id, $detail)
}
function Diagnostics($result) {
    if ($null -eq $result.diagnostics) { return @() }
    return @($result.diagnostics | ForEach-Object { "$($_.code): $($_.message)" })
}
function Has($lines, $fragment) {
    return @($lines | Where-Object { $_ -like "*$fragment*" }).Count -gt 0
}

$CSHARP = 'b6ba1144-02d6-4a2d-b53c-ec62e290eeb7'

Write-Host "gate-solver-state: run $Run"
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
$sessionId = (Api POST '/sessions' @{ name = 'solver-state gate' }).id
Write-Host "  session $sessionId"

function New-ScriptComponent($label) {
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
                        componentTypeId = $CSHARP; pivot = 'gptino:auto'; resultOutput = $null
                    }
                }
            })
    }
    if ($result.state -ne 'committed') { throw "create $label failed: $($result.state) — $($result.message)" }
    return $objectId
}

function Set-Source($objectId, $label, $source) {
    $resource = @{ kind = 'grasshopperComponentSource'; id = $objectId; field = '*' }
    $result = Api POST "/dev/change/$sessionId" @{
        summary    = "gate: source $label"
        writeSet   = @(@{ resource = $resource; expectedFingerprint = 'gptino:auto' })
        operations = @(@{
                operationId = "src-$label"; kind = 'updatePythonSource'; owner = 'script'
                reads = @(); writes = @($resource); reversible = $true
                payload = @{
                    bridgeOperation = 'python.setSource'
                    arguments       = @{
                        operationId = "src-$label"; componentId = $objectId
                        expectedSourceSha256 = 'gptino:auto'; source = $source
                        runtime = 'csharp'; expireSolution = $true
                    }
                }
            })
    }
    if ($result.state -ne 'committed') { throw "source $label failed: $($result.state) — $($result.message)" }
}

function Invoke-Execute($objectId, $label) {
    $resource = @{ kind = 'grasshopperComponentValue'; id = $objectId; field = '*' }
    return Api POST "/dev/change/$sessionId" @{
        summary    = "gate: execute $label"
        writeSet   = @(@{ resource = $resource; expectedFingerprint = 'gptino:auto' })
        operations = @(@{
                operationId = "exec-$label"; kind = 'executePython'; owner = 'script'
                reads = @(); writes = @($resource); reversible = $true
                payload = @{
                    bridgeOperation = 'python.execute'
                    arguments       = @{
                        operationId = "exec-$label"; componentId = $objectId
                        expireUpstream = $false; recomputeDocument = $false
                    }
                }
            })
    }
}

# --- S1/S3: a normal execute confirms the solver was on, and claims no re-enable ---------------
$producer = New-ScriptComponent 'producer'
Set-Source $producer 'producer' @"
// #! csharp
using System;
a = 42;
"@
$normal = Invoke-Execute $producer 'normal'
$normalDiag = Diagnostics $normal
Check 'S1.was-enabled' (Has $normalDiag 'solver was enabled and a solution ran') `
    ("$($normal.state); " + (($normalDiag | Select-Object -First 3) -join ' | '))
Check 'S3.no-false-reenable' (-not (Has $normalDiag 'was disabled')) `
    'a healthy execute must not claim Vino re-enabled anything'

# --- S2: with the solver genuinely off, the next execute says it re-enabled it ------------------
# A script component can turn the global solver off during its own solve — the only way to reach
# this state headlessly, and exactly the state a user reaches with Solution > Disable Solver.
$saboteur = New-ScriptComponent 'saboteur'
Set-Source $saboteur 'saboteur' @"
// #! csharp
using Grasshopper.Kernel;
GH_Document.EnableSolutions = false;
a = 1;
"@
Invoke-Execute $saboteur 'saboteur' | Out-Null
Start-Sleep -Seconds 1
$afterOff = Invoke-Execute $producer 'after-off'
$afterDiag = Diagnostics $afterOff
Check 'S2.re-enabled' (Has $afterDiag 'was disabled') `
    ("$($afterOff.state); " + (($afterDiag | Select-Object -First 3) -join ' | '))

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
$out = Join-Path $Run 'gate-solver-state.json'
$summary | ConvertTo-Json -Depth 8 | Set-Content -Path $out -Encoding utf8
Write-Host ("gate-solver-state: {0} — {1} passed, {2} failed → {3}" -f $summary.verdict, $summary.passed, $summary.failed, $out)
if ($failed.Count -gt 0) { exit 1 }
