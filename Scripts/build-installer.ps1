<#
.SYNOPSIS
    Builds the PulseBar installer with Velopack: self-contained publish, then vpk pack.
.DESCRIPTION
    Output goes to releases/ (ignored by git): PulseBarApp-win-Setup.exe, the full (and, when an older
    release is in releases/, delta) .nupkg, PulseBarApp-win-Portable.zip and releases.win.json.
    The version defaults to <Version> in PulseBar.csproj; CI passes it from the tag.
    Release notes come from that version's CHANGELOG.md section.
.EXAMPLE
    ./Scripts/build-installer.ps1
    ./Scripts/build-installer.ps1 -Version 1.0.1
#>
param(
    [string]$Version,
    [string]$OutputDir = 'releases'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\PulseBar\PulseBar.csproj'
$icon = Join-Path $root 'src\PulseBar\Assets\PulseBar.ico'
$publishDir = Join-Path $root 'artifacts\publish\win-x64'
$notesFile = Join-Path $root 'artifacts\release-notes.md'
if (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $root $OutputDir
}

if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.2.3 (got '$Version')"
}

Write-Host "Building PulseBar $Version" -ForegroundColor Cyan

function Invoke-Step([string]$name, [scriptblock]$command) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$name failed (exit code $LASTEXITCODE)"
    }
}

Push-Location $root
try {
    if (Test-Path $publishDir) {
        Remove-Item -Recurse -Force $publishDir
    }

    # Self-contained, so users don't need .NET installed. Release, so the Debug-only
    # "quit the running PulseBar before building" step never runs here.
    Invoke-Step 'dotnet publish' {
        dotnet publish $project -c Release -r win-x64 --self-contained true `
            -p:Version=$Version -o $publishDir -nologo
    }

    & (Join-Path $PSScriptRoot 'get-release-notes.ps1') -Version $Version -OutFile $notesFile

    Invoke-Step 'dotnet tool restore' { dotnet tool restore }

    Invoke-Step 'vpk pack' {
        dotnet vpk pack `
            --packId PulseBarApp `
            --packTitle PulseBar `
            --packAuthors PulseBar `
            --packVersion $Version `
            --packDir $publishDir `
            --runtime win-x64 `
            --mainExe PulseBar.exe `
            --icon $icon `
            --releaseNotes $notesFile `
            --shortcuts StartMenuRoot `
            --outputDir $OutputDir
    }

    Write-Host "Done: $OutputDir" -ForegroundColor Green
    Get-ChildItem $OutputDir | Format-Table Name, Length -AutoSize
}
finally {
    Pop-Location
}
