using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace WorkspaceAgent.Integrations;

public sealed record JiraIssue(string Key, string Summary, string? Status, string? Assignee, string? Priority, string? IssueType, string? Updated);

public sealed record CreatedIssue(string Id, string Key, string Self);

/// <summary>
/// Jira Cloud REST API v3 client.
/// Search: POST /rest/api/3/search/jql (the enhanced JQL search endpoint). Create: POST /rest/api/3/issue
/// with an Atlassian Document Format description. Auth: basic (email + API token).
/// </summary>
public sealed class JiraClient(HttpClient http, AtlassianOptions options)
{
    private static readonly string[] Fields = ["summary", "status", "assignee", "priority", "issuetype", "updated"];

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, int maxResults, CancellationToken ct = default)
    {
        maxResults = Math.Clamp(maxResults, 1, 50);
        var body = new JsonObject
        {
            ["jql"] = jql,
            ["maxResults"] = maxResults,
            ["fields"] = new JsonArray(Fields.Select(f => (JsonNode)f).ToArray()),
        };
        using var resp = await HttpSupport.SendWithRetryAsync(http, () => Request(HttpMethod.Post, "/rest/api/3/search/jql", body), MaxRetryDelay, ct);
        var json = await HttpSupport.ReadJsonOrThrowAsync(resp, "jira", ct);

        return (json["issues"] as JsonArray ?? []).Select(i =>
        {
            var f = i!["fields"];
            return new JiraIssue(
                i["key"]!.GetValue<string>(),
                f?["summary"]?.GetValue<string>() ?? "",
                f?["status"]?["name"]?.GetValue<string>(),
                f?["assignee"]?["displayName"]?.GetValue<string>(),
                f?["priority"]?["name"]?.GetValue<string>(),
                f?["issuetype"]?["name"]?.GetValue<string>(),
                f?["updated"]?.GetValue<string>());
        }).ToList();
    }

    public async Task<CreatedIssue> CreateIssueAsync(string projectKey, string summary, string description, string issueType, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["project"] = new JsonObject { ["key"] = projectKey },
                ["summary"] = summary,
                ["issuetype"] = new JsonObject { ["name"] = issueType },
                ["description"] = Adf(description),
            },
        };
        // No retry on create: a timed-out POST may have succeeded, and a second one would duplicate the issue.
        using var resp = await http.SendAsync(Request(HttpMethod.Post, "/rest/api/3/issue", body), ct);
        var json = await HttpSupport.ReadJsonOrThrowAsync(resp, "jira", ct);
        return new CreatedIssue(json["id"]!.GetValue<string>(), json["key"]!.GetValue<string>(), json["self"]?.GetValue<string>() ?? "");
    }

    /// <summary>Plain text to an Atlassian Document Format doc: one paragraph per non-empty line.</summary>
    public static JsonObject Adf(string text) => new()
    {
        ["type"] = "doc",
        ["version"] = 1,
        ["content"] = new JsonArray(text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)
            .Select(l => (JsonNode)new JsonObject
            {
                ["type"] = "paragraph",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = l }),
            }).ToArray()),
    };

    private HttpRequestMessage Request(HttpMethod method, string path, JsonNode body) =>
        new(method, options.BaseUrl.TrimEnd('/') + path)
        {
            Content = JsonContent.Create(body),
            Headers = { Authorization = HttpSupport.Basic(options.Email, options.ApiToken), Accept = { new("application/json") } },
        };
}

public sealed record ConfluencePage(string Id, string Title, string Excerpt, string? Url, string? LastModified, string? Space);

/// <summary>
/// Confluence Cloud content search: GET /wiki/rest/api/search?cql=... The model supplies free text and an
/// optional space key; this client builds the CQL itself and escapes the text, so the model cannot write
/// arbitrary CQL.
/// </summary>
public sealed class ConfluenceClient(HttpClient http, AtlassianOptions options)
{
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public static string BuildCql(string text, string? spaceKey)
    {
        var cql = $"type = page AND text ~ \"{EscapeCql(text)}\"";
        if (!string.IsNullOrWhiteSpace(spaceKey)) cql += $" AND space = \"{EscapeCql(spaceKey)}\"";
        return cql;
    }

    private static string EscapeCql(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public async Task<IReadOnlyList<ConfluencePage>> SearchAsync(string text, string? spaceKey, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 25);
        var url = $"{options.BaseUrl.TrimEnd('/')}/wiki/rest/api/search?cql={Uri.EscapeDataString(BuildCql(text, spaceKey))}&limit={limit}";
        using var resp = await HttpSupport.SendWithRetryAsync(http, () => new HttpRequestMessage(HttpMethod.Get, url)
        {
            Headers = { Authorization = HttpSupport.Basic(options.Email, options.ApiToken), Accept = { new("application/json") } },
        }, MaxRetryDelay, ct);
        var json = await HttpSupport.ReadJsonOrThrowAsync(resp, "confluence", ct);

        var baseLink = json["_links"]?["base"]?.GetValue<string>();
        return (json["results"] as JsonArray ?? []).Select(r =>
        {
            var content = r!["content"];
            var rel = r["url"]?.GetValue<string>();
            return new ConfluencePage(
                content?["id"]?.GetValue<string>() ?? "",
                content?["title"]?.GetValue<string>() ?? r["title"]?.GetValue<string>() ?? "",
                StripHighlight(r["excerpt"]?.GetValue<string>() ?? ""),
                rel is null ? null : (baseLink ?? "") + rel,
                r["lastModified"]?.GetValue<string>(),
                r["resultGlobalContainer"]?["title"]?.GetValue<string>());
        }).ToList();
    }

    private static string StripHighlight(string s) => s.Replace("@@@hl@@@", "").Replace("@@@endhl@@@", "");
}
