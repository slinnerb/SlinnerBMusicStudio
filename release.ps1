# Publishes a new release of SlinnerB's Music Studio.
# Run from the SlinnerBMusicStudio folder:  .\release.ps1

[CmdletBinding()]
param(
    [string]$Version,
    [string]$Notes
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

function Read-Required([string]$prompt) {
    while ($true) {
        $value = Read-Host $prompt
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value.Trim() }
        Write-Host "  (required)" -ForegroundColor Yellow
    }
}

# --- 1. Collect inputs ----------------------------------------------------
$csprojPath = Join-Path $PSScriptRoot 'SlinnerBMusicStudio.csproj'
[xml]$csproj = Get-Content $csprojPath
$currentVersion = $csproj.Project.PropertyGroup.Version
Write-Host "Current version: $currentVersion"

if (-not $Version) {
    $Version = Read-Required "New version (e.g. 1.1.0)"
}
$Version = $Version.TrimStart('v','V')
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Version must look like 1.2.3 (got '$Version')"
}

if (-not $Notes) {
    Write-Host ""
    Write-Host "Enter release notes (what changed and why). Required."
    Write-Host "Press Enter on an empty line when done:"
    $lines = @()
    while ($true) {
        $line = Read-Host
        if ($line -eq '') { break }
        $lines += $line
    }
    if ($lines.Count -eq 0) { throw "Release notes are required." }
    $Notes = ($lines -join "`n").Trim()
}

$tag = "v$Version"
Write-Host ""
Write-Host "About to release:" -ForegroundColor Cyan
Write-Host "  Version: $Version"
Write-Host "  Tag:     $tag"
Write-Host "  Notes:"
($Notes -split "`n") | ForEach-Object { Write-Host "    $_" }
Write-Host ""
$confirm = Read-Host "Proceed? (y/N)"
if ($confirm -notin 'y','Y','yes') { Write-Host "Cancelled."; exit 0 }

# --- 2. Bump version in csproj -------------------------------------------
Write-Host ""
Write-Host "==> Bumping version to $Version" -ForegroundColor Green
$csproj.Project.PropertyGroup.Version = $Version
$csproj.Save($csprojPath)

# --- 3. Build self-contained portable folder -----------------------------
Write-Host "==> Building..." -ForegroundColor Green
$publishDir = Join-Path $PSScriptRoot 'bin\Release\portable'
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $publishDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# --- 4. Zip it -----------------------------------------------------------
Write-Host "==> Zipping..." -ForegroundColor Green
$zipPath = Join-Path $PSScriptRoot "bin\Release\SlinnerBs-Music-Studio-$tag.zip"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -Force
$zipSize = "{0:N1} MB" -f ((Get-Item $zipPath).Length / 1MB)
Write-Host "    $zipPath ($zipSize)"

# --- 5. Commit, tag, push ------------------------------------------------
Write-Host "==> Committing version bump..." -ForegroundColor Green
git add SlinnerBMusicStudio.csproj | Out-Host
git commit -m "Release $tag" | Out-Host
git tag $tag | Out-Host
git push origin main | Out-Host
git push origin $tag | Out-Host

# --- 6. Publish GitHub release ------------------------------------------
Write-Host "==> Creating GitHub release..." -ForegroundColor Green
$notesFile = New-TemporaryFile
Set-Content -Path $notesFile -Value $Notes -Encoding UTF8
try {
    gh release create $tag $zipPath --title $tag --notes-file $notesFile | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
} finally {
    Remove-Item -Force $notesFile
}

Write-Host ""
Write-Host "Released $tag" -ForegroundColor Green
Write-Host "https://github.com/slinnerb/SlinnerBMusicStudio/releases/tag/$tag"
