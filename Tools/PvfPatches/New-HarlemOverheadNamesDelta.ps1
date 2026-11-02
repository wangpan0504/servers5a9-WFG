[CmdletBinding()]
param([string]$PvfPath, [string]$PvfLibPath, [string]$OutputDirectory)

$ErrorActionPreference = 'Stop'

function New-HarlemOverheadNamesPlan($Archive) {
    $flag = 0
    if (-not $Archive.TryFindStringValue('[enable states]', [ref]$flag)) {
        throw 'Native name-display marker is missing from the string pool.'
    }
    $ids = @(107010514,107010515,107010516,107010517,107010518,107010519,107000273,107000274,107000275)
    $index = $Archive.ReadTokens('monster/monster.lst')
    $plan = @()
    foreach ($id in $ids) {
        $paths = @(for ($i=0; $i+1 -lt $index.Count; $i+=2) {
            if ($index[$i].Type -eq 0 -and $index[$i].Value -eq $id -and $index[$i+1].Type -eq 6) {
                'monster/' + $Archive.ResolveString($index[$i+1].Value)
            }
        })
        if ($paths.Count -ne 1) { throw "Missing or ambiguous monster mapping: $id" }
        $path = $paths[0]
        $entry = $Archive.FindFile($path)
        if ($null -eq $entry -or $entry.DataType -ne 1) { throw "Missing token MOB: $path" }
        $tokens = $Archive.ReadTokens($path)
        $raw = $Archive.ReadRaw($path)
        if ($raw.Length -ne 5*$tokens.Count) { throw "Unknown token layout: $path" }
        $names = @()
        $markers = @()
        for ($i=0; $i -lt $tokens.Count; $i++) {
            if ($tokens[$i].Type -ne 3) { continue }
            $tag = $Archive.ResolveString($tokens[$i].Value)
            if ($tag -ceq '[name]') { $names += $i }
            if ($tag -ceq '[enable states]') { $markers += $i }
        }
        if ($names.Count -ne 1 -or $names[0]+1 -ge $tokens.Count -or
            $tokens[$names[0]+1].Type -notin @(6,7,8) -or $markers.Count -gt 1) {
            throw "Unknown monster name or duplicate display marker: $path"
        }
        # 原生加载器03E3ABD2只置模板+3A40，不读取参数；原名称和全部既有token原样保留。
        $patched = $raw
        if ($markers.Count -eq 0) {
            $patched = [byte[]]::new($raw.Length+5)
            $raw.CopyTo($patched,0)
            $patched[$raw.Length] = 3
            [BitConverter]::GetBytes([int]$flag).CopyTo($patched,$raw.Length+1)
        }
        $plan += [pscustomobject]@{
            Id=$id; Path=$path; RawFile="$id.mob.raw"; Raw=$patched
            State=$(if ($markers.Count) {'already_applied'} else {'patched'})
            BeforeSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$raw)).ToLowerInvariant()
            AfterSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$patched)).ToLowerInvariant()
        }
    }
    return $plan
}

function Write-HarlemOverheadNamesDelta($Plan, [string]$Directory) {
    $target=[IO.Path]::GetFullPath($Directory)
    if (Test-Path -LiteralPath $target) { throw 'Output already exists.' }
    [void][IO.Directory]::CreateDirectory($target)
    $rows=@()
    foreach ($item in $Plan) {
        $rawPath=Join-Path $target $item.RawFile
        [IO.File]::WriteAllBytes($rawPath,$item.Raw)
        if ($item.State -eq 'patched') {
            $rows += [ordered]@{path=$item.Path;raw_file=$rawPath;expected_before_sha256=$item.BeforeSha256;after_sha256=$item.AfterSha256}
        }
    }
    [IO.File]::WriteAllText((Join-Path $target 'manifest.json'),
        (@{replacements=@($rows)} | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
}

if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $PvfPath -or -not $PvfLibPath -or -not $OutputDirectory) { throw 'PvfPath, PvfLibPath and OutputDirectory are required.' }
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive=[PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
try {
    $plan=@(New-HarlemOverheadNamesPlan $archive)
    Write-HarlemOverheadNamesDelta $plan $OutputDirectory
    Write-Output ("PASS entries={0} patched={1} already_applied={2}" -f $plan.Count,
        @($plan|Where-Object State -eq 'patched').Count,@($plan|Where-Object State -eq 'already_applied').Count)
} finally { $archive.Dispose() }
