[CmdletBinding()]
param([string]$PvfPath, [string]$PvfLibPath, [string]$OutputDirectory)

$ErrorActionPreference = 'Stop'

function New-HazySetStructurePlan($Archive) {
    $path = 'equipment/character/partset2/set_item_12594.equ'
    $entry = $Archive.FindFile($path)
    if ($null -eq $entry -or $entry.DataType -ne 1) { throw 'Missing token EQU: 12594' }
    [byte[]]$raw = $Archive.ReadRaw($path)
    $before = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw)).ToLowerInvariant()
    # 仅绑定这一份已核对的资源条目，不计算或依赖整份PVF哈希；不同字符串池/布局必须重新取证。
    $original = '4c55fba21dd266084eba788c32069c4782e4589c4debe8422726841a3aa2d91c'
    $corrected = '0fa7ec005958b4411d6d5e3318982c557f53468ed944b03a50466ee7b9824143'
    if ($raw.Length -eq 475 -and $before -ceq $original) {
        $tokens = $Archive.ReadTokens($path)
        foreach ($pair in @(@(21, '[if]'), @(22, '[/module]'), @(23, '[time]'), @(27, '[/if]'))) {
            if ($tokens[$pair[0]].Type -ne 3 -or $Archive.ResolveString($tokens[$pair[0]].Value) -cne $pair[1]) {
                throw 'Unknown orphan module placement.'
            }
        }
        # 只删token22（偏移110..114）的孤立闭合；其余470字节按原顺序逐字保留。
        $patched = [byte[]]::new(470)
        [Array]::Copy($raw, 0, $patched, 0, 110)
        [Array]::Copy($raw, 115, $patched, 110, 360)
        $state = 'patched'
    } elseif ($raw.Length -eq 470 -and $before -ceq $corrected) {
        $patched = [byte[]]$raw.Clone()
        $state = 'already_applied'
    } else { throw 'Unknown EQU 12594 layout or content; no delta generated.' }
    $after = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($patched)).ToLowerInvariant()
    if ($after -cne $corrected) { throw 'Corrected EQU identity mismatch.' }
    return [pscustomobject]@{
        Path = $path; RawFile = 'set_item_12594.equ.raw'; State = $state
        BeforeSha256 = $before; AfterSha256 = $after; Raw = $patched
    }
}

function Write-HazySetStructureDelta($Plan, [string]$Directory) {
    $target = [IO.Path]::GetFullPath($Directory)
    if (Test-Path -LiteralPath $target) { throw 'Output already exists.' }
    $parent = [IO.Path]::GetDirectoryName($target)
    [void][IO.Directory]::CreateDirectory($parent)
    $name = '.hazy-set-' + [Guid]::NewGuid().ToString('N')
    $temporary = [IO.Path]::GetFullPath((Join-Path $parent $name))
    try {
        [void][IO.Directory]::CreateDirectory($temporary)
        [IO.File]::WriteAllBytes((Join-Path $temporary $Plan.RawFile), $Plan.Raw)
        $replacements = @()
        if ($Plan.State -eq 'patched') {
            $replacements += [ordered]@{
                path = $Plan.Path; raw_file = Join-Path $target $Plan.RawFile
                expected_before_sha256 = $Plan.BeforeSha256; after_sha256 = $Plan.AfterSha256; state = $Plan.State
            }
        }
        [IO.File]::WriteAllText((Join-Path $temporary 'manifest.json'),
            (@{ replacements = $replacements } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        [IO.Directory]::Move($temporary, $target)
    } finally {
        if ([IO.Directory]::Exists($temporary)) {
            $cleanup = [IO.Path]::GetFullPath($temporary)
            if ([IO.Path]::GetDirectoryName($cleanup) -cne $parent -or [IO.Path]::GetFileName($cleanup) -cne $name) {
                throw 'Unsafe temporary cleanup path.'
            }
            [IO.Directory]::Delete($cleanup, $true)
        }
    }
}

# 点源只装载函数，供定向检查使用；不写包、不修改运行资源。
if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $PvfPath -or -not $PvfLibPath -or -not $OutputDirectory) { throw 'PvfPath, PvfLibPath and OutputDirectory are required.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output already exists.' }
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
try {
    $plan = New-HazySetStructurePlan $archive
    Write-HazySetStructureDelta $plan $OutputDirectory
    Write-Output "PASS entries=1 state=$($plan.State) after_sha256=$($plan.AfterSha256)"
} finally { $archive.Dispose() }
