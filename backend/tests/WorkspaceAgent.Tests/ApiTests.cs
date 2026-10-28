using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace WorkspaceAgent.Tests;

public sealed record SseEvent(string Type, JsonNode Data);

/// <summary>
/// The ASP.NET Core host end to end: scripted model (demo/demo-script.json), real tool clients pointed at
/// WireMock, real governed warehouse. Exercises the SSE contract in docs/api-contract.md and the approval gate.
/// </summary>
public sealed class ApiTests : IDisposable
{
    private readonly WireMockServer _stubs = WireMockServer.Start();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"wa-api-{Guid.NewGuid():n}.db");

    public ApiTests()
    {
        _stubs.Given(Request.Create().WithPath("/rest/api/3/issue").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id = "10141", key = "MODEM-141", self = "http://stub/rest/api/3/issue/10141" }));
        _stubs.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                issues = new[] { new { id = "1", key = "MODEM-131", fields = new { summary = "Throughput regression", status = new { name = "In Progress" } } } },
            }));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Atlassian:BaseUrl", _stubs.Url);
            b.UseSetting("Graph:BaseUrl", _stubs.Url + "/graph/v1.0");
            b.UseSetting("Warehouse:Path", _db);
            b.UseSetting("WORKSPACE_AGENT_MODEL", "scripted");
            b.UseSetting("Model:ScriptPath", Path.Combine(AppContext.BaseDirectory, "demo", "demo-script.json"));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _stubs.Stop();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch (IOException) { }
    }

    private async Task<List<SseEvent>> PostSse(string path, object body, string user = "alice")
    {
        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Demo-User", user);
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);
        return Parse(await resp.Content.ReadAsStringAsync());
    }

    public static List<SseEvent> Parse(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(block =>
        {
            var lines = block.Split('\n');
            var type = lines.First(l => l.StartsWith("event: "))["event: ".Length..];
            var data = lines.First(l => l.StartsWith("data: "))["data: ".Length..];
            return new SseEvent(type, JsonNode.Parse(data)!);
        }).ToList();

    private int IssueCreates => _stubs.LogEntries.Count(e => e.RequestMessage!.Path == "/rest/api/3/issue");

    [Fact]
    public async Task SqlQuestionStreamsAGovernedResultTable()
    {
        var events = await PostSse("/api/chat", new { message = "How many open defects per program?" });

        Assert.Equal("session", events[0].Type);
        Assert.Equal("done", events[^1].Type);
        Assert.Equal("end_turn", events[^1].Data["stopReason"]!.GetValue<string>());
        var sql = events.Single(e => e.Type == "sql_result").Data;
        Assert.Contains("defects: program_id IN (SELECT program_id FROM programs WHERE business_unit = 'Modem')",
            sql["appliedFilters"]!.AsArray().Select(f => f!.GetValue<string>()));
        var names = sql["rows"]!.AsArray().Select(r => r![0]!.GetValue<string>()).ToList();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.Contains(n, new[] { "5G Modem Gen 5", "NTN Satellite Link", "RF Front-End Gen 4" }));
        Assert.Contains(events, e => e.Type == "text");
    }

    [Fact]
    public async Task RestrictedColumnIsRejectedBeforeExecution()
    {
        var events = await PostSse("/api/chat", new { message = "what are the salaries?" }, user: "carol");
        var rejected = events.Single(e => e.Type == "sql_rejected").Data;
        Assert.Contains("salary_usd", rejected["reasons"]!.ToJsonString());
        Assert.DoesNotContain(events, e => e.Type == "sql_result");
        Assert.False(events.Single(e => e.Type == "tool_result").Data["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WriteToolPausesForApprovalAndApprovedCallCreatesTheIssue()
    {
        var first = await PostSse("/api/chat", new { message = "please file a bug for the NTN handover drop" });

        var approval = first.Single(e => e.Type == "approval_required").Data;
        Assert.Equal("jira_create_issue", approval["tool"]!.GetValue<string>());
        Assert.True(first.Single(e => e.Type == "tool_call").Data["requiresApproval"]!.GetValue<bool>());
        Assert.Equal("awaiting_approval", first[^1].Data["stopReason"]!.GetValue<string>());
        Assert.Equal(0, IssueCreates);

        var sessionId = first[0].Data["sessionId"]!.GetValue<string>();
        var callId = approval["callId"]!.GetValue<string>();
        var second = await PostSse($"/api/chat/{sessionId}/approvals/{callId}", new { approved = true });

        Assert.Equal(1, IssueCreates);
        var result = second.Single(e => e.Type == "tool_result").Data;
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal("created MODEM-141", result["summary"]!.GetValue<string>());
        Assert.Equal("end_turn", second[^1].Data["stopReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task RejectedCallIsNeverExecuted()
    {
        var first = await PostSse("/api/chat", new { message = "create an issue for the handover drop" });
        var sessionId = first[0].Data["sessionId"]!.GetValue<string>();
        var callId = first.Single(e => e.Type == "approval_required").Data["callId"]!.GetValue<string>();

        var second = await PostSse($"/api/chat/{sessionId}/approvals/{callId}", new { approved = false });

        Assert.Equal(0, IssueCreates);
        var result = second.Single(e => e.Type == "tool_result").Data;
        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Contains("not executed", result["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnotherUserCannotApproveSomeoneElsesCall()
    {
        var first = await PostSse("/api/chat", new { message = "file a ticket for the handover drop" });
        var sessionId = first[0].Data["sessionId"]!.GetValue<string>();
        var callId = first.Single(e => e.Type == "approval_required").Data["callId"]!.GetValue<string>();

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/chat/{sessionId}/approvals/{callId}") { Content = JsonContent.Create(new { approved = true }) };
        req.Headers.Add("X-Demo-User", "bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(req)).StatusCode);
        Assert.Equal(0, IssueCreates);
    }

    [Fact]
    public async Task ReadToolCallsTheStubbedJiraSearch()
    {
        var events = await PostSse("/api/chat", new { message = "what are the open jira issues?" });
        var result = events.Single(e => e.Type == "tool_result").Data;
        Assert.Equal("jira_search_issues", result["tool"]!.GetValue<string>());
        Assert.Equal("MODEM-131", result["data"]![0]!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownUserIsUnauthorized()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users")).StatusCode);
        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "hi" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task CodeAskCitesTheRetrievedChunks()
    {
        var events = await PostSse("/api/code/ask", new
        {
            question = "where is the BM25 scorer?",
            chunks = new[] { new { path = "src/indexer/bm25.ts", startLine = 10, endLine = 42, text = "export function score() {}" } },
        });
        var text = string.Concat(events.Where(e => e.Type == "text").Select(e => e.Data["delta"]!.GetValue<string>()));
        Assert.Contains("[src/indexer/bm25.ts:10-42]", text);
        Assert.Equal("end_turn", events[^1].Data["stopReason"]!.GetValue<string>());
    }
}
