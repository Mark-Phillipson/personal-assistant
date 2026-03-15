internal sealed class TelegramAttachmentService
{
    private const int DefaultMaxAttachmentBytes = 20 * 1024 * 1024;
    private const int MaxAllowedAttachmentBytes = 50 * 1024 * 1024;
    private readonly string _rootPath;
    private readonly long _maxAttachmentBytes;

    private TelegramAttachmentService(string rootPath, long maxAttachmentBytes)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _maxAttachmentBytes = maxAttachmentBytes;

        Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    public long MaxAttachmentBytes => _maxAttachmentBytes;

    public static TelegramAttachmentService FromEnvironment()
    {
        var defaultRoot = Path.Combine(Path.GetTempPath(), "personal-assistant", "telegram-attachments");
        var configuredRoot = EnvironmentSettings.ReadString("TELEGRAM_ATTACHMENT_STORAGE_PATH", defaultRoot);
        var maxAttachmentBytes = EnvironmentSettings.ReadInt(
            "TELEGRAM_ATTACHMENT_MAX_BYTES",
            fallback: DefaultMaxAttachmentBytes,
            min: 1_024,
            max: MaxAllowedAttachmentBytes);

        return new TelegramAttachmentService(configuredRoot, maxAttachmentBytes);
    }

    public async Task<TelegramStoredAttachment?> TryStoreMessageAttachmentAsync(
        TelegramMessage message,
        TelegramApiClient telegram,
        CancellationToken cancellationToken)
    {
        var candidate = GetAttachmentCandidate(message);
        if (candidate is null)
        {
            return null;
        }

        EnsureAllowedSize(candidate.FileSize);

        var remoteFile = await telegram.GetFileAsync(candidate.FileId, cancellationToken);
        EnsureAllowedSize(remoteFile.FileSize);

        if (string.IsNullOrWhiteSpace(remoteFile.FilePath))
        {
            throw new InvalidOperationException("Telegram returned an attachment without a downloadable file path.");
        }

        var chatFolder = Path.Combine(_rootPath, message.Chat.Id.ToString());
        Directory.CreateDirectory(chatFolder);

        var fileName = BuildFileName(candidate, remoteFile.FilePath);
        var destinationPath = BuildUniquePath(chatFolder, fileName);

        await telegram.DownloadFileAsync(remoteFile.FilePath, destinationPath, cancellationToken);

        var storedFileInfo = new FileInfo(destinationPath);
        if (storedFileInfo.Length > _maxAttachmentBytes)
        {
            TryDeleteFile(destinationPath);
            throw new InvalidOperationException(
                $"Telegram attachment is too large ({storedFileInfo.Length} bytes). Max allowed is {_maxAttachmentBytes} bytes.");
        }

        return new TelegramStoredAttachment(destinationPath, fileName, candidate.Kind, candidate.MimeType, storedFileInfo.Length);
    }

    public void DeleteStoredAttachment(TelegramStoredAttachment? attachment)
    {
        if (attachment is null)
        {
            return;
        }

        TryDeleteFile(attachment.LocalPath);
    }

    private void EnsureAllowedSize(long? sizeInBytes)
    {
        if (sizeInBytes.HasValue && sizeInBytes.Value > _maxAttachmentBytes)
        {
            throw new InvalidOperationException(
                $"Telegram attachment is too large ({sizeInBytes.Value} bytes). Max allowed is {_maxAttachmentBytes} bytes.");
        }
    }

    private static TelegramAttachmentCandidate? GetAttachmentCandidate(TelegramMessage message)
    {
        if (message.Document is { FileId.Length: > 0 } document)
        {
            return new TelegramAttachmentCandidate(
                document.FileId,
                "document",
                document.FileName,
                document.MimeType,
                document.FileSize);
        }

        var largestPhoto = message.Photo?
            .Where(photo => !string.IsNullOrWhiteSpace(photo.FileId))
            .OrderByDescending(photo => photo.FileSize ?? 0)
            .ThenByDescending(photo => photo.Width * photo.Height)
            .FirstOrDefault();

        if (largestPhoto is null)
        {
            return null;
        }

        return new TelegramAttachmentCandidate(
            largestPhoto.FileId,
            "photo",
            null,
            "image/jpeg",
            largestPhoto.FileSize);
    }

    private static string BuildFileName(TelegramAttachmentCandidate candidate, string remoteFilePath)
    {
        var extension = Path.GetExtension(remoteFilePath);
        var baseName = SanitizeFileName(candidate.FileName);

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = $"{candidate.Kind}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}";
        }

        if (string.IsNullOrWhiteSpace(Path.GetExtension(baseName)) && !string.IsNullOrWhiteSpace(extension))
        {
            baseName += extension;
        }

        return baseName;
    }

    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(fileName.Trim().Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return cleaned.Trim();
    }

    private static string BuildUniquePath(string directoryPath, string fileName)
    {
        var fileBaseName = Path.GetFileNameWithoutExtension(fileName);
        var fileExtension = Path.GetExtension(fileName);
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];

        return Path.Combine(directoryPath, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{uniqueSuffix}-{fileBaseName}{fileExtension}");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}

internal sealed record TelegramStoredAttachment(
    string LocalPath,
    string DisplayName,
    string Kind,
    string? MimeType,
    long SizeInBytes);

internal sealed record TelegramAttachmentCandidate(
    string FileId,
    string Kind,
    string? FileName,
    string? MimeType,
    long? FileSize);