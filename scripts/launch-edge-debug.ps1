param(
    [string]$Profile = "$env:TEMP\edge-playwright-profile",
    [int]$Port = 9222
)

$edgePaths = @("C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe","C:\Program Files\Microsoft\Edge\Application\msedge.exe")
$edge = $edgePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { Write-Error 'Edge executable not found. Adjust path or install Edge.'; exit 1 }

if (-not (Test-Path $Profile)) { New-Item -ItemType Directory -Force -Path $Profile | Out-Null }

Start-Process -FilePath $edge -ArgumentList "--remote-debugging-port=$Port","--user-data-dir=$Profile" -NoNewWindow
Write-Host "Launched Edge with remote debugging on port $Port using profile: $Profile"
