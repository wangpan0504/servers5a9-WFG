param(
    [string]$Repository=(Split-Path $PSScriptRoot -Parent),
    [Parameter(Mandatory)][string]$RegistryFile,
    [Parameter(Mandatory)][string]$Runner
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'DevelopmentRegistryComparison.ps1')
try {
    $source=Get-ResultSourceManifest $Repository
    $runnerItem=Get-Item -LiteralPath $Runner -Force -ErrorAction Stop
    $inputItem=Get-Item -LiteralPath $RegistryFile -Force -ErrorAction Stop
    foreach($item in @($runnerItem,$inputItem)) {
        if($item.PSIsContainer -or $item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '输入必须是普通文件。'}
    }
    if($runnerItem.Name -cne 'DfoServer.dll' -or $inputItem.Length -gt 1048576){throw '程序集名称或注册清单大小无效。'}
    $runnerDigest=Get-ResultFileDigest $runnerItem.FullName
    $inputDigest=Get-ResultFileDigest $inputItem.FullName
    $text=[IO.File]::ReadAllText($inputItem.FullName)
    $document=[Text.Json.JsonDocument]::Parse($text)
    try { Assert-RegistryJsonProperties $document.RootElement } finally { $document.Dispose() }
    $registry=$text | ConvertFrom-Json -AsHashtable -Depth 30
    $result=Compare-DevelopmentRegistry $Repository $registry $runnerDigest
    if((Get-ResultSourceManifest $Repository).Key -cne $source.Key -or
       (Get-ResultFileDigest $runnerItem.FullName) -cne $runnerDigest -or
       (Get-ResultFileDigest $inputItem.FullName) -cne $inputDigest){throw '核对期间输入变化。'}
    $result.Source=$source.Key; $result.RegistryFileDigest=$inputDigest
    $result | ConvertTo-Json -Depth 20
} catch {
    @{State='blocked';Execution='not-run';ProofOfCoverage=$false;Reason=$_.Exception.Message} | ConvertTo-Json
    exit 2
}
