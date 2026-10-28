using System.Collections.Concurrent;
using System.Text.Json.Nodes;

// Local stand-in for Jira Cloud, Confluence Cloud and Microsoft Graph, for the demo only.
// It serves the response shapes the real APIs document for the endpoints the agent calls, with canned
// data. It is not an emulator: JQL/CQL/$search are matched loosely, and there is no permission model
// beyond requiring an Authorization header.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var issues = new ConcurrentDictionary<string, JsonObject>();
int nextIssue = 140;
void Seed(string key, string summary, string status, string priority, string assignee, string type) =>
    issues[key] = Issue(key, summary, status, priority, assignee, type);
Seed("MODEM-131", "Throughput regression on carrier aggregation build 5.2", "In Progress", "Highest", "Alice Chen", "Bug");
Seed("MODEM-127", "NTN link budget doc out of date", "To Do", "Medium", "Farid Khan", "Task");
Seed("MODEM-124", "Flaky RF calibration test on release branch", "In Review", "High", "Alice Chen", "Bug");
Seed("MODEM-118", "Power profiling for idle mode", "Done", "Low", "Grace Ito", "Story");
Seed("CAM-88", "ISP v7 HDR merge artifacts", "In Progress", "High", "Bob Patel", "Bug");

app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Headers.Authorization.Any()) { ctx.Response.StatusCode = 401; return; }
    await next();
});

// ---- Jira Cloud REST v3
app.MapPost("/rest/api/3/search/jql", async (HttpRequest req) =>
{
    var body = (await JsonNode.ParseAsync(req.Body))!;
    var jql = body["jql"]?.GetValue<string>() ?? "";
    int max = body["maxResults"]?.GetValue<int>() ?? 50;
    var project = System.Text.RegularExpressions.Regex.Match(jql, @"project\s*=\s*""?(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    bool notDone = jql.Contains("!= Done", StringComparison.OrdinalIgnoreCase);
    var hits = issues.Values
        .Where(i => !project.Success || i["key"]!.GetValue<string>().StartsWith(project.Groups[1].Value + "-", StringComparison.OrdinalIgnoreCase))
        .Where(i => !notDone || i["fields"]!["status"]!["name"]!.GetValue<string>() != "Done")
        .OrderBy(i => i["key"]!.GetValue<string>())
        .Take(max)
        .Select(i => i.DeepClone())
        .ToArray();
    return Results.Ok(new JsonObject { ["issues"] = new JsonArray(hits), ["isLast"] = true });
});

app.MapPost("/rest/api/3/issue", async (HttpRequest req) =>
{
    var f = (await JsonNode.ParseAsync(req.Body))?["fields"];
    var project = f?["project"]?["key"]?.GetValue<string>();
    var summary = f?["summary"]?.GetValue<string>();
    if (project is null || string.IsNullOrWhiteSpace(summary))
        return Results.BadRequest(new JsonObject { ["errorMessages"] = new JsonArray(), ["errors"] = new JsonObject { ["summary"] = "You must specify a summary of the issue." } });
    var key = $"{project}-{Interlocked.Increment(ref nextIssue)}";
    issues[key] = Issue(key, summary, "To Do", "Medium", "Unassigned", f?["issuetype"]?["name"]?.GetValue<string>() ?? "Task");
    var id = (10000 + nextIssue).ToString();
    return Results.Created($"/rest/api/3/issue/{id}", new JsonObject { ["id"] = id, ["key"] = key, ["self"] = $"{req.Scheme}://{req.Host}/rest/api/3/issue/{id}" });
});

// ---- Confluence Cloud search
var pages = new[]
{
    ("1001", "NTN handover runbook", "MODEM", "Steps to recover the satellite link after a cell handover"),
    ("1002", "Carrier aggregation design doc", "MODEM", "Design of the CA scheduler for 5G Modem Gen 5"),
    ("1003", "ISP v7 tuning guide", "CAM", "HDR merge and tone mapping parameters"),
};
app.MapGet("/wiki/rest/api/search", (string cql, int? limit) =>
{
    var text = System.Text.RegularExpressions.Regex.Match(cql, "text ~ \"((?:[^\"\\\\]|\\\\.)*)\"").Groups[1].Value;
    var space = System.Text.RegularExpressions.Regex.Match(cql, "space = \"([^\"]+)\"").Groups[1].Value;
    var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var results = pages
        .Where(p => space.Length == 0 || p.Item3 == space)
        .Where(p => words.Length == 0 || words.Any(w => (p.Item2 + " " + p.Item4).Contains(w, StringComparison.OrdinalIgnoreCase)))
        .Take(limit ?? 25)
        .Select(p => (JsonNode)new JsonObject
        {
            ["content"] = new JsonObject { ["id"] = p.Item1, ["type"] = "page", ["status"] = "current", ["title"] = p.Item2 },
            ["title"] = p.Item2,
            ["excerpt"] = $"@@@hl@@@{p.Item4}@@@endhl@@@",
            ["url"] = $"/spaces/{p.Item3}/pages/{p.Item1}",
            ["resultGlobalContainer"] = new JsonObject { ["title"] = p.Item3 },
            ["lastModified"] = "2026-10-20T16:05:00.000Z",
        }).ToArray();
    return Results.Ok(new JsonObject
    {
        ["results"] = new JsonArray(results), ["size"] = results.Length, ["start"] = 0, ["limit"] = limit ?? 25,
        ["_links"] = new JsonObject { ["base"] = "http://localhost:5090/wiki" },
    });
});

// ---- Microsoft Graph v1.0 (/me)
app.MapGet("/graph/v1.0/me/calendarView", () => Results.Ok(Values(
    Event("Modem program sync", "2026-10-28T16:00:00.0000000", "2026-10-28T16:30:00.0000000", "Farid Khan", "Teams"),
    Event("NTN field test review", "2026-10-29T18:00:00.0000000", "2026-10-29T19:00:00.0000000", "Priya Raman", "Bldg Q, 3rd floor"),
    Event("1:1 with manager", "2026-10-30T17:00:00.0000000", "2026-10-30T17:30:00.0000000", "Grace Ito", "Teams"))));

app.MapGet("/graph/v1.0/me/messages", () => Results.Ok(Values(
    Mail("Field test results: NTN handover", "priya.raman@example.com", "2026-10-27T22:14:00Z", "Link dropped twice after handover at site B; logs attached."),
    Mail("Re: field test logistics", "farid.khan@example.com", "2026-10-26T15:02:00Z", "Van leaves at 7am, bring the spare antenna."))));

app.MapGet("/graph/v1.0/me/people", () => Results.Ok(Values(
    new JsonObject
    {
        ["displayName"] = "Priya Raman", ["jobTitle"] = "RF Systems Engineer", ["department"] = "Modem",
        ["scoredEmailAddresses"] = new JsonArray(new JsonObject { ["address"] = "priya.raman@example.com", ["relevanceScore"] = 8.0 }),
    })));

app.Run();

static JsonObject Issue(string key, string summary, string status, string priority, string assignee, string type) => new()
{
    ["id"] = key.GetHashCode().ToString(),
    ["key"] = key,
    ["fields"] = new JsonObject
    {
        ["summary"] = summary,
        ["status"] = new JsonObject { ["name"] = status },
        ["priority"] = new JsonObject { ["name"] = priority },
        ["assignee"] = new JsonObject { ["displayName"] = assignee },
        ["issuetype"] = new JsonObject { ["name"] = type },
        ["updated"] = "2026-10-27T09:30:00.000+0000",
    },
};

static JsonObject Values(params JsonObject[] items) => new() { ["value"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()) };

static JsonObject Event(string subject, string start, string end, string organizer, string location) => new()
{
    ["subject"] = subject,
    ["start"] = new JsonObject { ["dateTime"] = start, ["timeZone"] = "UTC" },
    ["end"] = new JsonObject { ["dateTime"] = end, ["timeZone"] = "UTC" },
    ["organizer"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = organizer } },
    ["location"] = new JsonObject { ["displayName"] = location },
};

static JsonObject Mail(string subject, string from, string received, string preview) => new()
{
    ["subject"] = subject,
    ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = from } },
    ["receivedDateTime"] = received,
    ["bodyPreview"] = preview,
};
