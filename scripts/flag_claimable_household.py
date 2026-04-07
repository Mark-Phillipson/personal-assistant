import argparse
import csv
import re
import sys
from decimal import Decimal, InvalidOperation
from pathlib import Path

DEFAULT_CSV = r"c:\Users\MPhil\Downloads\Transactions_20260407141051.csv"
DEFAULT_OUT = r"c:\Users\MPhil\source\repos\personal-assistant\scripts\household_claimable.csv"

KEYWORDS = [
    'energy', 'electric', 'gas', 'octopus', 'water', 'insur', 'insurance',
    'broadband', 'internet', 'phone', 'telecom', 'tv licence', 'television',
    'licence', 'maidstone', 'council', 'council tax', 'rates', 'rent',
    'mortgage', 'home insurance', 'golding homes', 'maidstone borough', 'maidstonebc',
    'aa home insurance', 'tv licence mbp'
]

def parse_money(s: str) -> Decimal:
    if s is None:
        return Decimal('0')
    s = s.strip().replace(',','')
    if s == '':
        return Decimal('0')
    try:
        return Decimal(s)
    except InvalidOperation:
        cleaned = ''.join(ch for ch in s if (ch.isdigit() or ch in '.-'))
        if cleaned == '':
            return Decimal('0')
        return Decimal(cleaned)

def find_keyword(desc: str):
    if not desc:
        return None
    desc_l = desc.lower()
    for kw in KEYWORDS:
        if kw in desc_l:
            return kw
    for kw in KEYWORDS:
        if re.search(r"\b" + re.escape(kw) + r"\b", desc_l):
            return kw
    return None

def flag_claimable(input_path: str, out_path: str):
    flagged = []
    total_flagged = Decimal('0')
    count_flagged = 0
    total_household = Decimal('0')
    count_household = 0

    with open(input_path, newline='', encoding='utf-8') as f:
        reader = csv.DictReader(f)
        for row in reader:
            typ = (row.get('MyTransactionType') or '').strip()
            if typ.lower() == 'household':
                count_household += 1
                val = parse_money(row.get('MoneyOut'))
                total_household += val
                reason = find_keyword(row.get('Description') or '')
                likely = 'Yes' if reason else 'No'
                if reason:
                    flagged.append({**row, 'LikelyClaimable': likely, 'Reason': reason})
                    total_flagged += val
                    count_flagged += 1
                else:
                    flagged.append({**row, 'LikelyClaimable': likely, 'Reason': ''})

    # write full household file with flags
    if flagged:
        keys = list(flagged[0].keys())
        with open(out_path, 'w', newline='', encoding='utf-8') as out:
            writer = csv.DictWriter(out, fieldnames=keys)
            writer.writeheader()
            for r in flagged:
                writer.writerow(r)

    return {
        'count_household': count_household,
        'total_household': total_household,
        'count_flagged': count_flagged,
        'total_flagged': total_flagged,
        'out_path': out_path
    }

def main(argv=None):
    argv = argv if argv is not None else sys.argv[1:]
    p = argparse.ArgumentParser(description='Flag likely claimable Household rows')
    p.add_argument('--input', '-i', default=DEFAULT_CSV, help='Path to transactions CSV')
    p.add_argument('--output', '-o', default=DEFAULT_OUT, help='Output CSV path for flagged rows')
    args = p.parse_args(argv)

    inp = Path(args.input)
    outp = Path(args.output)
    if not inp.exists():
        print(f'Input CSV not found: {inp}', file=sys.stderr)
        raise SystemExit(2)

    res = flag_claimable(str(inp), str(outp))
    print(f"Household rows: {res['count_household']}")
    print(f"Household MoneyOut total: {res['total_household']:.2f}")
    print(f"Flagged rows (likely claimable): {res['count_flagged']}")
    print(f"Flagged MoneyOut total: {res['total_flagged']:.2f}")
    print(f"Wrote flagged CSV to: {res['out_path']}")

if __name__ == '__main__':
    main()
