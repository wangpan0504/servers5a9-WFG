#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PvfPath,
    [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).Path
$source=(Resolve-Path -LiteralPath $PvfPath).Path
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a new output directory.'}
function Hash-Raw([byte[]]$Raw){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Raw)).ToLowerInvariant()}
$archive=[PvfLib.PvfArchive]::Open($source)
try{
    $jobs=@('swordman','fighter','gunner','mage','priest','atgunner','thief','atfighter','atswordman','demonicswordman','creatormage','knight','demoniclancer','atmage','atpriest','gunblader')
    New-Item -ItemType Directory -Path $output|Out-Null
    $entries=@(foreach($job in $jobs){
        $path='skill/'+$job+'/basicattackup.skl'
        $tokens=$archive.ReadTokens($path)
        $indices=@(for($i=0;$i -lt $tokens.Count-1;$i++){
            if($tokens[$i].Type -eq 3 -and $archive.ResolveString($tokens[$i].Value) -ceq '[maximum level]'){$i+1}
        })
        if($indices.Count -ne 1 -or $tokens[$indices[0]].Type -ne 0 -or $tokens[$indices[0]].Value -ne 100){throw ('Unexpected maximum; review newer resource: '+$path)}
        $text=$archive.ReadText($path)
        foreach($pair in @(@('interval level',1),@('add level per interval',1),@('is apply contract skillmaster',0),@('required level',1))){
            if($text -notmatch ('\['+[regex]::Escape($pair[0])+'\]\s*'+$pair[1]+'\s')){throw ('Unexpected fixed skill rule: '+$path)}
        }
        # 只修改全局上限整数，保留全部等级系数、固定成长、学习资格和文字引用。
        $raw=$archive.ReadRaw($path)
        [BitConverter]::GetBytes([int]95).CopyTo($raw,$indices[0]*5+1)
        $file=Join-Path $output ($job+'.raw')
        [IO.File]::WriteAllBytes($file,$raw)
        [ordered]@{path=$path;raw_file=$file;before_sha256=(Hash-Raw $archive.ReadRaw($path));after_sha256=(Hash-Raw $raw);changed_token=$indices[0]}
    })
    $info=Get-Item -LiteralPath $source
    [ordered]@{schema='basic-mastery-level95-v1';source=$source;source_length=$info.Length;source_utc_ticks=$info.LastWriteTimeUtc.Ticks;entries=$entries}|
        ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'delta.json') -Encoding utf8
    Write-Output "PREPARED basic-mastery jobs=$($entries.Count)"
}finally{$archive.Dispose()}
