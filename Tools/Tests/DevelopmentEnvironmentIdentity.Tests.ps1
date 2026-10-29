$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentChatRateProfile.ps1')
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentArtifactWorkspace.ps1')
$count=0
function Assert($Condition,[string]$Message){if(-not $Condition){throw $Message};$script:count++}
# 纯输入行测试，不修改进程代理，不连接网络，也不打印真实环境值。
$rows=@([pscustomobject]@{Name='HTTP_PROXY';Value='synthetic-upper'},[pscustomobject]@{Name='http_proxy';Value='synthetic-lower'},[pscustomobject]@{Name='PWD';Value='logical-output'})
$before=Get-DevelopmentEnvironmentMap -Entries $rows
Assert ($before.Count -eq 2 -and $before.Contains('HTTP_PROXY') -and $before.Contains('http_proxy')) 'Linux大小写不同环境名不得折叠。'
$first=Get-ResultDigest (ConvertTo-ResultCanonical $before)
$rows[0].Value='synthetic-upper-changed'
$after=Get-DevelopmentEnvironmentMap -Entries $rows
Assert ($after['http_proxy'] -ceq 'synthetic-lower') '改变大写项不得覆盖小写项。'
Assert ((Get-ResultDigest (ConvertTo-ResultCanonical $after)) -cne $first) '仅大写值改变也必须使完整环境身份失效。'
$rows[0].Value='synthetic-upper';$rows[1].Value='synthetic-lower-changed'
Assert ((Get-ResultDigest (ConvertTo-ResultCanonical (Get-DevelopmentEnvironmentMap -Entries $rows))) -cne $first) '仅小写值改变必须同样失效。'
$rows += [pscustomobject]@{Name='pwd';Value='not-the-audited-uppercase-binding'}
Assert ((Get-DevelopmentEnvironmentMap -Entries $rows).Contains('pwd')) '只排除明确逻辑PWD，不能连不同大小写变量一起抹掉。'
$firstDirectory=Get-ResultDigest (ConvertTo-ResultCanonical (Get-DevelopmentDirectoryEnvironmentMap $before))
Assert ((Get-ResultDigest (ConvertTo-ResultCanonical (Get-DevelopmentDirectoryEnvironmentMap $after))) -ceq $firstDirectory) '已观察代理只从目录身份排除，防复制多个相同编译目录。'
Assert ((Get-ResultDigest (ConvertTo-ResultCanonical $after)) -cne $first) '完整构建/结果身份仍保留代理漂移，不能假等价。'
$other=Get-DevelopmentEnvironmentMap -Entries @([pscustomobject]@{Name='MSBuildUnknownInput';Value='one'})
$directoryBefore=Get-ResultDigest (ConvertTo-ResultCanonical (Get-DevelopmentDirectoryEnvironmentMap $other))
$other['MSBuildUnknownInput']='two'
Assert ((Get-ResultDigest (ConvertTo-ResultCanonical (Get-DevelopmentDirectoryEnvironmentMap $other))) -cne $directoryBefore) '未知/编译环境变量不能被泛前缀排除。'
Write-Output "PASS $count environment identity assertions"
