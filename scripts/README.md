# Scripts: transaction claim helpers

Convenience wrappers and scripts to reproduce transaction scanning and claim calculations.

How to run

From the workspace root you can run the bundled runner, the PowerShell wrapper, or the individual Python scripts.

- Run the Python runner (recommended):

  python .\scripts\run_claims.py

  This runs the flagging and business-sum scripts, computes 10% of the flagged household total, and writes `scripts/combined_claim.txt`.

- Run the PowerShell wrapper (calls the Python runner):

  powershell -ExecutionPolicy Bypass -File .\scripts\run_claims.ps1

- Run individual scripts (useful to override inputs/outputs):

  python .\scripts\flag_claimable_household.py --input "C:\path\to\Transactions.csv" --output .\scripts\household_claimable.csv
  python .\scripts\sum_household_moneyout.py --input "C:\path\to\Transactions.csv" --output .\scripts\household_summary.txt
  python .\scripts\sum_business_moneyout.py --input "C:\path\to\Transactions.csv" --output .\scripts\business_summary.txt

VS Code task

You can run the runner from VS Code via: Command Palette → Run Task → "Run claims (Python)" (task is defined in `.vscode/tasks.json`).

Outputs

- `scripts/household_claimable.csv` — flagged household rows (CSV)
- `scripts/household_claim_10pct.txt` — previously generated example 10% household note
- `scripts/business_claim_10pct.txt` — business total note
- `scripts/combined_claim.txt` — runner/wrapper output (Household 10% + Business)

Notes and tips

- The Python scripts accept `--input` and `--output` where appropriate — use those to point at your transactions CSV if it's not in `C:\Users\MPhil\Downloads`.
- If you prefer PowerShell, use the wrapper; if you prefer a one-command cross-platform run, use `python .\scripts\run_claims.py`.
- If you'd like, I can add `--input` handling to the runner itself so it accepts a custom CSV path directly.
# Migration scripts

This folder contains simple tooling to export open (not completed) todos from the Voice Admin SQLite database and migrate them into GitHub Issues in the `Personal-Todos` repository.

Files:

- `export_open_todos.sql` - SQL query to list/export open todos.
- `migrate_to_github.py` - Python script that can perform a dry-run or create issues via the GitHub API.
- `requirements.txt` - Python dependencies for the script.

Quick start

1. Install dependencies (Python 3.8+):

```bash
python -m pip install -r scripts/requirements.txt
```

2. Dry-run (no GitHub changes):

```bash
python scripts/migrate_to_github.py --repo Mark-Phillipson/Personal-Todos --dry-run
```

3. Real run (provide token via env or flag):

PowerShell example:

```powershell
$env:GITHUB_TOKEN = 'ghp_...'
python scripts/migrate_to_github.py --repo Mark-Phillipson/Personal-Todos
```

Or pass token directly (less secure):

```bash
python scripts/migrate_to_github.py --repo Mark-Phillipson/Personal-Todos --token YOUR_TOKEN_HERE
```

Notes
- The script reads `VOICE_ADMIN_DB_PATH` from environment if `--db` is not provided.
- The migration records a CSV mapping file `migrations_<timestamp>.csv` with `source_id` -> `issue_url`.
- The script does not modify the source DB; marking source rows as migrated is left as an optional next step.
