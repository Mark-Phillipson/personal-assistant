using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Service for managing personal todos stored as GitHub Issues in the configured Personal-Todos repository.
/// </summary>
internal sealed class GitHubTodosService
{
    private readonly string? _token;
    private readonly string? _owner;
    private readonly string? _repo;
    private readonly HttpClient _http;
    private readonly bool _autoCreate;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private GitHubTodosService(string? token, string? owner, string? repo, bool autoCreate)
    {
        _token = token;
        _owner = owner;
        _repo = repo;
        _autoCreate = autoCreate;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("personal-assistant/1.0");
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_token)
        && !string.IsNullOrWhiteSpace(_owner)
        && !string.IsNullOrWhiteSpace(_repo);

    public static GitHubTodosService FromEnvironment()
    {
        var token = EnvironmentSettings.ReadOptionalString("GITHUB_PERSONAL_TODOS_TOKEN")
                    ?? EnvironmentSettings.ReadOptionalString("GITHUB_TOKEN");

        var repoSlug = EnvironmentSettings.ReadOptionalString("GITHUB_TODOS_REPO")
                       ?? "Mark-Phillipson/Personal-Todos";

        var autoCreate = EnvironmentSettings.ReadBool("GITHUB_TODOS_AUTO_CREATE", true);

        string? owner = null;
        string? repo = null;

        if (!string.IsNullOrWhiteSpace(repoSlug) && repoSlug.Contains('/'))
        {
            var parts = repoSlug.Split('/', 2);
            owner = parts[0].Trim();
            repo = parts[1].Trim();
        }

        return new GitHubTodosService(token, owner, repo, autoCreate);
    }

    public bool AutoCreateEnabled => _autoCreate;

    public string GetSetupStatusText()
    {
        if (!IsConfigured)
        {
            return "GitHub Personal Todos is not configured. Set GITHUB_PERSONAL_TODOS_TOKEN (or GITHUB_TOKEN) and optionally GITHUB_TODOS_REPO (default: Mark-Phillipson/Personal-Todos) in your .env file.";
        }

        var autoText = _autoCreate ? "auto-create enabled" : "auto-create disabled";
        return $"GitHub Personal Todos configured: repository={_owner}/{_repo} ({autoText}).";
    }

    public async Task<string> ListOpenIssuesAsync(string? label = null, int maxResults = 20, bool htmlFormat = true, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return GetSetupStatusText();

        var queryParams = $"state=open&per_page={Math.Min(maxResults, 100)}";
        if (!string.IsNullOrWhiteSpace(label))
            queryParams += $"&labels={Uri.EscapeDataString(label)}";

        var url = $"https://api.github.com/repos/{_owner}/{_repo}/issues?{queryParams}";

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Failed to call GitHub API: {ex.Message}";
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return $"GitHub API error ({response.StatusCode}): {errBody}";
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var issues = JsonSerializer.Deserialize<List<GitHubIssue>>(json, JsonOptions);

        if (issues == null || issues.Count == 0)
            return "No open personal todos found.";

        if (htmlFormat)
        {
            var lines = new List<string> { "<pre>", $"{"#",-5} {"Title",-50} {"Labels",-25}", new string('-', 82) };
            foreach (var issue in issues)
            {
                var labelsText = issue.Labels?.Count > 0
                    ? string.Join(", ", issue.Labels.Select(l => l.Name))
                    : "";
                lines.Add($"{issue.Number,-5} {Truncate(issue.Title ?? "", 50),-50} {Truncate(labelsText, 25),-25}");
            }
            lines.Add("</pre>");
            return string.Join("\n", lines);
        }

        var rows = issues.Select(i =>
        {
            var labelsText = i.Labels?.Count > 0 ? string.Join(", ", i.Labels.Select(l => l.Name)) : "";
            return $"#{i.Number} {i.Title}{(string.IsNullOrWhiteSpace(labelsText) ? "" : $" [{labelsText}]")}";
        });
        return $"Open Personal Todos ({issues.Count}):\n" + string.Join("\n", rows);
    }

    public async Task<string> AddTodoAsync(string title, string? body = null, string? label = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return GetSetupStatusText();

        if (string.IsNullOrWhiteSpace(title))
            return "Todo title is required.";

        var labels = new List<string>();
        if (!string.IsNullOrWhiteSpace(label))
            labels.Add(label.Trim());

        var payload = new GitHubCreateIssueRequest
        {
            Title = title.Trim(),
            Body = string.IsNullOrWhiteSpace(body) ? null : body.Trim(),
            Labels = labels.Count > 0 ? labels : null
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(
                $"https://api.github.com/repos/{_owner}/{_repo}/issues",
                content,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Failed to call GitHub API: {ex.Message}";
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return $"GitHub API error ({response.StatusCode}): {errBody}";
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var created = JsonSerializer.Deserialize<GitHubIssue>(json, JsonOptions);
        return $"Personal todo created: #{created?.Number} — {created?.Title}\n{created?.HtmlUrl}";
    }

    public async Task<string> UpdateTodoAsync(int issueNumber, string? newTitle = null, string? newBody = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return GetSetupStatusText();

        if (issueNumber <= 0)
            return "A valid issue number (from list_personal_todos) is required.";

        if (string.IsNullOrWhiteSpace(newTitle) && string.IsNullOrWhiteSpace(newBody))
            return "Provide at least a new title or new body to update.";

        var patch = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(newTitle))
            patch["title"] = newTitle.Trim();
        if (!string.IsNullOrWhiteSpace(newBody))
            patch["body"] = newBody.Trim();

        var content = new StringContent(
            JsonSerializer.Serialize(patch),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.PatchAsync(
                $"https://api.github.com/repos/{_owner}/{_repo}/issues/{issueNumber}",
                content,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Failed to call GitHub API: {ex.Message}";
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return $"GitHub API error ({response.StatusCode}): {errBody}";
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var updated = JsonSerializer.Deserialize<GitHubIssue>(json, JsonOptions);
        return $"Personal todo #{updated?.Number} updated: {updated?.Title}\n{updated?.HtmlUrl}";
    }

    public async Task<string> CloseTodoAsync(int issueNumber, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return GetSetupStatusText();

        if (issueNumber <= 0)
            return "A valid issue number (from list_personal_todos) is required.";

        var patch = new Dictionary<string, string> { ["state"] = "closed", ["state_reason"] = "completed" };
        var content = new StringContent(
            JsonSerializer.Serialize(patch),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.PatchAsync(
                $"https://api.github.com/repos/{_owner}/{_repo}/issues/{issueNumber}",
                content,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Failed to call GitHub API: {ex.Message}";
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return $"GitHub API error ({response.StatusCode}): {errBody}";
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var closed = JsonSerializer.Deserialize<GitHubIssue>(json, JsonOptions);
        return $"Personal todo #{closed?.Number} marked complete (closed): {closed?.Title}";
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    // ─── GitHub API models ───────────────────────────────────────────────────

    private sealed class GitHubIssue
    {
        public int Number { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public string? State { get; set; }
        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
        public List<GitHubLabel>? Labels { get; set; }
    }

    private sealed class GitHubLabel
    {
        public string? Name { get; set; }
    }

    private sealed class GitHubCreateIssueRequest
    {
        public string? Title { get; set; }
        public string? Body { get; set; }
        public List<string>? Labels { get; set; }
    }
}
