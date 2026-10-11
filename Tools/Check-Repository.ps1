param([string]$Repository=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $Repository).Path
$prefix=$root.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
$issues=[Collections.Generic.List[string]]::new()
$excludedDirectories=@('.git','.vs','.venv','__pycache__','build','bin','obj','artifacts','dist','runtime','node_modules')
$files=[Collections.Generic.List[string]]::new()
$directories=[Collections.Generic.Queue[string]]::new()
$directories.Enqueue($root)
while($directories.Count){
    foreach($item in Get-ChildItem -LiteralPath $directories.Dequeue() -Force){
        if($item.PSIsContainer){
            if($item.Name -in $excludedDirectories){continue}
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){
                $issues.Add("Linked directory is not a portable repository file: $($item.Name)");continue
            }
            $directories.Enqueue($item.FullName)
        }else{$files.Add($item.FullName.Substring($prefix.Length).Replace('\','/'))}
    }
}
$textExtensions=@('.md','.cs','.csproj','.sln','.ps1','.py','.cpp','.h','.asm','.def','.cmd','.bat','.sh','.json','.ini','.yml','.yaml','.txt','.sql')
function Check-Link([string]$Source,[int]$Line,[string]$Link){
    $target=$Link.Trim()
    if($target.StartsWith('<')){$target=$target.Substring(1,$target.IndexOf('>')-1)}
    else{$target=($target -split '\s+["'']',2)[0]}
    if(-not $target -or $target.StartsWith('#') -or $target -match '^(https?://|mailto:)'){return}
    if($target -match '^[A-Za-z][A-Za-z0-9+.-]*:' -or $target.StartsWith('/') -or $target.StartsWith('\')){
        $issues.Add("${Source}:${Line}: non-portable document link: $target");return
    }
    $target=[Uri]::UnescapeDataString(($target -split '[#?]',2)[0])
    if(-not $target){return}
    $path=[IO.Path]::GetFullPath((Join-Path (Split-Path (Join-Path $root $Source) -Parent) $target))
    if($path -ne $root -and -not $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){
        $issues.Add("${Source}:${Line}: document link leaves repository: $target");return
    }
    if(-not(Test-Path -LiteralPath $path)){
        $issues.Add("${Source}:${Line}: missing document link: $target");return
    }
    if($path -eq $root){return}
    $relative=$path.Substring($prefix.Length).Replace('\','/')
    if((Test-Path -LiteralPath $path -PathType Container) -and -not @($files|Where-Object {$_.StartsWith($relative+'/',[StringComparison]::OrdinalIgnoreCase)}).Count){
        $issues.Add("${Source}:${Line}: document link targets an excluded/generated directory: $target")
    }
    if($relative -notin $files -and (Test-Path -LiteralPath $path -PathType Leaf)){
        $issues.Add("${Source}:${Line}: document link targets an excluded/generated file: $target")
    }
}
foreach($file in ($files|Sort-Object -Unique)){
    $path=Join-Path $root $file
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){continue}
    if([IO.Path]::GetExtension($file) -notin $textExtensions -and $file -notin @('.gitignore','.gitattributes')){continue}
    $number=0
    foreach($line in [IO.File]::ReadLines($path)){
        $number++
        # Windows device names, registry keys and user-provided paths are not drive paths.
        if($file.Replace('\','/') -ne 'Tools/Check-Repository.ps1' -and
           ($line -match '(?i)(?<![A-Za-z0-9\\])[A-Z]:[/\\]' -or $line -match '(?i)(file://|/(Users|home)/[^/\s]+/)')){
            $issues.Add("${file}:${number}: machine absolute path")
        }
        if([IO.Path]::GetExtension($file) -eq '.md'){
            foreach($match in [regex]::Matches($line,'!?\[[^\]]*\]\(([^)]+)\)')){Check-Link $file $number $match.Groups[1].Value}
            if($line -match '^\s*\[[^\]]+\]:\s*(\S+)'){Check-Link $file $number $Matches[1]}
        }
    }
}
if($issues.Count){$issues|Write-Output;throw "Repository check failed: $($issues.Count) issue(s)"}
Write-Output "Repository check passed: $($files.Count) source/document files; no runtime assets required."
