[CmdletBinding()]
param([string]$PvfPath, [string]$PvfLibPath, [string]$OutputDirectory)

$ErrorActionPreference = 'Stop'

function New-BrilliantEmblemBoxStackPlan($Archive) {
    $path = 'stackable/cash/chn_490704572.stk'
    $index = [PvfLib.PvfScript]::List($Archive.ReadText('stackable/stackable.lst'))
    if (-not $index.ContainsKey(490704572) -or $index[490704572] -cne 'cash/chn_490704572.stk') {
        throw 'Missing or changed emblem box mapping.'
    }
    $entry = $Archive.FindFile($path)
    if ($null -eq $entry -or $entry.DataType -ne 1) { throw 'Missing token STK.' }
    $raw = $Archive.ReadRaw($path)
    $tokens = $Archive.ReadTokens($path)
    if ($raw.Length -ne 570 -or $tokens.Count * 5 -ne $raw.Length) { throw 'Unknown emblem box layout.' }
    $indices = @(for ($i = 0; $i -lt $tokens.Count; $i++) {
        if ($tokens[$i].Type -eq 3 -and $Archive.ResolveString($tokens[$i].Value) -ceq '[stack limit]') { $i }
    })
    if ($indices.Count -ne 1 -or $indices[0] + 1 -ge $tokens.Count) { throw 'Missing or duplicate stack limit.' }
    $valueIndex = $indices[0] + 1
    $value = $tokens[$valueIndex]
    if ($value.Type -ne 0 -or $value.Value -notin @(1, 1000)) { throw 'Unknown emblem box stack limit.' }

    # 只校验这一份STK，拒绝同ID复用或累计资源中的额外改动；不计算整份PVF哈希。
    $original = [byte[]]$raw.Clone()
    [BitConverter]::GetBytes([int]1).CopyTo($original, $valueIndex * 5 + 1)
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($original)) -cne
        '82C0079C4E47430DEDD1569951BFC1BF74C53530533A80C3DF7ED6303AB2A046') {
        throw 'Unknown emblem box baseline.'
    }
    # 1000是本服堆叠兼容政策；保留30天期限、账号绑定及全部37种自选奖励。
    $patched = [byte[]]$raw.Clone()
    [BitConverter]::GetBytes([int]1000).CopyTo($patched, $valueIndex * 5 + 1)
    [pscustomobject]@{
        Path = $path; RawFile = '490704572.stk.raw'; Raw = $patched
        State = $(if ($value.Value -eq 1000) { 'already_applied' } else { 'patched' })
        BeforeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$raw)).ToLowerInvariant()
        AfterSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($patched)).ToLowerInvariant()
    }
}

function Write-BrilliantEmblemBoxStackDelta($Plan, [string]$Directory) {
    $target = [IO.Path]::GetFullPath($Directory)
    if (Test-Path -LiteralPath $target) { throw 'Output already exists.' }
    $parent = [IO.Path]::GetDirectoryName($target)
    [void][IO.Directory]::CreateDirectory($parent)
    $temporaryName = '.emblem-stack-' + [Guid]::NewGuid().ToString('N')
    $temporary = [IO.Path]::GetFullPath((Join-Path $parent $temporaryName))
    try {
        [void][IO.Directory]::CreateDirectory($temporary)
        [IO.File]::WriteAllBytes((Join-Path $temporary $Plan.RawFile), $Plan.Raw)
        $replacements = @()
        if ($Plan.State -eq 'patched') {
            $replacements += [ordered]@{
                path = $Plan.Path; raw_file = Join-Path $target $Plan.RawFile
                expected_before_sha256 = $Plan.BeforeSha256; after_sha256 = $Plan.AfterSha256
            }
        }
        [IO.File]::WriteAllText((Join-Path $temporary 'manifest.json'),
            (@{ replacements = $replacements } | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
        [IO.Directory]::Move($temporary, $target)
    } finally {
        if ([IO.Directory]::Exists($temporary)) {
            $cleanup = [IO.Path]::GetFullPath($temporary)
            if ([IO.Path]::GetDirectoryName($cleanup) -cne $parent -or
                [IO.Path]::GetFileName($cleanup) -cne $temporaryName) { throw 'Unsafe temporary cleanup path.' }
            [IO.Directory]::Delete($cleanup, $true)
        }
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $PvfPath -or -not $PvfLibPath -or -not $OutputDirectory) { throw 'PvfPath, PvfLibPath and OutputDirectory are required.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output already exists.' }
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
try {
    $plan = New-BrilliantEmblemBoxStackPlan $archive
    Write-BrilliantEmblemBoxStackDelta $plan $OutputDirectory
    Write-Output "PASS emblem box 490704572 stack=1000 state=$($plan.State)"
} finally { $archive.Dispose() }
