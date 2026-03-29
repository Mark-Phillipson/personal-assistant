using System.Diagnostics;
using System.Text;

internal sealed class KnownFolderExplorerService
{
    private readonly Dictionary<string, string> _rootByAlias;

    private KnownFolderExplorerService(Dictionary<string, string> rootByAlias)
    {
        _rootByAlias = rootByAlias;
    }

    public static KnownFolderExplorerService FromEnvironment()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var repoRoot = EnvironmentSettings.ReadString("ASSISTANT_REPO_DIRECTORY", Directory.GetCurrentDirectory());

        var reposRoot = EnvironmentSettings.ReadOptionalString("ASSISTANT_REPOS_DIRECTORY");
        if (string.IsNullOrWhiteSpace(reposRoot))
        {
            var repoDirectory = Path.GetFullPath(repoRoot);
            reposRoot = Path.GetFullPath(Path.Combine(repoDirectory, ".."));
        }

        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["documents"] = Path.GetFullPath(Fallback(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(userProfile, "Documents"))),
            ["desktop"] = Path.GetFullPath(Fallback(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.Combine(userProfile, "Desktop"))),
            ["downloads"] = Path.GetFullPath(Path.Combine(userProfile, "Downloads")),
            ["pictures"] = Path.GetFullPath(Fallback(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Path.Combine(userProfile, "Pictures"))),
            ["videos"] = Path.GetFullPath(Fallback(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Path.Combine(userProfile, "Videos"))),
            ["repo"] = Path.GetFullPath(repoRoot),
            ["repos"] = Path.GetFullPath(reposRoot)
        };

        return new KnownFolderExplorerService(roots);
    }

    public string GetSetupStatusText()
    {
        var entries = _rootByAlias
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value} {(Directory.Exists(pair.Value) ? "(exists)" : "(missing)")}");

        return "Known folder explorer is configured. " + string.Join(", ", entries);
    }

    public Task<string> OpenInExplorerAsync(string folderAlias, string? relativePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult("Opening File Explorer is only supported on Windows hosts.");
        }

        if (string.IsNullOrWhiteSpace(folderAlias))
        {
            return Task.FromResult("No folder alias was provided. Allowed aliases: documents, desktop, downloads, pictures, videos, repo, repos.");
        }

        if (!TryResolveAlias(folderAlias, out var canonicalAlias))
        {
            return Task.FromResult($"Unknown folder alias '{folderAlias}'. Allowed aliases: documents, desktop, downloads, pictures, videos, repo, repos.");
        }

        var root = _rootByAlias[canonicalAlias];
        if (!Directory.Exists(root))
        {
            return Task.FromResult($"Folder root is not available for alias '{canonicalAlias}': {root}");
        }

        try
        {
            var targetDirectory = ResolveDirectoryPath(root, relativePath);
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { targetDirectory },
                UseShellExecute = true
            });

            var relativeLabel = string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath.Trim();
            return Task.FromResult($"Opened File Explorer at alias '{canonicalAlias}' path '{relativeLabel}' ({targetDirectory})");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to open File Explorer for alias '{canonicalAlias}': {ex.Message}");
        }
    }

    public Task<string> OpenFileInVsCodeAsync(string folderAlias, string relativeFilePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult("Opening files in VS Code is only supported on Windows hosts.");
        }

        if (string.IsNullOrWhiteSpace(folderAlias))
        {
            return Task.FromResult("No folder alias was provided. Allowed aliases: documents, desktop, downloads, pictures, videos, repo, repos.");
        }

        if (string.IsNullOrWhiteSpace(relativeFilePath))
        {
            return Task.FromResult("No file path was provided.");
        }

        if (!TryResolveAlias(folderAlias, out var canonicalAlias))
        {
            return Task.FromResult($"Unknown folder alias '{folderAlias}'. Allowed aliases: documents, desktop, downloads, pictures, videos, repo, repos.");
        }

        var root = _rootByAlias[canonicalAlias];
        if (!Directory.Exists(root))
        {
            return Task.FromResult($"Folder root is not available for alias '{canonicalAlias}': {root}");
        }

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, relativeFilePath.Trim()));

            if (!IsUnderRoot(root, fullPath))
            {
                return Task.FromResult("Requested file path is outside the selected folder root.");
            }

            if (!File.Exists(fullPath))
            {
                return Task.FromResult($"File not found: {fullPath}");
            }

            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "code",
                ArgumentList = { fullPath },
                UseShellExecute = true
            });

            return Task.FromResult($"Opened '{relativeFilePath}' in Visual Studio Code ({fullPath}).");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to open file in VS Code: {ex.Message}");
        }
    }

    public Task<string> ListFilesAsync(string folderAlias, string? subPath = null, string? fileFilter = null, int maxResults = 50)
    {
        if (string.IsNullOrWhiteSpace(folderAlias))
        {
            return Task.FromResult("No folder alias was provided. Allowed aliases: documents, desktop, downloads, pictures, videos, repo, repos.");
        }

        if (!TryResolveAlias(folderAlias, out var canonicalAlias))
        {
            return Task.FromResult($"Unknown folder alias '{folderAlias}'. Allowed aliases: documents, desktop, downloads, pictures, videos, repo, repos.");
        }

        var root = _rootByAlias[canonicalAlias];
        if (!Directory.Exists(root))
        {
            return Task.FromResult($"Folder root is not available for alias '{canonicalAlias}': {root}");
        }

        try
        {
            var folder = ResolveDirectoryPath(root, subPath);
            var pattern = string.IsNullOrWhiteSpace(fileFilter) ? "*" : fileFilter.Trim();
            var filePaths = Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly)
                .OrderBy(x => x)
                .Take(Math.Max(1, maxResults));

            var builder = new StringBuilder();
            var count = 0;
            foreach (var path in filePaths)
            {
                count++;
                var info = new FileInfo(path);
                var relative = Path.GetRelativePath(root, path);
                builder.AppendLine($"{count}. {info.Name} | {relative} | {info.Length} bytes | {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            }

            if (count == 0)
            {
                return Task.FromResult($"No files found in '{canonicalAlias}'{(string.IsNullOrWhiteSpace(subPath) ? "" : $"/{subPath}")}.\n");
            }

            return Task.FromResult(builder.ToString());
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to list files: {ex.Message}");
        }
    }

    public bool TryResolveFilePath(string folderAlias, string relativeFilePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(folderAlias) || string.IsNullOrWhiteSpace(relativeFilePath))
        {
            return false;
        }

        if (!TryResolveAlias(folderAlias, out var canonicalAlias))
        {
            return false;
        }

        if (!_rootByAlias.TryGetValue(canonicalAlias, out var root))
        {
            return false;
        }

        try
        {
            var candidate = Path.Combine(root, relativeFilePath.Trim());
            fullPath = Path.GetFullPath(candidate);

            if (!IsUnderRoot(root, fullPath) || !File.Exists(fullPath))
            {
                fullPath = string.Empty;
                return false;
            }

            return true;
        }
        catch
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private static string Fallback(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static bool TryResolveAlias(string alias, out string canonicalAlias)
    {
        var normalized = NormalizeAlias(alias);
        canonicalAlias = normalized switch
        {
            "documents" or "document" or "docs" => "documents",
            "desktop" => "desktop",
            "downloads" or "download" => "downloads",
            "pictures" or "picture" or "photos" or "photo" => "pictures",
            "videos" or "video" => "videos",
            "repo" or "repository" or "project" or "projectroot" or "root" => "repo",
            "repos" or "source" or "allrepos" => "repos",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(canonicalAlias);
    }

    private static string NormalizeAlias(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static string ResolveDirectoryPath(string root, string? relativePath)
    {
        var candidate = string.IsNullOrWhiteSpace(relativePath)
            ? root
            : Path.Combine(root, relativePath.Trim());

        var fullPath = Path.GetFullPath(candidate);

        if (!IsUnderRoot(root, fullPath))
        {
            throw new InvalidOperationException("Requested path is outside the selected folder root.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");
        }

        return fullPath;
    }

    private static bool IsUnderRoot(string root, string fullPath)
    {
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalized.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}