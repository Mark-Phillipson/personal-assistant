using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PersonalAssistant.Tests;

public class DatabasePhaseThreeTests
{
    [Fact]
    public void PersonalityProfile_LoadFromEnvironmentOrJson_UsesSignatureArrays()
    {
        var tempFile = Path.GetTempFileName();

        try
        {
            var json = JsonSerializer.Serialize(new
            {
                signatureGreetings = new[] { "  Oy,  ", "Right," },
                signatureFarewells = new[] { "Out.", "Sorted." }
            });

            File.WriteAllText(tempFile, json);
            Environment.SetEnvironmentVariable("ASSISTANT_PERSONALITY_CONFIG_PATH", tempFile);
            Environment.SetEnvironmentVariable("ASSISTANT_SIGNATURE_GREETING", "Hello");
            Environment.SetEnvironmentVariable("ASSISTANT_SIGNATURE_FAREWELL", "Bye");

            var profile = PersonalityProfile.LoadFromEnvironmentOrJson(PersonalityProfile.FromEnvironment());

            Assert.Equal(new[] { "Oy,", "Right," }, profile.SignatureGreetings);
            Assert.Equal(new[] { "Out.", "Sorted." }, profile.SignatureFarewells);
            Assert.Equal("Oy,", profile.SignatureGreeting);
            Assert.Equal("Out.", profile.SignatureFarewell);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASSISTANT_PERSONALITY_CONFIG_PATH", null);
            Environment.SetEnvironmentVariable("ASSISTANT_SIGNATURE_GREETING", null);
            Environment.SetEnvironmentVariable("ASSISTANT_SIGNATURE_FAREWELL", null);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SystemPromptBuilder_Build_ChoosesFromSignatureArrays()
    {
        var profile = new PersonalityProfile
        {
            Name = "Bob",
            Tone = AssistantTone.Irreverent,
            UseEmoji = true,
            EmojiDensity = EmojiDensity.Moderate,
            SignatureGreetings = new[] { "Oy", "Right" },
            SignatureFarewells = new[] { "Out.", "Sorted." }
        };

        var prompts = Enumerable.Range(0, 64)
            .Select(_ => SystemPromptBuilder.Build(profile))
            .ToArray();

        Assert.All(prompts, prompt =>
        {
            Assert.True(
                prompt.Contains("Preferred greeting style: Oy.")
                || prompt.Contains("Preferred greeting style: Right."));

            Assert.DoesNotContain("Preferred farewell style: GPT-5", prompt, StringComparison.OrdinalIgnoreCase);
        });

        Assert.Contains(prompts, prompt => prompt.Contains("Preferred greeting style: Oy."));
        Assert.Contains(prompts, prompt => prompt.Contains("Preferred greeting style: Right."));
        Assert.Contains(prompts, prompt => prompt.Contains("Preferred farewell style: Out GPT-5", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(prompts, prompt => prompt.Contains("Preferred farewell style: Sorted.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WindowsFocusAssistService_ToggleOnOff_WorksWhenWindowsAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var service = WindowsFocusAssistService.FromEnvironment();

        var onResult = await service.SetFocusAssistModeAsync("on");
        Assert.Contains("Focus mode command accepted", onResult);

        var dndResult = await service.SetFocusAssistModeAsync("do not disturb");
        Assert.Contains("Focus mode command accepted", dndResult);

        var offResult = await service.SetFocusAssistModeAsync("off");
        Assert.Contains("Focus mode command accepted", offResult);

        var disableDndResult = await service.SetFocusAssistModeAsync("disable do not disturb");
        Assert.Contains("Focus mode command accepted", disableDndResult);
    }

    [Fact]
    public void SqlSecurityHelper_AllowsSelectAndRejectsWrites()
    {
        Assert.True(SqlSecurityHelper.IsSelectQueryOnly("SELECT * FROM T"));
        Assert.True(SqlSecurityHelper.IsSelectQueryOnly("  WITH cte AS (SELECT 1 as X) SELECT * FROM cte "));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("INSERT INTO T VALUES (1)"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("DELETE FROM T"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("DROP TABLE T"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("SELECT * FROM T; DROP TABLE T;"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly(""));
    }

    [Fact]
    public void TelegramMessageHandler_DetectsTodoAddRequestAndIgnoresListIntent()
    {
        var type = typeof(TelegramMessageHandler);
        var addMethod = type.GetMethod("LooksLikeTodoAddRequest", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(addMethod);

        var listMethod = type.GetMethod("LooksLikeTodoListRequest", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(listMethod);

        Assert.True((bool)addMethod!.Invoke(null, new object[] { "add a todo remind me to drink water" })!);
        Assert.False((bool)listMethod!.Invoke(null, new object[] { "add a todo remind me to drink water" })!);

        Assert.True((bool)listMethod!.Invoke(null, new object[] { "show my open todo list" })!);
        Assert.False((bool)addMethod!.Invoke(null, new object[] { "show my open todo list" })!);
    }

    [Fact]
    public void TelegramMessageHandler_IsAudioReplyRequested_RequiresReplyIntent()
    {
        var type = typeof(TelegramMessageHandler);
        var method = type.GetMethod("IsAudioReplyRequested", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, new object[] { "reply in audio" })!);
        Assert.True((bool)method.Invoke(null, new object[] { "read that out loud" })!);
        Assert.True((bool)method.Invoke(null, new object[] { "send me a test message as a wave file" })!);
        Assert.False((bool)method.Invoke(null, new object[] { "give me a text representation of this wav file" })!);
        Assert.False((bool)method.Invoke(null, new object[] { "transcribe this audio to text" })!);
    }

    [Fact]
    public void TelegramMessageHandler_TextRepresentationOfAudio_DisablesTelegramAudio()
    {
        var type = typeof(TelegramMessageHandler);
        var textRepresentationMethod = type.GetMethod("IsTextRepresentationOfAudioRequested", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(textRepresentationMethod);

        var shouldSendTelegramAudioMethod = type.GetMethod("ShouldSendTelegramAudio", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(shouldSendTelegramAudioMethod);

        const string transcriptionRequest = "give me a text representation of the previously sent wave file";

        Assert.True((bool)textRepresentationMethod!.Invoke(null, new object[] { transcriptionRequest })!);
        Assert.False((bool)shouldSendTelegramAudioMethod!.Invoke(null, new object?[] { transcriptionRequest })!);
    }

    [Fact]
    public async Task GenericDatabaseService_UnknownAliasAndUnsafeSqlBehaviorAsync()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempFile }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE People (Id INTEGER PRIMARY KEY, Name TEXT);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO People (Name) VALUES ('Alice'), ('Bob');";
                cmd.ExecuteNonQuery();
            }

            var source = new DatabaseSource("test", DatabaseProviderType.SQLite, tempFile, "TempSqlite", "main", true);
            var registry = new DatabaseRegistry(new[] { source });
            var service = new GenericDatabaseService(registry);

            var unknownTables = (await service.ListTablesAsync("unknown")).ToList();
            Assert.Empty(unknownTables);

            var filtered = (await service.QueryTableAsync("test", "People", "Name = 'Alice'", 10)).ToList();
            Assert.Single(filtered);
            Assert.Equal("Alice", filtered[0]["Name"]);

            var selectResults = (await service.ExecuteReadOnlySqlAsync("test", "SELECT Name FROM People ORDER BY Name", 10)).ToList();
            Assert.Equal(2, selectResults.Count);
            Assert.Equal("Alice", selectResults[0]["Name"]);

            var unsafeSql = (await service.ExecuteReadOnlySqlAsync("test", "DROP TABLE People", 10)).ToList();
            Assert.Empty(unsafeSql);
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException)
            {
                // safe to ignore temp file cleanup issues during tests.
            }
        }
    }

    [Fact]
    public async Task SqliteDatabaseProvider_QueryAndSqlExecuteWorkAsync()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // create database file
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempFile }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE People (Id INTEGER PRIMARY KEY, Name TEXT);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO People (Name) VALUES ('Alice'), ('Bob');";
                cmd.ExecuteNonQuery();
            }

            var source = new DatabaseSource("test", DatabaseProviderType.SQLite, tempFile, "TempSqlite", "main", true);
            var provider = new SqliteDatabaseProvider(source);

            var tables = (await provider.ListTablesAsync()).ToList();
            Assert.Contains("People", tables);

            var schema = (await provider.GetTableSchemaAsync("People")).ToList();
            Assert.Contains(schema, c => c.Name == "Id" && c.IsPrimaryKey);
            Assert.Contains(schema, c => c.Name == "Name");

            var count = await provider.CountRowsAsync("People");
            Assert.Equal(2, count);

            var preview = (await provider.PreviewRowsAsync("People", 2)).ToList();
            Assert.Equal(2, preview.Count);
            Assert.Equal("Alice", preview[0]["Name"]);

            var query = (await provider.QueryTableAsync("People", "Name = 'Bob'", 10)).ToList();
            Assert.Single(query);
            Assert.Equal("Bob", query[0]["Name"]);

            var sqlRows = (await provider.ExecuteReadOnlySqlAsync("SELECT Name FROM People ORDER BY Name", 10)).ToList();
            Assert.Equal(2, sqlRows.Count);
            Assert.Equal("Alice", sqlRows[0]["Name"]);

            var dangerous = await provider.ExecuteReadOnlySqlAsync("DROP TABLE People", 10);
            Assert.Empty(dangerous);
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException)
            {
                // safe to ignore temp file cleanup issues during tests.
            }
        }
    }

    [Fact]
    public void DatabaseRegistry_ParsesJsonAndLegacyAlias()
    {
        var json = "{\"databases\":[{\"alias\":\"users\",\"provider\":\"sqlite\",\"connectionString\":\"data.db\",\"displayName\":\"Users DB\",\"defaultSchema\":\"main\",\"readOnly\":true}]}";
        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp, json);
            Environment.SetEnvironmentVariable("DATABASE_CONFIG_PATH", temp);

            var registry = DatabaseRegistry.FromEnvironment();
            Assert.True(registry.HasSources);
            Assert.True(registry.TryGetSource("users", out var source));
            Assert.Equal("Users DB", source.DisplayName);
            Assert.Equal(DatabaseProviderType.SQLite, source.ProviderType);

            Environment.SetEnvironmentVariable("DATABASE_CONFIG_PATH", null);
            Environment.SetEnvironmentVariable("VOICE_ADMIN_DB_PATH", "C:\\does-not-exist.db");
            var legacyReg = DatabaseRegistry.FromEnvironment();
            Assert.True(legacyReg.HasSources);
            Assert.True(legacyReg.TryGetSource("voiceadmin", out var legacySource));
            Assert.Equal(DatabaseProviderType.SQLite, legacySource.ProviderType);
        }
        finally
        {
            File.Delete(temp);
            Environment.SetEnvironmentVariable("DATABASE_CONFIG_PATH", null);
            Environment.SetEnvironmentVariable("VOICE_ADMIN_DB_PATH", null);
        }
    }

    [Fact]
    public void EnvironmentSettings_ReadOptionalString_RemovesSurroundingQuotes()
    {
        Environment.SetEnvironmentVariable("DATABASE_SQLSERVER_INSTANCES", "\"localhost\"");
        var value = EnvironmentSettings.ReadOptionalString("DATABASE_SQLSERVER_INSTANCES");
        Assert.Equal("localhost", value);

        Environment.SetEnvironmentVariable("DATABASE_SQLSERVER_INSTANCES", "'localhost'");
        value = EnvironmentSettings.ReadOptionalString("DATABASE_SQLSERVER_INSTANCES");
        Assert.Equal("localhost", value);

        Environment.SetEnvironmentVariable("DATABASE_SQLSERVER_INSTANCES", null);
    }

    [Fact]
    public void SqlSecurityHelper_RejectsDangerousSqlComprehensively()
    {
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("INSERT INTO T VALUES(1)"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("UPDATE T SET A=1"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("DELETE FROM T"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("DROP TABLE T"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("CREATE TABLE T(Id INT)"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("ALTER TABLE T ADD COL INT"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("EXEC sp_help"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("USE master"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("SELECT * FROM A; DROP TABLE A;"));
        Assert.False(SqlSecurityHelper.IsSelectQueryOnly("SELECT 1; SELECT 2"));
        Assert.True(SqlSecurityHelper.IsSelectQueryOnly(" SELECT * FROM A; "));
        Assert.True(SqlSecurityHelper.IsSelectQueryOnly("WITH cte AS (SELECT 1 AS x) SELECT * FROM cte"));
    }

    [Fact]
    public async Task SqliteDatabaseProvider_HandlesQuotedNamesAndMixedCaseAsync()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempFile }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE [Mixed Case Table] ([Id] INTEGER PRIMARY KEY, [Value] TEXT);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO [Mixed Case Table] ([Value]) VALUES ('A'), ('B');";
                cmd.ExecuteNonQuery();
            }

            var source = new DatabaseSource("testname", DatabaseProviderType.SQLite, tempFile, "TempSqlite", "main", true);
            var provider = new SqliteDatabaseProvider(source);
            var tables = (await provider.ListTablesAsync()).ToList();
            Assert.Contains("Mixed Case Table", tables);

            var schema = (await provider.GetTableSchemaAsync("Mixed Case Table")).ToList();
            Assert.Contains(schema, c => c.Name == "Id" && c.IsPrimaryKey);
            Assert.Contains(schema, c => c.Name == "Value");

            var count = await provider.CountRowsAsync("Mixed Case Table");
            Assert.Equal(2, count);

            var preview = (await provider.PreviewRowsAsync("Mixed Case Table", 5)).ToList();
            Assert.Equal(2, preview.Count);
            Assert.Equal("A", preview[0]["Value"]);

            var query = (await provider.QueryTableAsync("Mixed Case Table", "Value = 'B'", 10)).ToList();
            Assert.Single(query);
            Assert.Equal("B", query[0]["Value"]);
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void DatabaseRegistry_DiscoverSqlServerDatabases_DoesNotThrow()
    {
        var sources = DatabaseRegistry.DiscoverSqlServerDatabases().ToList();
        Assert.NotNull(sources);
    }

    [Fact]
    public void DatabaseRegistry_DiscoverSqlServerDatabases_SkipsLocalDbByDefault()
    {
        Environment.SetEnvironmentVariable("DATABASE_SQLSERVER_INCLUDE_LOCALDB", "false");
        var sources = DatabaseRegistry.DiscoverSqlServerDatabases().ToList();
        Assert.DoesNotContain(sources, s => s.Alias.Contains("localdb", StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("DATABASE_SQLSERVER_INCLUDE_LOCALDB", null);
    }

    [Fact]
    public async Task VoiceAdminService_GetIncompleteTodosRowsAsync_ReturnsOpenTodosAsync()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempFile }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE Todos (Id INTEGER PRIMARY KEY, Title TEXT, Description TEXT, Project TEXT, Created TEXT, SortPriority INTEGER, Completed INTEGER, Archived INTEGER);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE TABLE Categories (ID INTEGER PRIMARY KEY, Category TEXT);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "INSERT INTO Todos (Title, Description, Project, Created, SortPriority, Completed, Archived) VALUES ('Task A', 'Desc A', 'Work', '2026-03-24', 10, 0, 0), ('Task B', 'Desc B', 'Home', '2026-03-23', 5, 1, 0), ('Task C', 'Desc C', 'Work', '2026-03-22', 1, 0, 0);";
                cmd.ExecuteNonQuery();
            }

            Environment.SetEnvironmentVariable("VOICE_ADMIN_DB_PATH", tempFile);
            var service = VoiceAdminService.FromEnvironment();

            var result = await service.GetIncompleteTodosRowsAsync();
            Assert.True(result.Success);
            Assert.Equal(2, result.Rows.Count());
            Assert.Contains(result.Rows, r => r.ContainsKey("Title") && r["Title"]?.ToString() == "Task A");
            Assert.Contains(result.Rows, r => r.ContainsKey("Title") && r["Title"]?.ToString() == "Task C");

            var filtered = await service.GetIncompleteTodosRowsAsync("Work");
            Assert.True(filtered.Success);
            Assert.Equal(2, filtered.Rows.Count());
            Assert.DoesNotContain(filtered.Rows, r => r.ContainsKey("Title") && r["Title"]?.ToString() == "Task B");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOICE_ADMIN_DB_PATH", null);
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task SqlServerDatabaseProvider_RequiresReachableServerAsync()
    {
        var localDbName = "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=master;Integrated Security=True;";
        var source = new DatabaseSource("mssql", DatabaseProviderType.SqlServer, localDbName, "LocalDb", "dbo", true);
        var provider = new SqlServerDatabaseProvider(source);

        try
        {
            var available = await provider.IsAvailableAsync();
            if (!available)
            {
                return;
            }

            var tables = (await provider.ListTablesAsync()).ToList();
            Assert.NotNull(tables);
        }
        catch (Exception ex)
        {
            Assert.Fail("SqlServerDatabaseProvider test failed; ensure LocalDB is installed and available. " + ex.Message);
        }
    }

    [Fact]
    public void TextToSpeechService_BuildSpeechInput_PlainText_NoSsml()
    {
        var method = typeof(TextToSpeechService).GetMethod("BuildSpeechInput", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { "Hello world, this is a test.", "en-GB-RyanNeural" })!;
        var isSsml = (bool)result.GetType().GetProperty("IsSsml")!.GetValue(result)!;
        var content = (string)result.GetType().GetProperty("Content")!.GetValue(result)!;

        Assert.False(isSsml);
        Assert.Equal("Hello world, this is a test.", content);
        Assert.DoesNotContain("**", content);
    }

    [Fact]
    public void TextToSpeechService_BuildSpeechInput_BoldMarkdown_ProducesSsmlEmphasis()
    {
        var method = typeof(TextToSpeechService).GetMethod("BuildSpeechInput", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { "This is **very** important to know.", "en-GB-RyanNeural" })!;
        var isSsml = (bool)result.GetType().GetProperty("IsSsml")!.GetValue(result)!;
        var content = (string)result.GetType().GetProperty("Content")!.GetValue(result)!;

        Assert.True(isSsml);
        Assert.Contains("<emphasis level=\"moderate\">very</emphasis>", content);
        Assert.Contains("This is ", content);
        Assert.Contains(" important to know.", content);
        Assert.DoesNotContain("**", content);
    }

    [Fact]
    public void TextToSpeechService_BuildSpeechInput_SingleAsterisk_ProducesSsmlEmphasis()
    {
        var method = typeof(TextToSpeechService).GetMethod("BuildSpeechInput", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { "This is *very* important to know.", "en-GB-RyanNeural" })!;
        var isSsml = (bool)result.GetType().GetProperty("IsSsml")!.GetValue(result)!;
        var content = (string)result.GetType().GetProperty("Content")!.GetValue(result)!;

        Assert.True(isSsml);
        Assert.Contains("<emphasis level=\"moderate\">very</emphasis>", content);
        Assert.Contains("This is ", content);
        Assert.Contains(" important to know.", content);
        Assert.DoesNotContain("*", content);
    }

    [Fact]
    public void TextToSpeechService_BuildSpeechInput_MultipleBoldRuns_AllEmphasized()
    {
        var method = typeof(TextToSpeechService).GetMethod("BuildSpeechInput", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { "**First** and **second** emphasized.", "en-GB-RyanNeural" })!;
        var isSsml = (bool)result.GetType().GetProperty("IsSsml")!.GetValue(result)!;
        var content = (string)result.GetType().GetProperty("Content")!.GetValue(result)!;

        Assert.True(isSsml);
        Assert.Contains("<emphasis level=\"moderate\">First</emphasis>", content);
        Assert.Contains("<emphasis level=\"moderate\">second</emphasis>", content);
        Assert.DoesNotContain("**", content);
    }

    [Fact]
    public void TextToSpeechService_BuildSpeechInput_StrayAsterisks_Stripped()
    {
        var method = typeof(TextToSpeechService).GetMethod("BuildSpeechInput", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        // Unmatched ** should be stripped and not produce SSML.
        var result = method!.Invoke(null, new object[] { "Hello ** world", "en-GB-RyanNeural" })!;
        var isSsml = (bool)result.GetType().GetProperty("IsSsml")!.GetValue(result)!;
        var content = (string)result.GetType().GetProperty("Content")!.GetValue(result)!;

        Assert.False(isSsml);
        Assert.DoesNotContain("**", content);
        Assert.Contains("Hello", content);
        Assert.Contains("world", content);
    }

    [Fact]
    public void TextToSpeechService_BuildSpeechInput_XmlSpecialChars_Escaped()
    {
        var method = typeof(TextToSpeechService).GetMethod("BuildSpeechInput", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        // Bold word containing XML-unsafe text; rest contains & and < that must be escaped.
        var result = method!.Invoke(null, new object[] { "Use **bold & <strong>** tags.", "en-GB-RyanNeural" })!;
        var isSsml = (bool)result.GetType().GetProperty("IsSsml")!.GetValue(result)!;
        var content = (string)result.GetType().GetProperty("Content")!.GetValue(result)!;

        Assert.True(isSsml);
        Assert.DoesNotContain("&<", content);
        Assert.DoesNotContain("& <", content);
        Assert.Contains("&amp;", content);
        Assert.Contains("&lt;", content);
        Assert.DoesNotContain("**", content);
    }

    [Fact]
    public void TextToSpeechService_UrlsReplacedWithWebAddress()
    {
        var extract = typeof(TextToSpeechService).GetMethod("ExtractPreviewText", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(extract);

        var result = (string)extract!.Invoke(null, new object[] { "Visit https://www.example.com/some/long/path?query=1 for details.", 100 })!;
        Assert.DoesNotContain("https://", result);
        Assert.Contains("web address", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Visit", result);
        Assert.Contains("for details", result);
    }

    [Fact]
    public void TextToSpeechService_EmojisStrippedFromPreviewText()
    {
        var extract = typeof(TextToSpeechService).GetMethod("ExtractPreviewText", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(extract);

        var result = (string)extract!.Invoke(null, new object[] { "Hey there! 👋 I'd be happy to help 😊 with your request 🎉", 100 })!;
        Assert.DoesNotContain("👋", result);
        Assert.DoesNotContain("😊", result);
        Assert.DoesNotContain("🎉", result);
        Assert.Contains("Hey there", result);
        Assert.Contains("help", result);
    }

    [Fact]
    public void TextToSpeechService_MarkdownLinkUrlReplaced()
    {
        var extract = typeof(TextToSpeechService).GetMethod("ExtractPreviewText", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(extract);

        // Telegram HTML link text is stripped by StripHtmlTags; plain markdown links should also be covered.
        var result = (string)extract!.Invoke(null, new object[] { "See [map](https://goo.gl/maps/XR3L5YLadG12) for the location.", 100 })!;
        Assert.DoesNotContain("https://", result);
        Assert.Contains("web address", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TextToSpeechService_ExtractSpeechText_DoesNotTruncateWhenMaxWordsNotProvided()
    {
        var extract = typeof(TextToSpeechService).GetMethod("ExtractSpeechText", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(extract);

        var full = "This is the entire message that should not be cut off when generating full WAV file audio.";
        var result = (string)extract!.Invoke(null, new object?[] { full, null })!;

        Assert.Equal(full, result);
    }

    [Fact]
    public void TelegramMessageHandler_TryParsePronunciationPayload_SupportsAsAndEquals()
    {
        var parseMethod = typeof(TelegramMessageHandler).GetMethod("TryParsePronunciationPayload", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(parseMethod);

        var argsAs = new object?[] { "Tonbridge as Tunbridge", string.Empty, string.Empty, null };
        var successAs = (bool)parseMethod!.Invoke(null, argsAs)!;
        Assert.True(successAs);
        Assert.Equal("Tonbridge", argsAs[1]);
        Assert.Equal("Tunbridge", argsAs[2]);
        Assert.Null(argsAs[3]);

        var argsEquals = new object?[] { "Malling=Mawling", string.Empty, string.Empty, null };
        var successEquals = (bool)parseMethod!.Invoke(null, argsEquals)!;
        Assert.True(successEquals);
        Assert.Equal("Malling", argsEquals[1]);
        Assert.Equal("Mawling", argsEquals[2]);
        Assert.Null(argsEquals[3]);

        var argsWithIpa = new object?[] { "Ightham as Eyetum ipa /ˈaɪtəm/", string.Empty, string.Empty, null };
        var successWithIpa = (bool)parseMethod!.Invoke(null, argsWithIpa)!;
        Assert.True(successWithIpa);
        Assert.Equal("Ightham", argsWithIpa[1]);
        Assert.Equal("Eyetum", argsWithIpa[2]);
        Assert.Equal("ˈaɪtəm", argsWithIpa[3]);
    }

    [Fact]
    public void TelegramMessageHandler_TryParsePronunciationAddRequest_SupportsVoicePhrasing()
    {
        var parseMethod = typeof(TelegramMessageHandler).GetMethod("TryParsePronunciationAddRequest", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(parseMethod);

        var args = new object?[] { "add pronunciation for Ightham as Eyetum ipa ˈaɪtəm", string.Empty, string.Empty, null };
        var success = (bool)parseMethod!.Invoke(null, args)!;
        Assert.True(success);
        Assert.Equal("Ightham", args[1]);
        Assert.Equal("Eyetum", args[2]);
        Assert.Equal("ˈaɪtəm", args[3]);
    }

    [Fact]
    public void TelegramMessageHandler_IsSendTimeoutError_DetectsTimeoutMessage()
    {
        var method = typeof(TelegramMessageHandler).GetMethod("IsSendTimeoutError", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var timeoutException = new Exception("SendAndWaitAsync timed out after 00:01:00");
        var isTimeout = (bool)method!.Invoke(null, new object[] { timeoutException })!;
        Assert.True(isTimeout);

        var differentException = new Exception("some other failure");
        var isDifferentTimeout = (bool)method.Invoke(null, new object[] { differentException })!;
        Assert.False(isDifferentTimeout);
    }

    [Fact]
    public void TelegramApiClient_NormalizePlainTextTableForHtml_WrapsPipeTableInBackticks()
    {
        var method = typeof(TelegramApiClient).GetMethod("NormalizePlainTextTableForHtml", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var table = """
| ID | Name | Value |
|----|------|-------|
| 1  | Alice| 123   |
| 2  | Bob  | 456   |
""";

        var result = (string)method!.Invoke(null, new object[] { table })!;

        Assert.StartsWith("```", result);
        Assert.EndsWith("```", result.Trim());
        Assert.Contains("| ID | Name | Value |", result);
        Assert.DoesNotContain("<pre>", result);
    }

    [Fact]
    public void TelegramApiClient_NormalizePlainTextTableForHtml_ConvertsHtmlPreTableToMarkdownFencedBlock()
    {
        var method = typeof(TelegramApiClient).GetMethod("NormalizePlainTextTableForHtml", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var htmlTable = "<b>Found 2 rows</b>\n<pre>| ID | Name | Value |\n|----|------|-------|\n| 1 | Alice | 123 |\n| 2 | Bob | 456 |</pre>";
        var result = (string)method!.Invoke(null, new object[] { htmlTable })!;

        Assert.StartsWith("```", result);
        Assert.EndsWith("```", result.Trim());
        Assert.Contains("| ID | Name | Value |", result);
        Assert.DoesNotContain("<pre>", result);
    }

    [Fact]
    public void TelegramApiClient_ChunkTextPreservingMarkdownCode_KeepsFencedCodeBlocks()
    {
        var method = typeof(TelegramApiClient).GetMethod("ChunkTextPreservingMarkdownCode", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var inner = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"row{i:000}"));
        var source = $"```\n{inner}\n```";

        var result = (IEnumerable<string>)method!.Invoke(null, new object[] { source, 120 })!;
        var chunks = result.ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => {
            Assert.StartsWith("```", chunk);
            Assert.EndsWith("```", chunk);
        });

        var combined = string.Join("\n", chunks.Select(chunk => chunk.Trim('`', '\n')));
        Assert.Contains("row001", combined);
        Assert.Contains("row050", combined);
    }

    private static void LoadDotEnvIfPresent()
    {
        // Try repo root (developer running from workspace), then test binary base dir.
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env"),
        };

        foreach (var path in candidates.Where(File.Exists))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                var eq = trimmed.IndexOf('=');
                if (eq < 0) continue;
                var key = trimmed[..eq].Trim();
                var val = trimmed[(eq + 1)..].Trim().Trim('"').Trim('\'');
                if (!string.IsNullOrEmpty(key))
                    Environment.SetEnvironmentVariable(key, val);
            }
            break; // Use first found .env only.
        }
    }

    /// <summary>
    /// Integration test: actually synthesizes speech to the default audio device so you can hear the emphasis.
    /// Skipped automatically when AZURE_SPEECH_KEY or AZURE_SPEECH_REGION are not set.
    /// </summary>
    [Fact]
    public async Task TextToSpeechService_BoldEmphasis_AudibleSmokeTest()
    {
        // Load .env from repo root so Azure credentials are available without needing them set in the system environment.
        LoadDotEnvIfPresent();

        var key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
        var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION");

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region))
        {
            // Skip gracefully — credentials not available in this environment.
            return;
        }

        var tts = TextToSpeechService.FromEnvironment();

        // Phrase with bold markers: you should hear "very" and "important" spoken with emphasis.
        const string phrase = "This is a **very** important test. Please listen carefully. The word **important** should sound emphasised.";

        // force:true bypasses the ASSISTANT_TTS_ENABLED env check.
        await tts.TrySpeakPreviewAsync(phrase, CancellationToken.None, force: true);
    }
}
