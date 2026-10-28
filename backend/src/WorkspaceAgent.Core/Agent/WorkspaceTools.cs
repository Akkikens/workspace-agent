using System.Text.Json;
using System.Text.Json.Nodes;
using WorkspaceAgent.Identity;
using WorkspaceAgent.Integrations;
using WorkspaceAgent.Llm;
using WorkspaceAgent.Sql;

namespace WorkspaceAgent.Agent;

/// <param name="ModelContent">What the model sees (compact JSON). Tool output is data, never instructions.</param>
/// <param name="Extra">An additional UI event (sql_result / sql_rejected), if any.</param>
public sealed record ToolOutcome(bool Ok, string Summary, string ModelContent, object? Data, (string Type, object Data)? Extra = null);

/// <summary>
/// The agent's tools. Read tools run immediately; <c>jira_create_issue</c> is the only write and is gated
/// on human approval by <see cref="AgentRunner"/> before <see cref="ExecuteAsync"/> is ever called.
/// </summary>
public sealed class WorkspaceTools(JiraClient jira, ConfluenceClient confluence, GraphClient graph, SqlGovernor governor, Warehouse warehouse)
{
    public const string QuerySql = "query_program_data";
    public const string JiraCreate = "jira_create_issue";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool RequiresApproval(string tool) => tool == JiraCreate;

    public IReadOnlyList<ToolSpec> Specs { get; } =
    [
        Spec("jira_search_issues", "Search Jira issues with JQL. Returns key, summary, status, assignee, priority.",
            ("jql", "string", "JQL, e.g. project = MODEM AND statusCategory != Done ORDER BY priority DESC", true),
            ("max_results", "integer", "1-50, default 10", false)),
        Spec(JiraCreate, "Create a Jira issue. A human must approve this before it runs.",
            ("project_key", "string", "Jira project key, e.g. MODEM", true),
            ("summary", "string", "one-line summary", true),
            ("description", "string", "plain-text description", true),
            ("issue_type", "string", "Task, Bug or Story; default Task", false)),
        Spec("confluence_search", "Full-text search of Confluence pages. Returns title, excerpt, link.",
            ("query", "string", "words to search for", true),
            ("space", "string", "optional space key", false),
            ("limit", "integer", "1-25, default 5", false)),
        Spec("graph_calendar", "The user's calendar events between two instants (Microsoft Graph calendarView).",
            ("start", "string", "ISO 8601 start, e.g. 2026-10-28T00:00:00Z", true),
            ("end", "string", "ISO 8601 end", true)),
        Spec("graph_mail", "The user's recent mail (subject, sender, preview), optionally filtered by a search phrase.",
            ("search", "string", "optional search phrase", false),
            ("top", "integer", "1-25, default 10", false)),
        Spec("graph_people", "People relevant to the user, matched by name (Microsoft Graph people API).",
            ("query", "string", "name to search for", true)),
        Spec(QuerySql, "Run one read-only SQLite SELECT over the governed program warehouse described in the system prompt. " +
                       "Row-level security and column grants are enforced by the server.",
            ("sql", "string", "a single SELECT statement", true)),
    ];

    public async Task<ToolOutcome> ExecuteAsync(ToolCall call, UserContext user, CancellationToken ct)
    {
        var a = call.Args;
        switch (call.Name)
        {
            case "jira_search_issues":
            {
                var issues = await jira.SearchAsync(Str(a, "jql"), Int(a, "max_results", 10), ct);
                return Ok($"{issues.Count} Jira issue(s)", issues);
            }
            case JiraCreate:
            {
                var created = await jira.CreateIssueAsync(Str(a, "project_key"), Str(a, "summary"), Str(a, "description"),
                    OptStr(a, "issue_type") ?? "Task", ct);
                return Ok($"created {created.Key}", created);
            }
            case "confluence_search":
            {
                var pages = await confluence.SearchAsync(Str(a, "query"), OptStr(a, "space"), Int(a, "limit", 5), ct);
                return Ok($"{pages.Count} Confluence page(s)", pages);
            }
            case "graph_calendar":
            {
                var events = await graph.CalendarViewAsync(DateTimeOffset.Parse(Str(a, "start")), DateTimeOffset.Parse(Str(a, "end")), ct);
                return Ok($"{events.Count} calendar event(s)", events);
            }
            case "graph_mail":
            {
                var mail = await graph.ListMessagesAsync(OptStr(a, "search"), Int(a, "top", 10), ct);
                return Ok($"{mail.Count} message(s)", mail);
            }
            case "graph_people":
            {
                var people = await graph.FindPeopleAsync(Str(a, "query"), 10, ct);
                return Ok($"{people.Count} people", people);
            }
            case QuerySql:
                return RunSql(call.Id, Str(a, "sql"), user);
            default:
                return new ToolOutcome(false, $"unknown tool {call.Name}", $"error: unknown tool {call.Name}", null);
        }
    }

    private ToolOutcome RunSql(string callId, string sql, UserContext user)
    {
        var decision = governor.Govern(sql, user);
        if (!decision.Allowed)
        {
            var rejected = new { callId, proposedSql = sql, reasons = decision.Reasons };
            return new ToolOutcome(false, "query rejected by governance",
                "The query was rejected and not executed: " + string.Join("; ", decision.Reasons), null, ("sql_rejected", rejected));
        }
        var q = decision.Query!;
        QueryResult result;
        try { result = warehouse.ExecuteGoverned(q, governor.Layer.Functions); }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or AuthorizerDeniedException)
        {
            var rejected = new { callId, proposedSql = sql, reasons = new[] { e.Message } };
            return new ToolOutcome(false, "query failed", "The query failed: " + e.Message, null, ("sql_rejected", rejected));
        }

        var payload = new
        {
            callId,
            proposedSql = sql,
            executedSql = q.Sql,
            columns = result.Columns,
            rows = result.Rows,
            rowCount = result.Rows.Count,
            truncated = result.Rows.Count >= q.Limit,
            appliedFilters = q.AppliedFilters,
        };
        var preview = new { columns = result.Columns, rows = result.Rows.Take(50), rowCount = result.Rows.Count, payload.truncated };
        return new ToolOutcome(true, $"{result.Rows.Count} row(s)", JsonSerializer.Serialize(preview, Json), null, ("sql_result", payload));
    }

    private static ToolOutcome Ok(string summary, object data) =>
        new(true, summary, JsonSerializer.Serialize(data, Json), data);

    private static string Str(JsonObject a, string k) =>
        OptStr(a, k) ?? throw new ArgumentException($"missing required argument '{k}'");

    private static string? OptStr(JsonObject a, string k) =>
        a[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int Int(JsonObject a, string k, int fallback) =>
        a[k] is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<string>(out var s) && int.TryParse(s, out i))) ? i : fallback;

    private static ToolSpec Spec(string name, string description, params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props) properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        return new ToolSpec(name, description, new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(props.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray()),
        });
    }
}
