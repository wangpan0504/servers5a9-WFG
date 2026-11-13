[CmdletBinding()]
param([Parameter(Mandatory)][string]$PvfPath, [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$CandidatePvfPath)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'New-AntonFleetEnergyDelta.ps1') -PvfPath $PvfPath -PvfLibPath $PvfLibPath
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
$candidate = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $CandidatePvfPath).ProviderPath)
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
try {
    $plan = New-AntonFleetEnergyPlan $archive
    $again = New-AntonFleetEnergyPlan $candidate
    Assert-True ($plan.State -eq 'patched' -and $again.State -eq 'already_applied') 'Expected one change and an idempotent reapplication.'
    Assert-True ([Linq.Enumerable]::SequenceEqual([byte[]]$plan.Raw, [byte[]]$again.Raw)) 'Candidate differs from the generated delta.'
    Assert-True ([Linq.Enumerable]::SequenceEqual([byte[]]$plan.Before, [byte[]]$plan.Raw[0..($plan.Before.Length - 1)])) 'Original death animation changed.'
    $tokens = $candidate.ReadTokens($plan.Path)
    $tail = @($tokens | Select-Object -Skip ($plan.Before.Length / 5))
    $values = @($tail | ForEach-Object { if ($_.Type -eq 3 -or $_.Type -eq 6) { $candidate.ResolveString($_.Value) } else { $_.Value } })
    Assert-True (($values -join '|') -ceq '[TRIGGER]|[LIMIT]|1|[DUNGEON INDEX]|216|[/DUNGEON INDEX]|[FRAME]|0|0|[DO BEHAVIOR]|[ME]|0|[/TRIGGER]|[BEHAVIOR]|[CREATE PASSIVEOBJECT]|[INDEX]|18736|[PARTICLE FILENAME]||[LEVEL]|0|[POS]|0|0|50|[/CREATE PASSIVEOBJECT]|[/BEHAVIOR]') 'Producer must create one ground ball only in dungeon 216.'
    # 保护其它Last行为：未知源不能把本补丁叠加到另一套产球/奖励实现上。
    $proxy = [pscustomobject]@{ Archive = $archive; Text = '[TRIGGER] [LIMIT] 2'; AllowString = $true }
    $proxy | Add-Member ScriptMethod ReadText { param($Path) $this.Text }
    $proxy | Add-Member ScriptMethod ReadRaw { param($Path) ,$this.Archive.ReadRaw($Path) }
    $proxy | Add-Member ScriptMethod TryFindStringValue {
        param($Text, $Value)
        if (-not $this.AllowString) { return $false }
        $this.Archive.TryFindStringValue($Text, $Value)
    }
    $rejected = $false
    try { New-AntonFleetEnergyPlan $proxy | Out-Null } catch {
        if ($_.Exception.Message -notlike 'Unknown guardian Last action:*') { throw }
        $rejected = $true
    }
    Assert-True $rejected 'Unknown gameplay must be rejected.'
    $proxy.Text = $archive.ReadText($plan.Path)
    $proxy.AllowString = $false
    $rejected = $false
    try { New-AntonFleetEnergyPlan $proxy | Out-Null } catch {
        if ($_.Exception.Message -notlike 'Required token is absent:*') { throw }
        $rejected = $true
    }
    Assert-True $rejected 'Missing string tokens must be rejected.'
    Write-Output 'PASS Anton fleet energy delta: original animation, exact producer, candidate readback, reapplication, unknown gameplay and missing token rejection.'
} finally { $candidate.Dispose(); $archive.Dispose() }
