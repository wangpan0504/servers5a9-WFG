#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PvfPath,
    [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$ReceiptPath
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).Path
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).Path)
$failures = [Collections.Generic.List[string]]::new()
$checks = 0
function Check([bool]$Condition,[string]$Message) {
    $script:checks++
    if (-not $Condition) { $failures.Add($Message) }
}
try {
    $ui = [xml]$archive.ReadText('ui/raid/shop/raidshopwindowver2.xui').TrimEnd([char]0)
    Check ($ui.ROOT.VARIABLE.VAR_201 -ceq 'INTVECTOR:12, 2, 7, 4') 'Native category count must be seven.'
    foreach ($id in 0..6) {
        foreach ($suffix in @('','_effect','_over','_text')) {
            Check ($ui.SelectNodes('//*[@ID="STR:shop_category_' + $id + $suffix + '"]').Count -eq 1) "Missing or duplicate category control $id$suffix."
        }
        Check ($ui.SelectNodes('//*[@ID="STR:txtGuide_' + $id + '"]').Count -eq 1) "Missing native guide $id."
    }
    $directory = $archive.ReadText('etc/additionalraidreward.etc')
    foreach ($entry in @(@(3,258,'卢克'),@(4,274,'超时空漩涡'),@(5,400000052,'普雷·伊希斯'),@(6,257,'安徒恩'))) {
        $records = @([regex]::Matches($directory,'\[shop\](.*?)\[/shop\]','Singleline') | Where-Object {
            $_.Groups[1].Value -match ('\[category\]\s*' + $entry[0] + '\s') -and
            $_.Groups[1].Value -match ('\[item shop\]\s*' + $entry[1] + '\s')
        })
        Check ($records.Count -eq 1) "Native category/stock mismatch $($entry[0])/$($entry[1])."
        $label=$ui.SelectSingleNode('//*[@ID="STR:shop_category_' + $entry[0] + '_text"]')
        Check ($null -ne $label -and $label.string -ceq ('STR:' + $entry[2])) "Category label mismatch $($entry[0])."
    }
    $stock = $archive.ReadText('itemshop/raidshop_anton.shp')
    $wuun = $archive.ReadText('itemshop/wuun_normal.shp')
    $pattern='\[sell info\](.*?)\[/sell info\]'
    Check ([regex]::Match($stock,$pattern,'Singleline').Value -ceq [regex]::Match($wuun,$pattern,'Singleline').Value) 'Anton retained stock or price changed.'
    $receipt=[ordered]@{passed=($failures.Count -eq 0);checks=$checks;failures=@($failures);pvf=(Resolve-Path -LiteralPath $PvfPath).Path;scope='Native control and directory contracts; requires paired client dispatch patch; no game QA claim.'}
    $receipt|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $ReceiptPath -Encoding utf8
    if ($failures.Count) { $failures | Write-Output; exit 1 }
    Write-Output "PASS raid-shop native contracts checks=$checks"
} finally { $archive.Dispose() }
