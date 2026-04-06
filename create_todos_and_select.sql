-- Create todos and todo_deps tables (SQLite-compatible)
CREATE TABLE IF NOT EXISTS todos (
  id TEXT PRIMARY KEY,
  title TEXT NOT NULL,
  description TEXT,
  status TEXT DEFAULT 'pending',
  created_at DATETIME DEFAULT (datetime('now')),
  updated_at DATETIME DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS todo_deps (
  todo_id TEXT NOT NULL,
  depends_on TEXT NOT NULL,
  PRIMARY KEY (todo_id, depends_on)
);

-- Select statement to view todos
SELECT id, title, description, status, created_at, updated_at FROM todos;
