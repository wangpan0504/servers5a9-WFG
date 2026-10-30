$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentResultStore.ps1')
$count=0
function Assert($Condition,[string]$Message){if(-not $Condition){throw $Message};$script:count++}
function Assert-Rejected([scriptblock]$Action,[string]$Message){
    $failed=$false
    try { $null=& $Action } catch { $failed=$true }
    Assert $failed $Message
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('input-identity-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
try {
    foreach($size in @(0,1,4096,131073)) {
        $path=Join-Path $root ('literal['+$size+'].bin')
        $bytes=[byte[]]::new($size);[Random]::new($size).NextBytes($bytes)
        [IO.File]::WriteAllBytes($path,$bytes)
        $expected=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert ((Get-ResultFileDigest $path) -ceq $expected) "SHA256必须与原字面入口相等：$size"
        $exclusive=[IO.File]::Open($path,'Open','ReadWrite','None')
        try { Assert $exclusive.CanWrite '散列完成后不得留下文件句柄。' } finally { $exclusive.Dispose() }
    }
    Push-Location $root
    try {
        $expected=(Get-FileHash -LiteralPath 'literal[4096].bin' -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert ((Get-ResultFileDigest 'literal[4096].bin') -ceq $expected) '相对路径必须采用PS当前位置，[]不展开。'
        Assert ((Get-ResultFileDigest ('FileSystem::'+(Join-Path $root 'literal[4096].bin'))) -ceq $expected) '文件系统provider限定路径保持可用。'
        $driveName='digest'+[guid]::NewGuid().ToString('N')
        New-PSDrive -Name $driveName -PSProvider FileSystem -Root $root|Out-Null
        try { Assert ((Get-ResultFileDigest ($driveName+':/literal[4096].bin')) -ceq $expected) '自定义PSDrive仍按字面路径解析。' }
        finally { Remove-PSDrive -Name $driveName }
    } finally { Pop-Location }
    Assert-Rejected { Get-ResultFileDigest 'Env:PATH' } '非文件系统provider必须拒绝。'
    Assert-Rejected { Get-ResultFileDigest (Join-Path $root 'missing') } '缺失文件不得返回空摘要。'
    Assert-Rejected { Get-ResultFileDigest $root } '目录不得作为文件摘要输入。'
    $path=Join-Path $root 'mutable';[IO.File]::WriteAllText($path,'before')
    $stamp=[IO.File]::GetLastWriteTimeUtc($path);$before=Get-ResultFileDigest $path
    [IO.File]::WriteAllText($path,'after!');[IO.File]::SetLastWriteTimeUtc($path,$stamp)
    Assert ((Get-ResultFileDigest $path) -cne $before) '同长度同mtime内容改变必须使摘要失效。'

    $tree=Join-Path $root 'tree';[IO.Directory]::CreateDirectory($tree)|Out-Null
    & git -C $tree init -q
    if($LASTEXITCODE -ne 0){throw '合成Git仓库初始化失败。'}
    $names=@('.hidden.cs','literal[1].cs','space name.cs','名称.cs')
    if($IsLinux){$names+=@('Case.cs','case.cs')}
    $expected=[Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
    foreach($name in $names) {
        $file=Join-Path $tree $name;[IO.File]::WriteAllText($file,$name)
        $mode=if($IsWindows){[string](Get-Item -LiteralPath $file -Force).Attributes}else{[string][IO.File]::GetUnixFileMode($file)}
        $expected[$name]=@{Digest=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant();Mode=$mode}
    }
    & git -C $tree add .
    if($LASTEXITCODE -ne 0){throw '合成Git输入登记失败。'}
    $manifest=Get-ResultSourceManifest $tree
    Assert ($manifest.Files.Count -eq $names.Count) '隐藏、字面、Unicode及大小写路径不能漏项或合并。'
    Assert ($manifest.Key -ceq (Get-ResultDigest (ConvertTo-ResultCanonical $expected))) '完整源码身份保持独立预期的内容及mode合同。'
    $path=Join-Path $tree 'literal[1].cs';[IO.File]::Delete($path)
    Assert-Rejected { Get-ResultSourceManifest $tree } '已跟踪文件缺失必须拒绝。'
    [IO.Directory]::CreateDirectory($path)|Out-Null
    Assert-Rejected { Get-ResultSourceManifest $tree } '已跟踪文件被目录替换必须拒绝。'
    [IO.Directory]::Delete($path);[IO.File]::WriteAllText($path,'literal[1].cs')
    if($IsLinux) {
        $mode=[IO.File]::GetUnixFileMode($path)
        [IO.File]::SetUnixFileMode($path,($mode -bor [IO.UnixFileMode]::UserExecute))
        Assert ((Get-ResultSourceManifest $tree).Key -cne $manifest.Key) 'Unix执行权限变化必须改变身份。'
        [IO.File]::SetUnixFileMode($path,$mode)
        $target=Join-Path $root 'target';[IO.File]::WriteAllText($target,'literal[1].cs')
        foreach($type in @('SymbolicLink','HardLink')) {
            [IO.File]::Delete($path);New-Item -ItemType $type -Path $path -Target $target|Out-Null
            Assert ((Get-ResultFileDigest $path) -ceq (Get-ResultFileDigest $target)) '文件散列原语保留按路径读取语义，由清单层拒绝链接输入。'
            Assert-Rejected { Get-ResultSourceManifest $tree } "$type 源码输入仍须拒绝。"
        }
        [IO.File]::Delete($path)
        New-Item -ItemType SymbolicLink -Path $path -Target (Join-Path $root 'absent')|Out-Null
        Assert-Rejected { Get-ResultSourceManifest $tree } '悬空符号链接仍须拒绝。'
    }
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
Write-Output "PASS $count input identity assertions"
