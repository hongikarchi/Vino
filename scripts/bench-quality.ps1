#requires -Version 5.1
# Quality-ceiling track runner: ONE deep multi-round cell per arm, not a timed sprint.
#
#   bench-quality.ps1 -Arm A -Rounds 3
#
# Differences from bench-run.ps1 (the regression bench): the arm receives the REFERENCE
# PHOTOS as image input (V via message attachments, B via codex --image), rounds alternate
# authoring with an automated critic (claude CLI reads the references + current captures and
# writes Korean form-only feedback that becomes the next turn), there is no meaningful
# timeout pressure, and every round's definition/captures/feedback/usage snapshot is kept
# for before/after judging. Scoring happens later via Arctic clay recap renders.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('A', 'B')][string]$Arm,
    [int]$Rounds = 3,
    [string]$Round = 'quality-albahar',
    [int]$RoundTimeoutSeconds = 2700
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$cellDir = Join-Path $repo "artifacts\bench\$Round\$Arm"
New-Item -ItemType Directory -Force -Path $cellDir | Out-Null
$refsDir = Join-Path $repo 'artifacts\bench\quality-albahar\refs'
$refs = Get-ChildItem $refsDir -Filter 'ref*.jpg' | Sort-Object Name
if (@($refs).Count -lt 3) { throw "reference photos missing under $refsDir" }
$prompt = (Get-Content (Join-Path $PSScriptRoot 'bench\tasks\Q1-albahar.txt') -Raw -Encoding UTF8).Trim()

# --- boot -----------------------------------------------------------------------------
& (Join-Path $PSScriptRoot 'dev-loop.ps1') -SceneKind paneling -GhTemplate 'bench-definition.gh' | Out-Null
$run = (Get-ChildItem (Join-Path $repo 'artifacts\dev-loop') -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'loop-state.json') } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$state = Get-Content (Join-Path $run 'loop-state.json') -Raw | ConvertFrom-Json
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
function Wait-SessionIdle($sessionId, $seconds) {
    $active = @('working', 'drafting', 'queued', 'verifying')
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        Start-Sleep -Seconds 8
        $s = (Api GET '/runtime').sessions | Where-Object { $_.id -eq $sessionId }
        $status = if ($s) { $s.status } else { 'gone' }
        if ($status -notin $active) {
            Start-Sleep -Seconds 15
            $s = (Api GET '/runtime').sessions | Where-Object { $_.id -eq $sessionId }
            $confirm = if ($s) { $s.status } else { 'gone' }
            if ($confirm -in $active) { $status = $confirm }
        }
    } while ($status -in $active -and (Get-Date) -lt $deadline)
    return $status
}
$canvasReady = 0
foreach ($i in 1..40) {
    try { Api GET '/dev/snapshot' | Out-Null; $canvasReady++ } catch { $canvasReady = 0 }
    if ($canvasReady -ge 2) { break }
    Start-Sleep -Seconds 3
}
if ($canvasReady -lt 2) { throw 'Canvas never became readable after boot.' }
$mcpUp = $false
foreach ($i in 1..20) {
    if (netstat -ano | Select-String ':26929.*LISTENING') { $mcpUp = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $mcpUp) { throw 'Cordyceps MCP did not come up (needed for gh save/captures).' }

function Invoke-CordycepsCall {
    param([string]$Tool, [hashtable]$Arguments)
    $body = @{ jsonrpc = '2.0'; id = 9; method = 'tools/call'
        params = @{ name = $Tool; arguments = $Arguments } } | ConvertTo-Json -Depth 6
    $resp = Invoke-WebRequest -Uri 'http://127.0.0.1:26929/mcp' -Method Post -TimeoutSec 60 `
        -UseBasicParsing -ContentType 'application/json' `
        -Headers @{ Accept = 'application/json, text/event-stream' } -Body $body
    $raw = $resp.Content
    if ($raw -match 'data:') {
        $raw = ($raw -split "`r?`n" | Where-Object { $_ -like 'data:*' } |
            ForEach-Object { $_.Substring(5).Trim() }) | Select-Object -Last 1
    }
    $raw | ConvertFrom-Json | Out-Null
}

function Save-RoundState([int]$roundIndex) {
    $tag = "round$roundIndex"
    try {
        Invoke-CordycepsCall 'gh_document' @{ action = 'save'; path = (Join-Path $cellDir "$tag.gh") }
    } catch { Add-Content (Join-Path $cellDir 'notes.txt') "$tag gh save failed: $($_.Exception.Message)" }
    foreach ($view in 'Perspective', 'Front') {
        try {
            Invoke-CordycepsCall 'gh_document' @{ action = 'capture_viewport'; view = $view
                path = (Join-Path $cellDir "$tag-$($view.ToLower()).png"); width = 1400; height = 900 }
        } catch { Add-Content (Join-Path $cellDir 'notes.txt') "$tag $view capture failed: $($_.Exception.Message)" }
    }
    try {
        Api GET '/runtime' | ConvertTo-Json -Depth 8 |
            Set-Content (Join-Path $cellDir "$tag-runtime.json") -Encoding utf8
    } catch { }
}

function Get-CriticFeedback([int]$roundIndex) {
    $tag = "round$roundIndex"
    $captures = @('perspective', 'front') | ForEach-Object { Join-Path $cellDir "$tag-$_.png" } |
        Where-Object { Test-Path $_ }
    if (@($captures).Count -eq 0) { return $null }
    # claude CLI has no image flag (that is codex's -i): headless claude SEES images by
    # Reading the files, so list absolute paths in the prompt and allow only Read.
    $refList = ($refs | ForEach-Object { $_.FullName }) -join "`n"
    $capList = ($captures) -join "`n"
    $criticPrompt = @"
너는 건축 형태 비평가다. 먼저 아래 참조 사진 파일들을 전부 Read로 읽어라(목표 건축물, 알 바하르 타워):
$refList
다음으로 현재 파라메트릭 모델의 렌더를 읽어라:
$capList
참조 대비 현재 모델의 형태 결함과 부족을 3~6개, 구체적으로 지적하라 - 어느 부위가, 어떻게 다른지, 어떤 방향으로 고쳐야 하는지. 형태만 다뤄라(재질·색·조명 언급 금지). 잘 재현된 점도 1~2개 말하라. 서론 없이 목록만 한국어로 출력하라.
"@
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $feedback = (& claude -p $criticPrompt --model sonnet --allowedTools Read 2>$null | Out-String).Trim()
    } finally { $ErrorActionPreference = $eap }
    if ($feedback) {
        Set-Content (Join-Path $cellDir "$tag-critic.txt") $feedback -Encoding utf8
    }
    return $feedback
}

$sw = [Diagnostics.Stopwatch]::StartNew()
if ($Arm -eq 'A') {
    $attachments = @($refs | ForEach-Object {
        @{ FileName = $_.Name; MediaType = 'image/jpeg'
           DataBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName)) }
    })
    $sid = (Api POST '/sessions' @{ Name = "quality-$Round"; ModelProfile = 'xhigh' }).id
    Api PUT "/sessions/$sid/permission" @{ mode = 'fullAuto' } | Out-Null
    Api POST "/sessions/$sid/messages" @{
        Content = $prompt; ClientMessageId = [guid]::NewGuid().ToString(); Attachments = $attachments
    } | Out-Null
    for ($r = 1; $r -le $Rounds; $r++) {
        $status = Wait-SessionIdle $sid $RoundTimeoutSeconds
        Write-Output "round $r turn ended: $status"
        Save-RoundState $r
        if ($r -eq $Rounds) { break }
        $feedback = Get-CriticFeedback $r
        if (-not $feedback) { Write-Output "round $r critic empty - stopping"; break }
        Api POST "/sessions/$sid/messages" @{
            Content = "[비평 라운드 $r] 아래 형태 비평을 반영해 모델을 개선하라. 스스로도 사진과 대조해 판단하라.`n`n$feedback"
            ClientMessageId = [guid]::NewGuid().ToString()
        } | Out-Null
    }
    $messages = Api GET "/sessions/$sid/messages"
    ($messages | ForEach-Object { "== [$($_.role)] $($_.createdAt)`n$($_.content)`n" }) |
        Set-Content (Join-Path $cellDir 'transcript.txt') -Encoding utf8
}
else {
    $armCwd = Join-Path $env:LOCALAPPDATA "Temp\vino-bench\$Round-B"
    if (Test-Path $armCwd) { Remove-Item $armCwd -Recurse -Force -Confirm:$false }
    New-Item -ItemType Directory -Force -Path $armCwd | Out-Null
    $mcpConfigCmd = @(
        '-c', 'mcp_servers.cordyceps.command="npx"',
        '-c', 'mcp_servers.cordyceps.args=[\"-y\",\"mcp-remote\",\"http://127.0.0.1:26929/mcp\"]'
    )
    $imgArgs = @(); foreach ($ref in $refs) { $imgArgs += @('-i', $ref.FullName) }
    $roundPrompt = $prompt
    for ($r = 1; $r -le $Rounds; $r++) {
        Push-Location $armCwd
        $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try {
            & codex exec --skip-git-repo-check @mcpConfigCmd @imgArgs $roundPrompt 2>&1 |
                Tee-Object -FilePath (Join-Path $cellDir "round$r-transcript.txt") | Out-Null
        }
        finally { $ErrorActionPreference = $eap; Pop-Location }
        Write-Output "round $r codex exec done"
        Save-RoundState $r
        if ($r -eq $Rounds) { break }
        $feedback = Get-CriticFeedback $r
        if (-not $feedback) { Write-Output "round $r critic empty - stopping"; break }
        # codex exec is one-shot: each round is a fresh process, continuity lives in the CANVAS.
        $roundPrompt = "이전 라운드에서 네가 이 Grasshopper 캔버스에 만든 알 바하르 타워 파라메트릭 정의가 그대로 열려 있다. " +
            "먼저 캔버스를 읽어 현재 상태를 파악한 뒤, 아래 형태 비평을 반영해 그 정의를 수정·개선하라(새로 만들지 말 것). " +
            "첨부 사진들이 목표다.`n`n[비평]`n$feedback"
    }
}
$sw.Stop()
Add-Content (Join-Path $cellDir 'notes.txt') "total $([math]::Round($sw.Elapsed.TotalMinutes)) minutes, rounds=$Rounds"

# --- teardown (PID-scoped) ------------------------------------------------------------
$benchRhino = Get-Process -Id $state.rhinoPid -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -eq 'Rhino' }
if ($benchRhino) { Stop-Process -Id $benchRhino.Id -Force -Confirm:$false; $benchRhino.WaitForExit() }
Get-Process Rhino -ErrorAction SilentlyContinue | Where-Object {
    $_.StartTime -gt (Get-Date).AddHours(-6) -and
    ($_.MainWindowTitle -eq '' -or $_.MainWindowTitle -match '^Untitled')
} | ForEach-Object { Stop-Process -Id $_.Id -Force -Confirm:$false -ErrorAction SilentlyContinue }
Write-Output "QUALITY CELL DONE ($Arm)"
