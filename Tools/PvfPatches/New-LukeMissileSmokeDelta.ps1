[CmdletBinding()]
param(
    [string]$PvfPath,
    [string]$PvfLibPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

function New-LukeMissileSmokePlan($Archive) {
    $oldPath = '../../particle/BossMissileSmog.ptl'
    $newPath = '../particle/BossMissileSmog.ptl'
    $newValue = 0
    if (-not $Archive.TryFindStringValue($newPath, [ref]$newValue)) {
        throw 'Required replacement string is absent from the existing string pool.'
    }
    $plan = @()
    foreach ($name in @('gisellemissile1', 'gisellemissile1_0')) {
        $root = "passiveobject/actionobject/monster/luke_raid/luke/$name"
        $path = "$root/action/gisellemissile1.act"
        $entry = $Archive.FindFile($path)
        if ($null -eq $entry -or $entry.DataType -ne 1) { throw "Missing token ACT: $path" }
        $raw = $Archive.ReadRaw($path)
        $tokens = $Archive.ReadTokens($path)
        if ($raw.Length -ne 625 -or $tokens.Count -ne 125) { throw "Unknown ACT layout: $path" }
        $smokeIndices = @()
        for ($i = 0; $i -lt $tokens.Count; $i++) {
            if ($tokens[$i].Type -eq 6) {
                $value = $Archive.ResolveString($tokens[$i].Value)
                if ($value -cmatch '(?i)BossMissileSmog\.ptl$') { $smokeIndices += $i }
            }
        }
        if ($smokeIndices.Count -ne 1 -or $smokeIndices[0] -ne 120) { throw "Unknown smoke placement: $path" }
        foreach ($pair in @(@(118, '[BEHAVIOR]'), @(119, '[PARTICLE]'), @(124, '[/BEHAVIOR]'))) {
            $token = $tokens[$pair[0]]
            if ($token.Type -ne 3 -or $Archive.ResolveString($token.Value) -cne $pair[1]) {
                throw "Unknown particle block: $path"
            }
        }
        foreach ($pair in @(@(121, 0), @(122, 0), @(123, -40))) {
            $token = $tokens[$pair[0]]
            if ($token.Type -ne 0 -or $token.Value -ne $pair[1]) { throw "Unknown particle coordinates: $path" }
        }
        $currentPath = $Archive.ResolveString($tokens[120].Value)
        if ($currentPath -cne $oldPath -and $currentPath -cne $newPath) { throw "Unknown smoke path: $path" }

        # 必须使用导弹自身的烟雾资源，不能替换为参数不同的通用同名资源。
        $particlePath = "$root/particle/bossmissilesmog.ptl"
        $particle = $Archive.FindFile($particlePath)
        if ($null -eq $particle -or $particle.DataType -ne 1) { throw "Missing token PTL: $particlePath" }
        $particleTokens = $Archive.ReadTokens($particlePath)
        foreach ($pair in @(@('[life time]', 4500), @('[creation frequency]', 20))) {
            $indices = @()
            for ($i = 0; $i -lt $particleTokens.Count; $i++) {
                if ($particleTokens[$i].Type -eq 3 -and $Archive.ResolveString($particleTokens[$i].Value) -ceq $pair[0]) { $indices += $i }
            }
            if ($indices.Count -ne 1 -or $indices[0] + 1 -ge $particleTokens.Count) { throw "Unknown PTL field: $particlePath" }
            $value = $particleTokens[$indices[0] + 1]
            if ($value.Type -ne 0 -or $value.Value -ne $pair[1]) { throw "Unexpected PTL parameter: $particlePath" }
        }
        $objectIndices = @()
        for ($i = 0; $i -lt $particleTokens.Count; $i++) {
            if ($particleTokens[$i].Type -eq 3 -and $Archive.ResolveString($particleTokens[$i].Value) -ceq '[object]') { $objectIndices += $i }
        }
        if ($objectIndices.Count -ne 1 -or $objectIndices[0] + 3 -ge $particleTokens.Count) { throw "Unknown PTL object: $particlePath" }
        $objectIndex = $objectIndices[0]
        if ($particleTokens[$objectIndex + 1].Type -ne 6 -or
            $Archive.ResolveString($particleTokens[$objectIndex + 1].Value) -cne '../Animation/MissileSmoke1.ani' -or
            $particleTokens[$objectIndex + 2].Type -ne 0 -or $particleTokens[$objectIndex + 2].Value -ne -1 -or
            $particleTokens[$objectIndex + 3].Type -ne 3 -or
            $Archive.ResolveString($particleTokens[$objectIndex + 3].Value) -cne '[/object]') { throw "Unknown PTL animation reference: $particlePath" }
        $animation = $Archive.FindFile("$root/animation/missilesmoke1.ani")
        if ($null -eq $animation -or $animation.DataType -ne 1) { throw "Missing smoke ANI: $root" }

        # 每条 token 为 5 字节，只改字符串值的 4 字节，保留其它所有原始数据。
        $patched = [byte[]]$raw.Clone()
        if ($currentPath -ceq $oldPath) { [BitConverter]::GetBytes([int]$newValue).CopyTo($patched, 601) }
        $plan += [pscustomobject]@{
            Path = $path
            RawFile = "$name.act.raw"
            BeforeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$raw)).ToLowerInvariant()
            AfterSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($patched)).ToLowerInvariant()
            State = $(if ($currentPath -ceq $newPath) { 'already_applied' } else { 'patched' })
            Raw = $patched
        }
    }
    return $plan
}

function Write-LukeMissileSmokeDelta($Plan, [string]$Directory) {
    $target = [IO.Path]::GetFullPath($Directory)
    if (Test-Path -LiteralPath $target) { throw 'Output already exists.' }
    $parent = [IO.Path]::GetDirectoryName($target)
    [void][IO.Directory]::CreateDirectory($parent)
    $temporaryName = '.luke-smoke-' + [Guid]::NewGuid().ToString('N')
    $temporary = [IO.Path]::GetFullPath((Join-Path $parent $temporaryName))
    try {
        [void][IO.Directory]::CreateDirectory($temporary)
        $replacements = @()
        foreach ($item in $Plan) {
            [IO.File]::WriteAllBytes((Join-Path $temporary $item.RawFile), $item.Raw)
            if ($item.State -ne 'patched') { continue }
            $replacements += [ordered]@{
                path = $item.Path
                raw_file = Join-Path $target $item.RawFile
                expected_before_sha256 = $item.BeforeSha256
                after_sha256 = $item.AfterSha256
                state = $item.State
            }
        }
        $manifest = [ordered]@{ replacements = $replacements }
        [IO.File]::WriteAllText((Join-Path $temporary 'manifest.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        [IO.Directory]::Move($temporary, $target)
    } finally {
        if ([IO.Directory]::Exists($temporary)) {
            $cleanup = [IO.Path]::GetFullPath($temporary)
            if (-not [string]::Equals([IO.Path]::GetDirectoryName($cleanup), $parent, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($cleanup) -cne $temporaryName) { throw 'Unsafe temporary cleanup path.' }
            [IO.Directory]::Delete($cleanup, $true)
        }
    }
}

# 点源只加载定向测试所需函数，不读取或写入资源。
if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $PvfPath -or -not $PvfLibPath -or -not $OutputDirectory) { throw 'PvfPath, PvfLibPath and OutputDirectory are required.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output already exists.' }
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
try {
    $plan = @(New-LukeMissileSmokePlan $archive)
    Write-LukeMissileSmokeDelta $plan $OutputDirectory
    Write-Output ("PASS entries={0} patched={1} already_applied={2}" -f $plan.Count, @($plan | Where-Object State -eq 'patched').Count, @($plan | Where-Object State -eq 'already_applied').Count)
} finally { $archive.Dispose() }
