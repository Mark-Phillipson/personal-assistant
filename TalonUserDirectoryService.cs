using System.Text;
using System.Diagnostics;

internal sealed class TalonUserDirectoryService
{
    private const int DefaultMaxListResults = 200;
    private const int AbsoluteMaxListResults = 1000;
    private const int DefaultMaxReadChars = 12000;
    private const int AbsoluteMaxReadChars = 50000;
    private const int DefaultMaxSearchResults = 100;
    private const int AbsoluteMaxSearchResults = 1000;

    private readonly string _rootPath;

    private TalonUserDirectoryService(string rootPath)
    {
        _rootPath = rootPath;
    }

    public string RootPath => _rootPath;

    public bool DirectoryExists => Directory.Exists(_rootPath);

    public static TalonUserDirectoryService FromEnvironment()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultRoot = Path.Combine(userProfile, "AppData", "Roaming", "talon", "user");
        var configuredRoot = EnvironmentSettings.ReadString("TALON_USER_DIRECTORY", defaultRoot);
        return new TalonUserDirectoryService(Path.GetFullPath(configuredRoot));
    }

    public string GetSetupStatusText()
    {
        if (DirectoryExists)
        {
            return $"Talon user directory is available at '{_rootPath}'. Read-only Talon file tools are ready.";
        }

        return $"Talon user directory was not found at '{_rootPath}'. Set TALON_USER_DIRECTORY to the correct path.";
    }

    public Task<string> ListFilesAsync(string? relativePath = null, string searchPattern = "*", bool recursive = true, int maxResults = DefaultMaxListResults)
    {
        if (!DirectoryExists)
        {
            return Task.FromResult(GetSetupStatusText());
        }

        var cappedMax = Math.Clamp(maxResults, 1, AbsoluteMaxListResults);
        var normalizedPattern = string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern.Trim();

        try
        {
            var targetDirectory = ResolveDirectoryPath(relativePath);
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.EnumerateFiles(targetDirectory, normalizedPattern, option)
                .Take(cappedMax + 1)
                .Select(ToRelativePath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var wasTruncated = files.Count > cappedMax;
            if (wasTruncated)
            {
                files = files.Take(cappedMax).ToList();
            }

            if (files.Count == 0)
            {
                return Task.FromResult($"No files found in '{ToRelativePath(targetDirectory)}' matching '{normalizedPattern}'.");
            }

            var lines = new List<string>
            {
                $"Talon files under '{ToRelativePath(targetDirectory)}' (pattern '{normalizedPattern}', recursive: {recursive}):"
            };
            lines.AddRange(files.Select(path => $"- {path}"));

            if (wasTruncated)
            {
                lines.Add($"(Results truncated to {cappedMax} files)");
            }

            return Task.FromResult(string.Join('\n', lines));
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to list Talon files: {ex.Message}");
        }
    }

    public Task<string> ReadFileAsync(string relativePath, int maxChars = DefaultMaxReadChars)
    {
        if (!DirectoryExists)
        {
            return Task.FromResult(GetSetupStatusText());
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return Task.FromResult("No relative file path was provided.");
        }

        var cappedMaxChars = Math.Clamp(maxChars, 200, AbsoluteMaxReadChars);

        try
        {
            var absolutePath = ResolveFilePath(relativePath);
            if (!File.Exists(absolutePath))
            {
                return Task.FromResult($"File not found: {ToRelativePath(absolutePath)}");
            }

            var fileInfo = new FileInfo(absolutePath);
            if (fileInfo.Length == 0)
            {
                return Task.FromResult($"File is empty: {ToRelativePath(absolutePath)}");
            }

            using var stream = File.OpenRead(absolutePath);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();
            var truncated = content.Length > cappedMaxChars;
            if (truncated)
            {
                content = content[..cappedMaxChars] + "\n\n[Content truncated]";
            }

            return Task.FromResult($"Path: {ToRelativePath(absolutePath)}\n\n{content}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to read Talon file: {ex.Message}");
        }
    }

    public Task<string> SearchTextAsync(string query, string? relativePath = null, string searchPattern = "*", bool recursive = true, int maxResults = DefaultMaxSearchResults)
    {
        if (!DirectoryExists)
        {
            return Task.FromResult(GetSetupStatusText());
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult("No search query was provided.");
        }

        var cappedMax = Math.Clamp(maxResults, 1, AbsoluteMaxSearchResults);
        var normalizedPattern = string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern.Trim();

        try
        {
            var targetDirectory = ResolveDirectoryPath(relativePath);
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.EnumerateFiles(targetDirectory, normalizedPattern, option);

            var matches = new List<string>(capacity: Math.Min(cappedMax, 100));

            foreach (var file in files)
            {
                if (matches.Count >= cappedMax)
                {
                    break;
                }

                IEnumerable<string> lines;
                try
                {
                    lines = File.ReadLines(file);
                }
                catch
                {
                    continue;
                }

                var lineNumber = 0;
                foreach (var line in lines)
                {
                    lineNumber++;
                    if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    matches.Add($"{ToRelativePath(file)}:{lineNumber}: {line.Trim()}");
                    if (matches.Count >= cappedMax)
                    {
                        break;
                    }
                }
            }

            if (matches.Count == 0)
            {
                return Task.FromResult($"No text matches for '{query}' in '{ToRelativePath(targetDirectory)}'.");
            }

            var linesOut = new List<string>
            {
                $"Text matches for '{query}' in '{ToRelativePath(targetDirectory)}':"
            };
            linesOut.AddRange(matches.Select(match => $"- {match}"));

            if (matches.Count >= cappedMax)
            {
                linesOut.Add($"(Results capped at {cappedMax} matches)");
            }

            return Task.FromResult(string.Join('\n', linesOut));
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to search Talon files: {ex.Message}");
        }
    }

    public Task<string> OpenInExplorerAsync(string? relativePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult("Opening File Explorer is only supported on Windows hosts.");
        }

        if (!DirectoryExists)
        {
            return Task.FromResult(GetSetupStatusText());
        }

        try
        {
            var targetDirectory = ResolveDirectoryPath(relativePath);
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { targetDirectory },
                UseShellExecute = true
            });

            return Task.FromResult($"Opened File Explorer at Talon path: '{ToRelativePath(targetDirectory)}' ({targetDirectory})");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to open File Explorer for Talon directory: {ex.Message}");
        }
    }

    private string ResolveDirectoryPath(string? relativePath)
    {
        var candidate = string.IsNullOrWhiteSpace(relativePath)
            ? _rootPath
            : Path.Combine(_rootPath, relativePath.Trim());
        var fullPath = Path.GetFullPath(candidate);

        if (!IsUnderRoot(fullPath))
        {
            throw new InvalidOperationException("Requested path is outside the Talon user directory root.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {ToRelativePath(fullPath)}");
        }

        return fullPath;
    }

    private string ResolveFilePath(string relativePath)
    {
        var combined = Path.Combine(_rootPath, relativePath.Trim());
        var fullPath = Path.GetFullPath(combined);

        if (!IsUnderRoot(fullPath))
        {
            throw new InvalidOperationException("Requested file is outside the Talon user directory root.");
        }

        return fullPath;
    }

    private bool IsUnderRoot(string fullPath)
    {
        var rootWithSeparator = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalized.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private string ToRelativePath(string absolutePath)
    {
        if (string.Equals(absolutePath.TrimEnd(Path.DirectorySeparatorChar), _rootPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return ".";
        }

        if (absolutePath.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetRelativePath(_rootPath, absolutePath);
        }

        return absolutePath;
    }
}