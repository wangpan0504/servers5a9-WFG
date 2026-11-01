param(
    [Parameter(Mandatory)][string]$PvfPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$MaterialSuccessPercent = '30,70,100',
    [string]$PvfLibPath = "$PSScriptRoot/PvfLib/bin/Debug/net10.0/PvfLib.dll",
    [string]$VerifyRawPath
)
$ErrorActionPreference = 'Stop'
$materialChances = @($MaterialSuccessPercent.Split(',') | ForEach-Object { [int]::Parse($_.Trim(), [Globalization.CultureInfo]::InvariantCulture) })
if ($materialChances.Count -ne 3 -or
    @($materialChances | Where-Object { $_ -lt 0 -or $_ -gt 100 }).Count -gt 0 -or
    $materialChances[0] -gt $materialChances[1] -or $materialChances[2] -ne 100) {
    throw 'Expected three increasing percentages with the third equal to 100.'
}
Add-Type -Path $PvfLibPath
$archive = [PvfLib.PvfArchive]::Open([IO.Path]::GetFullPath($PvfPath))
try {
    $resourcePath = 'etc/monstercardcontents.etc'
    $tokens = $archive.ReadTokens($resourcePath)
    $raw = $archive.ReadRaw($resourcePath)
    if ($raw.Length -ne $tokens.Length * 5) { throw 'Unsupported token/raw layout.' }
    $candidate = [byte[]]$raw.Clone()
    $inside = $false
    $opened = 0
    $closed = 0
    $rarities = [Collections.Generic.HashSet[int]]::new()
    $changes = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $tokens.Length; $index++) {
        if ($tokens[$index].Type -ne 3) { continue }
        $tag = $archive.ResolveString($tokens[$index].Value).ToLowerInvariant()
        if ($tag -eq '[upgrade infos]') { $inside = $true; $opened++; continue }
        if ($tag -eq '[/upgrade infos]') { $inside = $false; $closed++; continue }
        if (!$inside -or $tag -ne '[upgrade info]') { continue }
        if ($index + 7 -ge $tokens.Length) { throw 'Truncated upgrade row.' }
        for ($field = 1; $field -le 6; $field++) {
            if ($tokens[$index + $field].Type -ne 0) { throw 'Non-integer upgrade row.' }
        }
        if ($tokens[$index + 7].Type -ne 3 -or $tokens[$index + 1].Value -lt 0 -or
            !$rarities.Add($tokens[$index + 1].Value)) { throw 'Unsupported or duplicate rarity row.' }
        # 只改三列材料成功率，稀有度、后续保留列、字符串索引和其它奖池原样保留。
        for ($material = 0; $material -lt 3; $material++) {
            $tokenIndex = $index + 2 + $material
            $before = $tokens[$tokenIndex].Value
            $after = $materialChances[$material]
            [Array]::Copy([BitConverter]::GetBytes([int]$after), 0, $candidate, $tokenIndex * 5 + 1, 4)
            if ($before -ne $after) {
                $changes.Add([pscustomobject]@{ Rarity = $tokens[$index + 1].Value; Materials = $material + 1
                    Token = $tokenIndex; Before = $before; After = $after })
            }
        }
        if ($tokens[$index + 5].Value -ne 0 -or $tokens[$index + 6].Value -ne 0) {
            throw 'Unexpected fourth or fifth material chance; review the resource before patching.'
        }
    }
    if ($opened -ne 1 -or $closed -ne 1 -or $inside -or $rarities.Count -eq 0) { throw 'Missing or ambiguous upgrade table.' }
    if ($VerifyRawPath) {
        $actual = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($VerifyRawPath))
        if (![Linq.Enumerable]::SequenceEqual[byte]($candidate, $actual)) { throw 'Candidate differs from the exact permitted upgrade-table change.' }
        Write-Output "PASS exact raw candidate; rarities=$($rarities.Count); no PVF mutation"
        return
    }
    $outputPath = [IO.Path]::GetFullPath($OutputDirectory)
    [IO.Directory]::CreateDirectory($outputPath) | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $outputPath 'monstercardcontents.raw'), $candidate)
    [pscustomobject]@{
        Rule = 'monster-card-material-local-v1'
        SourcePvf = [IO.Path]::GetFullPath($PvfPath)
        ResourcePath = $resourcePath
        MaterialSuccessPercent = $materialChances
        Rarities = @($rarities | Sort-Object)
        TokenCount = $tokens.Length
        Changes = $changes.ToArray()
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'manifest.json') -Encoding utf8
    Write-Output "PASS generated raw candidate; rarities=$($rarities.Count); changed-values=$($changes.Count); no PVF mutation"
} finally {
    $archive.Dispose()
}
