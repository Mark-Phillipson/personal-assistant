internal sealed class TelegramChatIdStore
{
    private readonly string _filePath;

    public TelegramChatIdStore(string filePath)
    {
        _filePath = filePath;
    }

    public static TelegramChatIdStore FromEnvironment()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var defaultPath = Path.Combine(appData, "personal-assistant", "telegram-chat-id.txt");
        var configuredPath = EnvironmentSettings.ReadString("TELEGRAM_CHAT_ID_STORE_PATH", defaultPath);
        return new TelegramChatIdStore(configuredPath);
    }

    public async Task SaveAsync(long chatId)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(_filePath, chatId.ToString());
    }

    public async Task<long?> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return null;
        var text = await File.ReadAllTextAsync(_filePath);
        return long.TryParse(text.Trim(), out var id) ? id : null;
    }
}
