using System.Text;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WorkspaceAgent.Integrations;

namespace WorkspaceAgent.Tests;

/// <summary>
/// The HTTP clients against WireMock stubs that return the documented response shapes. These check
/// request shape (path, method, auth, body, query) and parsing; they do not prove behavior against a
/// real Atlassian or Microsoft 365 tenant.
/// </summary>
public sealed class IntegrationClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _http = new();
    private AtlassianOptions Atlassian => new() { BaseUrl = _server.Url!, Email = "dev@example.com", ApiToken = "tok" };
    private static string ExpectedBasic => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("dev@example.com:tok"));

    public void Dispose() { _server.Stop(); _http.Dispose(); }

    private JsonNode LastBody() => JsonNode.Parse(_server.LogEntries.Last().RequestMessage!.Body!)!;
    private string LastHeader(string name) => _server.LogEntries.Last().RequestMessage!.Headers![name].First();

    [Fact]
    public async Task JiraSearchPostsJqlWithBasicAuthAndParsesIssues()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                issues = new[]
                {
                    new { id = "1", key = "MODEM-1", fields = new { summary = "Throughput drop", status = new { name = "In Progress" },
                        assignee = new { displayName = "Alice Chen" }, priority = new { name = "High" }, issuetype = new { name = "Bug" }, updated = "2026-10-27" } },
                },
                isLast = true,
            }));

        var issues = await new JiraClient(_http, Atlassian).SearchAsync("project = MODEM", 500);

        var issue = Assert.Single(issues);
        Assert.Equal(("MODEM-1", "In Progress", "Alice Chen", "High"), (issue.Key, issue.Status, issue.Assignee, issue.Priority));
        Assert.Equal(ExpectedBasic, LastHeader("Authorization"));
        var body = LastBody();
        Assert.Equal("project = MODEM", body["jql"]!.GetValue<string>());
        Assert.Equal(50, body["maxResults"]!.GetValue<int>()); // clamped
        Assert.Contains("summary", body["fields"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    [Fact]
    public async Task JiraCreateSendsAdfDescriptionAndReturnsTheKey()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/issue").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id = "10042", key = "MODEM-42", self = "https://x/rest/api/3/issue/10042" }));

        var created = await new JiraClient(_http, Atlassian).CreateIssueAsync("MODEM", "Link drops", "line one\nline two", "Bug");

        Assert.Equal("MODEM-42", created.Key);
        var f = LastBody()["fields"]!;
        Assert.Equal("MODEM", f["project"]!["key"]!.GetValue<string>());
        Assert.Equal("Bug", f["issuetype"]!["name"]!.GetValue<string>());
        Assert.Equal("doc", f["description"]!["type"]!.GetValue<string>());
        Assert.Equal(2, f["description"]!["content"]!.AsArray().Count);
        Assert.Equal("line two", f["description"]!["content"]![1]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task JiraValidationErrorsSurfaceTheFieldMessage()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/issue").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBodyAsJson(new { errorMessages = Array.Empty<string>(), errors = new { summary = "You must specify a summary of the issue." } }));

        var e = await Assert.ThrowsAsync<IntegrationException>(() => new JiraClient(_http, Atlassian).CreateIssueAsync("MODEM", "", "", "Task"));
        Assert.Contains("summary: You must specify a summary", e.Message);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, e.Status);
    }

    [Fact]
    public async Task JiraSearchRetriesOnceAfter429()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost()).InScenario("rate").WillSetStateTo("limited")
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "0"));
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost()).InScenario("rate").WhenStateIs("limited")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { issues = Array.Empty<object>() }));

        var issues = await new JiraClient(_http, Atlassian).SearchAsync("project = X", 10);

        Assert.Empty(issues);
        Assert.Equal(2, _server.LogEntries.Count());
    }

    [Fact]
    public async Task JiraCreateIsNeverRetried()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/issue").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503).WithHeader("Retry-After", "0").WithBody("busy"));

        await Assert.ThrowsAsync<IntegrationException>(() => new JiraClient(_http, Atlassian).CreateIssueAsync("MODEM", "s", "d", "Task"));
        Assert.Single(_server.LogEntries);
    }

    [Fact]
    public void ConfluenceCqlEscapesQuotesSoTextCannotBreakOut()
    {
        var cql = ConfluenceClient.BuildCql("handover\" OR space = \"HR", "MODEM");
        Assert.Equal("type = page AND text ~ \"handover\\\" OR space = \\\"HR\" AND space = \"MODEM\"", cql);
    }

    [Fact]
    public async Task ConfluenceSearchParsesResultsAndBuildsAbsoluteLinks()
    {
        _server.Given(Request.Create().WithPath("/wiki/rest/api/search").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                results = new[]
                {
                    new { content = new { id = "1001", type = "page", title = "NTN handover runbook" }, title = "NTN handover runbook",
                        excerpt = "@@@hl@@@handover@@@endhl@@@ steps", url = "/spaces/MODEM/pages/1001", lastModified = "2026-10-20T16:05:00.000Z",
                        resultGlobalContainer = new { title = "MODEM" } },
                },
                _links = new { @base = "https://example.atlassian.net/wiki" },
            }));

        var pages = await new ConfluenceClient(_http, Atlassian).SearchAsync("handover", "MODEM", 5);

        var page = Assert.Single(pages);
        Assert.Equal("handover steps", page.Excerpt);
        Assert.Equal("https://example.atlassian.net/wiki/spaces/MODEM/pages/1001", page.Url);
        var req = _server.LogEntries.Last().RequestMessage!;
        Assert.Equal("type = page AND text ~ \"handover\" AND space = \"MODEM\"", req.Query!["cql"].First());
        Assert.Equal("5", req.Query["limit"].First());
        Assert.Equal(ExpectedBasic, req.Headers!["Authorization"].First());
    }

    private GraphClient Graph() => new(_http, new GraphOptions { BaseUrl = _server.Url + "/v1.0" }, new StaticGraphToken("graph-token"));

    [Fact]
    public async Task GraphCalendarViewSendsWindowBearerAndUtcPreference()
    {
        _server.Given(Request.Create().WithPath("/v1.0/me/calendarView").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                value = new[]
                {
                    new { subject = "Program sync", start = new { dateTime = "2026-10-28T16:00:00", timeZone = "UTC" },
                        end = new { dateTime = "2026-10-28T16:30:00", timeZone = "UTC" },
                        organizer = new { emailAddress = new { name = "Farid Khan" } }, location = new { displayName = "Teams" } },
                },
            }));

        var events = await Graph().CalendarViewAsync(DateTimeOffset.Parse("2026-10-28T00:00:00Z"), DateTimeOffset.Parse("2026-10-29T00:00:00Z"));

        Assert.Equal("Farid Khan", Assert.Single(events).Organizer);
        var req = _server.LogEntries.Last().RequestMessage!;
        Assert.Equal("2026-10-28T00:00:00Z", req.Query!["startDateTime"].First());
        Assert.Equal("2026-10-29T00:00:00Z", req.Query["endDateTime"].First());
        Assert.Equal("Bearer graph-token", req.Headers!["Authorization"].First());
        Assert.Equal("outlook.timezone=\"UTC\"", req.Headers["Prefer"].First());
    }

    [Fact]
    public async Task GraphMailSearchQuotesThePhraseAndPeopleParsesEmail()
    {
        _server.Given(Request.Create().WithPath("/v1.0/me/messages").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                value = new[] { new { subject = "Field test", from = new { emailAddress = new { address = "p@example.com" } }, receivedDateTime = "2026-10-27T22:14:00Z", bodyPreview = "logs attached" } },
            }));
        _server.Given(Request.Create().WithPath("/v1.0/me/people").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                value = new[] { new { displayName = "Priya Raman", jobTitle = "RF Engineer", department = "Modem", scoredEmailAddresses = new[] { new { address = "priya@example.com" } } } },
            }));

        var mail = await Graph().ListMessagesAsync("field test", 5);
        Assert.Equal("\"field test\"", _server.LogEntries.Last().RequestMessage!.Query!["$search"].First());
        Assert.Equal("p@example.com", Assert.Single(mail).From);

        var people = await Graph().FindPeopleAsync("Priya", 5);
        Assert.Equal("priya@example.com", Assert.Single(people).Email);
    }

    [Fact]
    public async Task GraphErrorsSurfaceCodeAndMessage()
    {
        _server.Given(Request.Create().WithPath("/v1.0/me/messages").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403).WithBodyAsJson(new { error = new { code = "ErrorAccessDenied", message = "Access is denied." } }));

        var e = await Assert.ThrowsAsync<IntegrationException>(() => Graph().ListMessagesAsync(null, 5));
        Assert.Contains("ErrorAccessDenied: Access is denied.", e.Message);
    }
}
