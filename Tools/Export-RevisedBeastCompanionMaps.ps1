param(
    [Parameter(Mandatory)][string]$PvfPath,
    [Parameter(Mandatory)][string]$ServerAssembly,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputPath) { throw 'Output already exists' }
$serverPath = (Resolve-Path -LiteralPath $ServerAssembly).Path
[Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $serverPath) 'PvfLib.dll')) | Out-Null
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
$assembly = [Reflection.Assembly]::LoadFrom($serverPath)
$catalogType = $assembly.GetType('DfoServer.GameWorld.Dungeons.DungeonCatalog', $true)
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).Path)
try {
    $catalog = $catalogType.GetMethod('Load').Invoke($null, @($archive, $null, $null))
    $maps = @{}
    # 魔兽不在普通 Mazes 中，必须走正式专用解析器，覆盖三档实际遭遇房。
    foreach ($id in 5335..5339) {
        $definition = $catalogType.GetMethod('Get').Invoke($catalog, @([int]$id))
        if (!$definition.AllowsCompanionAi) { throw "Companions rejected: $id" }
        foreach ($difficulty in 0..2) {
            $maze = $catalogType.GetMethod('RevisedBeastMaze').Invoke($catalog, @([int]$id, [byte]$difficulty))
            if (@($maze.MapSpecification).Count -ne 2) { throw "Unexpected beast layout: $id/$difficulty" }
            foreach ($room in $maze.MapSpecification) {
                $path = $room.Path
                if (!$maps.ContainsKey($path)) { $maps[$path] = [Collections.Generic.List[string]]::new() }
                $maps[$path].Add("$id/$difficulty")
            }
        }
    }
    if ($maps.Count -ne 20) { throw "Unexpected reachable map count: $($maps.Count)" }
    $entries = @(foreach ($path in ($maps.Keys | Sort-Object)) {
        [ordered]@{
            path = $path
            expected_before_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($archive.ReadRaw($path))).ToLowerInvariant()
            owners = @($maps[$path].ToArray())
        }
    })
    [ordered]@{ schema = 'female-beast-map-approval-v1'; source_pvf = [IO.Path]::GetFullPath($PvfPath)
        server_assembly = $serverPath; entries = $entries } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    Write-Output 'BEAST_MAP_APPROVAL dungeons=5 difficulties=3 maps=20; PVF read-only'
}
finally { $archive.Dispose() }
