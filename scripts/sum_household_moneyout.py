import argparse
import csv
import sys
from decimal import Decimal, InvalidOperation
from pathlib import Path

DEFAULT_CSV = r"c:\Users\MPhil\Downloads\Transactions_20260407141051.csv"

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

def sum_household(input_path: str):
    total = Decimal('0')
    count = 0
    with open(input_path, newline='', encoding='utf-8') as f:
        reader = csv.DictReader(f)
        for row in reader:
            typ = (row.get('MyTransactionType') or '').strip()
            if typ.lower() == 'household':
                val = parse_money(row.get('MoneyOut'))
                total += val
                count += 1
    return count, total

def main(argv=None):
    argv = argv if argv is not None else sys.argv[1:]
    p = argparse.ArgumentParser(description='Sum MoneyOut for Household rows')
    p.add_argument('--input', '-i', default=DEFAULT_CSV, help='Path to transactions CSV')
    p.add_argument('--output', '-o', help='Optional output file to write summary')
    args = p.parse_args(argv)

    inp = Path(args.input)
    if not inp.exists():
        print(f'Input CSV not found: {inp}', file=sys.stderr)
        raise SystemExit(2)

    count, total = sum_household(str(inp))
    out_lines = [f'Household rows: {count}', f'Household MoneyOut total: {total:.2f}']
    out_text = '\n'.join(out_lines)
    print(out_text)
    if args.output:
        Path(args.output).write_text(out_text + '\n')
    return count, total

if __name__ == '__main__':
    main()
