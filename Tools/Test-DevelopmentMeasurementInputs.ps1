# 仅合成小文件，不编译、不读取资源、不申请重型预算。
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'DevelopmentMeasurementInputs.ps1')
$checks=0
function Assert([bool]$Value,[string]$Message) { if(-not $Value){throw "FAIL: $Message"};$script:checks++ }
function Reject([scriptblock]$Action,[string]$Message) { $failed=$false;try{& $Action | Out-Null}catch{$failed=$true};Assert $failed $Message }
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('measurement-inputs-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
try {
    $path=Join-Path $fixture '.literal[1].txt'
    [IO.File]::WriteAllText($path,'abcd')
    Assert ((Get-MeasurementFileDigest $path) -ceq (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) '字面隐藏路径摘要与旧入口一致'
    $empty=Join-Path $fixture 'empty';[IO.File]::WriteAllBytes($empty,[byte[]]@())
    Assert ((Get-MeasurementFileDigest $empty) -ceq (Get-FileHash -LiteralPath $empty -Algorithm SHA256).Hash) '空文件完整读取'
    Reject { Get-MeasurementFileDigest (Join-Path $fixture 'missing') } '缺失输入拒绝'
    Reject { Get-MeasurementFileDigest (Join-Path $fixture 'missing-parent/file') } '缺失祖先拒绝'
    Reject { Get-MeasurementFileDigest $fixture } '目录不是普通输入'
    $before=Get-MeasurementFileObservation $path
    Assert-MeasurementFileUnchanged $before $before 4
    Assert $true '未变元数据通过'
    foreach($key in @('Length','WriteTicks','CreationTicks','Attributes')) {
        $after=[ordered]@{};foreach($name in $before.Keys){$after[$name]=$before[$name]}
        $after[$key]=[long]$after[$key]+1
        Reject { Assert-MeasurementFileUnchanged $before $after 4 } "读取中变化拒绝：$key"
    }
    Reject { Assert-MeasurementFileUnchanged $before $before 3 } '实际流长度不符拒绝'
    $stamp=[IO.File]::GetLastWriteTimeUtc($path)
    [IO.File]::SetLastWriteTimeUtc($path,$stamp.AddSeconds(1))
    Reject { Assert-MeasurementFileUnchanged $before (Get-MeasurementFileObservation $path) 4 } '真实touch可见'
    [IO.File]::SetLastWriteTimeUtc($path,$stamp)
    # 确定性故障注入：第二次元数据观测变化，不依赖后台线程调度。
    $originalObservation=${function:Get-MeasurementFileObservation}
    $script:observationCalls=0
    try {
        function Get-MeasurementFileObservation([string]$Path) {
            $value=& $originalObservation $Path
            $script:observationCalls++
            if($script:observationCalls -eq 2){$value.WriteTicks++}
            $value
        }
        $failure=$null
        try { Get-MeasurementFileDigest $path | Out-Null } catch { $failure=$_.Exception.Message }
        Assert ($failure -like '*读取期间输入元数据变化*' -and $script:observationCalls -eq 2) '真实摘要入口拒绝读后观测变化'
    } finally { ${function:Get-MeasurementFileObservation}=$originalObservation }
    $probe=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
    $probe.Dispose()
    Assert $true '失败路径释放文件流'
    $first=Get-MeasurementFileDigest $path
    [IO.File]::WriteAllText($path,'efgh');[IO.File]::SetLastWriteTimeUtc($path,$stamp)
    Assert ((Get-MeasurementFileDigest $path) -cne $first) '同长且恢复mtime仍重新读取全部内容'
    if($IsLinux) {
        $link=Join-Path $fixture 'leaf-link'
        [IO.File]::CreateSymbolicLink($link,$path) | Out-Null
        Reject { Get-MeasurementFileDigest $link } '文件链接拒绝'
        $broken=Join-Path $fixture 'broken-link'
        [IO.File]::CreateSymbolicLink($broken,(Join-Path $fixture 'missing')) | Out-Null
        Reject { Get-MeasurementFileDigest $broken } '悬空链接拒绝'
        $real=Join-Path $fixture 'real';[IO.Directory]::CreateDirectory($real) | Out-Null
        [IO.File]::WriteAllText((Join-Path $real 'file'),'x')
        $alias=Join-Path $fixture 'dir-link';[IO.Directory]::CreateSymbolicLink($alias,$real) | Out-Null
        Reject { Get-MeasurementFileDigest (Join-Path $alias 'file') } '祖先链接拒绝'
        # 首次成功不能缓存祖先状态；第二次同路径替换为链接仍拒绝。
        $moving=Join-Path $fixture 'moving';[IO.Directory]::CreateDirectory($moving) | Out-Null
        [IO.File]::WriteAllText((Join-Path $moving 'file'),'x')
        $null=Get-MeasurementFileDigest (Join-Path $moving 'file')
        [IO.Directory]::Delete($moving,$true);[IO.Directory]::CreateSymbolicLink($moving,$real) | Out-Null
        Reject { Get-MeasurementFileDigest (Join-Path $moving 'file') } '不复用祖先安全判断'
        $mode=[IO.File]::GetUnixFileMode($path)
        try {
            [IO.File]::SetUnixFileMode($path,[IO.UnixFileMode]0)
            Reject { Get-MeasurementFileDigest $path } '权限读取错误拒绝，不记缺测为成功'
        } finally { [IO.File]::SetUnixFileMode($path,$mode) }
    }
    Write-Host "PASS: $checks checks (synthetic input files only)"
} finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
