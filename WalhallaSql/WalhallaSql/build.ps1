<#
.SYNOPSIS
    WalhallaSql CI build script.
    Builds all projects and runs tests for all target frameworks.
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sln = Join-Path $root "WalhallaSql.sln"

Write-Host "=== WalhallaSql Build ($Configuration) ===" -ForegroundColor Cyan

# Restore
Write-Host "[1/3] Restoring packages..." -ForegroundColor Yellow
dotnet restore $sln
if ($LASTEXITCODE -ne 0) { throw "Restore failed" }

# Build
Write-Host "[2/3] Building..." -ForegroundColor Yellow
dotnet build $sln -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# Test
if (-not $SkipTests) {
    Write-Host "[3/3] Running tests..." -ForegroundColor Yellow
    dotnet test $sln -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
} else {
    Write-Host "[3/3] Tests skipped." -ForegroundColor DarkYellow
}

Write-Host "=== Build succeeded ===" -ForegroundColor Green
