---
name: transaction-claims
description: "Skill to flag household transactions and compute claimable totals (household/business). Includes scripts and a wrapper for one-step reporting. Use for recurring annual tax prep and quick reporting."
---

# Transaction Claims Skill

Purpose: bundle the CSV scanning and claim calculations you used today into a reusable skill for next year.

What it does:
- Flags `Household` transactions likely to be claimable (keyword heuristic) and writes `scripts/household_claimable.csv`.
- Totals `Household` and `Business Expense` MoneyOut values.
- Produces simple text outputs: `scripts/household_claim_10pct.txt`, `scripts/business_claim_10pct.txt`, and `scripts/combined_claim.txt` (via wrapper).

Files included (in this repo):
- `scripts/flag_claimable_household.py` — flags household rows and writes `scripts/household_claimable.csv`.
- `scripts/sum_household_moneyout.py` — sums `Household` MoneyOut.
- `scripts/sum_business_moneyout.py` — sums `Business Expense` MoneyOut.
- `scripts/run_claims.ps1` — wrapper that runs the two sums and writes `scripts/combined_claim.txt` (10% household + business by default).
- `scripts/household_claim_10pct.txt`, `scripts/business_claim_10pct.txt` — example outputs.

Quick usage

- From PowerShell (recommended):

  1. Open a terminal in the workspace root.
  2. Run:

     powershell -ExecutionPolicy Bypass -File .\scripts\run_claims.ps1

  3. Outputs:
     - `scripts/combined_claim.txt` — combined summary
     - `scripts/household_claimable.csv` — flagged household rows

Notes & recommended improvements

- The Python scripts currently use fixed paths to `Downloads/Transactions_...csv`. For portability, the next step is to add `--input`/`--output` CLI args.
- Consider adding a VS Code `task` to run the wrapper from the Tasks menu.
- The current flagging heuristic is keyword-based — review `KEYWORDS` in `scripts/flag_claimable_household.py` to tune.

Maintenance

- Update `KEYWORDS` and the wrapper rounding logic as needed each tax year.
- Keep `scripts/` under source control; the SKILL.md documents how to run them.

If you want, I can:
- add CLI args to the Python scripts, or
- add the VS Code `tasks.json` entry, or
- register this as a formal assistant skill trigger (description keywords) so it is discoverable by chat.
