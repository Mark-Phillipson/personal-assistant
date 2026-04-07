import subprocess
import sys
from pathlib import Path
from decimal import Decimal
import re
from datetime import datetime

SCRIPTS_DIR = Path(__file__).resolve().parent
CSV_DEFAULT = Path(r"c:\Users\MPhil\Downloads\Transactions_20260407141051.csv")

def run_script(cmd, cwd=None):
    res = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)
    if res.returncode != 0:
        raise RuntimeError(f"Command failed: {' '.join(cmd)}\n{res.stderr}")
    return res.stdout

def parse_total(output: str, label: str):
    # looks for lines like: 'Household MoneyOut total: 11509.80'
    m = re.search(rf"{re.escape(label)}\s*:\s*([0-9\.,-]+)", output)
    if not m:
        raise ValueError(f"Could not find '{label}' in output")
    return Decimal(m.group(1).replace(',', ''))

def main():
    inp = CSV_DEFAULT
    scripts_cwd = SCRIPTS_DIR

    # run flagging script (produces flagged CSV)
    out_flagged = scripts_cwd / 'household_claimable.csv'
    cmd_flag = [sys.executable, 'flag_claimable_household.py', '--input', str(inp), '--output', str(out_flagged)]
    print('Running:', ' '.join(cmd_flag))
    out1 = run_script(cmd_flag, cwd=str(scripts_cwd))
    print(out1)
    household_total = parse_total(out1, 'Household MoneyOut total')
    flagged_total = parse_total(out1, 'Flagged MoneyOut total')

    # run business sum
    cmd_bus = [sys.executable, 'sum_business_moneyout.py', '--input', str(inp)]
    print('Running:', ' '.join(cmd_bus))
    out2 = run_script(cmd_bus, cwd=str(scripts_cwd))
    print(out2)
    business_total = parse_total(out2, 'Business Expense MoneyOut total')

    # compute 10% of flagged household
    ten_pct = (flagged_total * Decimal('0.10')).quantize(Decimal('0.01'))

    combined = []
    combined.append(f'Household flagged total: £{flagged_total:.2f}')
    combined.append(f'10% of household flagged total: £{ten_pct:.2f}')
    combined.append(f'Business Expense total: £{business_total:.2f}')
    combined.append('')
    combined.append(f'Combined claim guidance: Household 10% + Business = £{(ten_pct + business_total):.2f}')
    combined.append('')
    combined.append(f'Generated: {datetime.utcnow().isoformat()}Z')

    out_file = scripts_cwd / 'combined_claim.txt'
    out_file.write_text('\n'.join(combined) + '\n')
    print('Wrote combined claim to:', out_file)

if __name__ == '__main__':
    main()
