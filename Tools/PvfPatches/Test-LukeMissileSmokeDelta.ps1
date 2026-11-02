[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PvfPath,
    [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$TemporaryDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'New-LukeMissileSmokeDelta.ps1') -PvfPath $PvfPath -PvfLibPath $PvfLibPath
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Rejected([scriptblock]$Action, [string]$ExpectedMessage) {
    try { & $Action } catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw }
        return
    }
    throw "Expected rejection: $ExpectedMessage"
}

# 只读真实归档；仅在内存代理上覆盖 token 或模拟缺失，不改写 PVF。
$proxy = [pscustomobject]@{ Archive = $archive; Overrides = @{}; MissingPath = ''; AllowString = $true; AliasValue = $null }
$proxy | Add-Member ScriptMethod FindFile {
    param($Path)
    if ($Path -eq $this.MissingPath) { return $null }
    return $this.Archive.FindFile($Path)
}
$proxy | Add-Member ScriptMethod ReadRaw {
    param($Path)
    if ($this.Overrides.ContainsKey($Path)) { return ,$this.Overrides[$Path] }
    return ,$this.Archive.ReadRaw($Path)
}
$proxy | Add-Member ScriptMethod ReadTokens {
    param($Path)
    if (-not $this.Overrides.ContainsKey($Path)) { return ,$this.Archive.ReadTokens($Path) }
    $raw = $this.Overrides[$Path]
    $tokens = for ($offset = 0; $offset -lt $raw.Length; $offset += 5) {
        [pscustomobject]@{ Type = $raw[$offset]; Value = [BitConverter]::ToInt32($raw, $offset + 1) }
    }
    return ,$tokens
}
$proxy | Add-Member ScriptMethod ResolveString {
    param($Value)
    if ($null -ne $this.AliasValue -and $Value -eq $this.AliasValue) { return '../particle/BossMissileSmog.ptl' }
    return $this.Archive.ResolveString($Value)
}
$proxy | Add-Member ScriptMethod TryFindStringValue {
    param($Text, $Value)
    if (-not $this.AllowString) { return $false }
    return $this.Archive.TryFindStringValue($Text, $Value)
}

try {
    $target = [IO.Path]::GetFullPath($TemporaryDirectory)
    if (Test-Path -LiteralPath $target) { throw 'Test directory already exists.' }
    $plan = @(New-LukeMissileSmokePlan $archive)
    Assert-True ($plan.Count -eq 2) 'Expected two replacements.'
    foreach ($item in $plan) {
        $before = $archive.ReadRaw($item.Path)
        $changes = @(for ($i = 0; $i -lt $before.Length; $i++) { if ($before[$i] -ne $item.Raw[$i]) { $i } })
        Assert-True ($item.State -eq 'patched' -and $changes.Count -gt 0 -and $changes.Count -le 4) 'Expected an unapplied smoke path.'
        Assert-True (@($changes | Where-Object { $_ -lt 601 -or $_ -gt 604 }).Count -eq 0) 'Unrelated ACT bytes changed.'
    }
    Assert-True ($plan[0].AfterSha256 -ne $plan[1].AfterSha256) 'Distinct ACT resources were conflated.'
    Write-LukeMissileSmokeDelta $plan $target
    $manifest = Get-Content -LiteralPath (Join-Path $target 'manifest.json') -Raw | ConvertFrom-Json
    Assert-True ($manifest.replacements.Count -eq 2) 'Manifest replacement count differs.'
    foreach ($item in $manifest.replacements) {
        Assert-True ([IO.Path]::IsPathFullyQualified($item.raw_file)) 'Manifest raw path must be absolute.'
        $hash = (Get-FileHash -LiteralPath $item.raw_file -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-True ($hash -eq $item.after_sha256) 'Output hash differs.'
    }
    Assert-Rejected { Write-LukeMissileSmokeDelta $plan $target } 'Output already exists.'
    Assert-True (@(Get-ChildItem -LiteralPath $target -File).Count -eq 3) 'Output conflict altered published files.'
    $proxy.Overrides[$plan[0].Path] = $plan[0].Raw
    $mixed = @(New-LukeMissileSmokePlan $proxy)
    Assert-True ($mixed[0].State -eq 'already_applied' -and $mixed[1].State -eq 'patched') 'Mixed application state failed.'
    Write-LukeMissileSmokeDelta $mixed ($target + '-mixed')
    $mixedManifest = Get-Content -LiteralPath (Join-Path ($target + '-mixed') 'manifest.json') -Raw | ConvertFrom-Json
    Assert-True ($mixedManifest.replacements.Count -eq 1 -and $mixedManifest.replacements[0].path -eq $plan[1].Path) 'Mixed manifest must contain only the changed ACT.'
    Assert-True (@(Get-ChildItem -LiteralPath ($target + '-mixed') -File -Filter '*.raw').Count -eq 2) 'Mixed output must retain both raw files.'
    $proxy.Overrides[$plan[1].Path] = $plan[1].Raw
    $applied = @(New-LukeMissileSmokePlan $proxy)
    Assert-True (@($applied | Where-Object State -eq 'already_applied').Count -eq 2) 'Already-applied state failed.'
    foreach ($item in $applied) { Assert-True ($item.BeforeSha256 -eq $item.AfterSha256) 'Already-applied ACT changed.' }
    Write-LukeMissileSmokeDelta $applied ($target + '-applied')
    $appliedManifest = Get-Content -LiteralPath (Join-Path ($target + '-applied') 'manifest.json') -Raw | ConvertFrom-Json
    Assert-True ($appliedManifest.replacements.Count -eq 0) 'Already-applied manifest must be empty.'
    Assert-True (@(Get-ChildItem -LiteralPath ($target + '-applied') -File -Filter '*.raw').Count -eq 2) 'Already-applied output must retain both raw files.'
    $proxy.AliasValue = -2147483000
    $duplicate = [byte[]]$plan[0].Raw.Clone()
    [BitConverter]::GetBytes([int]$proxy.AliasValue).CopyTo($duplicate, 601)
    $proxy.Overrides[$plan[0].Path] = $duplicate
    $duplicatePlan = @(New-LukeMissileSmokePlan $proxy)
    Assert-True ($duplicatePlan[0].State -eq 'already_applied' -and $duplicatePlan[0].BeforeSha256 -eq $duplicatePlan[0].AfterSha256) 'Already-applied duplicate string offset changed.'
    $proxy.AliasValue = $null
    $proxy.Overrides[$plan[0].Path] = $plan[0].Raw
    $corrupt = [byte[]]$plan[1].Raw.Clone()
    $corrupt[606] = 1
    $proxy.Overrides[$plan[1].Path] = $corrupt
    Assert-Rejected { New-LukeMissileSmokePlan $proxy } 'Unknown particle coordinates'
    $proxy.Overrides.Clear()
    $proxy.MissingPath = 'passiveobject/actionobject/monster/luke_raid/luke/gisellemissile1_0/particle/bossmissilesmog.ptl'
    Assert-Rejected { New-LukeMissileSmokePlan $proxy } 'Missing token PTL'
    $proxy.MissingPath = ''
    $proxy.AllowString = $false
    Assert-Rejected { New-LukeMissileSmokePlan $proxy } 'Required replacement string is absent'
    Assert-True (@(Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($target)) -Directory -Filter '.luke-smoke-*').Count -eq 0) 'Temporary publication directory leaked.'
    Write-Output 'PASS real_input byte_scope manifest output_conflict mixed_state already_applied duplicate_string_offset corrupt_layout missing_dependency missing_string'
} finally { $archive.Dispose() }
