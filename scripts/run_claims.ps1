#!/usr/bin/env pwsh
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Prefer the Python runner which handles parsing and writes combined_claim.txt
$runner = Join-Path $scriptDir 'run_claims.py'
if (-Not (Test-Path $runner)) {
    Write-Error "Python runner not found: $runner"
    exit 2
}

try {
    $proc = & python $runner 2>&1
    Write-Host $proc
} catch {
    Write-Error "Error running Python runner: $_"
    exit 3
}

$outPath = Join-Path $scriptDir 'combined_claim.txt'
if (Test-Path $outPath) {
    Write-Host "Wrote combined claim to: $outPath"
} else {
    Write-Error "Expected output not found: $outPath"
    exit 4
}
