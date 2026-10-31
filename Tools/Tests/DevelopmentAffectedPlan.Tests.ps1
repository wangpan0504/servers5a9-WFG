$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentAffectedPlan.ps1')
$count=0
function Assert($value,[string]$message){if(-not $value){throw $message};$script:count++}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('affected-plan-'+[guid]::NewGuid().ToString('N'))
$repository=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
[IO.Directory]::CreateDirectory($temp)|Out-Null
function Invoke-FixtureGit([string[]]$Arguments){ Invoke-AffectedPlanGit $temp $Arguments }
function Write-Fixture([string]$Path,[string]$Text){
    $full=Join-Path $temp $Path;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))|Out-Null
    [IO.File]::WriteAllText($full,$Text)
}
function Plan { Get-DevelopmentAffectedPlan $temp $script:base }
function Safe($plan){
    Assert ($plan.StrictRequired -eq $true -and $plan.ProofOfOmission -eq $false -and $plan.Execution -ceq 'not-run') '所有分支不授权省略或伪造执行。'
}
try {
    $null=Invoke-FixtureGit @('init','--quiet');$null=Invoke-FixtureGit @('config','user.name','Synthetic');$null=Invoke-FixtureGit @('config','user.email','synthetic@example.invalid')
    $registry='Server/DfoServer/SelfTests/Tcp/TcpRegressionTests.cs'
    Write-Fixture $registry ([IO.File]::ReadAllText((Join-Path $repository $registry)))
    $leaf='Server/DfoServer/SelfTests/FrameCrcTests.cs'
    Write-Fixture $leaf 'synthetic baseline, never compiled'
    Write-Fixture '.gitignore' "ignored/`n"
    $null=Invoke-FixtureGit @('add','.');$null=Invoke-FixtureGit @('commit','--quiet','-m','合成基线')
    $script:base=(Invoke-FixtureGit @('rev-parse','HEAD')).Trim()
    $clean=Plan;Safe $clean
    Assert ($clean.State -ceq 'advisory' -and $clean.Changed.Count -eq 0) '无改动仅建议，仍无覆盖证明。'
    if ($IsLinux) {
        Write-Fixture 'Server/DfoServer/SelfTests/framecrctests.cs' 'case variant is not reviewed path'
        $caseVariant=Plan;Safe $caseVariant
        Assert ($caseVariant.PriorityGroups.Count -eq 0) '大小写变体不借已审路径获得优先映射。'
        [IO.File]::Delete((Join-Path $temp 'Server/DfoServer/SelfTests/framecrctests.cs'))
    }
    Write-Fixture $leaf 'staged candidate';$null=Invoke-FixtureGit @('add',$leaf)
    Write-Fixture $leaf 'unstaged candidate'
    Write-Fixture 'new generator.cs' 'untracked executable input'
    $dirty=Plan;Safe $dirty
    Assert (@($dirty.Changed|Where-Object Stage -eq 'Staged').Count -eq 1) '真实staged纳入。'
    Assert (@($dirty.Changed|Where-Object Stage -eq 'Unstaged').Count -eq 1) '真实unstaged纳入。'
    Assert (@($dirty.Changed|Where-Object Stage -eq 'Untracked').Count -eq 1) '未跟踪文件含空格不丢。'
    Assert ($dirty.Identity.Worktree -cne $clean.Identity.Worktree -and $dirty.Identity.Index -cne $clean.Identity.Index) '工作区和索引身份分开捕获。'
    Assert ($dirty.PriorityGroups.Count -eq 1 -and $dirty.PriorityGroups[0].Group -ceq 'frame-crc-rules') '已审消费者仅提示优先组，不接受新内容闭包。'
    Write-Fixture $leaf 'synthetic baseline, never compiled'
    $reverted=Plan;Safe $reverted
    Assert (@($reverted.Changed|Where-Object Path -eq $leaf).Count -eq 2) '工作区改回HEAD仍保留索引差异两层，不能只看净diff。'
    Write-Fixture 'ignored/generated.cs' 'ignored actual input remains unknown'
    $ignored=Plan;Safe $ignored
    Assert ($ignored.Reasons -contains 'ignored-content-not-hashed' -and $ignored.Identity.IgnoredPaths.Count -eq 1) 'ignored没有伪称完整闭包。'
    Write-Fixture 'README.md' 'commands or generated input contract'
    $docs=Plan;Safe $docs
    Assert ($docs.Reasons -contains 'documentation-executable-contract-not-proven-independent') '文档不盲目豁免。'
    Write-Fixture 'Server/DfoServer/Network/Core/GameFrame.cs' 'reflection shared production source'
    Write-Fixture 'Directory.Build.targets' 'generator build input'
    $shared=Plan;Safe $shared
    Assert ($shared.Reasons -contains 'shared-build-registry-or-dependency-change') '共享/反射/构建仍严格范围。'
    Assert ($shared.PriorityGroups.Count -eq 1 -and $shared.PriorityGroups[0].Paths.Count -eq 2) '同组多路径合并依据，不建议重复运行同组。'
    $chat='Server/DfoServer/Game/Chat/ChatRateLimiter.cs'
    Write-Fixture $chat 'synthetic changed limiter'
    $chatPlan=Plan;Safe $chatPlan
    Assert (@($chatPlan.PriorityGroups | Where-Object Group -CEQ 'chat-rate-rules').Count -eq 1) '已审核限流调用关系只增加优先建议，不扩缓存或缩范围。'
    [IO.File]::Delete((Join-Path $temp $chat))
    Write-Fixture $registry 'unknown registry'
    $badRegistry=Plan;Safe $badRegistry
    Assert ($badRegistry.PriorityGroups.Count -eq 0 -and $badRegistry.Reasons -contains 'registry-unreviewed') '注册变动不沿旧组建议。'
    Write-Fixture $registry ([IO.File]::ReadAllText((Join-Path $repository $registry)))
    $null=Invoke-FixtureGit @('add','.');$null=Invoke-FixtureGit @('commit','--quiet','-m','合成新提交')
    $committed=Plan;Safe $committed
    Assert ($committed.Identity.BaseCommit -ceq $script:base -and $committed.Identity.HeadCommit -cne $script:base -and @($committed.Changed|Where-Object Stage -eq 'Committed').Count -gt 0) '精确base/head不暗换merge-base。'
    $null=Invoke-FixtureGit @('mv',$leaf,'renamed.cs')
    $renamed=Plan;Safe $renamed
    Assert (@($renamed.Changed|Where-Object { $_.Path -ceq $leaf }).Count -gt 0 -and @($renamed.Changed|Where-Object Path -eq 'renamed.cs').Count -gt 0) 'rename两端都纳入，不漏删除旧依赖。'
    $bad=Get-DevelopmentAffectedPlan $temp 'missing-base';Safe $bad
    Assert ($bad.State -ceq 'blocked' -and $null -eq $bad.Changed) '未知base不伪造空变更。'
    $bad=Get-DevelopmentAffectedPlan $temp '';Safe $bad
    Assert ($bad.State -ceq 'blocked') '不默认猜master。'
    if (-not $IsWindows) {
        $link=Join-Path $temp 'linked.cs'
        [IO.File]::CreateSymbolicLink($link,(Join-Path $temp 'README.md'))|Out-Null
        $linked=Plan;Safe $linked
        Assert ($linked.State -ceq 'blocked' -and $null -eq $linked.Changed) '符号链接输入未知不能输出成功计划。'
        [IO.File]::Delete($link)
    }
    $original=(Get-Command Get-ResultSourceManifest).ScriptBlock
    $script:scans=0
    function Get-ResultSourceManifest([string]$Root){
        $script:scans++
        if($script:scans -eq 2){Write-Fixture 'midscan.cs' 'changed during capture'}
        & $original $Root
    }
    $drift=Plan;Safe $drift
    Assert ($drift.State -ceq 'blocked' -and $drift.Reasons -contains 'inputs-changed-during-plan' -and $drift.PriorityGroups.Count -eq 0) '采集中漂移不发布旧建议。'
    Set-Item Function:Get-ResultSourceManifest $original
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
$entry=Join-Path $repository 'Tools/Select-AffectedTests.ps1'
$pwsh=(Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
foreach($invalid in @(@('-Run'),@('-Full'),@('-Paths','fake.cs'),@('-NoCache'),@('-IncludeUncommitted:$false'))) {
    $null=& $pwsh -NoProfile -File $entry -ConservativePlan -Base HEAD @invalid 2>&1
    Assert ($LASTEXITCODE -ne 0) '保守计划拒绝执行或绕过真实输入的旧参数。'
}
"PASS $count conservative plan assertions; synthetic Git and rejected CLI only, no build or server"
