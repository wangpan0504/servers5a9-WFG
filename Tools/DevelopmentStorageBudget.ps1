# 只读逻辑容量观测，不获取/修改锁，不删除证据，也不提供磁盘硬配额。
function Get-DevelopmentStorageInventory([string]$Store, [string[]]$Repositories, [ValidateRange(1,1000000)][int]$MaxEntries=100000) {
    $rows=[Collections.Generic.List[object]]::new()
    $issues=[Collections.Generic.List[object]]::new()
    $roots=@(@{Kind='store';Path=[IO.Path]::GetFullPath($Store)})
    foreach($repository in $Repositories){$roots+=@{Kind='artifacts';Path=[IO.Path]::GetFullPath((Join-Path $repository 'build/development'))}}
    $visited=0; $truncated=$false
    foreach($root in $roots){
        $row=@{Kind=$root.Kind;Path=$root.Path;LogicalBytes=[long]0;Files=[long]0;Locks=[long]0;Activity='unknown';State='observed';Categories=@{}}
        $rows.Add($row)
        # 逐级核对祖先，不能从链接根进入外部目录；普通硬链接按路径累计。
        try {
            $ancestor=$root.Path
            while($ancestor){
                $item=$null
                try {$item=Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop}
                catch {if($_.CategoryInfo.Category -ne [Management.Automation.ErrorCategory]::ObjectNotFound){throw}}
                if($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'linked-path'}
                $parent=[IO.Path]::GetDirectoryName($ancestor);if($parent -eq $ancestor){break};$ancestor=$parent
            }
            $first=Get-Item -LiteralPath $root.Path -Force -ErrorAction Stop
            if(-not $first.PSIsContainer){throw 'not-directory'}
        } catch {
            if($_.CategoryInfo.Category -eq [Management.Automation.ErrorCategory]::ObjectNotFound){$row.State='absent';continue}
            $row.State='unknown';$issues.Add(@{Path=$root.Path;Reason='root-not-verifiable'});continue
        }
        $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($root.Path)
        while($pending.Count -and -not $truncated){
            $directory=$pending.Pop()
            try {
                # 流式枚举，达到总条目上限后不继续展开；未知不当作零字节。
                foreach($path in [IO.Directory]::EnumerateFileSystemEntries($directory)){
                    if(++$visited -gt $MaxEntries){$truncated=$true;$row.State='unknown';$issues.Add(@{Path=$directory;Reason='entry-limit'});break}
                    $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
                    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){$row.State='unknown';$issues.Add(@{Path=$path;Reason='link-not-followed'});continue}
                    if($item.PSIsContainer){$pending.Push($path);continue}
                    if((($IsLinux -or $IsMacOS) -and [string]$item.UnixStat.ItemType -ne 'File') -or ($item.Attributes -band [IO.FileAttributes]::Device)){
                        $row.State='unknown';$issues.Add(@{Path=$path;Reason='not-regular-file'});continue
                    }
                    $relative=[IO.Path]::GetRelativePath($root.Path,$path)
                    $category=if($root.Kind -eq 'artifacts'){'stable-artifacts'}else{($relative -split '[/\\]')[0]}
                    if($category -notin @('runs','records','locks','stable-artifacts')){$category='other'}
                    if(-not $row.Categories.ContainsKey($category)){$row.Categories[$category]=@{LogicalBytes=[long]0;Files=[long]0}}
                    $row.Files++;$row.LogicalBytes+=$item.Length
                    $row.Categories[$category].Files++;$row.Categories[$category].LogicalBytes+=$item.Length
                    if($item.Extension -eq '.lock'){$row.Locks++}
                    # 只检查小型结果JSON能否解析，不将格式有效解释成成功或可回收。
                    if($root.Kind -eq 'store' -and $item.Extension -eq '.json'){
                        try {
                            if($item.Length -gt 1048576){throw 'oversized-json'}
                            $parsed=Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                            if($null -eq $parsed){throw 'empty-json'}
                        } catch {$row.State='unknown';$issues.Add(@{Path=$path;Reason='evidence-not-verifiable'})}
                    }
                }
            } catch {$row.State='unknown';$issues.Add(@{Path=$directory;Reason='enumeration-not-verifiable'})}
        }
        if($truncated){$row.State='unknown'}
    }
    $bytes=[long]0;$files=[long]0;foreach($row in $rows){$bytes+=$row.LogicalBytes;$files+=$row.Files}
    @{SchemaVersion=1;State=$(if($issues.Count){'unknown'}else{'observed'});LogicalBytes=$bytes;Files=$files;Roots=@($rows.ToArray());Issues=@($issues.ToArray());Activity='unknown';Measurement='logical-by-path-not-physical';AtomicSnapshot=$false;Eviction='disabled'}
}
function Test-DevelopmentStorageBudget($Inventory, [ValidateRange(0,[long]::MaxValue)][long]$MaxBytes=0, [ValidateRange(0,[long]::MaxValue)][long]$MaxFiles=0) {
    $reason=if(-not $MaxBytes -and -not $MaxFiles){'disabled'}elseif($Inventory.State -ne 'observed'){'storage-unknown'}elseif($MaxBytes -and $Inventory.LogicalBytes -ge $MaxBytes){'storage-byte-threshold'}elseif($MaxFiles -and $Inventory.Files -ge $MaxFiles){'storage-file-threshold'}else{'below-threshold'}
    @{State=$(if($reason -in @('disabled','below-threshold')){'allowed'}else{'blocked'});Reason=$reason;MaxBytes=$MaxBytes;MaxFiles=$MaxFiles;Inventory=$Inventory;Guarantee='preflight-only-no-reservation'}
}
