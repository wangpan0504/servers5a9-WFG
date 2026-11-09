#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$CandidatePath,
    [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$PvfReplacePath
)
$ErrorActionPreference='Stop'
$manifest=Get-Content -LiteralPath $ManifestPath -Raw|ConvertFrom-Json
if($manifest.schema -cne 'raid-shop-native-category-v1'){throw 'Unexpected schema.'}
$target=[IO.Path]::GetFullPath($CandidatePath)
if(Test-Path -LiteralPath $target){throw 'Candidate must not exist.'}
if([StringComparer]::OrdinalIgnoreCase.Equals($target,[IO.Path]::GetFullPath($manifest.source))){throw 'Source overwrite forbidden.'}
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).Path
function Hash-Raw([byte[]]$Raw){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Raw)).ToLowerInvariant()}
$source=[PvfLib.PvfArchive]::Open($manifest.source)
try{
    $file=Get-Item -LiteralPath $manifest.source
    if($file.Length -ne $manifest.source_length -or $file.LastWriteTimeUtc.Ticks -ne $manifest.source_utc_ticks){throw 'Source changed.'}
    $allowed=@('etc/additionalraidreward.etc','ui/raid/shop/raidshopwindowver2.xui')
    if(@($manifest.entries).Count -ne 2 -or @($manifest.entries.path|Sort-Object -Unique).Count -ne 2){throw 'Exactly two entries required.'}
    foreach($entry in $manifest.entries){
        if($entry.path -cnotin $allowed -or (Hash-Raw $source.ReadRaw($entry.path)) -cne $entry.before_sha256 -or (Hash-Raw ([IO.File]::ReadAllBytes($entry.raw_file))) -cne $entry.after_sha256){throw 'Raw identity mismatch.'}
    }
}finally{$source.Dispose()}
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force|Out-Null
$arguments=@($manifest.source,$target)+@($manifest.entries|ForEach-Object{$_.path+'='+$_.raw_file})
& $PvfReplacePath @arguments
if($LASTEXITCODE -ne 0){throw 'PVF repack failed.'}
