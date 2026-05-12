#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the DocFX documentation site and serves it at http://localhost:8080.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path "$PSScriptRoot\..\.."
$dotnetToolsPath = "$env:USERPROFILE\.dotnet\tools"
$docfxExe = Join-Path $dotnetToolsPath "docfx.exe"

if (-not (Test-Path $docfxExe)) {
    Write-Host "docfx not found. Install it with:" -ForegroundColor Red
    Write-Host "  dotnet tool install -g docfx --add-source https://api.nuget.org/v3/index.json --ignore-failed-sources" -ForegroundColor Yellow
    exit 1
}

if ($env:PATH -notlike "*$dotnetToolsPath*") {
    $env:PATH = "$dotnetToolsPath;$env:PATH"
}

Write-Host "`n[1/3] Cleaning previous output..." -ForegroundColor Cyan
$docsDir = "$repoRoot\docs"
Get-ChildItem $docsDir -Exclude "_src" | Remove-Item -Recurse -Force

Write-Host "`n[2/3] Generating API metadata..." -ForegroundColor Cyan
& $docfxExe metadata "$repoRoot\docfx.json"
if ($LASTEXITCODE -ne 0) { Write-Error "docfx metadata failed."; exit 1 }

Write-Host "`n[3/3] Building and serving at http://localhost:8080 ..." -ForegroundColor Cyan
& $docfxExe "$repoRoot\docfx.json" --serve
