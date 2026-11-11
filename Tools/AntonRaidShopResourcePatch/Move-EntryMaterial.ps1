#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PvfPath,
    [Parameter(Mandatory)][string]$CandidatePath,
    [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$PvfReplacePath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$source=(Resolve-Path -LiteralPath $PvfPath).Path
$target=[IO.Path]::GetFullPath($CandidatePath)
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $target){throw 'Candidate must not exist.'}
if(Test-Path -LiteralPath $output){throw 'Use a new evidence directory.'}
if([StringComparer]::OrdinalIgnoreCase.Equals($source,$target)){throw 'Source overwrite forbidden.'}
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).Path
function Hash-Raw([byte[]]$Raw){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Raw)).ToLowerInvariant()}
function Encode-Tokens($Tokens){
    $raw=[byte[]]::new($Tokens.Count*5)
    for($i=0;$i -lt $Tokens.Count;$i++){
        $raw[$i*5]=$Tokens[$i].Type
        [BitConverter]::GetBytes([int]$Tokens[$i].Value).CopyTo($raw,$i*5+1)
    }
    return ,$raw
}
$before=[PvfLib.PvfArchive]::Open($source)
try{
    $stamp=Get-Item -LiteralPath $source
    $sourceLength=$stamp.Length;$sourceTicks=$stamp.LastWriteTimeUtc.Ticks
    $paths=@('itemshop/naen_raid.shp','itemshop/raidshop_anton.shp')
    $item=10096296
    $oldNaen=$before.ReadTokens($paths[0]);$oldAnton=$before.ReadTokens($paths[1])
    $positions=@(for($i=0;$i -lt $oldNaen.Count;$i++){if($oldNaen[$i].Type -eq 0 -and $oldNaen[$i].Value -eq $item){$i}})
    if($positions.Count -ne 1){throw 'Expected exactly one native entry material in Naen raid shop.'}
    if(@($oldAnton|Where-Object{$_.Type -eq 0 -and $_.Value -eq $item}).Count){throw 'Anton already contains the entry material; review newer cumulative input.'}
    $starts=@(for($i=0;$i -lt $oldAnton.Count;$i++){if($oldAnton[$i].Type -eq 3 -and $before.ResolveString($oldAnton[$i].Value) -ceq '[item list]'){$i}})
    if($starts.Count -ne 5 -or $oldAnton[$starts[0]+1].Type -ne 0){throw 'Expected restored native five-page Anton stock.'}
    # 只移动商品整数；页名、全部原商品顺序、模板售价和其它商店不改。
    $newNaen=@(for($i=0;$i -lt $oldNaen.Count;$i++){if($i -ne $positions[0]){$oldNaen[$i]}})
    $newAnton=@($oldAnton[0..$starts[0]])+@($oldNaen[$positions[0]])+@($oldAnton[($starts[0]+1)..($oldAnton.Count-1)])
    $template='stackable/10096001/10096296.stk'
    $templateHash=Hash-Raw $before.ReadRaw($template)
    $null=New-Item -ItemType Directory -Path $output
    $raws=@((Encode-Tokens $newNaen),(Encode-Tokens $newAnton))
    $entries=@(for($i=0;$i -lt $paths.Count;$i++){
        $rawFile=Join-Path $output ($i.ToString()+'.raw')
        [IO.File]::WriteAllBytes($rawFile,$raws[$i])
        [ordered]@{path=$paths[$i];raw_file=$rawFile;before_sha256=(Hash-Raw $before.ReadRaw($paths[$i]));after_sha256=(Hash-Raw $raws[$i])}
    })
    [ordered]@{schema='anton-entry-material-move-v1';source=$source;source_length=$sourceLength;source_utc_ticks=$sourceTicks;
        candidate=$target;item=$item;entries=$entries;template=$template;template_sha256=$templateHash;
        scope='Move only one stock token from Naen raid shop to Anton first page first slot; all template prices and conditions preserved.'}|
        ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'delta.json') -Encoding utf8
    $current=Get-Item -LiteralPath $source
    if($current.Length -ne $sourceLength -or $current.LastWriteTimeUtc.Ticks -ne $sourceTicks){throw 'Source changed before packing.'}
    $null=New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($target))
    $arguments=@($source,$target)+@($entries|ForEach-Object{$_.path+'='+$_.raw_file})
    & $PvfReplacePath @arguments
    if($LASTEXITCODE -ne 0){throw 'PVF repack failed.'}
    $after=[PvfLib.PvfArchive]::Open($target)
    try{
        if($after.FileCount -ne $before.FileCount){throw 'PVF file count changed.'}
        foreach($entry in $entries){if((Hash-Raw $after.ReadRaw($entry.path)) -cne $entry.after_sha256){throw 'Packed stock differs from planned token delta.'}}
        if((Hash-Raw $after.ReadRaw($template)) -cne $templateHash){throw 'Entry material template changed.'}
        Add-Type -ReferencedAssemblies (Resolve-Path -LiteralPath $PvfLibPath).Path -TypeDefinition @'
using System;
using PvfLib;
public static class AntonEntryMaterialAudit {
 public static long[] Compare(PvfArchive before,PvfArchive after) {
  long compared=0,unchanged=0,changed=0;
  before.ScanRawFiles(before.Files,(file,read)=>{
   var other=after.FindFile(file.Path);
   if(other==null||other.DataType!=file.DataType)throw new InvalidOperationException("Identity changed: "+file.Path);
   byte[] a=read(),b=after.ReadRaw(file.Path);bool same=a.Length==b.Length;
   for(int i=0;same&&i<a.Length;i++)same=a[i]==b[i];
   compared++;
   if(same)unchanged++;
   else if(file.Path=="itemshop/naen_raid.shp"||file.Path=="itemshop/raidshop_anton.shp")changed++;
   else throw new InvalidOperationException("Unexpected raw change: "+file.Path);
  });
  return new[]{compared,unchanged,changed};
 }
}
'@
        $audit=[AntonEntryMaterialAudit]::Compare($before,$after)
        if($audit[0] -ne $before.FileCount -or $audit[2] -ne 2){throw 'Full raw audit mismatch.'}
        [ordered]@{passed=$true;source=$source;candidate=$target;compared=$audit[0];unchanged=$audit[1];changed=$paths;
            unexpected=0;item=$item;first_page_first_slot=$true;price_template_unchanged=$true;whole_pvf_hash=$false}|
            ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $output 'receipt.json') -Encoding utf8
        Write-Output "PASS full_raw=$($audit[0]) unchanged=$($audit[1]) changed=2 first_slot=$item"
    }finally{$after.Dispose()}
}finally{$before.Dispose()}
