$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentChatRateProfile.ps1')
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentArtifactWorkspace.ps1')
$count=0
function Assert($Condition,[string]$Message){if(-not $Condition){throw $Message};$script:count++}
$root=Join-Path ([IO.Path]::GetTempPath()) ('development-hardlink-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
try {
    $workspace=Join-Path $root 'workspace';$bin=Join-Path $workspace 'bin';[IO.Directory]::CreateDirectory($bin)|Out-Null
    $output=Join-Path $bin 'app.dll';[IO.File]::WriteAllText($output,'original fixture bytes')
    $time=(Get-Item $output).LastWriteTimeUtc
    $mode=if(-not $IsWindows){[IO.File]::GetUnixFileMode($output)}else{$null}
    $before=Get-ProfileTreeDigest $bin
    $alias=Join-Path $root 'fixture.dll';New-Item -ItemType HardLink -Path $alias -Target $output|Out-Null
    Assert ((Get-Item $output).LinkType -ceq 'HardLink') '必须实际创建硬链接，而非假mock。'
    Assert ((Get-ProfileTreeDigest $bin) -ceq $before) 'fixture只读硬链装配不改变包身份。'
    $identity=Get-DevelopmentOutputIdentity $workspace
    $inputs=@{Source='synthetic';Files=@{};BuildEnvironmentDigest='fixed'}
    $lease=Enter-DevelopmentArtifactLock $workspace 0
    try {
        Start-DevelopmentIncrementalBuild $workspace $inputs|Out-Null
        Assert (-not (Get-Item $output).LinkType) '下一次构建前工作区文件必须私有化。'
        Assert ((Get-DevelopmentOutputIdentity $workspace) -ceq $identity) '分离保持全部字节。'
        Assert ((Get-Item $output).LastWriteTimeUtc -eq $time) '分离保持mtime，不制造虚假新鲜输入。'
        if(-not $IsWindows){Assert ([IO.File]::GetUnixFileMode($output) -eq $mode) '分离保持Unix mode。'}
        [IO.File]::WriteAllText($output,'next build bytes')
        Assert ([IO.File]::ReadAllText($alias) -ceq 'original fixture bytes') '未来构建不能改历史fixture inode。'
    } finally {Exit-DevelopmentArtifactLock $lease $true}
    New-Item -ItemType SymbolicLink -Path (Join-Path $bin 'redirect.dll') -Target $alias|Out-Null
    $blocked=$false;try{Get-ProfileTreeDigest $bin|Out-Null}catch{$blocked=$true}
    Assert $blocked '只读摘要仍拒symlink。'
    $blocked=$false;try{Start-DevelopmentIncrementalBuild $workspace $inputs|Out-Null}catch{$blocked=$true}
    Assert $blocked '构建写前仍拒symlink。'
    Assert ([IO.File]::ReadAllText($alias) -ceq 'original fixture bytes') '拒绝不得写外部目标。'
    $dirlink=Join-Path $root 'redirect-dir';New-Item -ItemType SymbolicLink -Path $dirlink -Target $bin|Out-Null
    $blocked=$false;try{Get-ProfileTreeDigest $dirlink|Out-Null}catch{$blocked=$true}
    Assert $blocked '摘要根目录链接也必须拒绝。'
    Write-Output "PASS $count hardlink assertions"
} finally {Remove-Item -LiteralPath $root -Recurse -Force}
