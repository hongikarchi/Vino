#requires -Version 5.1
# Live gate for layer curation:
#
#   layerSemantics audit -> server-synthesized proposal card -> partial grant -> apply -> verify
#
# Needs a dev-loop run booted with -SceneKind layer-curation -NoGrasshopper. That fixture plants
# a layer table with one of every branch: an exact Korean alias, a variant mark that only the
# digit pattern catches, case twins, a whitespace-padded name, a compound Korean name no rule
# resolves, a custom-coloured layer, a layer that already has a render material, and a layer whose
# geometry exists only inside a block definition. A tidy fixture would let this report PASS
# without ever running the branches it claims to prove. -NoGrasshopper is deliberate too: layer
# work needs no canvas, so the gate also proves the Rhino-only target carries a real feature.
#
# Nothing here trusts the agent's prose. Every claim is crossed between two independent
# observations: GET /layers (what the document says) and /dev/audit (what the scanner says).
# The two disagreeing IS the bug — that is why both are read.
#
# NOTE: this file must stay UTF-8 WITH BOM. Windows PowerShell 5.1 reads a BOM-less .ps1 as the
# system ANSI codepage, which turns every Korean prompt below into mojibake -- and the agent then
# answers a question nobody asked.
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
if ($state.scene3dm -notmatch 'layer-curation') {
    throw "This gate needs a -SceneKind layer-curation run; got $($state.scene3dm)"
}
$base = $state.uiBaseUrl.TrimEnd('/') + '/api/v1'
$headers = @{ 'X-GPTino-Token' = $state.token }
function Api($method, $path, $body) {
    $uri = $base + $path
    if ($null -ne $body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 8 -Compress))
        return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -Body $bytes `
            -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
    }
    return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec 60
}
function Get-Session($sessionId) {
    return (Api GET '/runtime').sessions | Where-Object { $_.id -eq $sessionId }
}
function Send-Turn($sessionId, $text, $seconds) {
    Api POST "/sessions/$sessionId/messages" @{ Content = $text; ClientMessageId = [guid]::NewGuid().ToString() } | Out-Null
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        Start-Sleep -Seconds 5
        $s = Get-Session $sessionId
        $status = if ($s) { $s.status } else { 'gone' }
    } while ($status -eq 'working' -and (Get-Date) -lt $deadline)
    return $status
}
function Layer-Map($layers) {
    $map = @{}
    foreach ($l in $layers) { $map[$l.fullPath] = $l }
    return $map
}
function User-Text($layer, $key) {
    if (-not $layer.userText) { return $null }
    $prop = $layer.userText.PSObject.Properties | Where-Object { $_.Name -eq $key }
    if ($prop) { return $prop.Value }
    return $null
}

$results = [ordered]@{}

# --- 0. the fixture must actually exercise the branches -------------------------
$scan = (Api GET '/dev/audit?kind=layerSemantics&limit=100').result
$results['0-scanned-layers'] = $scan.scannedObjects
$results['0-findings'] = $scan.findings.Count
if ($scan.findings.Count -lt 8) {
    throw "layerSemantics reported $($scan.findings.Count) unlabeled layer(s); the fixture defines at least 8. A gate that runs on an empty finding set proves nothing."
}
# Every finding must carry the structured facts the proposal synthesis consumes.
$withFacts = @($scan.findings | Where-Object { $_.layerFacts })
$results['0-findings-with-facts'] = $withFacts.Count
# The block-only layer is the scope-gap fixture: top-level enumeration sees nothing on it.
$blockOnly = @($scan.findings | Where-Object { $_.layerFacts.fullPath -eq 'BlockOnly' })
$results['0-block-only-member-count'] = if ($blockOnly.Count -eq 1) { $blockOnly[0].layerFacts.blockMemberObjectCount } else { 'missing' }
$results['0-block-only-top-level'] = if ($blockOnly.Count -eq 1) { $blockOnly[0].layerFacts.topLevelObjectCount } else { 'missing' }
# The pre-materialled layer must report its material NAME (the matcher's second signal).
$withMaterial = @($scan.findings | Where-Object { $_.layerFacts.renderMaterialName })
$results['0-layers-with-render-material'] = $withMaterial.Count

$layersBefore = (Api GET '/layers').result.layers
$beforeMap = Layer-Map $layersBefore
$results['0-layers-total'] = $layersBefore.Count

# --- 1. ask for curation --------------------------------------------------------
$sessionId = (Api POST '/sessions' @{ Name = 'layer-curation-gate'; ModelProfile = 'xhigh' }).id
$results['1-scan-turn'] = Send-Turn $sessionId `
    '레이어를 정리해줘. 재료별로 색을 입히고 이름 라벨도 붙여줘.' `
    $TimeoutSeconds

# --- 2. the proposal card -------------------------------------------------------
$raw = (Get-Session $sessionId).approvalCard
if (-not $raw) { throw 'No approval card was proposed - the agent never called approval_request.' }
$card = $raw | ConvertFrom-Json
$results['2-card-kind'] = $card.kind
$results['2-card-items'] = $card.items.Count
$rows = @($card.items | Where-Object { $_.layerRow })
$results['2-items-with-server-rows'] = $rows.Count
if ($card.kind -ne 'layerSemantics' -or $rows.Count -lt 1) {
    throw 'The card is not a server-synthesized layer proposal (kind/layerRow missing).'
}
# The confidence spectrum must be real, not all one bucket.
$results['2-confidence-high'] = @($rows | Where-Object { $_.layerRow.confidence -eq 'high' }).Count
$results['2-confidence-medium'] = @($rows | Where-Object { $_.layerRow.confidence -eq 'medium' }).Count
$results['2-confidence-low'] = @($rows | Where-Object { $_.layerRow.confidence -eq 'low' }).Count
# THE number this gate exists to measure: how much of a realistic table the shipped
# alias seed actually resolves without asking the model.
$results['2-deterministic-match-rate'] =
    "$($results['2-confidence-high'] + $results['2-confidence-medium'])/$($rows.Count)"
$results['2-preset-offered'] = [bool]$card.preset
# A custom-coloured layer must arrive unticked so a bulk approve cannot stomp it.
$custom = @($rows | Where-Object { $_.layerRow.fullPath -match '외벽' })
$results['2-custom-colour-prechecked'] = if ($custom.Count -eq 1) { $custom[0].layerRow.preChecked } else { 'missing' }

# --- 3. grant all but one, and refuse one deliberately --------------------------
$refused = $rows | Select-Object -Last 1
$granted = @($rows | Where-Object { $_.id -ne $refused.id } | ForEach-Object { $_.id })
$answer = @{ status = 'granted'; approvedItemIds = $granted }
# A triage row needs the user's material pick; send the first offered option for each.
$choices = @{}
foreach ($item in $rows) {
    if ($granted -contains $item.id -and $item.choices -and $item.choices.Count -gt 0) {
        $choices[$item.id] = $item.choices[0]
    }
}
if ($choices.Count -gt 0) { $answer.choices = $choices }
$results['3-choices-sent'] = $choices.Count
# Switch the colour convention on the card: the server must re-derive every granted row.
if ($card.preset -and $card.preset.options.Count -gt 1) {
    $other = ($card.preset.options | Where-Object { $_.id -ne $card.preset.selected } | Select-Object -First 1).id
    $answer.preset = $other
    $results['3-preset-switched-to'] = $other
}
Api PUT "/sessions/$sessionId/approval" $answer | Out-Null
$granted_card = (Get-Session $sessionId).approvalCard | ConvertFrom-Json
$results['3-grant-minted'] = [bool]$granted_card.grantId
$results['3-preset-persisted'] = if ($answer.preset) { $granted_card.preset.selected -eq $answer.preset } else { 'not-switched' }
# Every granted row must now carry a full label set — a triage row included.
$grantedRows = @($granted_card.items | Where-Object { $granted -contains $_.id -and $_.layerRow })
$results['3-granted-rows-fully-labeled'] =
    -not @($grantedRows | Where-Object { -not $_.layerRow.canonical -or -not $_.layerRow.material }).Count

# --- 4. apply -------------------------------------------------------------------
$results['4-apply-turn'] = Send-Turn $sessionId '승인했어. 적용해줘.' $TimeoutSeconds

# --- 5. two independent observations, crossed ------------------------------------
$layersAfter = (Api GET '/layers').result.layers
$afterMap = Layer-Map $layersAfter
$rescan = (Api GET '/dev/audit?kind=layerSemantics&limit=100').result
$results['5-findings-after'] = $rescan.findings.Count

$applied = 0; $colourMatches = 0; $labelMatches = 0
foreach ($item in $grantedRows) {
    $path = $item.layerRow.fullPath
    $after = $afterMap[$path]
    if (-not $after) { continue }
    $applied++
    if ($after.argbColor -eq $item.layerRow.proposedArgbColor) { $colourMatches++ }
    if ((User-Text $after 'gptino.canonical') -eq $item.layerRow.canonical -and
        (User-Text $after 'gptino.material') -eq $item.layerRow.material) { $labelMatches++ }
}
$results['5-granted-layers-found'] = "$applied/$($grantedRows.Count)"
$results['5-colour-applied'] = "$colourMatches/$applied"
$results['5-labels-applied'] = "$labelMatches/$applied"

# Observation 2 must agree with observation 1: a labeled layer drops out of the scan.
$stillUnlabeled = @()
foreach ($item in $grantedRows) {
    if (@($rescan.findings | Where-Object { $_.layerFacts.fullPath -eq $item.layerRow.fullPath }).Count -gt 0) {
        $stillUnlabeled += $item.layerRow.fullPath
    }
}
$results['5-granted-still-reported-unlabeled'] = $stillUnlabeled -join ', '
$results['5-observations-agree'] = ($stillUnlabeled.Count -eq 0 -and $labelMatches -eq $applied)

# The refused row must be untouched on BOTH observations.
$refusedPath = $refused.layerRow.fullPath
$refusedAfter = $afterMap[$refusedPath]
$results['5-refused-colour-unchanged'] =
    ($refusedAfter -and $refusedAfter.argbColor -eq $beforeMap[$refusedPath].argbColor)
$results['5-refused-still-unlabeled'] =
    (@($rescan.findings | Where-Object { $_.layerFacts.fullPath -eq $refusedPath }).Count -eq 1)

# The pre-batch snapshot is the only revert path for colours; it must exist.
$results['5-snapshot-saved'] =
    [bool](@((Api GET '/layers').result.namedLayerStates | Where-Object { $_ -match 'GPTino' }).Count)

$pass = $results['1-scan-turn'] -eq 'idle' -and
        $results['4-apply-turn'] -eq 'idle' -and
        $results['2-card-kind'] -eq 'layerSemantics' -and
        $results['2-custom-colour-prechecked'] -eq $false -and
        $results['3-grant-minted'] -and
        $results['3-granted-rows-fully-labeled'] -and
        ($results['3-preset-persisted'] -ne $false) -and
        $applied -eq $grantedRows.Count -and
        $colourMatches -eq $applied -and
        $labelMatches -eq $applied -and
        $results['5-observations-agree'] -and
        $results['5-refused-colour-unchanged'] -and
        $results['5-refused-still-unlabeled'] -and
        $results['5-snapshot-saved']
$results['GATE'] = if ($pass) { 'PASS' } else { 'FAIL' }
$results['sessionId'] = $sessionId
[pscustomobject]$results | Format-List
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Run 'gate-layer-curation.json') -Encoding utf8
if (-not $pass) { exit 1 }
