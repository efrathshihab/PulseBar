<#
.SYNOPSIS
    Prints the CHANGELOG.md section for one version (the lines under "## [x.y.z]" up to the next "## ").
.EXAMPLE
    ./Scripts/get-release-notes.ps1 -Version 1.0.1 -OutFile artifacts/release-notes.md
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ChangelogPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'CHANGELOG.md'),
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

$lines = Get-Content -LiteralPath $ChangelogPath -Encoding UTF8
$header = '## [' + $Version + ']'
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i].Trim().StartsWith($header)) {
        $start = $i + 1
        break
    }
}

if ($start -lt 0) {
    throw "CHANGELOG.md has no '$header' section"
}

$section = New-Object System.Collections.Generic.List[string]
for ($i = $start; $i -lt $lines.Count; $i++) {
    if ($lines[$i].StartsWith('## ')) {
        break
    }

    $section.Add($lines[$i])
}

$notes = ($section -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($notes)) {
    throw "CHANGELOG.md section '$header' is empty"
}

if ($OutFile) {
    $dir = Split-Path $OutFile -Parent
    if ($dir) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    # UTF-8 without BOM
    [System.IO.File]::WriteAllText($OutFile, $notes + "`n", (New-Object System.Text.UTF8Encoding($false)))
}
else {
    $notes
}
