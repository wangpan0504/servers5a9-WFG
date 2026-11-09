#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$CandidatePath,
    [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$PvfReplacePath,
    [Parameter(Mandatory)][string]$ReceiptPath
)
$ErrorActionPreference='Stop'
$m=Get-Content -LiteralPath $ManifestPath -Raw|ConvertFrom-Json
if($m.schema -cne 'basic-mastery-level95-v1'){throw 'Unexpected schema.'}
$target=[IO.Path]::GetFullPath($CandidatePath)
if((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $ReceiptPath)){throw 'Preserve previous output.'}
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).Path
function Hash-Raw([byte[]]$Raw){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Raw)).ToLowerInvariant()}
$before=[PvfLib.PvfArchive]::Open($m.source)
try{
    $info=Get-Item -LiteralPath $m.source
    if($info.Length -ne $m.source_length -or $info.LastWriteTimeUtc.Ticks -ne $m.source_utc_ticks){throw 'Source changed.'}
    if(@($m.entries).Count -ne 16 -or @($m.entries.path|Sort-Object -Unique).Count -ne 16){throw 'Exactly sixteen jobs required.'}
    $expected=@('swordman','fighter','gunner','mage','priest','atgunner','thief','atfighter','atswordman','demonicswordman','creatormage','knight','demoniclancer','atmage','atpriest','gunblader')|ForEach-Object{'skill/'+$_+'/basicattackup.skl'}
    foreach($e in $m.entries){
        if($e.path -cnotin $expected){throw 'Unexpected path.'}
        $a=$before.ReadRaw($e.path);$b=[IO.File]::ReadAllBytes($e.raw_file)
        if((Hash-Raw $a) -cne $e.before_sha256 -or (Hash-Raw $b) -cne $e.after_sha256 -or $a.Length -ne $b.Length){throw 'Raw identity mismatch.'}
        $tokens=$before.ReadTokens($e.path);$index=[int]$e.changed_token
        if($index -le 0 -or $index -ge $tokens.Count -or $tokens[$index-1].Type -ne 3 -or $before.ResolveString($tokens[$index-1].Value) -cne '[maximum level]' -or $tokens[$index].Type -ne 0 -or $tokens[$index].Value -ne 100){throw 'Wrong maximum token.'}
        [BitConverter]::GetBytes([int]95).CopyTo($a,$index*5+1)
        if((Hash-Raw $a) -cne (Hash-Raw $b)){throw 'Unexpected change outside maximum integer.'}
    }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force|Out-Null
    $arguments=@($m.source,$target)+@($m.entries|ForEach-Object{$_.path+'='+$_.raw_file})
    & $PvfReplacePath @arguments
    if($LASTEXITCODE -ne 0){throw 'PVF repack failed.'}
    $after=[PvfLib.PvfArchive]::Open($target)
    try{
        if($before.FileCount -ne $after.FileCount){throw 'File count changed.'}
        foreach($e in $m.entries){if((Hash-Raw $after.ReadRaw($e.path)) -cne $e.after_sha256){throw 'Candidate mismatch.'}}
        Add-Type -ReferencedAssemblies (Resolve-Path -LiteralPath $PvfLibPath).Path -TypeDefinition @'
using System;
using PvfLib;
public static class BasicMasteryRawAudit {
 public static long[] Compare(PvfArchive before,PvfArchive after,string[] paths) {
  long compared=0,unchanged=0,changed=0;
  before.ScanRawFiles(before.Files,(file,read)=>{
   var other=after.FindFile(file.Path);
   if(other==null||other.DataType!=file.DataType)throw new InvalidOperationException("Identity changed: "+file.Path);
   byte[] a=read(),b=after.ReadRaw(file.Path);bool same=a.Length==b.Length;
   for(int i=0;same&&i<a.Length;i++)same=a[i]==b[i];compared++;
   if(same)unchanged++;else if(Array.IndexOf(paths,file.Path)>=0)changed++;else throw new InvalidOperationException("Unexpected raw change: "+file.Path);
  });return new[]{compared,unchanged,changed};
 }
}
'@
        $audit=[BasicMasteryRawAudit]::Compare($before,$after,[string[]]$m.entries.path)
        if($audit[0] -ne $before.FileCount -or $audit[2] -ne 16){throw 'Full raw audit mismatch.'}
        [ordered]@{passed=$true;source=$m.source;candidate=$target;compared=$audit[0];unchanged=$audit[1];changed=$m.entries.path;maximum=95;only_maximum_integer_changed=$true;whole_pvf_hash=$false}|
            ConvertTo-Json -Depth 5|Set-Content -LiteralPath $ReceiptPath -Encoding utf8
        Write-Output "PASS full_raw=$($audit[0]) unchanged=$($audit[1]) changed=16"
    }finally{$after.Dispose()}
}finally{$before.Dispose()}
