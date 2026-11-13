param(
    [string]$ApiBase = 'http://127.0.0.1:27000',
    [string]$OutputPvf,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'

function Invoke-Pvf([string]$Endpoint, [string]$Method = 'GET', $Payload = $null) {
    $parameters = @{ Uri = $ApiBase.TrimEnd('/') + '/Api/PvfUtiltiy/' + $Endpoint; Method = $Method; TimeoutSec = 120 }
    if ($null -ne $Payload) {
        $parameters.ContentType = 'application/json; charset=utf-8'
        $parameters.Body = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Payload -Depth 6))
    }
    $result = Invoke-RestMethod @parameters
    if ($null -eq $result -or $result.IsError -ne $false) { throw "PVF API failed: $Endpoint; $($result.Msg)" }
    return $result.Data
}
function Read-Pvf([string]$Path) {
    return [string](Invoke-Pvf ('GetFileContent?filePath=' + [uri]::EscapeDataString($Path)))
}
function Section([string]$Text, [string]$Name) {
    $matches = [regex]::Matches($Text, '(?is)\[' + [regex]::Escape($Name) + '\](?<body>.*?)\[/' + [regex]::Escape($Name) + '\]')
    if ($matches.Count -ne 1) { throw "Expected one section: [$Name]" }
    return $matches[0].Groups['body']
}
function Tokens([string]$Text) {
    return @([regex]::Matches($Text, '`[^`]*`|\S+') | ForEach-Object { $_.Value })
}

$sourcePvf = [string](Invoke-Pvf 'GetPvfPackFilePath')
if ([string]::IsNullOrWhiteSpace($sourcePvf)) { throw 'Load the target PVF in pvfUtility first.' }
if ($Apply) {
    if (-not $OutputPvf -or -not [IO.Path]::IsPathRooted($OutputPvf)) { throw 'Apply requires an absolute -OutputPvf path.' }
    $OutputPvf = [IO.Path]::GetFullPath($OutputPvf)
    if ($OutputPvf -eq [IO.Path]::GetFullPath($sourcePvf) -or (Test-Path -LiteralPath $OutputPvf)) {
        throw 'Output must be a new file; the loaded source must not be overwritten.'
    }
    if (-not (Test-Path -LiteralPath (Split-Path -Parent $OutputPvf) -PathType Container)) { throw 'Output parent directory does not exist.' }
}
$roomPath = 'map/common/(r)gate_seria.map'
$hiddenPath = 'etc/hiddennpc.etc'
$room = Read-Pvf $roomPath
$hidden = Read-Pvf $hiddenPath
$definition = Read-Pvf 'npc/chn_monstercolosseum_nolan.npc'
foreach ($pattern in @('(?is)\[role\].*?`\[item shop\]`\s+400000097\b.*?\[/role\]',
                       '(?is)\[role\].*?`\[open monster mock battle\]`\s+1094\b.*?\[/role\]',
                       '(?i)\[open visible event\]\s+2532\b')) {
    if ($definition -notmatch $pattern) { throw 'NPC400001107 does not match the expected original roles/event.' }
}

$npcSection = Section $room 'NPC'
$npcTokens = [regex]::Matches($npcSection.Value, '`[^`]*`|\S+')
if ($npcTokens.Count % 5) { throw 'NPC placement must contain groups of five tokens.' }
$primary = @(); $overlay = @()
for ($index = 0; $index -lt $npcTokens.Count; $index += 5) {
    if ($npcTokens[$index].Value -eq '400001107') { $primary += $index }
    if ($npcTokens[$index].Value -eq '400001112') { $overlay += $index }
}
if ($primary.Count -ne 1 -or $overlay.Count -gt 1) { throw 'Unexpected Nolan placement count.' }
$newRoom = $room
if ($overlay.Count) {
    for ($field = 1; $field -lt 5; $field++) {
        if ($npcTokens[$primary[0] + $field].Value -ne $npcTokens[$overlay[0] + $field].Value) {
            throw 'Nolan variants have different placement; inspect manually before removing NPC112.'
        }
    }
    $first = $npcTokens[$overlay[0]]
    $last = $npcTokens[$overlay[0] + 4]
    $newRoom = $room.Remove($npcSection.Index + $first.Index, $last.Index + $last.Length - $first.Index)
}
$hiddenSection = Section $hidden 'hidden npc'
$hiddenTokens = [regex]::Matches($hiddenSection.Value, '\S+')
$hidden107 = @($hiddenTokens | Where-Object { $_.Value -eq '400001107' })
if (@($hiddenTokens | Where-Object { $_.Value -notmatch '^\d+$' }).Count -or $hidden107.Count -gt 1) {
    throw 'Unexpected hidden NPC table; inspect manually.'
}
$newHidden = $hidden
if ($hidden107.Count) {
    $newHidden = $hidden.Remove($hiddenSection.Index + $hidden107[0].Index, $hidden107[0].Length)
}
$changes = @(
    @{ FilePath = $roomPath; FileContent = $newRoom },
    @{ FilePath = $hiddenPath; FileContent = $newHidden }
)
Write-Output (ConvertTo-Json -Compress -InputObject @{
    Source = $sourcePvf; RemovedRoomNpc112 = $overlay.Count; RemovedHiddenNpc107 = $hidden107.Count; Apply = [bool]$Apply
})
if (-not $Apply) { Write-Output 'Preview complete. Use -Apply -OutputPvf <new-absolute-path> to import and save.'; return }
if ((Read-Pvf $roomPath) -cne $room -or (Read-Pvf $hiddenPath) -cne $hidden -or
    ([string](Invoke-Pvf 'GetPvfPackFilePath')) -ne $sourcePvf) { throw 'Loaded PVF changed during preparation; rerun.' }
if ($overlay.Count -or $hidden107.Count) { $null = Invoke-Pvf 'ImportFiles' 'POST' $changes }
foreach ($change in $changes) {
    if (((Tokens (Read-Pvf $change.FilePath)) -join "`n") -cne ((Tokens $change.FileContent) -join "`n")) {
        throw 'Import verification failed. Reload the source PVF before retrying; output has not been saved.'
    }
}
$null = Invoke-Pvf ('SaveAsPvfFile?filePath=' + [uri]::EscapeDataString($OutputPvf))
if (-not (Test-Path -LiteralPath $OutputPvf -PathType Leaf)) { throw 'PVF API returned without creating the output file.' }
Write-Output "SAVED $OutputPvf"
