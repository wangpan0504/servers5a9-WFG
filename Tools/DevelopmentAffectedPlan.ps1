# 仅提供可追溯的优先顺序；不证明任意新代码闭包，不授权省略或复用结果。
. (Join-Path $PSScriptRoot 'DevelopmentResumePlan.ps1')
function Invoke-AffectedPlanGit([string]$Repository,[string[]]$Arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source)
    $start.UseShellExecute=$false; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach($arg in (@('--no-optional-locks','-c','core.fsmonitor=false','-C',$Repository)+$Arguments)){ $start.ArgumentList.Add($arg) }
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
    try {
        if(-not $process.Start()){throw 'git-start-failed'}
        $output=$process.StandardOutput.ReadToEndAsync(); $errorText=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(30000)){ $process.Kill($true); $process.WaitForExit(); throw 'git-read-timeout' }
        $text=$output.GetAwaiter().GetResult(); $null=$errorText.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0){throw 'git-read-failed'}
        return $text
    } finally { $process.Dispose() }
}
function Get-AffectedPlanPaths([string]$Text) { @($Text.Split([char]0) | Where-Object { $_.Length -gt 0 }) }
function Get-AffectedPlanGitState([string]$Repository) {
    @{
        Head=(Invoke-AffectedPlanGit $Repository @('rev-parse','--verify','HEAD^{commit}')).Trim()
        Index=Get-ResultDigest (Invoke-AffectedPlanGit $Repository @('ls-files','--stage','-z'))
        Status=Get-ResultDigest (Invoke-AffectedPlanGit $Repository @('status','--porcelain=v1','-z','--untracked-files=all','--ignore-submodules=none'))
    }
}
function Get-DevelopmentAffectedPlan([string]$Repository,[string]$Base) {
    $plan=[ordered]@{SchemaVersion=1;State='blocked';Execution='not-run';StrictRequired=$true;ProofOfOmission=$false;
        StrictRecommendation='Complete plus separately required targeted/platform scopes; missing inputs remain blocked';
        BaseRequested=$Base;Identity=$null;Changed=@();PriorityGroups=@();Reasons=@();
        InputBoundary='tracked and nonignored worktree content plus Git index/status; ignored contents and external/build inputs unproven';
        Note='仅建议优先顺序，不是覆盖证明、成功收据或省略授权；旧默认选择及Complete不变。'}
    try {
        if([string]::IsNullOrWhiteSpace($Base) -or $Base.StartsWith('-')){throw 'explicit-base-required'}
        $Repository=[IO.Path]::GetFullPath($Repository)
        $top=(Invoke-AffectedPlanGit $Repository @('rev-parse','--show-toplevel')).Trim()
        if([IO.Path]::GetFullPath($top) -cne $Repository.TrimEnd([IO.Path]::DirectorySeparatorChar)){throw 'repository-root-required'}
        $baseCommit=(Invoke-AffectedPlanGit $Repository @('rev-parse','--verify','--end-of-options',($Base+'^{commit}'))).Trim()
        $before=Get-AffectedPlanGitState $Repository
        $source=Get-ResultSourceManifest $Repository
        $stages=[ordered]@{
            Committed=@('diff','--no-ext-diff','--no-textconv','--no-renames','--name-only','-z',$baseCommit,$before.Head,'--')
            Staged=@('diff','--cached','--no-ext-diff','--no-textconv','--no-renames','--name-only','-z',$before.Head,'--')
            Unstaged=@('diff','--no-ext-diff','--no-textconv','--no-renames','--name-only','-z','--')
            Untracked=@('ls-files','--others','--exclude-standard','-z')
        }
        $rows=[Collections.Generic.List[object]]::new()
        foreach($stage in $stages.Keys){
            foreach($path in @(Get-AffectedPlanPaths (Invoke-AffectedPlanGit $Repository $stages[$stage]))){
                $rows.Add(@{Path=$path;Stage=$stage})
            }
        }
        $ignored=@(Get-AffectedPlanPaths (Invoke-AffectedPlanGit $Repository @('ls-files','--others','--ignored','--exclude-standard','-z')))
        $reasons=[Collections.Generic.List[string]]::new()
        $reasons.Add('new-content-dependency-closure-unproven')
        if($ignored.Count){$reasons.Add('ignored-content-not-hashed')}
        # 精确07f4审核时消费者是core、收据合同和定向包装；仅提示先跑，不推断新内容等价。
        $focus=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        $focus.Add('Server/DfoServer/SelfTests/EquipmentPowerScoreTests.cs','equipment-power-rules')
        $focus.Add('Server/DfoServer/Game/Characters/EquipmentPowerScore.cs','equipment-power-rules')
        $focus.Add('Server/DfoServer/SelfTests/FrameCrcTests.cs','frame-crc-rules')
        $focus.Add('Server/DfoServer/Network/Core/GameFrame.cs','frame-crc-rules')
        $catalog=@(); try { $catalog=@(Get-DevelopmentGroupCatalog $Repository) } catch { $reasons.Add('registry-unreviewed') }
        $priorities=[Collections.Generic.List[object]]::new()
        foreach($path in @($rows | ForEach-Object Path | Sort-Object -Unique -CaseSensitive)){
            if($focus.ContainsKey($path) -and @($catalog | Where-Object { $_.Name -ceq $focus[$path] -and $_.Kind -ceq 'targeted' }).Count -eq 1){
                $existing=@($priorities | Where-Object { $_.Group -ceq $focus[$path] })
                if($existing.Count){$existing[0].Paths+=@($path)}
                else {$priorities.Add(@{Group=$focus[$path];Paths=@($path);Basis='reviewed-07f4-callers-core-receipt-targeted; priority-only';RequiresFresh=$true})}
            }
            if($path -match '(?i)(^|/)(Program|SelfTestRunner|TestCheck|Tcp\w*)\.cs$|/(Application|Infrastructure|Network/Core|Persistence)/|^Tools/|\.(csproj|props|targets|sln|slnx|json|config|sql)$'){
                $reasons.Add('shared-build-registry-or-dependency-change')
            } elseif($path -match '(?i)\.md$|^Docs/') { $reasons.Add('documentation-executable-contract-not-proven-independent') }
            else { $reasons.Add('unknown-or-new-code-change') }
        }
        # 前后检查仅发现可见漂移，不声称是受控不可变快照；索引修改后改回也不能作为覆盖证明。
        $after=Get-AffectedPlanGitState $Repository
        if((ConvertTo-ResultCanonical $before) -cne (ConvertTo-ResultCanonical $after) -or
            (Get-ResultSourceManifest $Repository).Key -cne $source.Key -or
            (Invoke-AffectedPlanGit $Repository @('rev-parse','--verify','--end-of-options',($Base+'^{commit}'))).Trim() -cne $baseCommit){throw 'inputs-changed-during-plan'}
        $plan.Identity=@{BaseCommit=$baseCommit;HeadCommit=$before.Head;
            BaseTree=(Invoke-AffectedPlanGit $Repository @('rev-parse',($baseCommit+'^{tree}'))).Trim();
            HeadTree=(Invoke-AffectedPlanGit $Repository @('rev-parse',($before.Head+'^{tree}'))).Trim();
            Worktree=$source.Key;Index=$before.Index;Status=$before.Status;IgnoredPaths=$ignored}
        $plan.Changed=@($rows); $plan.PriorityGroups=@($priorities)
        if(-not $rows.Count){$reasons.Add('no-visible-change-is-not-coverage-evidence')}
        $plan.Reasons=@($reasons | Sort-Object -Unique); $plan.State='advisory'
    } catch {
        # 读取失败不能伪造空变更；保持blocked和严格建议，不输出可能已漂移的优先组。
        $plan.Reasons=@('input-read-or-validation-failed',[string]$_.Exception.Message)
        $plan.Changed=$null; $plan.PriorityGroups=@(); $plan.Identity=$null
    }
    return $plan
}
