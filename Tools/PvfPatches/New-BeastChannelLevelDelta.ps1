[CmdletBinding()]
param([string]$PvfPath, [string]$PvfLibPath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'

function New-BeastChannelLevelPlan($Archive) {
    $path = 'etc/channelrecommendlevel.etc'
    $text = $Archive.ReadText($path)
    $rows = [regex]::Matches($text, '\[channel\]\s*`\[beast\]`\s*(-?\d+)\s+(-?\d+)\s*\[/channel\]')
    if ($rows.Count -gt 1 -or ($text.Contains('`[beast]`') -and $rows.Count -ne 1)) { throw 'Unknown beast recommendation declaration.' }
    $before = $Archive.ReadRaw($path)
    if ($rows.Count -eq 1) {
        if ($rows[0].Groups[1].Value -ne '90' -or $rows[0].Groups[2].Value -ne '95') { throw 'Unknown beast recommendation range.' }
        return [pscustomobject]@{ Path = $path; State = 'already_applied'; Before = $before; Raw = $before }
    }
    # 只补频道推荐等级，不改角色、地区及地下城准入；原表与字符串池保持原样。
    $raw = [Collections.Generic.List[byte]]::new()
    $raw.AddRange([byte[]]$before)
    foreach ($entry in @(@(3, '[channel]'), @(6, '[beast]'), @(0, 90), @(0, 95), @(3, '[/channel]'))) {
        $value = 0
        if ($entry[0] -eq 0) { $value = $entry[1] }
        elseif (-not $Archive.TryFindStringValue($entry[1], [ref]$value)) { throw "Required token is absent: $($entry[1])" }
        $raw.Add([byte]$entry[0]); $raw.AddRange([BitConverter]::GetBytes([int]$value))
    }
    [pscustomobject]@{ Path = $path; State = 'patched'; Before = $before; Raw = $raw.ToArray() }
}
if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $PvfPath -or -not $PvfLibPath -or -not $OutputDirectory) { throw 'PvfPath, PvfLibPath and OutputDirectory are required.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output already exists.' }
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
try {
    $plan = New-BeastChannelLevelPlan $archive
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory))
    $rawPath = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'channelrecommendlevel.etc.raw'
    [IO.File]::WriteAllBytes($rawPath, $plan.Raw)
    $replacements = @()
    if ($plan.State -eq 'patched') {
        $replacements += [ordered]@{ path = $plan.Path; state = $plan.State; raw_file = $rawPath
            expected_before_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$plan.Before))
            after_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$plan.Raw)) }
    }
    $manifest = [ordered]@{ replacements = $replacements }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'manifest.json'), ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    Write-Output ($manifest | ConvertTo-Json -Depth 4 -Compress)
} finally { $archive.Dispose() }
