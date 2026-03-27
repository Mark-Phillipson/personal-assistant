using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Manages pronunciation corrections for TTS output.
/// Loads corrections from a JSON file at startup and provides lookup/application methods.
/// </summary>
internal sealed class PronunciationDictionaryService
{
    private Dictionary<string, PronunciationCorrection>? _corrections;
    private readonly string _dictionaryPath;
    private readonly ReaderWriterLockSlim _lock = new();

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "pronunciation-debug.log");

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        try
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // ignore file logging failures
        }
        Console.Error.WriteLine(line);
    }

    public PronunciationDictionaryService(string dictionaryPath)
    {
        _dictionaryPath = dictionaryPath;
    }

    /// <summary>
    /// Loads pronunciation corrections from the JSON file.
    /// If the file does not exist or is malformed, initializes an empty dictionary with safe fallback.
    /// </summary>
    public async Task LoadFromFileAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_dictionaryPath))
            {
                Log($"[pronunciation] Pronunciation dictionary not found at {_dictionaryPath}; starting with empty corrections.");
                _lock.EnterWriteLock();
                try
                {
                    _corrections = new Dictionary<string, PronunciationCorrection>(StringComparer.OrdinalIgnoreCase);
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
                return;
            }

            var json = await File.ReadAllTextAsync(_dictionaryPath, ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var correctionsList = new List<PronunciationCorrection>();
            if (root.TryGetProperty("corrections", out var correctionsArray))
            {
                foreach (var item in correctionsArray.EnumerateArray())
                {
                    var correction = JsonSerializer.Deserialize<PronunciationCorrection>(item.GetRawText());
                    if (correction?.Enabled == true && !string.IsNullOrWhiteSpace(correction.OriginalWord))
                    {
                        correctionsList.Add(correction);
                    }
                }
            }

            _lock.EnterWriteLock();
            try
            {
                _corrections = correctionsList.ToDictionary(c => c.OriginalWord, StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            Log($"[pronunciation] Loaded {_corrections.Count} pronunciation corrections from {_dictionaryPath}.");
        }
        catch (Exception ex)
        {
            Log($"[pronunciation] Failed to load pronunciation dictionary from {_dictionaryPath}; starting with empty corrections. Error: {ex.Message}");
            _lock.EnterWriteLock();
            try
            {
                _corrections = new Dictionary<string, PronunciationCorrection>(StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
    }

    /// <summary>
    /// Saves all corrections back to the JSON file (admin-only operation).
    /// </summary>
    public async Task SaveToFileAsync(CancellationToken ct = default)
    {
        try
        {
            _lock.EnterReadLock();
            try
            {
                if (_corrections == null || _corrections.Count == 0)
                {
                    Log("[pronunciation] No corrections to save.");
                    return;
                }

                var dto = new PronunciationDictionaryDto
                {
                    Corrections = _corrections.Values.ToList()
                };

                var options = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
                var json = JsonSerializer.Serialize(dto, options);

                var directory = Path.GetDirectoryName(_dictionaryPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(_dictionaryPath, json, ct);
                Log($"[pronunciation] Saved {_corrections.Count} pronunciation corrections to {_dictionaryPath}.");
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        catch (Exception ex)
        {
            Log($"[pronunciation] Failed to save pronunciation dictionary to {_dictionaryPath}. Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Tries to get a pronunciation correction for a word (case-insensitive exact match).
    /// </summary>
    public bool TryGetCorrection(string word, out PronunciationCorrection? correction)
    {
        correction = null;
        if (string.IsNullOrWhiteSpace(word))
            return false;

        _lock.EnterReadLock();
        try
        {
            if (_corrections == null)
                return false;

            return _corrections.TryGetValue(word.Trim(), out correction);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Adds or updates a pronunciation correction.
    /// Increments usage count if the word already exists.
    /// </summary>
    public async Task AddCorrectionAsync(string word, string replacement, string? ssmlPhoneme, string? context = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            Log("[pronunciation] Invalid correction: word or replacement is empty.");
            return;
        }

        _lock.EnterWriteLock();
        try
        {
            _corrections ??= new Dictionary<string, PronunciationCorrection>(StringComparer.OrdinalIgnoreCase);

            var key = word.Trim();
            if (_corrections.TryGetValue(key, out var existing))
            {
                existing.UsageCount++;
                existing.LastModified = DateTime.UtcNow;
                Log($"[pronunciation] Updated pronunciation correction for {word}; usage count now {existing.UsageCount}.");
            }
            else
            {
                var correction = new PronunciationCorrection
                {
                    OriginalWord = key,
                    Replacement = replacement.Trim(),
                    SsmlPhoneme = ssmlPhoneme,
                    Context = context,
                    DateAdded = DateTime.UtcNow,
                    LastModified = DateTime.UtcNow,
                    UsageCount = 1,
                    Enabled = true
                };
                _corrections[key] = correction;
                Log($"[pronunciation] Added new pronunciation correction for {word}.");
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        await SaveToFileAsync(ct);
    }

    /// <summary>
    /// Removes a correction by word (case-insensitive).
    /// </summary>
    public async Task RemoveCorrectionAsync(string word, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(word))
            return;

        _lock.EnterWriteLock();
        try
        {
            if (_corrections != null && _corrections.Remove(word.Trim()))
            {
                Log($"[pronunciation] Removed pronunciation correction for {word}.");
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        await SaveToFileAsync(ct);
    }

    /// <summary>
    /// Applies all active pronunciation corrections to text using word-boundary matching.
    /// Returns a mapping of (original word) -> (replacement) for logging/auditing.
    /// </summary>
    public (string correctedText, Dictionary<string, string> appliedCorrections) ApplyCorrections(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (text, new Dictionary<string, string>());

        var applied = new Dictionary<string, string>();

        _lock.EnterReadLock();
        try
        {
            if (_corrections == null || _corrections.Count == 0)
                return (text, applied);

            var result = text;
            foreach (var (word, correction) in _corrections)
            {
                // Use word-boundary matching to avoid partial replacements.
                // E.g., "todo" should match "todo" and "todos" with boundaries, not "todolist".
                var pattern = $@"\b{Regex.Escape(word)}\b";
                var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                var matches = regex.Matches(result);
                if (matches.Count > 0)
                {
                    result = regex.Replace(result, correction.Replacement);
                    applied[word] = correction.Replacement;
                }
            }

            return (result, applied);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns all enabled corrections for auditing/status reporting.
    /// </summary>
    public async IAsyncEnumerable<PronunciationCorrection> ListAllAsync()
    {
        _lock.EnterReadLock();
        try
        {
            if (_corrections != null)
            {
                foreach (var correction in _corrections.Values.OrderBy(c => c.OriginalWord))
                {
                    yield return correction;
                    await Task.Yield();
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns the count of active corrections.
    /// </summary>
    public int GetCorrectionCount()
    {
        _lock.EnterReadLock();
        try
        {
            return _corrections?.Count ?? 0;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}

/// <summary>
/// Represents a single pronunciation correction entry.
/// </summary>
public class PronunciationCorrection
{
    [JsonPropertyName("originalWord")]
    public string OriginalWord { get; set; } = string.Empty;

    [JsonPropertyName("replacement")]
    public string Replacement { get; set; } = string.Empty;

    [JsonPropertyName("ssmlPhoneme")]
    public string? SsmlPhoneme { get; set; }

    [JsonPropertyName("context")]
    public string? Context { get; set; }

    [JsonPropertyName("dateAdded")]
    public DateTime DateAdded { get; set; }

    [JsonPropertyName("lastModified")]
    public DateTime? LastModified { get; set; }

    [JsonPropertyName("usageCount")]
    public int UsageCount { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// DTO for JSON serialization of the entire dictionary.
/// </summary>
internal class PronunciationDictionaryDto
{
    [JsonPropertyName("corrections")]
    public List<PronunciationCorrection> Corrections { get; set; } = new();
}
