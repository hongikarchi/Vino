#requires -Version 5.1
# Vino dev-loop API driver. Reads loop-state.json (newest dev-loop run unless -Run
# is given) for the loopback endpoint + token, and drives the AgentHost head-lessly.
#
#   dev-drive.ps1 runtime
#   dev-drive.ps1 new-session -Name "ref-test" -Profile xhigh -Model gpt-5.6-mini
#   dev-drive.ps1 send -SessionId <guid> -Content "..."   (Korean-safe UTF-8)
#   dev-drive.ps1 messages -SessionId <guid>
#   dev-drive.ps1 jobs                                     (live-jobs.db summary)
#
# -Profile takes the CURRENT effort vocabulary (low..ultra); the legacy words
# (auto/fast/standard/deep) stay accepted because the server still normalizes them.
# -Model pins a catalog model for the session (model/effort A-B matrices need both knobs).
# -Permission sets the session permission level right after creation (gate/benchmark setup).
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('runtime', 'new-session', 'send', 'messages', 'jobs', 'selection')]
    [string]$Action,
    [string]$SessionId,
    [string]$Content,
    [string]$Name = 'dev',
    [ValidateSet('low', 'medium', 'high', 'xhigh', 'max', 'ultra', 'auto', 'fast', 'standard', 'deep')]
    [string]$Profile = 'xhigh',
    [string]$Model,
    [ValidateSet('review', 'standard', 'fullAuto')]
    [string]$Permission,
    [string]$Run,
    [switch]$Raw
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

if (-not $Run) {
    $Run = (Get-ChildItem (Join-Path $repo 'artifacts\dev-loop') -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'loop-state.json') } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $Run) { throw 'No dev-loop run with loop-state.json found.' }
$state = Get-Content -LiteralPath (Join-Path $Run 'loop-state.json') -Raw | ConvertFrom-Json
$base = $state.uiBaseUrl.TrimEnd('/') + '/api/v1'
$headers = @{ 'X-Vino-Token' = $state.token }

function Invoke-Api {
    param([string]$Method, [string]$Path, $Body)
    $uri = $base + $Path
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 8 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers `
            -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec 30
    }
    return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -TimeoutSec 30
}

switch ($Action) {
    'runtime' {
        $rt = Invoke-Api GET '/runtime'
        if ($Raw) { $rt | ConvertTo-Json -Depth 8 }
        else {
            [pscustomobject]@{
                health     = $rt.health
                codexAuth  = $rt.codexAuth.status
                ghDocs     = ($rt.grasshopperDocs | ForEach-Object { $_.id }) -join ','
                selection  = "$($rt.currentSelection.rhinoObjectCount) rhino / $($rt.currentSelection.grasshopperObjectCount) gh"
                sessions   = ($rt.sessions | ForEach-Object { "$($_.id.Substring(0,8)):$($_.status)" }) -join ', '
                queue      = $rt.queue.Count
                conflicts  = $rt.conflicts.Count
            } | Format-List
        }
    }
    'selection' {
        (Invoke-Api GET '/runtime').currentSelection | ConvertTo-Json -Depth 6
    }
    'new-session' {
        $body = @{ Name = $Name; ModelProfile = $Profile; GrasshopperDoc = $state.sceneGh }
        if ($Model) { $body.Model = $Model }
        # GrasshopperDoc binds by docKey; the sole open doc is the default, but pass the
        # observed id explicitly when available.
        $rt = Invoke-Api GET '/runtime'
        if ($rt.grasshopperDocs.Count -ge 1) { $body.GrasshopperDoc = $rt.grasshopperDocs[0].id }
        $session = Invoke-Api POST '/sessions' $body
        if ($Permission) {
            Invoke-Api PUT "/sessions/$($session.id)/permission" @{ mode = $Permission } | Out-Null
        }
        Write-Output $session.id
    }
    'send' {
        if (-not $SessionId) { throw '-SessionId is required for send.' }
        if (-not $Content) { throw '-Content is required for send.' }
        $body = @{ Content = $Content; ClientMessageId = [guid]::NewGuid().ToString() }
        Invoke-Api POST "/sessions/$SessionId/messages" $body | Out-Null
        Write-Output "sent"
    }
    'messages' {
        if (-not $SessionId) { throw '-SessionId is required for messages.' }
        $msgs = Invoke-Api GET "/sessions/$SessionId/messages"
        if ($Raw) { $msgs | ConvertTo-Json -Depth 8 }
        else {
            foreach ($m in $msgs) {
                $c = if ($m.content) { $m.content } else { '' }
                if ($c.Length -gt 4000) { $c = $c.Substring(0, 4000) + ' …[truncated]' }
                "== [$($m.role)] $($m.createdAt) =="
                $c
                ''
            }
        }
    }
    'jobs' {
        # Summarize live-jobs.db without a SQLite dependency by using the AgentHost's own
        # Microsoft.Data.Sqlite from the installed package.
        $pkg = Join-Path $env:APPDATA 'McNeel\Rhinoceros\packages\8.0\Vino'
        $sqlite = Get-ChildItem $pkg -Recurse -Filter 'Microsoft.Data.Sqlite.dll' -File | Select-Object -First 1
        $native = Get-ChildItem $pkg -Recurse -Filter 'e_sqlite3.dll' -File | Select-Object -First 1
        if (-not $sqlite) { throw 'Microsoft.Data.Sqlite.dll not found in the installed package.' }
        Add-Type -Path $sqlite.FullName
        $dbPath = Join-Path $state.runtime 'live-jobs.db'
        $cs = "Data Source=$dbPath;Mode=ReadOnly;Cache=Shared"
        $conn = [Microsoft.Data.Sqlite.SqliteConnection]::new($cs)
        $conn.Open()
        try {
            $cmd = $conn.CreateCommand()
            $cmd.CommandText = @'
SELECT state, COUNT(*) AS n FROM jobs GROUP BY state ORDER BY n DESC
'@
            $r = $cmd.ExecuteReader()
            while ($r.Read()) { "{0,-20} {1}" -f $r.GetString(0), $r.GetInt32(1) }
            $r.Close()
        }
        finally { $conn.Close() }
    }
}
