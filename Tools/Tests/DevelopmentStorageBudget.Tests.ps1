# 仅合成目录，不构建、不启动服务、不访问已有证据。
$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentStorageBudget.ps1')
$root=Join-Path ([IO.Path]::GetTempPath()) ('storage-budget-'+[guid]::NewGuid().ToString('N'))
$store=Join-Path $root 'store';$repo=Join-Path $root 'repo';$checks=0
function Assert($Condition,[string]$Message){if(-not $Condition){throw $Message};$script:checks++}
function Write-Fixture($Path,$Value){[IO.Directory]::CreateDirectory((Split-Path $Path -Parent))|Out-Null;[IO.File]::WriteAllText($Path,$Value)}
function Snapshot { @((Get-ChildItem $root -Recurse -Force | ForEach-Object {"$($_.FullName)|$(if($_.PSIsContainer){0}else{$_.Length})|$($_.LastWriteTimeUtc.Ticks)"})) -join "`n" }
try {
    $empty=Get-DevelopmentStorageInventory $store @($repo)
    Assert ($empty.State -eq 'observed' -and $empty.Files -eq 0 -and -not (Test-Path $root)) '缺失目录应只读且为空'
    Write-Fixture (Join-Path $store 'runs/one/log.txt') '12345'
    Write-Fixture (Join-Path $store 'records/key/result.json') '{}'
    Write-Fixture (Join-Path $repo 'build/development/debug/key/bin/data') '123'
    Write-Fixture (Join-Path $store 'locks/key.lock') ''
    $before=Snapshot
    $lease=[IO.File]::Open((Join-Path $store 'locks/key.lock'),'Open','ReadWrite','None')
    try {$inventory=Get-DevelopmentStorageInventory $store @($repo)}finally{$lease.Dispose()}
    Assert ($before -ceq (Snapshot)) '清单不得改变文件或锁'
    Assert ($inventory.LogicalBytes -eq 10 -and $inventory.Files -eq 4) '字节/文件总数'
    Assert ($inventory.Activity -eq 'unknown' -and $inventory.Roots[0].Locks -eq 1) '活动锁不得误报闲置'
    Assert ((Test-DevelopmentStorageBudget $inventory 11 5).State -eq 'allowed') '阈值以下允许'
    Assert ((Test-DevelopmentStorageBudget $inventory 10 0).State -eq 'blocked') '等于字节阈值拒绝'
    Assert ((Test-DevelopmentStorageBudget $inventory 0 4).State -eq 'blocked') '等于文件阈值拒绝'
    Assert ((Test-DevelopmentStorageBudget $inventory 0 0).Reason -eq 'disabled') '默认关闭'
    Assert ((Get-DevelopmentStorageInventory $store @($repo) 1).State -eq 'unknown') '扫描上限不能冒充完整'
    Write-Fixture (Join-Path $store 'records/key/broken.json') '{broken'
    $bad=Get-DevelopmentStorageInventory $store @($repo)
    Assert ($bad.State -eq 'unknown' -and (Test-DevelopmentStorageBudget $bad 1000).State -eq 'blocked') '损坏证据保守拒绝'
    Remove-Item (Join-Path $store 'records/key/broken.json')
    if($IsLinux){
        $outside=Join-Path $root 'outside';Write-Fixture (Join-Path $outside 'data') 'outside'
        New-Item -ItemType SymbolicLink -Path (Join-Path $store 'escape') -Target $outside | Out-Null
        $linked=Get-DevelopmentStorageInventory $store @($repo)
        Assert ($linked.State -eq 'unknown' -and $linked.LogicalBytes -eq 10) '目录链接不跟随'
        Remove-Item (Join-Path $store 'escape')
        New-Item -ItemType HardLink -Path (Join-Path $store 'hard') -Target (Join-Path $outside 'data')|Out-Null
        Assert ((Get-DevelopmentStorageInventory $store @($repo)).LogicalBytes -eq 17) '普通硬链接按路径累计'
        New-Item -ItemType SymbolicLink -Path (Join-Path $root 'alias') -Target $store | Out-Null
        Assert ((Get-DevelopmentStorageInventory (Join-Path $root 'alias/nested') @($repo)).State -eq 'unknown') '祖先目录链接不跟随'
    }
    if($IsLinux){
        & mkfifo (Join-Path $store 'pipe.json')
        Assert ((Get-DevelopmentStorageInventory $store @($repo)).State -eq 'unknown') '特殊文件不读取或阻塞'
        Remove-Item (Join-Path $store 'pipe.json')
    }
    # 真入口在输入/profile和所有创建行为之前拒绝；无dotnet、无编译。
    $script=Join-Path (Split-Path $PSScriptRoot -Parent) 'Test-Development.ps1'
    $before=Snapshot
    $raw=& (Get-Process -Id $PID).Path -NoProfile -File $script -Action Run -Mode Dev -Groups chat-rate-rules -Store $store -Repository $repo -StorageMaxBytes 1
    Assert ($LASTEXITCODE -eq 2 -and (($raw -join "`n"|ConvertFrom-Json).Reason -eq 'storage-byte-threshold')) 'Run须实际接通容量预检'
    Assert ($before -ceq (Snapshot)) 'Run拒绝不得创建运行现场或改写证据'
    "PASS: $checks storage budget checks"
} finally {if(Test-Path $root){Remove-Item $root -Recurse -Force}}
