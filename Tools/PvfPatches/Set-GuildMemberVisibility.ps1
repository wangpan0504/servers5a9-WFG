param(
    [Parameter(Mandatory=$true)][string]$InputPath,
    [Parameter(Mandatory=$true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$sourcePath = (Resolve-Path -LiteralPath $InputPath).ProviderPath
$targetPath = [IO.Path]::GetFullPath($OutputPath)
if ([string]::Equals($sourcePath, $targetPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Input and output must be different files.'
}
if (Test-Path -LiteralPath $targetPath) { throw 'Output already exists.' }

# PVF type 3 stores the original XUI as UTF-16LE. Preserve its BOM and terminator.
$raw = [IO.File]::ReadAllBytes($sourcePath)
if ($raw.Length -eq 0 -or $raw.Length % 2 -ne 0) { throw 'Expected a UTF-16LE XUI entry.' }
$encoding = New-Object Text.UnicodeEncoding($false, $false, $true)
$hasBom = $raw.Length -ge 2 -and $raw[0] -eq 255 -and $raw[1] -eq 254
$start = 0
if ($hasBom) { $start = 2 }
$text = $encoding.GetString($raw, $start, $raw.Length - $start)

function Read-Xui([string]$Content) {
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $stringReader = New-Object IO.StringReader($Content.TrimEnd([char]0))
    $reader = [Xml.XmlReader]::Create($stringReader, $settings)
    try {
        $document = New-Object Xml.XmlDocument
        $document.PreserveWhitespace = $true
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    } finally {
        $reader.Dispose()
        $stringReader.Dispose()
    }
}

$document = Read-Xui $text
$expected = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($group in @('sub_master', 'senior', 'newbie')) {
    for ($index = 0; $index -lt 10; $index++) {
        [void]$expected.Add(('STR:{0}_slot_bg_{1:00}' -f $group, $index))
    }
}
foreach ($id in $expected) {
    $nodes = @($document.SelectNodes('//*[@ID="' + $id + '"]'))
    if ($nodes.Count -ne 1 -or $nodes[0].Name -cne 'IControl') {
        throw "Expected exactly one IControl for $id."
    }
    $visibility = $nodes[0].GetAttribute('IsVisible')
    if ($visibility -and $visibility -cne 'BOOL:true' -and $visibility -cne 'BOOL:false') {
        throw "Unexpected visibility expression for $id."
    }
}

# Rewrite only opening-tag bytes; serializing the XML would rewrite unrelated data.
$tagPattern = '<IControl\b(?:[^>"'']|"[^"]*"|''[^'']*'')*>'
$edits = New-Object 'Collections.Generic.List[object]'
$seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($tag in [regex]::Matches($text, $tagPattern)) {
    $idMatch = [regex]::Match($tag.Value, '\sID\s*=\s*(["''])(?<id>.*?)\1')
    if (-not $idMatch.Success -or -not $expected.Contains($idMatch.Groups['id'].Value)) { continue }
    $id = $idMatch.Groups['id'].Value
    if (-not $seen.Add($id)) { throw "Duplicate row tag for $id." }
    $visible = [regex]::Match($tag.Value, '\sIsVisible\s*=\s*(["''])(?<value>.*?)\1')
    if ($visible.Success) {
        if ($visible.Groups['value'].Value -ceq 'BOOL:false') { continue }
        $value = $visible.Groups['value']
        $edits.Add(@{ Offset = $tag.Index + $value.Index; Length = $value.Length; Text = 'BOOL:false' })
    } else {
        $insert = $tag.Length - 1
        if ($tag.Value[$insert - 1] -eq '/') { $insert-- }
        $edits.Add(@{ Offset = $tag.Index + $insert; Length = 0; Text = ' IsVisible="BOOL:false"' })
    }
}
if ($seen.Count -ne 30) { throw 'Not all thirty member row tags were matched.' }
$builder = New-Object Text.StringBuilder($text)
foreach ($edit in ($edits | Sort-Object { $_.Offset } -Descending)) {
    [void]$builder.Remove($edit.Offset, $edit.Length)
    [void]$builder.Insert($edit.Offset, $edit.Text)
}
$patched = $builder.ToString()
$verified = Read-Xui $patched
foreach ($id in $expected) {
    if ($verified.SelectSingleNode('//*[@ID="' + $id + '"]').GetAttribute('IsVisible') -cne 'BOOL:false') {
        throw "Visibility verification failed for $id."
    }
}
$payload = $encoding.GetBytes($patched)
if ($hasBom) { $payload = [byte[]](@(255,254) + $payload) }
$stream = [IO.File]::Open($targetPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try { $stream.Write($payload, 0, $payload.Length) } finally { $stream.Dispose() }
Write-Output ("PASS rows=30 changed={0} bytes={1}" -f $edits.Count, $payload.Length)
