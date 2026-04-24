# opens the last 10 text-only clipboard history entries in VS Code
# Usage: Open PowerShell and run: .\tools\open-clipboard-history.ps1

$ErrorActionPreference = 'Stop'

# Path to clipboard DB (project default)
$dbPath = 'C:\Users\MPhil\source\repos\personal-assistant\clipboard-history.db'

if (-not (Test-Path $dbPath)) {
    Write-Error "Clipboard DB not found at: $dbPath"
    exit 1
}

# Temp output folder
$outDir = Join-Path $env:TEMP 'clipboard-history-for-vscode'
if (Test-Path $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir | Out-Null

# Create a small Python helper to extract entries (works where Python is available)
$py = @'
import sqlite3, sys, os

db = r"{DB}"
outdir = r"{OUT}"
try:
    con = sqlite3.connect(db)
    cur = con.cursor()
    rows = list(cur.execute("SELECT content FROM ClipboardHistory ORDER BY id DESC LIMIT 10"))
    for i, row in enumerate(rows, start=1):
        fn = os.path.join(outdir, f"{i:02}.txt")
        # Write raw content; keep newlines intact
        with open(fn, 'w', encoding='utf-8') as f:
            f.write(row[0])
    con.close()
except Exception as e:
    print('ERROR:'+str(e), file=sys.stderr)
    sys.exit(1)
'@

# Inject paths
$py = $py -replace '\{DB\}', [Regex]::Escape($dbPath)
$py = $py -replace '\{OUT\}', [Regex]::Escape($outDir)

# Write temp python file
$pyPath = Join-Path $env:TEMP ([IO.Path]::GetRandomFileName() + '.py')
Set-Content -Path $pyPath -Value $py -Encoding UTF8

try {
    & python "$pyPath"
    if ($LASTEXITCODE -ne 0) { throw "Python script failed (exit $LASTEXITCODE)" }
} catch {
    Remove-Item -LiteralPath $pyPath -ErrorAction SilentlyContinue
    Write-Error "Failed to extract clipboard entries: $_"
    exit 1
}

Remove-Item -LiteralPath $pyPath -ErrorAction SilentlyContinue

# Open the folder in VS Code ("code" should be on PATH)
try {
    Start-Process -FilePath code -ArgumentList "`"$outDir`""
    Write-Output "Opened last 10 clipboard entries in VS Code: $outDir"
} catch {
    Write-Warning "Could not launch VS Code with 'code' command. Folder with files is: $outDir"
}
