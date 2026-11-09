[CmdletBinding()]
param([Parameter(Mandatory)][string]$PvfPath, [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$TemporaryDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'New-EkalRidingPotionDungeonDelta.ps1') -PvfPath $PvfPath -PvfLibPath $PvfLibPath
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
function Assert-True($Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $rejected = $true
    }
    Assert-True $rejected "Expected rejection: $Message"
}

$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
$proxy = [pscustomobject]@{ Archive = $archive; Overrides = @{}; Missing = ''; Index = $null }
$proxy | Add-Member ScriptMethod ReadText { param($Path)
    if ($null -ne $this.Index -and $Path -ceq 'stackable/stackable.lst') { return $this.Index }
    $this.Archive.ReadText($Path)
}
$proxy | Add-Member ScriptMethod FindFile { param($Path)
    if ($Path -ceq $this.Missing) { return $null }
    $this.Archive.FindFile($Path)
}
$proxy | Add-Member ScriptMethod ReadRaw { param($Path)
    if ($this.Overrides.ContainsKey($Path)) { return ,$this.Overrides[$Path] }
    return ,$this.Archive.ReadRaw($Path)
}
$proxy | Add-Member ScriptMethod ReadTokens { param($Path)
    if (-not $this.Overrides.ContainsKey($Path)) { return ,$this.Archive.ReadTokens($Path) }
    $raw = $this.Overrides[$Path]
    $tokens = [PvfLib.PvfToken[]]::new([int]($raw.Length / 5))
    for ($i = 0; $i -lt $tokens.Length; $i++) {
        $tokens[$i] = [PvfLib.PvfToken]::new($raw[$i * 5], [BitConverter]::ToInt32($raw, $i * 5 + 1))
    }
    return ,$tokens
}
$proxy | Add-Member ScriptMethod ResolveString { param($Value) $this.Archive.ResolveString($Value) }

try {
    $plan = @(New-EkalRidingPotionDungeonPlan $archive)
    Assert-True ($plan.Count -eq 101) 'Expected 101 general-dungeon action usable targets.'
    $patchedItems = @($plan | Where-Object State -eq 'patched')
    $appliedItems = @($plan | Where-Object State -eq 'already_applied')
    Assert-True ($patchedItems.Count -eq 84 -and $appliedItems.Count -eq 17) 'Expected 84 patched and 17 already_applied baselines.'

    $resolve = [Func[int, string]]{ param($Value) $archive.ResolveString($Value) }
    foreach ($item in $plan) {
        $beforeRaw = $archive.ReadRaw($item.Path)
        $prefixBytes = ($item.OpenIndex + 1) * 5
        $suffixOffsetBefore = $item.CloseIndex * 5
        $suffixLength = $beforeRaw.Length - $suffixOffsetBefore
        $suffixOffsetAfter = $item.Raw.Length - $suffixLength
        Assert-True ($item.Raw.Length -eq $prefixBytes + $item.TargetCount * 5 + $suffixLength) "Unexpected patched length for $($item.Path)."
        Assert-True ([Convert]::ToBase64String($beforeRaw, 0, $prefixBytes) -ceq [Convert]::ToBase64String($item.Raw, 0, $prefixBytes)) "Prefix bytes changed for $($item.Path)."
        Assert-True ([Convert]::ToBase64String($beforeRaw, $suffixOffsetBefore, $suffixLength) -ceq [Convert]::ToBase64String($item.Raw, $suffixOffsetAfter, $suffixLength)) "Suffix bytes changed for $($item.Path)."
        $proxy.Overrides[$item.Path] = $item.Raw
    }

    foreach ($sampleId in @(490004179, 490005561, 490701640, 490702204, 10158745, 490004471, 490010019, 490001835, 490010774, 490010408, 490010680)) {
        $sample = $plan | Where-Object Id -eq $sampleId
        $tokens = $proxy.ReadTokens($sample.Path)
        $stk = [PvfLib.StackableFile]::Parse($sample.Id, $sample.Path, $tokens, $resolve)
        $rec = [PvfLib.RecoveryConsumableFile]::Parse($sample.Id, $sample.Path, $tokens, $resolve)
        Assert-True ($stk.StructureDiagnostics.Length -eq 0 -and $rec.StructureDiagnostics.Length -eq 0 -and
            $rec.ActionUsableDungeons.Length -eq $sample.TargetCount -and
            ($rec.ActionUsableDungeons -contains 3) -and ($rec.ActionUsableDungeons -contains 7165) -and
            ($rec.ActionUsableDungeons -contains 291100268) -and ($rec.ActionUsableDungeons -contains 7300)) "Typed parser check failed for $sampleId."
    }

    $again = @(New-EkalRidingPotionDungeonPlan $proxy)
    Assert-True (@($again | Where-Object { $_.State -ne 'already_applied' -or $_.BeforeSha256 -ne $_.AfterSha256 }).Count -eq 0) 'Repeated application changed bytes.'

    $first = $plan[0]
    $proxy.Missing = $first.Path
    Assert-Rejected { New-EkalRidingPotionDungeonPlan $proxy } 'Missing token STK'
    $proxy.Missing = ''

    $proxy.Index = ''
    Assert-Rejected { New-EkalRidingPotionDungeonPlan $proxy } 'Missing or changed ekal riding potion mapping'
    $proxy.Index = $archive.ReadText('stackable/stackable.lst').Replace('490004001/chn_490004179.stk', '490004001/other.stk')
    Assert-Rejected { New-EkalRidingPotionDungeonPlan $proxy } 'Missing or changed ekal riding potion mapping'
    $proxy.Index = $null

    $badDungeons = [byte[]]$first.Raw.Clone()
    [BitConverter]::GetBytes([int]999999).CopyTo($badDungeons, ($first.OpenIndex + 1) * 5 + 1)
    $proxy.Overrides[$first.Path] = $badDungeons
    Assert-Rejected { New-EkalRidingPotionDungeonPlan $proxy } 'Unknown ekal riding potion dungeon list'

    $badType = [byte[]]$first.Raw.Clone()
    $badType[($first.OpenIndex + 1) * 5] = 6
    $proxy.Overrides[$first.Path] = $badType
    Assert-Rejected { New-EkalRidingPotionDungeonPlan $proxy } 'Unknown ekal riding potion dungeon token'

    $badBaseline = [byte[]]$first.Raw.Clone()
    $badBaseline[1] = $badBaseline[1] -bxor 1
    $proxy.Overrides[$first.Path] = $badBaseline
    Assert-Rejected { New-EkalRidingPotionDungeonPlan $proxy } 'Unknown ekal riding potion baseline'

    $dupTag = [byte[]]$first.Raw.Clone()
    [Array]::Copy($first.Raw, $first.OpenIndex * 5, $dupTag, 0, 5)
    $proxy.Overrides[$first.Path] = $dupTag
    Assert-Rejected { New-EkalRidingPotionDungeonPlan $proxy } 'Missing or duplicate action usable dungeon'

    $proxy.Overrides[$first.Path] = [byte[]]::new(0)
    Assert-Rejected { New-EkalRidingPotionDungeonPlan $proxy } 'Unknown ekal riding potion layout'
    $proxy.Overrides[$first.Path] = $first.Raw

    Write-EkalRidingPotionDungeonDelta $plan $TemporaryDirectory
    $manifest = Get-Content -LiteralPath (Join-Path $TemporaryDirectory 'manifest.json') -Raw | ConvertFrom-Json
    Assert-True ($manifest.replacements.Count -eq 84) 'Unexpected manifest replacement count.'
    foreach ($row in $manifest.replacements) {
        Assert-True ((Get-FileHash -LiteralPath $row.raw_file).Hash.ToLowerInvariant() -ceq $row.after_sha256) 'Manifest raw hash differs.'
    }
    Assert-Rejected { Write-EkalRidingPotionDungeonDelta $plan $TemporaryDirectory } 'Output already exists.'
    Write-Output 'PASS ekal riding potion delta: 101 targets, typed readback, Otherverse/Harlem coverage, prefix/suffix byte preservation, idempotence, mapping/layout/baseline guards and manifest output.'
} finally { $archive.Dispose() }
