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

        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["documents"] = Path.GetFullPath(Fallback(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(userProfile, "Documents"))),
            ["desktop"] = Path.GetFullPath(Fallback(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.Combine(userProfile, "Desktop"))),
            ["downloads"] = Path.GetFullPath(Path.Combine(userProfile, "Downloads")),
            ["pictures"] = Path.GetFullPath(Fallback(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Path.Combine(userProfile, "Pictures"))),
            ["videos"] = Path.GetFullPath(Fallback(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Path.Combine(userProfile, "Videos"))),
            ["repo"] = Path.GetFullPath(repoRoot)
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
            return Task.FromResult("No folder alias was provided. Allowed aliases: documents, desktop, downloads, pictures, videos, repo.");
        }

        if (!TryResolveAlias(folderAlias, out var canonicalAlias))
        {
            return Task.FromResult($"Unknown folder alias '{folderAlias}'. Allowed aliases: documents, desktop, downloads, pictures, videos, repo.");
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
            return Task.FromResult("No folder alias was provided. Allowed aliases: documents, desktop, downloads, pictures, videos, repo.");
        }

        if (string.IsNullOrWhiteSpace(relativeFilePath))
        {
            return Task.FromResult("No file path was provided.");
        }

        if (!TryResolveAlias(folderAlias, out var canonicalAlias))
        {
            return Task.FromResult($"Unknown folder alias '{folderAlias}'. Allowed aliases: documents, desktop, downloads, pictures, videos, repo.");
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