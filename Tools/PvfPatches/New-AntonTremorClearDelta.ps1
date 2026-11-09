[CmdletBinding()]
param([string]$PvfPath, [string]$PvfLibPath, [string]$OutputDirectory)

$ErrorActionPreference = 'Stop'

function New-AntonTremorClearPlan($Archive, [int]$Dungeon) {
    if ($Dungeon -notin @(212, 214)) { throw 'Unsupported dungeon.' }
    # 团本DGN由现有白名单装配，不要求已缺项的普通dungeon.lst登记。
    $path = 'dungeon/anton/phase1/' + $(if ($Dungeon -eq 212) { 'strongleg_high.dgn' } else { 'strongleg_high2.dgn' })
    $text = $Archive.ReadText($path)
    $condition = '[clear condition] [destroy object] 15467 1 [/clear condition]'
    $existing = [regex]::Matches($text, '(?s)\[clear condition\].*?\[/clear condition\]')
    if ($existing.Count -gt 1 -or $existing.Count -eq 1 -and
        ($existing[0].Value -replace '\s+', '') -cne ($condition -replace '\s+', '')) {
        throw 'Unknown clear condition.'
    }
    $first = if ($Dungeon -eq 212) { 32220 } else { 32240 }
    $mapPath = 'map/' + $(if ($Dungeon -eq 212) { '212_strongleg_high' } else { '214_strongleg_high2' }) + '/(2,0)' + ($first + 2) + '_boss.map'
    $map = $Archive.ReadText($mapPath)
    if ($map -notmatch '(?s)\[passive object\].*?\b15467\s+630\s+303\s+0\b' -or
        $map -notmatch '\b69308\s+1\s+0\s+646\s+943\s+0\s+1\s+1' -or
        $text -notmatch '\[boss map\]\s+1\s+0') { throw 'Unknown tremor terminal.' }
    $before = $Archive.ReadRaw($path)
    $raw = [Collections.Generic.List[byte]]::new()
    $raw.AddRange([byte[]]$before)
    $state = 'already_applied'
    if ($existing.Count -eq 0) {
        $state = 'patched'
        # 只追加原生迷宫终末条件，不改地图、炮击动作、怪物或其它累计资源。
        foreach ($word in @('[clear condition]', '[destroy object]', 15467, 1, '[/clear condition]')) {
            $value = 0
            $type = 0
            if ($word -is [string]) {
                $type = 3
                if (-not $Archive.TryFindStringValue($word, [ref]$value)) { throw "Missing token: $word" }
            } else { $value = [int]$word }
            $raw.Add([byte]$type)
            $raw.AddRange([BitConverter]::GetBytes($value))
        }
    }
    [pscustomobject]@{ Path = $path; State = $state; Before = $before; Raw = $raw.ToArray() }
}

if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $PvfPath -or -not $PvfLibPath -or -not $OutputDirectory) { throw 'PvfPath, PvfLibPath and OutputDirectory are required.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output already exists.' }
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
try {
    $plans = @(212, 214 | ForEach-Object { New-AntonTremorClearPlan $archive $_ })
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory))
    $replacements = @()
    foreach ($plan in $plans) {
        if ($plan.State -ne 'patched') { continue }
        $rawPath = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([IO.Path]::GetFileName($plan.Path) + '.raw')
        [IO.File]::WriteAllBytes($rawPath, $plan.Raw)
        $replacements += [ordered]@{ path = $plan.Path; raw_file = $rawPath
            expected_before_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$plan.Before)) }
    }
    $manifest = [ordered]@{ replacements = $replacements }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'manifest.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Output ($manifest | ConvertTo-Json -Depth 5)
} finally { $archive.Dispose() }
