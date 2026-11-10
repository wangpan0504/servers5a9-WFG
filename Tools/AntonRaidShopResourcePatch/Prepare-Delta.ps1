#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PvfPath,
    [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
throw 'Legacy seven-category layout retired: it violates native callback/guide contracts. Use Prepare-Compatibility.ps1, Pack-Compatibility.ps1, Verify-Compatibility.ps1 and paired RaidShopCategory client patch.'
$sourcePath = (Resolve-Path -LiteralPath $PvfPath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new delta directory.' }
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).Path
function Hash-Raw([byte[]]$Raw) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Raw)).ToLowerInvariant() }
function Tag($Token, [string]$Name) { $Token.Type -eq 3 -and $archive.ResolveString($Token.Value) -ceq "[$Name]" }
function Section-Range($Tokens, [string]$Name) {
    $starts = @(for ($i = 0; $i -lt $Tokens.Count; $i++) { if (Tag $Tokens[$i] $Name) { $i } })
    if ($starts.Count -ne 1) { throw "Expected one section: $Name" }
    $start = $starts[0]
    for ($i = $start + 1; $i -lt $Tokens.Count; $i++) {
        if (Tag $Tokens[$i] "/$Name") { return @($start, $i) }
    }
    throw "Unterminated section: $Name"
}
function Set-Field($Tokens, [string]$Name, [int]$Kind, [int]$Value) {
    $indices = @(for ($i = 0; $i -lt $Tokens.Count; $i++) { if (Tag $Tokens[$i] $Name) { $i + 1 } })
    if ($indices.Count -ne 1 -or $Tokens[$indices[0]].Type -ne $Kind) { throw "Unexpected field: $Name" }
    $Tokens[$indices[0]] = [PvfLib.PvfToken]::new([byte]$Kind, $Value)
}
function Encode-Tokens($Tokens) {
    $raw = [byte[]]::new($Tokens.Count * 5)
    for ($i = 0; $i -lt $Tokens.Count; $i++) {
        $raw[$i * 5] = $Tokens[$i].Type
        [BitConverter]::GetBytes([int]$Tokens[$i].Value).CopyTo($raw, $i * 5 + 1)
    }
    return ,$raw
}
$archive = [PvfLib.PvfArchive]::Open($sourcePath)
try {
    $paths = @('itemshop/raidshop_anton.shp', 'etc/additionalraidreward.etc', 'ui/raid/shop/raidshopwindowver2.xui')
    $shop = $archive.ReadTokens($paths[0])
    $stock = $archive.ReadTokens('itemshop/wuun_normal.shp')
    $old = Section-Range $shop 'sell info'
    $origin = Section-Range $stock 'sell info'
    $emptyLists = @($shop | Where-Object Type -eq 0)
    if ($emptyLists.Count) { throw 'Anton stock is no longer empty; review the newer cumulative source.' }
    $products = @()
    for ($i = $origin[0]; $i -le $origin[1]; $i++) {
        if (Tag $stock[$i] 'item list') {
            for ($j = $i + 1; $j -lt $stock.Count -and $stock[$j].Type -eq 0; $j++) { $products += $stock[$j].Value }
        }
    }
    if ($products.Count -ne 33 -or @($products | Sort-Object -Unique).Count -ne 33) { throw 'Unexpected same-version Wuun catalog; review rather than invent stock.' }
    $newShop = @($shop[0..($old[0] - 1)]) + @($stock[$origin[0]..$origin[1]]) + @($shop[($old[1] + 1)..($shop.Count - 1)])

    $reward = $archive.ReadTokens($paths[1])
    $shopsRange = Section-Range $reward 'shop list'
    $blocks = @()
    for ($i = $shopsRange[0] + 1; $i -lt $shopsRange[1]; $i++) {
        if (-not (Tag $reward[$i] 'shop')) { throw 'Unexpected shop-list token.' }
        $start = $i
        while ($i -lt $shopsRange[1] -and -not (Tag $reward[$i] '/shop')) { $i++ }
        if ($i -ge $shopsRange[1]) { throw 'Unterminated shop record.' }
        $block = @($reward[$start..$i])
        $fields = @{}
        for ($j = 0; $j -lt $block.Count - 1; $j++) {
            if ($block[$j].Type -eq 3) { $fields[$archive.ResolveString($block[$j].Value)] = $block[$j + 1] }
        }
        $blocks += ,([pscustomobject]@{ Shop = $fields['[item shop]'].Value; Tokens = $block })
    }
    if ($blocks.Count -ne 6 -or @($blocks.Shop | Sort-Object -Unique).Count -ne 6) { throw 'Unexpected shop directory.' }
    $anton = @($blocks | Where-Object Shop -eq 101298)
    if ($anton.Count -ne 1) { throw 'Expected the broken Anton entry exactly once.' }
    $name = 0
    if (-not $archive.TryFindStringValue('安徒恩', [ref]$name)) { throw 'Missing same-version Anton name; do not add a new string pool.' }
    Set-Field $anton[0].Tokens 'category' 0 3
    Set-Field $anton[0].Tokens 'background name' 6 $name
    Set-Field $anton[0].Tokens 'item shop' 0 257
    Set-Field $anton[0].Tokens 'npc index' 0 328
    $rebuilt = @($reward[0..$shopsRange[0]])
    foreach ($record in $blocks) {
        if ($record.Shop -eq 101298) { continue }
        if ($record.Shop -eq 258) { $rebuilt += $anton[0].Tokens; Set-Field $record.Tokens 'category' 0 4 }
        elseif ($record.Shop -eq 274) { Set-Field $record.Tokens 'category' 0 5 }
        elseif ($record.Shop -eq 400000052) { Set-Field $record.Tokens 'category' 0 6 }
        elseif ($record.Shop -notin @(259,276)) { throw 'Unknown existing category; review the source.' }
        $rebuilt += $record.Tokens
    }
    $rebuilt += @($reward[$shopsRange[1]..($reward.Count - 1)])
    $uiSource = 'ui/raid/shop/raidshopwindowver2_prey.xui'
    $ui = $archive.ReadText($uiSource)
    foreach ($pair in @(@(3,'安徒恩'),@(4,'卢克'),@(5,'超时空漩涡'),@(6,'普雷·伊希斯'))) {
        if ($ui -notmatch ('ID="STR:shop_category_' + $pair[0] + '_text"[^>]*string="STR:' + [regex]::Escape($pair[1]) + '"')) { throw 'Unexpected retained native seven-category layout.' }
    }
    if ($ui -notmatch 'VAR_201="INTVECTOR:12, 2, 7, 4"') { throw 'Unexpected native category count.' }
    # 仅借用保留布局的安徒恩标签和图标；现行界面的隐藏页、材质及其他控件保持。
    $currentUi = $archive.ReadText($paths[2])
    if ($currentUi -notmatch 'VAR_201="INTVECTOR:12, 2, 6, 4"') { throw 'Unexpected current category count.' }
    $imagePattern = '<CNUIControlImage\b(?=[^>]*\bID="STR:shop_category_3")([^>]*)>.*?</CNUIControlImage>'
    $main = [regex]::Match($currentUi, $imagePattern, 'Singleline').Value
    $nativeMain = [regex]::Match($ui, $imagePattern, 'Singleline').Value
    if (-not $main -or -not $nativeMain -or $main -notmatch 'string="STR:卢克"' -or $nativeMain -notmatch 'imageIndex="NUM:1"') { throw 'Missing native Anton control or current Luke control.' }
    $main = $main.Replace('string="STR:卢克"', 'string="STR:安徒恩"')
    $main = [regex]::Replace($main, '\A(<CNUIControlImage[^>]*imageIndex=")NUM:2(")', '${1}NUM:1$2')
    $extra = @{}
    foreach ($suffix in @('_effect','_over')) {
        $control = [regex]::Match($currentUi, '<CNUIControlImage\b[^>]*\bID="STR:shop_category_3' + $suffix + '"[^>]*/>').Value
        if (-not $control) { throw 'Missing current category state control.' }
        $extra[$suffix] = $control
    }
    $newUi = [regex]::Replace($currentUi, 'ID="STR:shop_category_([3-5])([^"0-9]*)"', {
        param($match)
        'ID="STR:shop_category_' + ([int]$match.Groups[1].Value + 1) + $match.Groups[2].Value + '"'
    })
    $newUi = [regex]::Replace($newUi, '<CNUIControlImage\b[^>]*\bID="STR:shop_category_[4-6](?:_effect|_over)?"[^>]*>', {
        param($match)
        [regex]::Replace($match.Value, 'Pos="VECTOR:6, (48|100|152)"', {
            param($position)
            'Pos="VECTOR:6, ' + ([int]$position.Groups[1].Value + 52) + '"'
        })
    })
    $newUi = $newUi.Replace('VAR_201="INTVECTOR:12, 2, 6, 4"', 'VAR_201="INTVECTOR:12, 2, 7, 4"')
    $newUi = [regex]::Replace($newUi, '(?=<CNUIControlImage\b[^>]*\bID="STR:shop_category_4")', [Text.RegularExpressions.MatchEvaluator]{ param($match) $main + "`r`n                " })
    foreach ($suffix in @('_effect','_over')) {
        $copy = $extra[$suffix]
        $newUi = [regex]::Replace($newUi, '(?=<CNUIControlImage\b[^>]*\bID="STR:shop_category_4' + $suffix + '")', [Text.RegularExpressions.MatchEvaluator]{ param($match) $copy + "`r`n                " })
    }
    $raws = @((Encode-Tokens $newShop), (Encode-Tokens $rebuilt), [Text.Encoding]::Unicode.GetBytes($newUi))
    New-Item -ItemType Directory -Path $output | Out-Null
    $entries = @(for ($i = 0; $i -lt $paths.Count; $i++) {
        $rawFile = Join-Path $output ($i.ToString() + '.raw')
        [IO.File]::WriteAllBytes($rawFile, $raws[$i])
        [ordered]@{ path=$paths[$i]; raw_file=$rawFile; before_sha256=(Hash-Raw $archive.ReadRaw($paths[$i])); after_sha256=(Hash-Raw $raws[$i]) }
    })
    $file = Get-Item -LiteralPath $sourcePath
    $manifest = [ordered]@{
        schema='anton-same-version-shop-delta-v1'; source=$sourcePath; source_length=$file.Length; source_utc_ticks=$file.LastWriteTimeUtc.Ticks
        entries=$entries; stock_source='itemshop/wuun_normal.shp'; stock_source_sha256=(Hash-Raw $archive.ReadRaw('itemshop/wuun_normal.shp'))
        ui_source=$uiSource; ui_source_sha256=(Hash-Raw $archive.ReadRaw($uiSource)); products=$products
        scope='Same-version retained Wuun catalog and native Anton UI controls; existing hidden pages/materials/noncategory controls preserved. No historical stock/price claim, item-template edit, new string pool, schema or runtime change.'
    }
    $manifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'delta.json') -Encoding utf8
    Write-Output ('PREPARED ' + (Join-Path $output 'delta.json'))
} finally { $archive.Dispose() }
