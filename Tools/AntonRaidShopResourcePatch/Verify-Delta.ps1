#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$CandidatePath,
    [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [switch]$SkipRawAudit
)
$ErrorActionPreference = 'Stop'
throw 'Legacy seven-category layout retired: it violates native callback/guide contracts. Use Prepare-Compatibility.ps1, Pack-Compatibility.ps1, Verify-Compatibility.ps1 and paired RaidShopCategory client patch.'
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).Path
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -cne 'anton-same-version-shop-delta-v1') { throw 'Unexpected delta schema.' }
if (Test-Path -LiteralPath $ReceiptPath) { throw 'Preserve the previous receipt.' }
function Hash-Raw([byte[]]$Raw) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Raw)).ToLowerInvariant() }
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Sell-Info([string]$Text) { [regex]::Match($Text, '\[sell info\](.*?)\[/sell info\]', 'Singleline').Value }
$checks = 0
$source = [PvfLib.PvfArchive]::Open($manifest.source)
$candidate = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $CandidatePath).Path)
try {
    $file = Get-Item -LiteralPath $manifest.source
    Check ($file.Length -eq $manifest.source_length -and $file.LastWriteTimeUtc.Ticks -eq $manifest.source_utc_ticks) 'Source metadata changed.'
    Check ($source.FileCount -eq $candidate.FileCount) 'File count changed.'
    $expectedPaths = @('itemshop/raidshop_anton.shp','etc/additionalraidreward.etc','ui/raid/shop/raidshopwindowver2.xui')
    Check (@($manifest.entries).Count -eq 3 -and @($manifest.entries.path | Sort-Object -Unique).Count -eq 3) 'Unexpected replacement count.'
    foreach ($entry in $manifest.entries) {
        Check ($entry.path -cin $expectedPaths) 'Unexpected target.'
        Check ((Hash-Raw $source.ReadRaw($entry.path)) -ceq $entry.before_sha256) 'Source raw hash mismatch.'
        Check ((Hash-Raw $candidate.ReadRaw($entry.path)) -ceq $entry.after_sha256) 'Candidate raw hash mismatch.'
    }
    $shop = $candidate.ReadText($expectedPaths[0])
    $origin = $source.ReadText($manifest.stock_source)
    Check ((Sell-Info $shop) -ceq (Sell-Info $origin)) 'Restored products must exactly match same-version Wuun sell-info.'
    Check ($shop -match '\[type\]\s*`\[etc shop\]`' -and $shop -notmatch '\[NPC\]') 'Raid shop type/owner contract changed.'
    Check ((Hash-Raw $candidate.ReadRaw($manifest.stock_source)) -ceq $manifest.stock_source_sha256) 'Existing Wuun shop changed.'
    Check ((Hash-Raw $candidate.ReadRaw($manifest.ui_source)) -ceq $manifest.ui_source_sha256) 'Retained UI source changed.'
    $directory = $candidate.ReadText($expectedPaths[1])
    $records = @([regex]::Matches($directory, '\[shop\](.*?)\[/shop\]', 'Singleline') | ForEach-Object {
        $body = $_.Groups[1].Value
        [pscustomobject]@{
            Category = [int][regex]::Match($body,'\[category\]\s*(\d+)').Groups[1].Value
            Shop = [int][regex]::Match($body,'\[item shop\]\s*(\d+)').Groups[1].Value
            Npc = [int][regex]::Match($body,'\[npc index\]\s*(\d+)').Groups[1].Value
        }
    })
    Check ($records.Count -eq 6 -and @($records.Category | Sort-Object -Unique).Count -eq 6) 'Duplicate or missing shop directory categories.'
    foreach ($pair in @(@(1,259),@(2,276),@(3,257),@(4,258),@(5,274),@(6,400000052))) {
        Check (@($records | Where-Object { $_.Category -eq $pair[0] -and $_.Shop -eq $pair[1] }).Count -eq 1) 'Wrong seven-category shop mapping.'
    }
    Check (@($records | Where-Object { $_.Category -eq 3 -and $_.Npc -eq 328 }).Count -eq 1) 'Anton must use retained Wuun identity.'
    $originalDirectory = $source.ReadText($expectedPaths[1])
    Check ($directory.Substring($directory.IndexOf('[specific]', [StringComparison]::Ordinal)) -ceq $originalDirectory.Substring($originalDirectory.IndexOf('[specific]', [StringComparison]::Ordinal))) 'Luke special rewards changed.'
    $ui = $candidate.ReadText($expectedPaths[2])
    $oldUi = $source.ReadText($expectedPaths[2])
    Check ($ui -match 'VAR_201="INTVECTOR:12, 2, 7, 4"') 'UI category count must include all seven slots.'
    foreach ($pair in @(@(3,'安徒恩'),@(4,'卢克'),@(5,'超时空漩涡'),@(6,'普雷·伊希斯'))) {
        $id = $pair[0]
        Check (@([regex]::Matches($ui, '<CNUIControlImage\b[^>]*\bID="STR:shop_category_' + $id + '"')).Count -eq 1) 'Duplicate or missing clickable category.'
        Check ($ui -match ('ID="STR:shop_category_' + $id + '_text"[^>]*string="STR:' + [regex]::Escape($pair[1]) + '"')) 'Wrong category label.'
        foreach ($suffix in @('_effect','_over')) {
            Check (@([regex]::Matches($ui, '<CNUIControlImage\b[^>]*\bID="STR:shop_category_' + $id + $suffix + '"')).Count -eq 1) 'Missing category state.'
        }
    }
    foreach ($id in @(0,1,2)) {
        foreach ($suffix in @('','_effect','_over')) {
            $pattern = '<CNUIControlImage\b[^>]*\bID="STR:shop_category_' + $id + $suffix + '"[^>]*>'
            Check ([regex]::Match($ui,$pattern).Value -ceq [regex]::Match($oldUi,$pattern).Value) 'Existing trophy/coin controls changed.'
        }
    }
    $rightStart = '<IControl ID="STR:shop_right_panel"'
    $oldRight = $oldUi.IndexOf($rightStart, [StringComparison]::Ordinal)
    $newRight = $ui.IndexOf($rightStart, [StringComparison]::Ordinal)
    Check ($oldRight -ge 0 -and $newRight -ge 0 -and $oldUi.Substring($oldRight) -ceq $ui.Substring($newRight)) 'Noncategory UI tail changed.'
    # 逆转允许的四类变化后必须逐字符回到原UI，避免遗漏不在右栏内的隐藏控件和材质。
    $reverse = [regex]::Replace($ui, '<CNUIControlImage\b(?=[^>]*\bID="STR:shop_category_3")([^>]*)>.*?</CNUIControlImage>\r\n                ', '', 'Singleline')
    foreach ($suffix in @('_effect','_over')) {
        $reverse = [regex]::Replace($reverse, '<CNUIControlImage\b[^>]*\bID="STR:shop_category_3' + $suffix + '"[^>]*/>\r\n                ', '')
    }
    $reverse = [regex]::Replace($reverse, '<CNUIControlImage\b[^>]*\bID="STR:shop_category_[4-6](?:_effect|_over)?"[^>]*>', {
        param($match)
        [regex]::Replace($match.Value, 'Pos="VECTOR:6, (100|152|204)"', {
            param($position)
            'Pos="VECTOR:6, ' + ([int]$position.Groups[1].Value - 52) + '"'
        })
    })
    $reverse = [regex]::Replace($reverse, 'ID="STR:shop_category_([4-6])([^"0-9]*)"', {
        param($match)
        'ID="STR:shop_category_' + ([int]$match.Groups[1].Value - 1) + $match.Groups[2].Value + '"'
    })
    $reverse = $reverse.Replace('VAR_201="INTVECTOR:12, 2, 7, 4"', 'VAR_201="INTVECTOR:12, 2, 6, 4"')
    Check ($reverse -ceq $oldUi) 'UI differs outside the explicitly allowed insertion, indices, positions and category count.'

    # 商品模板的原价格和限购声明逐项保留；不把新表或猜测兑换成本写进资源。
    $items = @{}
    foreach ($row in [regex]::Matches($source.ReadText('stackable/stackable.lst'), '(\d+)\s+`([^`]+)`')) { $items[[int]$row.Groups[1].Value] = 'stackable/' + $row.Groups[2].Value }
    $products = @(foreach ($id in $manifest.products) {
        Check ($items.ContainsKey([int]$id)) 'Unregistered restored product.'
        $path = $items[[int]$id]
        $raw = $source.ReadRaw($path)
        Check ((Hash-Raw $raw) -ceq (Hash-Raw $candidate.ReadRaw($path))) 'Product template changed.'
        $text = $candidate.ReadText($path)
        Check ($text -match '\[need material\]') 'Restored product must have its existing material cost.'
        [ordered]@{ id=$id; path=$path; name=[regex]::Match($text,'\[name\]\s*`([^`]+)`').Groups[1].Value; raw_sha256=(Hash-Raw $raw); cost=[regex]::Match($text,'\[need material\](.*?)\[/need material\]','Singleline').Groups[1].Value.Trim() }
    })
    $audit = $null
    if (-not $SkipRawAudit) {
        # 现有读取器按压缩组扫描；回调只比较raw，不把解码格式或元数据相等当内容相等。
        Add-Type -ReferencedAssemblies (Resolve-Path -LiteralPath $PvfLibPath).Path -TypeDefinition @'
using System;
using PvfLib;
public static class AntonShopRawAudit
{
    public static long[] Compare(PvfArchive before, PvfArchive after)
    {
        long compared = 0, unchanged = 0, changed = 0;
        before.ScanRawFiles(before.Files, (file, read) =>
        {
            var other = after.FindFile(file.Path);
            if (other == null || other.DataType != file.DataType)
                throw new InvalidOperationException("Changed file identity: " + file.Path);
            byte[] left = read(), right = after.ReadRaw(file.Path);
            compared++;
            bool same = left.Length == right.Length;
            for (int i = 0; same && i < left.Length; i++) same = left[i] == right[i];
            if (same) unchanged++;
            else if (file.Path == "itemshop/raidshop_anton.shp" || file.Path == "etc/additionalraidreward.etc" ||
                     file.Path == "ui/raid/shop/raidshopwindowver2.xui") changed++;
            else throw new InvalidOperationException("Unexpected raw change: " + file.Path);
        });
        return new[] { compared, unchanged, changed };
    }
}
'@
        $state = [AntonShopRawAudit]::Compare($source, $candidate)
        Check ($state[0] -eq $source.FileCount -and $state[2] -eq 3) 'Full raw audit did not match exactly three changes.'
        $audit = [ordered]@{ compared=$state[0]; unchanged=$state[1]; changed=$expectedPaths; unexpected=0 }
    }
    [ordered]@{ status='passed'; checks=$checks; manifest=(Resolve-Path $ManifestPath).Path; candidate=(Resolve-Path $CandidatePath).Path; candidate_length=(Get-Item $CandidatePath).Length; source=$manifest.source; categories=$records; products=$products; raw_audit=$audit; raw_audit_skipped=[bool]$SkipRawAudit; whole_pvf_hash=$false; scope='Resource contracts and raw preservation only; no server TCP or client QA claim.' } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReceiptPath -Encoding utf8
    Write-Output "PASS resource contracts checks=$checks full_raw=$(-not $SkipRawAudit)"
} finally { $source.Dispose(); $candidate.Dispose() }
