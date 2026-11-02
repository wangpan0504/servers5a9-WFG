[CmdletBinding()]
param([Parameter(Mandatory)][string]$PvfPath,[Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$TemporaryDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'New-HarlemOverheadNamesDelta.ps1') -PvfPath $PvfPath -PvfLibPath $PvfLibPath
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive=[PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Assert-Rejected([scriptblock]$Action,[string]$Expected) {
    try { & $Action } catch { if ($_.Exception.Message -notlike "*$Expected*") {throw}; return }
    throw "Expected rejection: $Expected"
}
$proxy=[pscustomobject]@{Archive=$archive;Overrides=@{};Missing=''}
$proxy|Add-Member ScriptMethod FindFile {param($Path) if($Path -eq $this.Missing){return $null};$this.Archive.FindFile($Path)}
$proxy|Add-Member ScriptMethod ReadRaw {param($Path) if($this.Overrides.ContainsKey($Path)){return ,$this.Overrides[$Path]};return ,$this.Archive.ReadRaw($Path)}
$proxy|Add-Member ScriptMethod ReadTokens {
    param($Path)
    if(-not $this.Overrides.ContainsKey($Path)){return ,$this.Archive.ReadTokens($Path)}
    $raw=$this.Overrides[$Path]
    $tokens=@(for($at=0;$at -lt $raw.Length;$at+=5){[pscustomobject]@{Type=$raw[$at];Value=[BitConverter]::ToInt32($raw,$at+1)}})
    return ,$tokens
}
$proxy|Add-Member ScriptMethod ResolveString {param($Value) $this.Archive.ResolveString($Value)}
$proxy|Add-Member ScriptMethod TryFindStringValue {param($Text,$Value) $this.Archive.TryFindStringValue($Text,$Value)}
try {
    $plan=@(New-HarlemOverheadNamesPlan $archive)
    Assert-True ($plan.Count -eq 9) 'Expected nine distinct targets.'
    foreach($item in $plan){
        $raw=$archive.ReadRaw($item.Path)
        Assert-True ($item.State -eq 'patched' -and $item.Raw.Length -eq $raw.Length+5) 'Expected exactly one appended marker.'
        Assert-True ([Convert]::ToBase64String($item.Raw,0,$raw.Length) -ceq [Convert]::ToBase64String($raw)) 'Existing MOB bytes changed.'
        Assert-True ($item.Raw[$raw.Length] -eq 3 -and $archive.ResolveString([BitConverter]::ToInt32($item.Raw,$raw.Length+1)) -ceq '[enable states]') 'Wrong native marker.'
        $proxy.Overrides[$item.Path]=$item.Raw
    }
    $again=@(New-HarlemOverheadNamesPlan $proxy)
    Assert-True (@($again|Where-Object {$_.State -ne 'already_applied' -or $_.BeforeSha256 -ne $_.AfterSha256}).Count -eq 0) 'Repeated application changed resources.'
    $first=$plan[0]
    $proxy.Missing=$first.Path
    Assert-Rejected {New-HarlemOverheadNamesPlan $proxy} 'Missing token MOB'
    $proxy.Missing=''
    $duplicate=[byte[]]::new($first.Raw.Length+5)
    $first.Raw.CopyTo($duplicate,0)
    [Array]::Copy($first.Raw,$first.Raw.Length-5,$duplicate,$first.Raw.Length,5)
    $proxy.Overrides[$first.Path]=$duplicate
    Assert-Rejected {New-HarlemOverheadNamesPlan $proxy} 'duplicate display marker'
    Write-HarlemOverheadNamesDelta $plan $TemporaryDirectory
    $manifest=Get-Content -LiteralPath (Join-Path $TemporaryDirectory 'manifest.json') -Raw|ConvertFrom-Json
    Assert-True ($manifest.replacements.Count -eq 9) 'Manifest target count differs.'
    foreach($row in $manifest.replacements){Assert-True ((Get-FileHash -LiteralPath $row.raw_file).Hash.ToLowerInvariant() -ceq $row.after_sha256) 'Manifest raw hash differs.'}
    Assert-Rejected {Write-HarlemOverheadNamesDelta $plan $TemporaryDirectory} 'Output already exists.'
    Write-Output 'PASS nine target byte preservation, native marker, idempotence, missing resource, duplicate marker, output and manifest guards.'
} finally {$archive.Dispose()}
