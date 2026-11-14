[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PvfPath,
    [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$TemporaryDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'New-HazySetStructureDelta.ps1') -PvfPath $PvfPath -PvfLibPath $PvfLibPath
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Rejected([scriptblock]$Action, [string]$Expected) {
    try { & $Action } catch { if ($_.Exception.Message -notlike "*$Expected*") { throw }; return }
    throw "Expected rejection: $Expected"
}

# 覆盖只存在于内存；生产输入始终以只读归档打开。
$proxy = [pscustomobject]@{ Archive = $archive; Raw = $null; Missing = $false }
$proxy | Add-Member ScriptMethod FindFile { param($Path); if ($this.Missing) { return $null }; return $this.Archive.FindFile($Path) }
$proxy | Add-Member ScriptMethod ReadRaw { param($Path); if ($null -ne $this.Raw) { return ,$this.Raw }; return ,$this.Archive.ReadRaw($Path) }
$proxy | Add-Member ScriptMethod ReadTokens { param($Path); return ,$this.Archive.ReadTokens($Path) }
$proxy | Add-Member ScriptMethod ResolveString { param($Value); return $this.Archive.ResolveString($Value) }

try {
    $target = [IO.Path]::GetFullPath($TemporaryDirectory)
    if (Test-Path -LiteralPath $target) { throw 'Test directory already exists.' }
    $plan = New-HazySetStructurePlan $archive
    [byte[]]$before = $archive.ReadRaw($plan.Path)
    Assert-True ($plan.State -eq 'patched' -and $before.Length -eq 475 -and $plan.Raw.Length -eq 470) 'Expected original 12594 resource.'
    for ($i = 0; $i -lt $plan.Raw.Length; $i++) {
        $source = if ($i -lt 110) { $i } else { $i + 5 }
        Assert-True ($plan.Raw[$i] -eq $before[$source]) 'Unrelated raw byte changed.'
    }
    Assert-True ([Convert]::ToHexString($before[110..114]) -ceq '031A6C0100') 'Removed token differs.'
    [Func[int,string]]$resolve = { param($Value); $archive.ResolveString($Value) }
    Assert-Rejected { [void][PvfLib.PvfTokenDocument]::new($archive.ReadTokens($plan.Path), $resolve, $null, $false) } '闭合标签不匹配'
    [PvfLib.PvfToken[]]$tokens = for ($i = 0; $i -lt $plan.Raw.Length; $i += 5) {
        [PvfLib.PvfToken]::new($plan.Raw[$i], [BitConverter]::ToInt32($plan.Raw, $i + 1))
    }
    $document = [PvfLib.PvfTokenDocument]::new($tokens, $resolve, $null, $false)
    Assert-True ($document.Diagnostics.IsEmpty -and $document.Sections.Length -eq 1) 'Corrected resource is not strictly valid.'
    $tier = $document.Sections[0]
    Assert-True ($tier.Name -ceq 'piece set ability' -and $tier.Values.Length -eq 1 -and $tier.Values[0].Value -eq 3 -and $tier.IsBlock) 'Set threshold changed.'
    $rows = @($tier.Children | Where-Object Name -eq 'then' | ForEach-Object Children)
    foreach ($stat in @('unique increase damage', 'unique increase critical damage')) {
        $effect = @($rows | Where-Object Name -eq $stat)
        Assert-True ($effect.Count -eq 1 -and $effect[0].Values.Length -eq 3 -and
            $archive.ResolveString($effect[0].Values[1].Value) -ceq '%' -and $effect[0].Values[2].Value -eq 20) 'Set damage multiplier changed.'
    }
    Write-HazySetStructureDelta $plan $target
    $manifest = Get-Content -LiteralPath (Join-Path $target 'manifest.json') -Raw | ConvertFrom-Json
    Assert-True ($manifest.replacements.Count -eq 1 -and $manifest.replacements[0].path -ceq $plan.Path) 'Manifest scope differs.'
    $entry = $manifest.replacements[0]
    Assert-True ([IO.Path]::IsPathFullyQualified($entry.raw_file) -and
        (Get-FileHash -LiteralPath $entry.raw_file).Hash.ToLowerInvariant() -ceq $plan.AfterSha256 -and
        $entry.expected_before_sha256 -ceq $plan.BeforeSha256) 'Manifest identity differs.'
    Assert-Rejected { Write-HazySetStructureDelta $plan $target } 'Output already exists.'
    Assert-True (@(Get-ChildItem -LiteralPath $target -File).Count -eq 2) 'Output conflict altered files.'
    $proxy.Raw = $plan.Raw
    $applied = New-HazySetStructurePlan $proxy
    Assert-True ($applied.State -ceq 'already_applied' -and $applied.BeforeSha256 -ceq $applied.AfterSha256) 'Already-applied state changed bytes.'
    Write-HazySetStructureDelta $applied ($target + '-applied')
    $empty = Get-Content -LiteralPath (Join-Path ($target + '-applied') 'manifest.json') -Raw | ConvertFrom-Json
    Assert-True ($empty.replacements.Count -eq 0) 'Already-applied manifest is not empty.'
    foreach ($index in @(0, 110, 115, 474)) {
        $proxy.Raw = [byte[]]$before.Clone()
        $proxy.Raw[$index] = $proxy.Raw[$index] -bxor 1
        Assert-Rejected { New-HazySetStructurePlan $proxy } 'Unknown EQU 12594'
    }
    $proxy.Raw = $before[0..473]
    Assert-Rejected { New-HazySetStructurePlan $proxy } 'Unknown EQU 12594'
    $proxy.Missing = $true
    Assert-Rejected { New-HazySetStructurePlan $proxy } 'Missing token EQU'
    Assert-True (@(Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($target)) -Directory -Filter '.hazy-set-*').Count -eq 0) 'Temporary publication directory leaked.'
    Write-Output 'PASS real_input exact_5B_removal strict_parser set_threshold damage_terms manifest output_conflict already_applied unknown_content truncated missing_entry'
} finally { $archive.Dispose() }
