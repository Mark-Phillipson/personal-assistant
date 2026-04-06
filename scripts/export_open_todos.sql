-- Export open (not completed, not archived) todos from Voice Admin SQLite DB
-- Usage examples:
-- 1) Quick preview with sqlite3:
--    sqlite3 "C:\\path\\to\\voicelauncher.db" "SELECT Id, Title, Description FROM Todos WHERE COALESCE(Completed,0)=0 AND COALESCE(Archived,0)=0 LIMIT 10;"
-- 2) Export to CSV (sqlite3 v3+):
--    sqlite3 -header -csv "C:\\path\\to\\voicelauncher.db" "SELECT Id,Title,Description,Project,Created,SortPriority FROM Todos WHERE COALESCE(Completed,0)=0 AND COALESCE(Archived,0)=0 ORDER BY COALESCE(SortPriority,0) DESC;" > open_todos.csv

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
         t.Id DESC;
