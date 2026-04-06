#!/usr/bin/env python3
"""
migrate_to_github.py

Export open todos from Voice Admin SQLite DB and create GitHub Issues

Usage:
  python scripts/migrate_to_github.py --repo owner/repo --dry-run
  python scripts/migrate_to_github.py --repo owner/repo --token <GITHUB_TOKEN>

Environment:
  - VOICE_ADMIN_DB_PATH: optional, path to voice admin sqlite DB
  - GITHUB_TOKEN: optional, GitHub token with `repo` scope

The script will create a CSV mapping file with source Id -> created issue URL.
"""

import argparse
import csv
import os
import sqlite3
import sys
from datetime import datetime

try:
    import requests
except Exception:
    print("Missing dependency 'requests'. Install: pip install -r scripts/requirements.txt")
    raise

try:
    from dotenv import load_dotenv
    load_dotenv()
except Exception:
    # dotenv is optional; env vars can be provided by the caller
    pass


SELECT_OPEN_TODOS = '''
SELECT
  t.Id AS Id,
  COALESCE(t.Title, '') AS Title,
  COALESCE(t.Description, '') AS Description,
  COALESCE(t.Project, '') AS Project,
  COALESCE(t.Created, '') AS Created,
  COALESCE(t.SortPriority, 0) AS SortPriority
FROM Todos t
LEFT JOIN Categories c
  ON lower(trim(c.Category)) = lower(trim(COALESCE(t.Project, '')))
WHERE COALESCE(t.Completed, 0) = 0
  AND COALESCE(t.Archived, 0) = 0
ORDER BY COALESCE(t.SortPriority, 0) DESC,
         COALESCE(t.Created, '') DESC,
         t.Id DESC
'''


def fetch_open_todos(db_path, limit=None):
    if not os.path.exists(db_path):
        raise FileNotFoundError(f"DB not found at: {db_path}")
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    cur = conn.cursor()
    sql = SELECT_OPEN_TODOS
    if isinstance(limit, int) and limit > 0:
        sql = sql + f"\nLIMIT {limit}"
    cur.execute(sql)
    rows = [dict(r) for r in cur.fetchall()]
    conn.close()
    return rows


def create_github_issue(owner, repo, token, title, body, labels=None):
    url = f"https://api.github.com/repos/{owner}/{repo}/issues"
    headers = {
        "Authorization": f"token {token}",
        "Accept": "application/vnd.github+json",
    }
    payload = {"title": title, "body": body}
    if labels:
        payload["labels"] = labels
    r = requests.post(url, json=payload, headers=headers)
    if r.status_code not in (200, 201):
        raise RuntimeError(f"Failed to create issue ({r.status_code}): {r.text}")
    return r.json()


def main():
    p = argparse.ArgumentParser(description="Migrate open Voice Admin todos to GitHub Issues")
    p.add_argument("--db", help="Path to Voice Admin SQLite DB (overrides VOICE_ADMIN_DB_PATH env)")
    p.add_argument("--repo", required=True, help="Target GitHub repo in format owner/repo")
    p.add_argument("--token", help="GitHub token (or set GITHUB_TOKEN env)")
    p.add_argument("--dry-run", action="store_true", help="Do not create issues, just print what would be done")
    p.add_argument("--limit", type=int, help="Limit number of todos to migrate (for testing)")
    p.add_argument("--output", help="Output CSV mapping file (defaults to migrations_<timestamp>.csv)")
    args = p.parse_args()

    db_path = args.db or os.environ.get("VOICE_ADMIN_DB_PATH")
    if not db_path:
        print("ERROR: DB path not provided. Use --db or set VOICE_ADMIN_DB_PATH in environment.")
        sys.exit(1)
    repo = args.repo
    if "/" not in repo:
        print("ERROR: --repo should be in owner/repo format")
        sys.exit(1)
    owner, repo_name = repo.split("/", 1)
    token = args.token or os.environ.get("GITHUB_TOKEN")
    if not token and not args.dry_run:
        print("ERROR: No GitHub token provided. Set --token or GITHUB_TOKEN in environment, or use --dry-run.")
        sys.exit(1)

    todos = fetch_open_todos(db_path, limit=args.limit)
    print(f"Found {len(todos)} open todos (limit={args.limit})")
    if len(todos) == 0:
        return

    timestamp = datetime.utcnow().strftime("%Y%m%dT%H%M%SZ")
    out_csv = args.output or f"migrations_{timestamp}.csv"

    if args.dry_run:
        for t in todos:
            labels = []
            if t.get("Project"):
                labels.append(t.get("Project"))
            labels.append("voice-admin-migrated")
            print("---")
            print(f"Title: {t.get('Title')}")
            print(f"Labels: {labels}")
            print(f"Description: {t.get('Description')[:300]}")
        print("Dry-run complete. No issues created.")
        return

    # Real run: create issues and record mapping
    mappings = []
    for t in todos:
        src_id = t.get("Id")
        title = t.get("Title") or f"Todo {src_id}"
        body_lines = []
        if t.get("Description"):
            body_lines.append(t.get("Description"))
            body_lines.append("")
        body_lines.append(f"---\nSource: Voice Admin Todo Id {src_id}")
        if t.get("Project"):
            body_lines.append(f"Project: {t.get('Project')}")
        if t.get("Created"):
            body_lines.append(f"Created: {t.get('Created')}")
        body = "\n".join(body_lines)

        labels = []
        if t.get("Project"):
            labels.append(t.get("Project"))
        if t.get("SortPriority") and int(t.get("SortPriority")) > 0:
            labels.append("priority:high")
        labels.append("voice-admin-migrated")

        try:
            issue = create_github_issue(owner, repo_name, token, title, body, labels)
            issue_url = issue.get("html_url")
            print(f"Created issue: {issue_url} for source id {src_id}")
            mappings.append({"source_id": src_id, "issue_url": issue_url})
        except Exception as e:
            print(f"ERROR creating issue for source id {src_id}: {e}")

    # write mappings
    with open(out_csv, "w", newline='', encoding='utf-8') as fh:
        writer = csv.DictWriter(fh, fieldnames=["source_id", "issue_url"])
        writer.writeheader()
        for m in mappings:
            writer.writerow(m)

    print(f"Migration complete. Mappings saved to {out_csv}")


if __name__ == '__main__':
    main()
