# 每次扫描重新检查全部路径及内容；不缓存祖先、文件属性或摘要。
function Assert-NoMeasurementLinks([string]$Path) {
    for ($part=[IO.Path]::GetFullPath($Path); $part; $part=[IO.Path]::GetDirectoryName($part)) {
        # 直接文件系统 API 避免每层 PowerShell provider 枚举；缺失和权限错误均向上传播。
        if ([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint) {
            throw "源码路径不能含链接：$part"
        }
    }
}
function Get-MeasurementFileObservation([string]$Path) {
    $item=[IO.FileInfo]::new($Path)
    $item.Refresh()
    if (-not $item.Exists) { throw "缺失输入：$Path" }
    if ($item.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory)) {
        throw "输入必须是非链接普通文件：$Path"
    }
    [ordered]@{
        Length=$item.Length
        WriteTicks=$item.LastWriteTimeUtc.Ticks
        CreationTicks=$item.CreationTimeUtc.Ticks
        Attributes=[int]$item.Attributes
    }
}
function Assert-MeasurementFileUnchanged($Before,$After,[long]$ReadLength) {
    foreach($key in @('Length','WriteTicks','CreationTicks','Attributes')) {
        if ($Before[$key] -ne $After[$key]) { throw '读取期间输入元数据变化；结果 invalid。' }
    }
    if ($ReadLength -ne $Before.Length) { throw '读取期间输入长度变化；结果 invalid。' }
}
function Get-MeasurementFileDigest([string]$Path) {
    Assert-NoMeasurementLinks $Path
    $before=Get-MeasurementFileObservation $Path
    $stream=[IO.File]::OpenRead($Path)
    try {
        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
        $length=$stream.Length
        if ($stream.Position -ne $length) { throw '输入未完整读取；结果 invalid。' }
        # 同一轮也重查祖先，拒绝可观察的链接替换；不宣称这是文件系统事务快照。
        Assert-NoMeasurementLinks $Path
        $after=Get-MeasurementFileObservation $Path
        Assert-MeasurementFileUnchanged $before $after $length
        return $hash
    } finally { $stream.Dispose() }
}
