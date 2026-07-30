[CmdletBinding()]
param(
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$graphifyOutput = Join-Path $repoRoot 'graphify-out'
$graphifyIgnore = Join-Path $repoRoot '.graphifyignore'

function Test-CommandExists {
    param([Parameter(Mandatory)][string]$Name)

    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

if (-not (Test-CommandExists -Name 'uv')) {
    throw 'uv is required. Install it from https://docs.astral.sh/uv/ and run this script again.'
}

if (-not (Test-CommandExists -Name 'graphify')) {
    Write-Host 'graphify was not found. Installing graphifyy with uv...'
    uv tool install graphifyy
}

if (-not (Test-CommandExists -Name 'graphify')) {
    throw 'graphify is still unavailable after installation. Ensure the uv tool directory is on PATH.'
}

$requiredIgnoreEntries = @('docs/', 'tests/', 'samples/')
$existingIgnoreEntries = if (Test-Path -LiteralPath $graphifyIgnore) {
    Get-Content -LiteralPath $graphifyIgnore
} else {
    @()
}

$missingIgnoreEntries = $requiredIgnoreEntries | Where-Object {
    $_ -notin $existingIgnoreEntries
}

if ($missingIgnoreEntries.Count -gt 0) {
    Add-Content -LiteralPath $graphifyIgnore -Value $missingIgnoreEntries
}

New-Item -ItemType Directory -Path $graphifyOutput -Force | Out-Null

Push-Location $repoRoot
try {
    if ($Rebuild) {
        Write-Host 'Rebuilding the graph from src...'
    } else {
        Write-Host 'Building the graph from src...'
    }

    # Use the repository root as the output directory. The update subcommand
    # places output beside its input path, which would create src\\graphify-out.
    if ($Rebuild) {
        graphify extract src --code-only --force --no-viz --out .
    } else {
        graphify extract src --code-only --no-viz --out .
    }

    Write-Host 'Generating HTML visualization...'
    graphify export html

    Write-Host 'Running token-reduction benchmark...'
    graphify benchmark (Join-Path $graphifyOutput 'graph.json')
} finally {
    Pop-Location
}

Write-Host ''
Write-Host 'Graphify setup complete.'
Write-Host 'Daily update: .\scripts\graphify\setup-graphify.ps1'
