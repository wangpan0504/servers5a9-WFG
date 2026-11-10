#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PvfPath,
    [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).Path
$source=(Resolve-Path -LiteralPath $PvfPath).Path
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a new output directory.'}
function Hash-Raw([byte[]]$Raw){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Raw)).ToLowerInvariant()}
$archive=[PvfLib.PvfArchive]::Open($source)
try{
    $paths=@('etc/additionalraidreward.etc','ui/raid/shop/raidshopwindowver2.xui')
    $tokens=$archive.ReadTokens($paths[0])
    $changed=0
    for($i=0;$i -lt $tokens.Count;$i++){
        if($tokens[$i].Type -ne 3 -or $archive.ResolveString($tokens[$i].Value) -cne '[shop]'){continue}
        $category=-1;$shop=-1
        for($j=$i+1;$j -lt $tokens.Count;$j++){
            if($tokens[$j].Type -ne 3){continue}
            $tag=$archive.ResolveString($tokens[$j].Value)
            if($tag -ceq '[category]'){$category=$j+1}
            if($tag -ceq '[item shop]'){$shop=$tokens[$j+1].Value}
            if($tag -ceq '[/shop]'){break}
        }
        if($j -ge $tokens.Count -or $category -lt 0){throw 'Malformed shop directory.'}
        $expected=switch($shop){257{3}258{4}274{5}400000052{6}default{-1}}
        $native=switch($shop){257{6}258{3}274{4}400000052{5}default{-1}}
        if($expected -ge 0){
            if($tokens[$category].Type -ne 0 -or $tokens[$category].Value -ne $expected){throw 'Review newer category mapping instead of overwriting it.'}
            $tokens[$category]=[PvfLib.PvfToken]::new([byte]0,$native);$changed++
        }
        $i=$j
    }
    if($changed -ne 4){throw 'Expected exactly four legacy raid category records.'}
    $ui=$archive.ReadText($paths[1])
    if($ui -notmatch 'VAR_201="INTVECTOR:12, 2, 7, 4"' -or $ui -match 'ID="STR:txtGuide_6"'){throw 'Unexpected source layout; do not overwrite newer UI.'}
    foreach($pair in @(@(3,'安徒恩'),@(4,'卢克'),@(5,'超时空漩涡'),@(6,'普雷·伊希斯'))){
        if($ui -notmatch ('ID="STR:shop_category_'+$pair[0]+'_text"[^>]*string="STR:'+ [regex]::Escape($pair[1])+'"')){throw 'Unexpected source labels.'}
    }
    $mapping=@{3=6;4=3;5=4;6=5}
    $fixed=[regex]::Replace($ui,'ID="STR:shop_category_([3-6])([^"0-9]*)"',{
        param($match)
        'ID="STR:shop_category_'+$mapping[[int]$match.Groups[1].Value]+$match.Groups[2].Value+'"'
    })
    $guide=[regex]::Match($ui,'<CNUIControlText\b[^>]*ID="STR:txtGuide_5"[^>]*/>').Value
    if(-not $guide){throw 'Missing native guide template.'}
    $guide=[regex]::Replace($guide.Replace('STR:txtGuide_5','STR:txtGuide_6'),'string="STR:[^"]*"','string="STR:安徒恩攻坚商店"')
    $guide5=[regex]::Match($fixed,'<CNUIControlText\b[^>]*ID="STR:txtGuide_5"[^>]*/>').Value
    $fixed=$fixed.Replace($guide5,$guide5+"`r`n                        "+$guide)
    # 插入完整原生控件后验证XML，保留资源原有结尾NUL，不经XML序列化重排其它元素。
    $null=[xml]$fixed.TrimEnd([char]0)
    $raw=[byte[]]::new($tokens.Count*5)
    for($i=0;$i -lt $tokens.Count;$i++){$raw[$i*5]=$tokens[$i].Type;[BitConverter]::GetBytes([int]$tokens[$i].Value).CopyTo($raw,$i*5+1)}
    $raws=@($raw,[Text.Encoding]::Unicode.GetBytes($fixed))
    New-Item -ItemType Directory -Path $output|Out-Null
    $entries=@(for($i=0;$i -lt 2;$i++){
        $file=Join-Path $output ($i.ToString()+'.raw');[IO.File]::WriteAllBytes($file,$raws[$i])
        [ordered]@{path=$paths[$i];raw_file=$file;before_sha256=(Hash-Raw $archive.ReadRaw($paths[$i]));after_sha256=(Hash-Raw $raws[$i])}
    })
    $info=Get-Item -LiteralPath $source
    [ordered]@{schema='raid-shop-native-category-v1';source=$source;source_length=$info.Length;source_utc_ticks=$info.LastWriteTimeUtc.Ticks;entries=$entries;requires_client_patch='RaidShopCategory';scope='Only raid directory category values and native category control IDs/guide6; stock, prices, special rewards and other resources preserved.'}|
        ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'delta.json') -Encoding utf8
    Write-Output "PREPARED $output"
}finally{$archive.Dispose()}
