[CmdletBinding()]
param([string]$PvfPath, [string]$PvfLibPath, [string]$OutputDirectory)

$ErrorActionPreference = 'Stop'

function New-AntonFleetEnergyPlan($Archive) {
    $path = 'monster/newmonsters/anton/phase1/fleet_fire/basic_2/action/last.act'
    $original = @'
[MOTION]
[BASE ANI]
`../Animation/Last_0.ani`
[SUB ANI]
`../Animation/Last_1.ani` 0 -1
[/SUB ANI]
[/MOTION]
'@
    # 同资源的NPC台词明确由守护兵死亡产球；仅修216，不改变共用怪物的其它副本。
    # 沿18737掉落回地面的既有CREATE声明生成18736，拾取、跟随和三次充能仍由原脚本执行。
    $tail = @'
[TRIGGER]
[LIMIT]
1
[DUNGEON INDEX]
216
[/DUNGEON INDEX]
[FRAME]
0 0
[DO BEHAVIOR]
[ME]
0
[/TRIGGER]
[BEHAVIOR]
[CREATE PASSIVEOBJECT]
[INDEX]
18736
[PARTICLE FILENAME]
``
[LEVEL]
0
[POS]
0 0 50
[/CREATE PASSIVEOBJECT]
[/BEHAVIOR]
'@
    $current = $Archive.ReadText($path) -replace '\s+', ''
    $before = $Archive.ReadRaw($path)
    $state = if ($current -ceq (($original + $tail) -replace '\s+', '')) { 'already_applied' }
        elseif ($current -ceq ($original -replace '\s+', '')) { 'patched' }
        else { throw "Unknown guardian Last action: $path" }
    $raw = [Collections.Generic.List[byte]]::new()
    $raw.AddRange([byte[]]$before)
    if ($state -eq 'patched') {
        foreach ($match in [regex]::Matches($tail, '`[^`]*`|\[[^\]]+\]|-?\d+')) {
            $value = 0
            $type = 0
            if ($match.Value.StartsWith('[') -or $match.Value.StartsWith('`')) {
                $type = if ($match.Value.StartsWith('[')) { 3 } else { 6 }
                $text = if ($type -eq 3) { $match.Value } else { $match.Value.Trim('`') }
                if (-not $Archive.TryFindStringValue($text, [ref]$value)) { throw "Required token is absent: $text" }
            } else { $value = [int]$match.Value }
            $raw.Add([byte]$type)
            $raw.AddRange([BitConverter]::GetBytes([int]$value))
        }
    }
    [pscustomobject]@{ Path = $path; State = $state; Raw = $raw.ToArray(); Before = $before }
}

if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $PvfPath -or -not $PvfLibPath -or -not $OutputDirectory) { throw 'PvfPath, PvfLibPath and OutputDirectory are required.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output already exists.' }
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
try {
    $plan = New-AntonFleetEnergyPlan $archive
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory))
    $rawPath = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'guardian-last.act.raw'
    [IO.File]::WriteAllBytes($rawPath, $plan.Raw)
    $replacement = [ordered]@{ path = $plan.Path; state = $plan.State; raw_file = $rawPath
        expected_before_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$plan.Before))
        after_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$plan.Raw)) }
    $replacements = @()
    if ($plan.State -eq 'patched') { $replacements += $replacement }
    $manifest = [ordered]@{ replacements = $replacements }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'manifest.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Output ($manifest | ConvertTo-Json -Compress)
} finally { $archive.Dispose() }
