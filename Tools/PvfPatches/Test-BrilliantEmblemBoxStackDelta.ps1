[CmdletBinding()]
param([Parameter(Mandatory)][string]$PvfPath, [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$TemporaryDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'New-BrilliantEmblemBoxStackDelta.ps1') -PvfPath $PvfPath -PvfLibPath $PvfLibPath
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
$proxy = [pscustomobject]@{ Archive = $archive; Raw = $null; Missing = $false; Index = $null }
$proxy | Add-Member ScriptMethod ReadText { param($Path)
    if ($null -ne $this.Index) { return $this.Index }
    $this.Archive.ReadText($Path)
}
$proxy | Add-Member ScriptMethod FindFile { param($Path)
    if ($this.Missing) { return $null }
    $this.Archive.FindFile($Path)
}
$proxy | Add-Member ScriptMethod ReadRaw { param($Path) return ,$this.Raw }
$proxy | Add-Member ScriptMethod ReadTokens { param($Path)
    $tokens = [PvfLib.PvfToken[]]::new([int]($this.Raw.Length / 5))
    for ($i = 0; $i -lt $tokens.Length; $i++) {
        $tokens[$i] = [PvfLib.PvfToken]::new($this.Raw[$i * 5], [BitConverter]::ToInt32($this.Raw, $i * 5 + 1))
    }
    return ,$tokens
}
$proxy | Add-Member ScriptMethod ResolveString { param($Value) $this.Archive.ResolveString($Value) }
try {
    $plan = New-BrilliantEmblemBoxStackPlan $archive
    $raw = $archive.ReadRaw($plan.Path)
    $proxy.Raw = $plan.Raw
    $again = New-BrilliantEmblemBoxStackPlan $proxy
    Assert-True ($again.State -eq 'already_applied' -and $again.BeforeSha256 -eq $again.AfterSha256) 'Repeated application changed bytes.'
    $tokens = $proxy.ReadTokens($plan.Path)
    $resolve = [Func[int, string]]{ param($Value) $archive.ResolveString($Value) }
    $definition = [PvfLib.StackableFile]::Parse(490704572, $plan.Path, $tokens, $resolve)
    Assert-True ($definition.DeclaredStackLimit -eq 1000 -and $definition.TypeDeclaration.Label -ceq '[booster selection]') 'Typed parser did not read stack=1000 selection box.'
    $stackAt = @(for ($i = 0; $i -lt $tokens.Count; $i++) {
        if ($tokens[$i].Type -eq 3 -and $resolve.Invoke($tokens[$i].Value) -ceq '[stack limit]') { $i }
    })[0]
    $valueAt = ($stackAt + 1) * 5 + 1
    Assert-True ($raw.Length -eq $plan.Raw.Length) 'Resource size changed.'
    for ($i = 0; $i -lt $raw.Length; $i++) {
        if ($i -lt $valueAt -or $i -ge $valueAt + 4) {
            Assert-True ($raw[$i] -eq $plan.Raw[$i]) "Unrelated byte changed at $i."
        }
    }
    $document = [PvfLib.PvfTokenDocument]::new($tokens, $resolve)
    $period = @($document.Sections | Where-Object Name -eq 'usable period')
    $binding = @($document.Sections | Where-Object Name -eq 'attach type')
    Assert-True ($period.Count -eq 1 -and $period[0].Values.Length -eq 1 -and $period[0].Values[0].Value -eq 30) '30-day lifetime changed.'
    Assert-True ($binding.Count -eq 1 -and $resolve.Invoke($binding[0].Values[0].Value) -ceq '[account]') 'Account binding changed.'

    $proxy.Missing = $true
    Assert-Rejected { New-BrilliantEmblemBoxStackPlan $proxy } 'Missing token STK'
    $proxy.Missing = $false
    $proxy.Index = ''
    Assert-Rejected { New-BrilliantEmblemBoxStackPlan $proxy } 'Missing or changed emblem box mapping'
    $proxy.Index = $archive.ReadText('stackable/stackable.lst').Replace('cash/chn_490704572.stk', 'cash/other.stk')
    Assert-Rejected { New-BrilliantEmblemBoxStackPlan $proxy } 'Missing or changed emblem box mapping'
    $proxy.Index = $null
    $proxy.Raw = [byte[]]$plan.Raw.Clone()
    [BitConverter]::GetBytes([int]2).CopyTo($proxy.Raw, $valueAt)
    Assert-Rejected { New-BrilliantEmblemBoxStackPlan $proxy } 'Unknown emblem box stack limit'
    $proxy.Raw = [byte[]]$plan.Raw.Clone()
    $proxy.Raw[$valueAt - 1] = 4
    Assert-Rejected { New-BrilliantEmblemBoxStackPlan $proxy } 'Unknown emblem box stack limit'
    # 用已有标签替换另一字段，保持总长，验证重复和缺失不能被误认为幂等。
    $proxy.Raw = [byte[]]$plan.Raw.Clone()
    $periodAt = @(for ($i = 0; $i -lt $tokens.Count; $i++) {
        if ($tokens[$i].Type -eq 3 -and $resolve.Invoke($tokens[$i].Value) -ceq '[usable period]') { $i }
    })[0]
    [Array]::Copy($plan.Raw, $stackAt * 5, $proxy.Raw, $periodAt * 5, 5)
    Assert-Rejected { New-BrilliantEmblemBoxStackPlan $proxy } 'Missing or duplicate stack limit'
    $proxy.Raw = [byte[]]$plan.Raw.Clone()
    [Array]::Copy($plan.Raw, $periodAt * 5, $proxy.Raw, $stackAt * 5, 5)
    Assert-Rejected { New-BrilliantEmblemBoxStackPlan $proxy } 'Missing or duplicate stack limit'
    $proxy.Raw = [byte[]]$plan.Raw.Clone()
    [BitConverter]::GetBytes([int]31).CopyTo($proxy.Raw, ($periodAt + 1) * 5 + 1)
    Assert-Rejected { New-BrilliantEmblemBoxStackPlan $proxy } 'Unknown emblem box baseline'
    $proxy.Raw = [byte[]]::new(0)
    Assert-Rejected { New-BrilliantEmblemBoxStackPlan $proxy } 'Unknown emblem box layout'

    Write-BrilliantEmblemBoxStackDelta $plan $TemporaryDirectory
    $manifest = Get-Content -LiteralPath (Join-Path $TemporaryDirectory 'manifest.json') -Raw | ConvertFrom-Json
    $expectedCount = $(if ($plan.State -eq 'patched') { 1 } else { 0 })
    Assert-True ($manifest.replacements.Count -eq $expectedCount) 'Unexpected manifest replacement count.'
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $TemporaryDirectory $plan.RawFile)).Hash.ToLowerInvariant() -ceq $plan.AfterSha256) 'Output raw hash differs.'
    Assert-Rejected { Write-BrilliantEmblemBoxStackDelta $plan $TemporaryDirectory } 'Output already exists'
    Write-Output 'PASS emblem stack: typed readback=1000, 30-day lifetime, account binding, unrelated bytes, idempotence, mapping/layout/baseline guards and manifest output.'
} finally { $archive.Dispose() }
