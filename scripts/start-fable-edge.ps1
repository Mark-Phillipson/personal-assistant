$edgePath = (Get-Command msedge -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
if (-not $edgePath) {
    $possiblePaths = @(
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles(x86)\Microsoft\Edge\Application\msedge.exe",
        "C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    )

    $edgePath = $possiblePaths | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $edgePath) {
        throw "Microsoft Edge was not found on this machine. Install Edge or update the path in scripts/start-fable-edge.ps1."
    }
}

$userDataDir = Join-Path $env:LOCALAPPDATA "Microsoft\Edge\User Data\FableMonitor"
$targetUrl = "https://app.makeitfable.com/"

Write-Host "Starting Edge with remote debugging enabled..."
Write-Host "User data dir: $userDataDir"
Write-Host "Target URL: $targetUrl"

Start-Process -FilePath $edgePath -ArgumentList @(
    "--new-window",
    "--remote-debugging-port=9223",
    "--user-data-dir=$userDataDir",
    $targetUrl
) -WindowStyle Normal

Write-Host "Edge launched. Sign in to Fable in that window, then restart the personal assistant."
