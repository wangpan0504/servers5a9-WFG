#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$CandidatePath,
    [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$ReceiptPath
)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $ReceiptPath){throw 'Preserve existing receipt.'}
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).Path
$m=Get-Content -LiteralPath $ManifestPath -Raw|ConvertFrom-Json
if($m.schema -cne 'raid-shop-native-category-v1'){throw 'Unexpected schema.'}
function Hash-Raw([byte[]]$Raw){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Raw)).ToLowerInvariant()}
$before=[PvfLib.PvfArchive]::Open($m.source)
$after=[PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $CandidatePath).Path)
try{
    $info=Get-Item -LiteralPath $m.source
    if($info.Length -ne $m.source_length -or $info.LastWriteTimeUtc.Ticks -ne $m.source_utc_ticks){throw 'Source drift.'}
    if($before.FileCount -ne $after.FileCount){throw 'File count changed.'}
    $paths=@('etc/additionalraidreward.etc','ui/raid/shop/raidshopwindowver2.xui')
    if(@($m.entries).Count -ne 2 -or @($m.entries.path|Sort-Object -Unique).Count -ne 2){throw 'Exactly two entries required.'}
    foreach($entry in $m.entries){
        if($entry.path -cnotin $paths -or (Hash-Raw $before.ReadRaw($entry.path)) -cne $entry.before_sha256 -or (Hash-Raw $after.ReadRaw($entry.path)) -cne $entry.after_sha256){throw 'Raw identity mismatch.'}
    }
    # 除四个分类整数外，目录所有token必须逐字节保持。
    $left=$before.ReadTokens($paths[0]);$right=$after.ReadTokens($paths[0]);$changes=0
    if($left.Count -ne $right.Count){throw 'Directory token count changed.'}
    $mapping=@{3=6;4=3;5=4;6=5}
    for($i=0;$i -lt $left.Count;$i++){
        if($left[$i].Type -eq $right[$i].Type -and $left[$i].Value -eq $right[$i].Value){continue}
        if($i -eq 0 -or $left[$i].Type -ne 0 -or $right[$i].Type -ne 0 -or $left[$i-1].Type -ne 3 -or $before.ResolveString($left[$i-1].Value) -cne '[category]' -or -not $mapping.ContainsKey([int]$left[$i].Value) -or $right[$i].Value -ne $mapping[[int]$left[$i].Value]){throw 'Unexpected directory token change.'}
        $changes++
    }
    if($changes -ne 4){throw 'Expected four category changes.'}
    $old=$before.ReadText($paths[1]);$ui=$after.ReadText($paths[1])
    $guide=[regex]::Match($ui,'<CNUIControlText\b[^>]*ID="STR:txtGuide_6"[^>]*/>').Value
    if(-not $guide){throw 'Missing new guide.'}
    $reverse=$ui.Replace("`r`n                        "+$guide,'')
    $reverseMap=@{6=3;3=4;4=5;5=6}
    $reverse=[regex]::Replace($reverse,'ID="STR:shop_category_([3-6])([^"0-9]*)"',{
        param($match)
        'ID="STR:shop_category_'+$reverseMap[[int]$match.Groups[1].Value]+$match.Groups[2].Value+'"'
    })
    if($reverse -cne $old){throw 'UI differs outside category IDs and guide6.'}
    Add-Type -ReferencedAssemblies (Resolve-Path -LiteralPath $PvfLibPath).Path -TypeDefinition @'
using System;
using PvfLib;
public static class RaidShopCompatibilityAudit {
 public static long[] Compare(PvfArchive before,PvfArchive after) {
  long compared=0,unchanged=0,changed=0;
  before.ScanRawFiles(before.Files,(file,read)=>{
   var other=after.FindFile(file.Path);
   if(other==null||other.DataType!=file.DataType)throw new InvalidOperationException("Identity changed: "+file.Path);
   byte[] a=read(),b=after.ReadRaw(file.Path);bool same=a.Length==b.Length;
   for(int i=0;same&&i<a.Length;i++)same=a[i]==b[i];
   compared++;
   if(same)unchanged++;
   else if(file.Path=="etc/additionalraidreward.etc"||file.Path=="ui/raid/shop/raidshopwindowver2.xui")changed++;
   else throw new InvalidOperationException("Unexpected raw change: "+file.Path);
  });
  return new[]{compared,unchanged,changed};
 }
}
'@
    $audit=[RaidShopCompatibilityAudit]::Compare($before,$after)
    if($audit[0] -ne $before.FileCount -or $audit[2] -ne 2){throw 'Full raw audit mismatch.'}
    [ordered]@{passed=$true;source=$m.source;candidate=(Resolve-Path $CandidatePath).Path;compared=$audit[0];unchanged=$audit[1];changed=$paths;unexpected=0;directory_changed_integers=$changes;ui_inverse_exact=$true;whole_pvf_hash=$false}|
        ConvertTo-Json -Depth 5|Set-Content -LiteralPath $ReceiptPath -Encoding utf8
    Write-Output "PASS full_raw=$($audit[0]) unchanged=$($audit[1]) changed=2"
}finally{$before.Dispose();$after.Dispose()}
