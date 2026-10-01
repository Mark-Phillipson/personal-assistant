using System.Diagnostics;
using System.Text;

internal sealed class ComputerToolsService
{
    private readonly Dictionary<string, string> _rootByAlias;

    public ComputerToolsService()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _rootByAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["downloads"] = Path.Combine(userProfile, "Downloads"),
            ["documents"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["desktop"] = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ["userprofile"] = userProfile,
        };
    }

    public string GetAllowedFoldersText()
    {
        return string.Join(", ", _rootByAlias.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    public Task<string> ListFilesAsync(string folderAlias, int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(folderAlias))
        {
            return Task.FromResult($"No folder alias was provided. Allowed values: {GetAllowedFoldersText()}");
        }

        if (!TryResolveRoot(folderAlias, out var root))
        {
            return Task.FromResult($"Unknown folder alias '{folderAlias}'. Allowed values: {GetAllowedFoldersText()}");
        }

        return ListFilesAtPathAsync(root, maxResults, folderAlias);
    }

    public Task<string> ListFilesAtPathAsync(string rootPath, int maxResults = 20, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Task.FromResult("No folder path was provided.");
        }

        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            return Task.FromResult($"Folder path does not exist: {root}");
        }

        var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxResults))
            .ToList();

        if (files.Count == 0)
        {
            return Task.FromResult($"No files found in '{label ?? root}' ({root}).");
        }

        var builder = new StringBuilder();
        for (var i = 0; i < files.Count; i++)
        {
            var path = files[i];
            var info = new FileInfo(path);
            builder.AppendLine($"{i + 1}. {info.Name} | {info.Length} bytes | {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        }

        return Task.FromResult($"Files in '{label ?? root}' ({root}):\n{builder}");
    }

    public Task<string> SearchFilesAsync(string folderAlias, string pattern, int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(folderAlias))
        {
            return Task.FromResult($"No folder alias was provided. Allowed values: {GetAllowedFoldersText()}");
        }

        if (!TryResolveRoot(folderAlias, out var root))
        {
            return Task.FromResult($"Unknown folder alias '{folderAlias}'. Allowed values: {GetAllowedFoldersText()}");
        }

        return SearchFilesAtPathAsync(root, pattern, maxResults, folderAlias);
    }

    public Task<string> SearchFilesAtPathAsync(string rootPath, string pattern, int maxResults = 20, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Task.FromResult("No folder path was provided.");
        }

        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            return Task.FromResult($"Folder path does not exist: {root}");
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Task.FromResult("A file pattern is required, for example 'Axxonlab*' or '*.pdf'.");
        }

        var matches = Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxResults))
            .ToList();

        if (matches.Count == 0)
        {
            return Task.FromResult($"No files matched pattern '{pattern}' in '{label ?? root}' ({root}).");
        }

        return Task.FromResult($"Matches for '{pattern}' in '{label ?? root}':\n" + string.Join(Environment.NewLine, matches.Select(path => Path.GetFileName(path))));
    }

    public Task<string> OpenFolderAsync(string folderAlias)
    {
        if (!TryResolveRoot(folderAlias, out var root))
        {
            return Task.FromResult($"Unknown folder alias '{folderAlias}'. Allowed values: {GetAllowedFoldersText()}");
        }

        return OpenFolderAtPathAsync(root, folderAlias);
    }

    public Task<string> OpenFolderAtPathAsync(string rootPath, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Task.FromResult("No folder path was provided.");
        }

        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            return Task.FromResult($"Folder path does not exist: {root}");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = root,
                UseShellExecute = true,
            });

            return Task.FromResult($"Opened folder '{label ?? root}' in File Explorer: {root}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to open folder '{label ?? root}': {ex.Message}");
        }
    }

    public Task<string> OpenFileAsync(string filePath, string? folderAlias = null)
    {
        var candidate = ResolveFilePath(filePath, folderAlias);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Task.FromResult("A valid file path is required. Pass an absolute path or a relative path with a folder alias such as downloads, documents, or desktop.");
        }

        return OpenFileAtPathAsync(candidate);
    }

    public Task<string> OpenFileAtPathAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Task.FromResult("No file path was provided.");
        }

        var candidate = Path.GetFullPath(filePath);
        if (!File.Exists(candidate))
        {
            return Task.FromResult($"File not found: {candidate}");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = candidate,
                UseShellExecute = true,
            });

            return Task.FromResult($"Opened file: {candidate}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to open file '{candidate}': {ex.Message}");
        }
    }

    public Task<string> NotifyUserAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return Task.FromResult("No notification message was provided.");
        }

        Console.WriteLine($"[computer-tools.notify] {message}");
        return Task.FromResult($"Notification queued: {message}");
    }

    private bool TryResolveRoot(string folderAlias, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(folderAlias))
        {
            return false;
        }

        var normalized = folderAlias.Trim();
        if (_rootByAlias.TryGetValue(normalized, out var resolved))
        {
            root = resolved;
            return true;
        }

        if (Directory.Exists(folderAlias))
        {
            root = Path.GetFullPath(folderAlias);
            return true;
        }

        return false;
    }

    private string? ResolveFilePath(string filePath, string? folderAlias)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var trimmed = filePath.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        if (string.IsNullOrWhiteSpace(folderAlias) || !TryResolveRoot(folderAlias, out var root))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(root, trimmed));
    }
}
