#requires -Version 5.1
# Scored-round batch driver: runs a list of bench cells sequentially through bench-run.ps1.
#
#   bench-round.ps1 -Round round1-20260818 -Cells A-T2-r1,B-T2-r1,C-T2-r1
#
# Refuses to start (and to clean up after a failed cell) while a NON-BENCH Rhino is open:
# the runner's teardown kills Rhino processes, and a user session with real work must never
# be collateral. Bench Rhinos are recognized by their fixture document title.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Round,
    [Parameter(Mandatory)][string[]]$Cells,
    [int]$TimeoutSeconds = 1500
)
$ErrorActionPreference = 'Continue'

function Get-ForeignRhino {
    Get-Process Rhino -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -notmatch '^scene(-[a-z-]+)?\.3dm' }
}

$foreign = @(Get-ForeignRhino)
if ($foreign.Count -gt 0) {
    throw "Refusing to start: non-bench Rhino open ($($foreign[0].MainWindowTitle)). Close it first."
}

foreach ($cell in $Cells) {
    if ($cell -notmatch '^([ABC])-(T[12])-r(\d+)$') { Write-Output "SKIP malformed cell id: $cell"; continue }
    $arm = $Matches[1]; $task = $Matches[2]; $rep = [int]$Matches[3]
    Write-Output "===== CELL $cell ====="
    try {
        & (Join-Path $PSScriptRoot 'bench-run.ps1') -Arm $arm -Task $task -Rep $rep `
            -Round $Round -TimeoutSeconds $TimeoutSeconds
    }
    catch {
        Write-Output "CELL $cell FAILED: $($_.Exception.Message)"
        # A cell that died before its own teardown leaves the bench Rhino running; clear it so
        # the next cell boots clean. Only bench-titled Rhinos are ever touched.
        Get-Process Rhino -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowTitle -match '^scene(-[a-z-]+)?\.3dm' } |
            ForEach-Object { Stop-Process -Id $_.Id -Force -Confirm:$false }
        if (@(Get-ForeignRhino).Count -gt 0) {
            Write-Output 'ABORT: non-bench Rhino appeared; stopping the batch.'
            break
        }
    }
    Start-Sleep -Seconds 5
}
Write-Output "===== BATCH DONE ($Round) ====="
Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\bench\$Round\scores.csv") -ErrorAction SilentlyContinue
