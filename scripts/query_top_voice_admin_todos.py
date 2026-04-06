#!/usr/bin/env python3
"""
Query top open (not completed) Voice Admin todos from the SQLite DB.

Usage:
  python scripts/query_top_voice_admin_todos.py --limit 20

It reads `VOICE_ADMIN_DB_PATH` from the repository `.env` file if present, or falls back to `.env.example`.
"""

import argparse
import os
import sqlite3
import sys
from textwrap import shorten


def read_env_db_path():
    candidates = [".env", ".env.example"]
    for fname in candidates:
        if os.path.exists(fname):
            with open(fname, "r", encoding="utf-8") as fh:
                for line in fh:
                    line = line.strip()
                    if not line or line.startswith("#"):
                        continue
                    if line.startswith("VOICE_ADMIN_DB_PATH"):
                        parts = line.split('=', 1)
                        if len(parts) == 2:
                            val = parts[1].strip()
                            if (val.startswith('"') and val.endswith('"')) or (val.startswith("'") and val.endswith("'")):
                                val = val[1:-1]
                            return val
    return None


def query_top_todos(db_path, limit=20):
    if not os.path.exists(db_path):
        print(f"ERROR: DB file not found at: {db_path}")
        sys.exit(2)

    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    cur = conn.cursor()

    sql = f"""
SELECT
  t.Id AS Id,
  COALESCE(t.Title, '') AS Title,
  COALESCE(t.Description, '') AS Description,
  COALESCE(t.Project, '') AS Project,
  COALESCE(t.Created, '') AS Created,
  COALESCE(t.SortPriority, 0) AS SortPriority
FROM Todos t
WHERE COALESCE(t.Completed, 0) = 0
  AND COALESCE(t.Archived, 0) = 0
ORDER BY COALESCE(t.SortPriority, 0) DESC,
         COALESCE(t.Created, '') DESC,
         t.Id DESC
LIMIT {int(limit)};
"""

    cur.execute(sql)
    rows = cur.fetchall()
    conn.close()
    return rows


def main():
    p = argparse.ArgumentParser(description="Query top open Voice Admin todos")
    p.add_argument("--limit", type=int, default=20, help="Max number of todos to return")
    args = p.parse_args()

    db_path = os.environ.get("VOICE_ADMIN_DB_PATH") or read_env_db_path()
    if not db_path:
        print("ERROR: VOICE_ADMIN_DB_PATH not found in environment or .env/.env.example")
        sys.exit(1)

    rows = query_top_todos(db_path, args.limit)
    if not rows:
        print("No open todos found.")
        return

    print(f"Top {len(rows)} open Voice Admin todos (source: {db_path}):\n")
    for i, r in enumerate(rows, start=1):
        desc = r['Description'] or ''
        print(f"{i}. Id={r['Id']}  Title={r['Title']}")
        print(f"   Project={r['Project']}  Priority={r['SortPriority']}  Created={r['Created']}")
        if desc.strip():
            print(f"   Description: {shorten(desc.replace('\n', ' '), width=200)}")
        print("")


if __name__ == '__main__':
    main()
